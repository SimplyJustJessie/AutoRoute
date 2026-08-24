using AutoRoute.App.Hosting;
using AutoRoute.App.Services;
using AutoRoute.App.ViewModels;
using AutoRoute.Engine;
using AutoRoute.PipeWire;
using Microsoft.Extensions.DependencyInjection;

namespace AutoRoute.Tests;

/// <summary>
/// Confirms the always-on host's DI graph composes headlessly — the real service provider resolves
/// the full graph (including <see cref="BoardViewModel"/> and <see cref="RoutingWorker"/>) without
/// starting anything or opening a window, and the monitor choice honours <c>--poll</c>.
/// </summary>
public sealed class HostCompositionTests
{
    [Fact]
    public void Host_resolves_board_and_worker_as_shared_singletons()
    {
        using var host = HostFactory.Build(new AppOptions());
        var sp = host.Services;

        var board = sp.GetRequiredService<BoardViewModel>();
        var worker = sp.GetRequiredService<RoutingWorker>();

        Assert.NotNull(board);
        Assert.NotNull(worker);
        Assert.Same(worker, sp.GetRequiredService<RoutingWorker>());
        Assert.Same(board, sp.GetRequiredService<BoardViewModel>());
    }

    [Fact]
    public void Host_resolves_a_separate_video_board_singleton_sharing_the_same_graph_and_rules()
    {
        using var host = HostFactory.Build(new AppOptions());
        var sp = host.Services;

        var audio = sp.GetRequiredService<BoardViewModel>();
        var video = sp.GetRequiredService<VideoBoardViewModel>();

        Assert.NotSame(audio, video);
        Assert.Same(video, sp.GetRequiredService<VideoBoardViewModel>());
        Assert.Equal(MediaKind.Audio, audio.Kind);
        Assert.Equal(MediaKind.Video, video.Kind);
    }

    [Fact]
    public void Host_resolves_the_full_pipewire_and_engine_graph()
    {
        using var host = HostFactory.Build(new AppOptions());
        var sp = host.Services;

        Assert.NotNull(sp.GetRequiredService<IPwGraphService>());
        Assert.NotNull(sp.GetRequiredService<IPwLinker>());
        Assert.NotNull(sp.GetRequiredService<IRuleStore>());
        Assert.NotNull(sp.GetRequiredService<IRuleMatcher>());
        Assert.NotNull(sp.GetRequiredService<IReconciler>());
    }

    [Fact]
    public void Host_resolves_the_updater()
    {
        using var host = HostFactory.Build(new AppOptions());
        var sp = host.Services;

        Assert.NotNull(sp.GetRequiredService<AppVersion>());
        var updater = sp.GetRequiredService<UpdateService>();
        Assert.NotNull(updater);
        Assert.Same(updater, sp.GetRequiredService<UpdateService>());
    }

    [Fact]
    public void Default_monitor_is_pwmon()
    {
        using var host = HostFactory.Build(new AppOptions());
        Assert.IsType<PwMonMonitor>(host.Services.GetRequiredService<IGraphMonitor>());
    }

    [Fact]
    public void Poll_option_selects_the_polling_monitor()
    {
        using var host = HostFactory.Build(new AppOptions { Poll = true });
        Assert.IsType<PollingGraphMonitor>(host.Services.GetRequiredService<IGraphMonitor>());
    }

    [Fact]
    public void Automation_flag_defaults_on_and_toggles()
    {
        using var host = HostFactory.Build(new AppOptions());
        var worker = host.Services.GetRequiredService<RoutingWorker>();

        Assert.True(worker.AutomationEnabled);
        worker.AutomationEnabled = false;
        Assert.False(worker.AutomationEnabled);
        worker.AutomationEnabled = true;
        Assert.True(worker.AutomationEnabled);
    }

    [Theory]
    [InlineData(new string[0], false, false)]
    [InlineData(new[] { "--background" }, true, false)]
    [InlineData(new[] { "--poll" }, false, true)]
    [InlineData(new[] { "--background", "--poll" }, true, true)]
    public void Options_parse_background_and_poll(string[] args, bool background, bool poll)
    {
        var options = AppOptions.Parse(args);
        Assert.Equal(background, options.Background);
        Assert.Equal(poll, options.Poll);
    }
}
