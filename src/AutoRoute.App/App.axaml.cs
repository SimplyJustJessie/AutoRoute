using System;
using System.Runtime;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AutoRoute.App.Hosting;
using AutoRoute.App.Services;
using AutoRoute.PipeWire;
using AutoRoute.PipeWire.Meters;
using AutoRoute.App.ViewModels;
using AutoRoute.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace AutoRoute.App;

public partial class App : Application
{
    private static App? _current;

    private IServiceProvider? _services;
    private AppOptions _options = new();
    private MainWindow? _mainWindow;
    private MainWindowViewModel? _mainViewModel;
    // A hidden window still holds its visual tree, render caches and the GPU state behind them
    // (~130 MiB). Once it has been hidden this long it is closed for real, and the next Open builds
    // a fresh one; a quick close-and-reopen keeps the same window.
    private static readonly TimeSpan ReleaseHiddenWindowAfter = TimeSpan.FromSeconds(30);
    private DispatcherTimer? _releaseTimer;
    private bool _releasingWindow;
    private WindowPlacement? _placement;
    private TrayController? _tray;
    private LevelMeterFeed? _meters;

    // Whether we have put the window on screen (Show) and not hidden it since: meters key off this
    // as well as IsVisible, so nothing but a real Show can switch them on.
    private bool _windowShown;
    private Action? _updateMeters;
    private GracefulShutdown? _shutdown;
    private bool _quitting;

    // Set by Program before the Avalonia lifetime starts. Kept as static fields because Avalonia
    // constructs App via its parameterless ctor (AppBuilder.Configure<App>()).
    private static IServiceProvider? _pendingServices;
    private static AppOptions? _pendingOptions;

    // Set if a SIGTERM/SIGINT arrives during startup, before the UI thread is ready to act on it.
    private static volatile bool _shutdownRequested;

    /// <summary>Hand the real host services + parsed options to the app before it starts.</summary>
    public static void Configure(IServiceProvider services, AppOptions options)
    {
        _pendingServices = services;
        _pendingOptions = options;
    }

    /// <summary>
    /// Reveal the main window from any thread — the single-instance reveal callback and the tray
    /// "Open" both route through here. Safe to call before the window exists (no-op).
    /// </summary>
    public static void RequestReveal()
    {
        var app = _current;
        if (app is null) return;
        Dispatcher.UIThread.Post(app.ShowMainWindow);
    }

    /// <summary>
    /// Request a graceful shutdown from any thread — the SIGTERM/SIGINT handler (registered in
    /// Program) routes here. Runs the same one-shot teardown as tray Quit. If the signal arrives
    /// before the UI thread is ready, the request is latched and honoured once init completes, so a
    /// stop during startup still exits cleanly and unlinks the socket.
    /// </summary>
    public static void RequestShutdown()
    {
        _shutdownRequested = true;
        var app = _current;
        if (app is null) return;
        Dispatcher.UIThread.Post(() => app._shutdown?.RequestOnce());
    }

    public override void Initialize()
    {
        _current = this;
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Only the real desktop launch wires the host. The window-free smoke checks
        // (--smoke-ui) call SetupWithoutStarting with no classic-desktop lifetime and no pending
        // services, so this block is skipped and DevComposition remains in charge there.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            _pendingServices is not null)
        {
            _services = _pendingServices;
            _options = _pendingOptions ?? new AppOptions();

            // Closing the window only hides it; the watcher + tray keep running. Tray Quit — and a
            // SIGTERM/SIGINT (systemctl stop / Ctrl-C) — are the real stops (ADR-0005 / PLAN
            // "Process, tray, autostart").
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ShutdownExceptionFilter.InstallOnDispatcher(Dispatcher.UIThread);

            // One teardown path shared by tray Quit and the POSIX signal handler; runs at most once.
            _shutdown = new GracefulShutdown(RunTeardown);

            var board = _services.GetRequiredService<BoardViewModel>();
            var videoBoard = _services.GetRequiredService<VideoBoardViewModel>();
            var tabs = _services.GetRequiredService<TabSelectionState>();
            _mainViewModel = new MainWindowViewModel(board, videoBoard, tabs);

            // Live levels only while they can be seen: window shown, not minimized, Audio tab.
            _meters = new LevelMeterFeed(
                _services.GetRequiredService<ILevelMeter>(),
                _services.GetRequiredService<IPwGraphService>(),
                board);
            void UpdateMeters()
            {
                if (_meters is null || _mainWindow is null) return;
                _meters.Active = !_quitting && _windowShown && _mainWindow.IsVisible
                                 && _mainWindow.WindowState != WindowState.Minimized && !tabs.VideoSelected;
            }
            _updateMeters = UpdateMeters;
            tabs.PropertyChanged += (_, _) => UpdateMeters();

            var worker = _services.GetRequiredService<RoutingWorker>();
            // One Auto-route switch: the worker follows whatever the tray or the top bar sets.
            var automation = _services.GetRequiredService<AutomationState>();
            automation.Enabled = worker.AutomationEnabled;
            automation.PropertyChanged += (_, _) => worker.AutomationEnabled = automation.Enabled;
            _tray = new TrayController(this, automation, onOpen: ShowMainWindow, onQuit: Quit);

            if (!_options.Background)
                ShowMainWindow();

            // Wire each board's own graph/rule subscriptions and render its first snapshot without
            // blocking the UI thread. The worker has already primed the shared graph, so this is a
            // no-op reload; if it races ahead of the worker it does the real StartAsync safely. Both
            // boards share the same underlying graph/rule-store singletons (see
            // HostFactory/VideoBoardViewModel), so this is two cheap subscribes, not two pollers.
            InitializeBoard(board);
            InitializeBoard(videoBoard);

            // Honour a signal that arrived while Avalonia was still initializing.
            if (_shutdownRequested)
                Dispatcher.UIThread.Post(() => _shutdown?.RequestOnce());
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void InitializeBoard(BoardViewModel board)
    {
        try
        {
            await board.InitializeAsync();
        }
        catch (Exception ex)
        {
            // Startup rendering is best-effort; the graph service remains the source of truth.
            Console.Error.WriteLine("board initialization failed: " + ex);
        }
    }

    private MainWindow? EnsureMainWindow()
    {
        if (_mainWindow is not null || _mainViewModel is null || _quitting) return _mainWindow;
        var window = new MainWindow { DataContext = _mainViewModel };
        _placement?.ApplyTo(window);
        window.Closing += OnMainWindowClosing;
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty && e.NewValue is false) _windowShown = false;
            if (e.Property == Visual.IsVisibleProperty || e.Property == Window.WindowStateProperty)
                _updateMeters?.Invoke();
        };
        _mainWindow = window;
        return window;
    }

