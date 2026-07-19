using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AutoRoute.App.Services;
using AutoRoute.PipeWire.Process;
using AutoRoute.Tests.TestSupport;
using Xunit;

namespace AutoRoute.Tests;

public sealed class AutostartServiceTests : IDisposable
{
    private readonly string _configHome = Directory.CreateTempSubdirectory("autoroute-autostart-").FullName;
    private const string Target = "/home/jessie/.local/bin/AutoRoute-v0.2.0-x86_64.AppImage";

    private string UnitPath => Path.Combine(_configHome, "systemd", "user", "autoroute.service");
    private string DesktopPath => Path.Combine(_configHome, "autostart", "autoroute.desktop");

    private AutostartService Service(FakeProcessRunner runner, string? target = Target)
        => new(runner, log: null, launchTargetResolver: () => target, configHome: _configHome);

    public void Dispose()
    {
        try { Directory.Delete(_configHome, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Enable_writes_and_enables_the_systemd_unit()
    {
        var runner = new FakeProcessRunner()
            .EnqueueStdout("")   // daemon-reload
            .EnqueueStdout("");  // enable

        var outcome = await Service(runner).EnableAsync();

        Assert.True(outcome.Success);
        Assert.Equal(AutostartMechanism.Systemd, outcome.Mechanism);

        var unit = File.ReadAllText(UnitPath);
        Assert.Contains($"ExecStart=\"{Target}\" --background", unit);
        Assert.Contains("WantedBy=default.target", unit);
        Assert.False(File.Exists(DesktopPath));

        Assert.Equal(
            new[] { "--user", "daemon-reload" },
            runner.Calls[0].Arguments);
        Assert.Equal(
            new[] { "--user", "enable", "autoroute.service" },
            runner.Calls[1].Arguments);
        Assert.All(runner.Calls, c => Assert.Equal("systemctl", c.FileName));
    }

    [Fact]
    public async Task Enable_falls_back_to_xdg_when_systemctl_cannot_run()
    {
        // systemctl absent: the real ProcessRunner throws when it can't start the binary.
        var runner = new FakeProcessRunner()
            .EnqueueThrow(new PwToolException("systemctl", "--user daemon-reload", -1, "No such file"));

        var outcome = await Service(runner).EnableAsync();

        Assert.True(outcome.Success);
        Assert.Equal(AutostartMechanism.Xdg, outcome.Mechanism);

        var desktop = File.ReadAllText(DesktopPath);
        Assert.Contains($"Exec=\"{Target}\" --background", desktop);
        Assert.Contains("Type=Application", desktop);
    }

    [Fact]
    public async Task Enable_falls_back_to_xdg_when_enable_is_refused()
    {
        var runner = new FakeProcessRunner()
            .EnqueueStdout("")                               // daemon-reload ok
            .EnqueueFailure("Failed to enable unit", exit: 1); // enable refused

        var outcome = await Service(runner).EnableAsync();

        Assert.Equal(AutostartMechanism.Xdg, outcome.Mechanism);
        Assert.True(File.Exists(DesktopPath));
    }

    [Fact]
    public async Task Enable_fails_cleanly_when_own_path_is_unknown()
    {
        var runner = new FakeProcessRunner();

        var outcome = await Service(runner, target: null).EnableAsync();

        Assert.False(outcome.Success);
        Assert.Equal(AutostartMechanism.None, outcome.Mechanism);
        Assert.Empty(runner.Calls);            // never touched systemctl
        Assert.False(File.Exists(UnitPath));
        Assert.False(File.Exists(DesktopPath));
    }

    [Fact]
    public async Task GetState_reports_systemd_when_the_unit_is_enabled()
    {
        var runner = new FakeProcessRunner().EnqueueStdout("enabled\n");

        var state = await Service(runner).GetStateAsync();

        Assert.True(state.Enabled);
        Assert.Equal(AutostartMechanism.Systemd, state.Mechanism);
        Assert.Equal(new[] { "--user", "is-enabled", "autoroute.service" }, runner.Calls[0].Arguments);
    }

    [Fact]
    public async Task GetState_reports_xdg_when_only_the_desktop_file_exists()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DesktopPath)!);
        File.WriteAllText(DesktopPath, "[Desktop Entry]\n");
        var runner = new FakeProcessRunner().EnqueueFailure("disabled", exit: 1); // is-enabled: not enabled

        var state = await Service(runner).GetStateAsync();

        Assert.True(state.Enabled);
        Assert.Equal(AutostartMechanism.Xdg, state.Mechanism);
    }

    [Fact]
    public async Task GetState_reports_none_when_nothing_is_installed()
    {
        var runner = new FakeProcessRunner().EnqueueThrow(
            new PwToolException("systemctl", "--user is-enabled", -1, "No such file"));

        var state = await Service(runner).GetStateAsync();

        Assert.False(state.Enabled);
        Assert.Equal(AutostartMechanism.None, state.Mechanism);
    }

    [Fact]
    public async Task Disable_removes_the_xdg_entry_and_asks_systemd_to_disable()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DesktopPath)!);
        File.WriteAllText(DesktopPath, "[Desktop Entry]\n");
        var runner = new FakeProcessRunner().EnqueueStdout(""); // disable

        var outcome = await Service(runner).DisableAsync();

        Assert.True(outcome.Success);
        Assert.False(File.Exists(DesktopPath));
        Assert.Equal(new[] { "--user", "disable", "autoroute.service" }, runner.Calls[0].Arguments);
    }

    [Fact]
    public void Generated_files_quote_a_target_with_spaces()
    {
        const string spaced = "/home/a b/AutoRoute.AppImage";

        Assert.Contains($"ExecStart=\"{spaced}\" --background", AutostartService.BuildUnitFile(spaced));
        Assert.Contains($"Exec=\"{spaced}\" --background", AutostartService.BuildDesktopFile(spaced));
    }
}
