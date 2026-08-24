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
    public async Task Enable_writes_the_xdg_autostart_entry()
    {
        var runner = new FakeProcessRunner();

        var outcome = await Service(runner).EnableAsync();

        Assert.True(outcome.Success);
        Assert.Equal(AutostartMechanism.Xdg, outcome.Mechanism);

        var desktop = File.ReadAllText(DesktopPath);
        Assert.Contains($"Exec=\"{Target}\" --background", desktop);
        Assert.Contains("Type=Application", desktop);
        // No systemd unit is generated any more — the XDG entry is the mechanism.
        Assert.False(File.Exists(UnitPath));
    }

    [Fact]
    public async Task Enable_retires_a_systemd_unit_left_by_an_older_build()
    {
        // Simulate an upgrade: a unit an older build wrote is still on disk.
        Directory.CreateDirectory(Path.GetDirectoryName(UnitPath)!);
        File.WriteAllText(UnitPath, "[Unit]\n");
        var runner = new FakeProcessRunner()
            .EnqueueStdout("")   // disable
            .EnqueueStdout("")   // daemon-reload
            .EnqueueStdout("");  // reset-failed

        var outcome = await Service(runner).EnableAsync();

        Assert.Equal(AutostartMechanism.Xdg, outcome.Mechanism);
        Assert.True(File.Exists(DesktopPath));
        Assert.False(File.Exists(UnitPath));                       // old unit deleted
        Assert.Contains(runner.Calls, c => c.Arguments.SequenceEqual(new[] { "--user", "disable", "autoroute.service" }));
        Assert.Contains(runner.Calls, c => c.Arguments.SequenceEqual(new[] { "--user", "daemon-reload" }));
    }

    [Fact]
    public async Task Enable_survives_a_missing_systemctl_when_cleaning_up()
    {
        // No leftover unit, and systemctl can't even run — enabling must still succeed via XDG.
        var runner = new FakeProcessRunner()
            .EnqueueThrow(new PwToolException("systemctl", "--user disable", -1, "No such file"));

        var outcome = await Service(runner).EnableAsync();

        Assert.True(outcome.Success);
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
    public async Task GetState_reports_xdg_when_the_desktop_file_exists()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DesktopPath)!);
        File.WriteAllText(DesktopPath, "[Desktop Entry]\n");
        var runner = new FakeProcessRunner();

        var state = await Service(runner).GetStateAsync();

        Assert.True(state.Enabled);
        Assert.Equal(AutostartMechanism.Xdg, state.Mechanism);
        Assert.Empty(runner.Calls);            // XDG present → no need to ask systemd
    }

    [Fact]
    public async Task GetState_reports_systemd_when_a_hand_installed_unit_is_enabled()
    {
        var runner = new FakeProcessRunner().EnqueueStdout("enabled\n");

        var state = await Service(runner).GetStateAsync();

        Assert.True(state.Enabled);
        Assert.Equal(AutostartMechanism.Systemd, state.Mechanism);
        Assert.Equal(new[] { "--user", "is-enabled", "autoroute.service" }, runner.Calls[0].Arguments);
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
    public async Task Disable_removes_the_xdg_entry_and_retires_the_systemd_unit()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DesktopPath)!);
        File.WriteAllText(DesktopPath, "[Desktop Entry]\n");
        var runner = new FakeProcessRunner();

        var outcome = await Service(runner).DisableAsync();

        Assert.True(outcome.Success);
        Assert.False(File.Exists(DesktopPath));
        Assert.Contains(runner.Calls, c => c.Arguments.SequenceEqual(new[] { "--user", "disable", "autoroute.service" }));
    }

    [Fact]
    public void The_generated_desktop_entry_quotes_a_target_with_spaces()
    {
        const string spaced = "/home/a b/AutoRoute.AppImage";

        Assert.Contains($"Exec=\"{spaced}\" --background", AutostartService.BuildDesktopFile(spaced));
    }

    // ===== stale-entry repair ===========================================================
    // The live bug: autostart was enabled from AutoRoute-v0.2.0-x86_64.AppImage, the user upgraded
    // by downloading v0.3.1 alongside it and deleting the old file, and autostart silently died
    // because the entry still named the v0.2.0 path.

    /// <summary>An AppImage that exists on disk, standing in for an installed launcher.</summary>
    private string MakeLauncher(string name)
    {
        var path = Path.Combine(_configHome, name);
        File.WriteAllText(path, "#!/bin/sh\n");
        return path;
    }

    [Fact]
    public async Task Repair_repoints_an_entry_whose_launcher_is_gone()
    {
        var old = MakeLauncher("AutoRoute-v0.2.0-x86_64.AppImage");
        await Service(new FakeProcessRunner(), old).EnableAsync();
        File.Delete(old);                                    // the upgrade removes the old image
        var current = MakeLauncher("AutoRoute-v0.3.1-x86_64.AppImage");

        var repair = Service(new FakeProcessRunner(), current).RepairStaleEntry();

        Assert.True(repair.Repaired);
        Assert.Equal(old, repair.OldTarget);
        Assert.Equal(current, repair.NewTarget);
        Assert.Contains($"Exec=\"{current}\" --background", File.ReadAllText(DesktopPath));
    }

    [Fact]
    public async Task Repair_leaves_an_entry_alone_while_its_launcher_still_exists()
    {
        // Two installs coexisting is not a broken entry — choosing between them would be a guess,
        // so only a launcher that is actually gone counts as stale.
        var other = MakeLauncher("AutoRoute-v0.2.0-x86_64.AppImage");
        await Service(new FakeProcessRunner(), other).EnableAsync();
        var current = MakeLauncher("AutoRoute-v0.3.1-x86_64.AppImage");

        var repair = Service(new FakeProcessRunner(), current).RepairStaleEntry();

        Assert.False(repair.Repaired);
        Assert.Contains($"Exec=\"{other}\" --background", File.ReadAllText(DesktopPath));
    }

    [Fact]
    public void Repair_never_creates_an_entry_when_autostart_is_off()
    {
        // Autostart the user turned off must stay off — repair only ever rewrites, never installs.
        var repair = Service(new FakeProcessRunner(), MakeLauncher("AutoRoute")).RepairStaleEntry();

        Assert.False(repair.Repaired);
        Assert.False(File.Exists(DesktopPath));
    }

    [Theory]
    [InlineData("[Desktop Entry]\nExec=\"/opt/AutoRoute.AppImage\" --background\n", "/opt/AutoRoute.AppImage")]
    [InlineData("[Desktop Entry]\nExec=/usr/bin/autoroute --background\n", "/usr/bin/autoroute")]
    [InlineData("[Desktop Entry]\nExec=\"/home/a b/AutoRoute\" --background\n", "/home/a b/AutoRoute")]
    [InlineData("[Desktop Entry]\nExec=/usr/bin/autoroute\n", "/usr/bin/autoroute")]
    [InlineData("[Desktop Entry]\nName=AutoRoute\n", null)]
    [InlineData("", null)]
    public void Parses_the_launcher_out_of_an_exec_line(string desktopFile, string? expected)
        => Assert.Equal(expected, AutostartService.ParseExecTarget(desktopFile));
}
