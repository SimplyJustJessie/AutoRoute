using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AutoRoute.App.Hosting;
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
    private TrayController? _tray;
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

            // One teardown path shared by tray Quit and the POSIX signal handler; runs at most once.
            _shutdown = new GracefulShutdown(RunTeardown);

            var board = _services.GetRequiredService<BoardViewModel>();
            _mainWindow = new MainWindow { DataContext = board };
            _mainWindow.Closing += OnMainWindowClosing;

            var worker = _services.GetRequiredService<RoutingWorker>();
            _tray = new TrayController(this, worker, onOpen: ShowMainWindow, onQuit: Quit);

            if (!_options.Background)
                ShowMainWindow();

            // Wire the board's own graph/rule subscriptions and render the first snapshot without
            // blocking the UI thread. The worker has already primed the shared graph, so this is a
            // no-op reload; if it races ahead of the worker it does the real StartAsync safely.
            InitializeBoard(board);

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

    private void ShowMainWindow()
    {
        if (_mainWindow is null) return;
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Show();
        _mainWindow.Activate();
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // Hide-on-close: the app keeps running in the tray. A real shutdown (tray Quit / signal) sets
        // _quitting first, so the close is allowed through.
        if (_quitting) return;
        e.Cancel = true;
        _mainWindow?.Hide();
    }

    // Tray "Quit" and POSIX signals both funnel through the one-shot GracefulShutdown.
    private void Quit() => _shutdown?.RequestOnce();

    // The single teardown, run at most once (GracefulShutdown guarantees idempotency): drop the tray
    // and trigger the OnExplicitShutdown path — which returns from StartWithClassicDesktopLifetime so
    // Program stops the host (RuleStore watcher + graph monitor async-dispose) and unlinks the socket.
    private void RunTeardown()
    {
        _quitting = true;

        _tray?.Dispose();
        _tray = null;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