    private void ShowMainWindow()
    {
        _releaseTimer?.Stop();
        if (EnsureMainWindow() is not { } window) return;
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        _windowShown = true;
        window.Show();
        window.Activate();
        _updateMeters?.Invoke();
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // A real shutdown (tray Quit / signal) sets _quitting first, and releasing a hidden window
        // closes it on purpose, so both are allowed through.
        if (_quitting || _releasingWindow) return;

        // The desktop session is logging out or the PC is restarting: the session manager asks the
        // window to close as part of teardown. Vetoing that (as hide-on-close does below) makes
        // Avalonia's X11 session-management client refuse the request, which KDE/GNOME report as
        // "Logout canceled by <app>" and abort the restart. So on a shutdown-initiated close, don't
        // hide — run the one graceful teardown and let the window close so the whole app exits.
        if (e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown)
        {
            // Let this close proceed (no Cancel), and run the teardown on the next dispatcher turn so
            // we don't re-enter desktop.Shutdown() while the window is still mid-close.
            Dispatcher.UIThread.Post(() => _shutdown?.RequestOnce());
            return;
        }

        // Ordinary user close (the window's X button): hide to the tray and keep running.
        e.Cancel = true;
        _windowShown = false;
        _mainWindow?.Hide();
        _updateMeters?.Invoke();
        ScheduleWindowRelease();
    }

    private void ScheduleWindowRelease()
    {
        if (_releaseTimer is null)
        {
            // The (interval, priority, handler) constructor starts the timer, so build it stopped.
            _releaseTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ReleaseHiddenWindowAfter };
            _releaseTimer.Tick += (_, _) =>
            {
                _releaseTimer?.Stop();
                ReleaseHiddenWindow();
            };
        }
        _releaseTimer.Stop();
        _releaseTimer.Start();
    }

    private void ReleaseHiddenWindow()
    {
        if (_mainWindow is not { } window || window.IsVisible || _quitting) return;
        _placement = WindowPlacement.From(window);
        _mainWindow = null;
        _releasingWindow = true;
        try { window.Close(); }
        finally { _releasingWindow = false; }
        window.DataContext = null;
        _updateMeters?.Invoke();

        // Hand the memory back to the system now rather than whenever the next GC runs: an idle
        // tray app allocates too little to trigger one, and an ordinary collection keeps the freed
        // segments committed. Posted so the closed window's last render work has drained.
        Dispatcher.UIThread.Post(() =>
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            NativeHeap.TrimFreePages();
        }, DispatcherPriority.Background);
    }

    /// <summary>Where the window was, so a rebuilt one opens in the same place and size.</summary>
    private sealed record WindowPlacement(PixelPoint Position, double Width, double Height, WindowState State)
    {
        public static WindowPlacement From(Window w) =>
            new(w.Position, w.Width, w.Height, w.WindowState == WindowState.Minimized ? WindowState.Normal : w.WindowState);

        public void ApplyTo(Window w)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Position = Position;
            if (double.IsFinite(Width) && double.IsFinite(Height)) { w.Width = Width; w.Height = Height; }
            w.WindowState = State;
        }
    }

    // Tray "Quit" and POSIX signals both funnel through the one-shot GracefulShutdown.
    private void Quit() => _shutdown?.RequestOnce();

    // The single teardown, run at most once (GracefulShutdown guarantees idempotency): drop the tray
    // and trigger the OnExplicitShutdown path — which returns from StartWithClassicDesktopLifetime so
    // Program stops the host (RuleStore watcher + graph monitor async-dispose) and unlinks the socket.
    private void RunTeardown()
    {
        _quitting = true;
        ShutdownExceptionFilter.MarkShuttingDown();

        _tray?.Dispose();
        _tray = null;

        _meters?.Dispose();
        _meters = null;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
