using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.PipeWire.Process;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoRoute.App.Services;

/// <summary>Which mechanism (if any) currently autostarts AutoRoute.</summary>
public enum AutostartMechanism
{
    None,
    Systemd,
    Xdg,
}

/// <summary>The autostart state as it stands on disk / in the systemd user manager.</summary>
public readonly record struct AutostartState(bool Enabled, AutostartMechanism Mechanism);

/// <summary>The result of an enable/disable request, with a user-facing message.</summary>
public readonly record struct AutostartOutcome(bool Success, AutostartMechanism Mechanism, string Message);

/// <summary>
/// What <see cref="AutostartService.RepairStaleEntry"/> did: <paramref name="Repaired"/> is true only
/// when an installed entry pointed at a launcher that no longer exists and was re-pointed at this one.
/// </summary>
public readonly record struct AutostartRepair(bool Repaired, string? OldTarget, string? NewTarget);

/// <summary>
/// Installs and removes "start AutoRoute at login" on the user's own machine, from inside the app —
/// so an AppImage user never has to hand-write a systemd unit or a <c>.desktop</c> file.
///
/// The mechanism is an XDG <c>~/.config/autostart</c> entry, which needs no <c>systemctl</c> at all;
/// a systemd user unit is only ever recognised as a hand-install (or retired as an older build's
/// leftover). The launcher it points at is AutoRoute's own path — the <c>$APPIMAGE</c> file when
/// running from an AppImage (its in-mount executable path is ephemeral), otherwise the running
/// executable. Because that path is absolute, <see cref="RepairStaleEntry"/> re-points it when it
/// goes stale (an AppImage upgraded by filename); call it at startup.
///
/// It intentionally never <c>enable --now</c>s: the app is already running when the button is
/// clicked, and starting a second <c>--background</c> instance would just bounce off the
/// single-instance guard (ADR-0005). Autostart takes effect at the next login.
/// </summary>
public sealed class AutostartService
{
    public const string UnitName = "autoroute.service";
    public const string DesktopFileName = "autoroute.desktop";

    private readonly IProcessRunner _runner;
    private readonly ILogger _log;
    private readonly Func<string?> _launchTarget;
    private readonly string _configHome;

    public AutostartService(
        IProcessRunner runner,
        ILogger<AutostartService>? log = null,
        Func<string?>? launchTargetResolver = null,
        string? configHome = null)
    {
        _runner = runner;
        _log = log ?? NullLogger<AutostartService>.Instance;
        _launchTarget = launchTargetResolver ?? DefaultLaunchTarget;
        _configHome = configHome ?? DefaultConfigHome();
    }

    /// <summary>
    /// The absolute path autostart should launch: the AppImage file (<c>$APPIMAGE</c>) when running
    /// from one — its in-mount <see cref="Environment.ProcessPath"/> lives under an ephemeral
    /// <c>/tmp/.mount_*</c> and would be a dead path at the next boot — otherwise the running
    /// executable. <c>null</c> only if the runtime can't tell us either (very unusual).
    /// </summary>
    public string? LaunchTarget => _launchTarget();

    private string UnitPath => Path.Combine(_configHome, "systemd", "user", UnitName);
    private string DesktopPath => Path.Combine(_configHome, "autostart", DesktopFileName);

    /// <summary>
    /// The launcher path recorded in the installed XDG entry, or <c>null</c> when there is no entry
    /// (or its <c>Exec</c> line can't be parsed).
    /// </summary>
    public string? InstalledLaunchTarget
    {
        get
        {
            try
            {
                return File.Exists(DesktopPath) ? ParseExecTarget(File.ReadAllText(DesktopPath)) : null;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "autostart: reading the installed entry failed");
                return null;
            }
        }
    }

    /// <summary>
    /// Re-point an installed autostart entry that has gone stale. The entry records an absolute
    /// launcher path, and an AppImage carries its version in the filename — so upgrading by
    /// downloading <c>AutoRoute-v0.3.1-x86_64.AppImage</c> next to the v0.2.0 one leaves autostart
    /// aimed at a file that no longer exists, and it silently stops working. (An in-place self-update
    /// keeps the same path and is unaffected; so is a distro install, whose path has no version in
    /// it.) Re-pointing it at the running AutoRoute honours what the user already asked for.
    ///
    /// <para>Deliberately conservative: it only rewrites when the recorded launcher <b>no longer
    /// exists</b>. If it is still there, two installs coexist and picking one for the user would be a
    /// guess — a dead path is the only unambiguous case. Never creates an entry, so autostart the
    /// user turned off stays off.</para>
    /// </summary>
    public AutostartRepair RepairStaleEntry()
    {
        var recorded = InstalledLaunchTarget;
        if (recorded is null) return default;            // no entry, or unparseable — nothing to do
        if (File.Exists(recorded)) return default;       // still a real launcher — leave it alone

        var current = LaunchTarget;
        if (string.IsNullOrEmpty(current) || current == recorded) return default;

        try
        {
            WriteXdgEntry(current);
            _log.LogInformation(
                "autostart: repaired a stale entry — {Old} no longer exists, now launching {New}",
                recorded, current);
            return new AutostartRepair(true, recorded, current);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "autostart: repairing the stale entry failed");
            return default;
        }
    }

    /// <summary>
    /// Pull the launcher out of a desktop entry's <c>Exec=</c> line. We write it quoted
    /// (<c>Exec="/path" --background</c>); an unquoted first token is accepted too, so an entry a
    /// user hand-wrote still parses.
    /// </summary>
    public static string? ParseExecTarget(string? desktopFile)
    {
        if (string.IsNullOrEmpty(desktopFile)) return null;

        foreach (var raw in desktopFile.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("Exec=", StringComparison.Ordinal)) continue;

            var value = line["Exec=".Length..].Trim();
            if (value.Length == 0) return null;

            if (value[0] == '"')
            {
                var end = value.IndexOf('"', 1);
                return end > 1 ? value[1..end] : null;
            }

            var space = value.IndexOf(' ');
            return space < 0 ? value : value[..space];
        }
        return null;
    }

    /// <summary>Report whether AutoRoute is set to start at login, and by which mechanism.</summary>
    public async Task<AutostartState> GetStateAsync(CancellationToken ct = default)
    {
        // Our own mechanism is the XDG entry; a systemd unit is only ever a hand-installed opt-in
        // (or a leftover from an older build), so the desktop entry is what we check first.
        if (File.Exists(DesktopPath))
            return new AutostartState(true, AutostartMechanism.Xdg);
        if (await SystemdIsEnabledAsync(ct).ConfigureAwait(false))
            return new AutostartState(true, AutostartMechanism.Systemd);
        return new AutostartState(false, AutostartMechanism.None);
    }

    /// <summary>
    /// Install autostart via an XDG <c>~/.config/autostart</c> entry. The desktop session launches it
    /// as one of its own children, so it inherits the display, session DBus and tray environment a
    /// GUI/tray app needs — the reason this is preferred over a systemd user service, which starts
    /// under the systemd user manager and often lacks that environment (the tray app then can't reach
    /// a display and crash-loops). Any systemd unit a previous build left behind is removed so the two
    /// can't both fire at login. Headless/no-desktop setups can still hand-install
    /// <c>dist/systemd/autoroute.service</c>.
    /// </summary>
    public async Task<AutostartOutcome> EnableAsync(CancellationToken ct = default)
    {
        var target = LaunchTarget;
        if (string.IsNullOrEmpty(target))
        {
            _log.LogWarning("autostart: could not determine AutoRoute's own path");
            return new AutostartOutcome(false, AutostartMechanism.None,
                "Couldn't determine AutoRoute's own path — autostart not changed.");
        }

        try
        {
            WriteXdgEntry(target);
            _log.LogInformation("autostart: enabled via XDG desktop entry {Path}", DesktopPath);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "autostart: writing the XDG entry failed");
            return new AutostartOutcome(false, AutostartMechanism.None,
                $"Couldn't enable autostart: {ex.Message}");
        }

        // Retire any systemd unit an older build created, so it can't double-launch (or crash-loop)
        // alongside the desktop entry.
        await RemoveSystemdUnitAsync(ct).ConfigureAwait(false);
        return new AutostartOutcome(true, AutostartMechanism.Xdg,
            "AutoRoute will start automatically at your next login.");
    }

    /// <summary>Remove autostart from both mechanisms; disabling something that isn't there is a no-op.</summary>
    public async Task<AutostartOutcome> DisableAsync(CancellationToken ct = default)
    {
        RemoveXdgEntry();
        await RemoveSystemdUnitAsync(ct).ConfigureAwait(false);
        _log.LogInformation("autostart: disabled");
        return new AutostartOutcome(true, AutostartMechanism.None,
            "AutoRoute will no longer start automatically at login.");
    }

    // ===== systemd (detect a hand-install; clean up our old auto-generated unit) =========

    private async Task<bool> SystemdIsEnabledAsync(CancellationToken ct)
    {
        var result = await TryRunSystemctlAsync(ct, "is-enabled", UnitName).ConfigureAwait(false);
        // is-enabled prints the state on stdout; exit 0 only for enabled/enabled-runtime.
        return result is { ExitCode: 0 } r && r.StdOut.Trim().StartsWith("enabled", StringComparison.Ordinal);
    }

    /// <summary>
    /// Retire a systemd user unit AutoRoute may have written in an older build: disable it, delete the
    /// unit file, reload, and clear any latched failure. All best-effort — no systemd manager, or no
    /// such unit, is a no-op.
    /// </summary>
    private async Task RemoveSystemdUnitAsync(CancellationToken ct)
    {
        var hadUnit = File.Exists(UnitPath);
        await TryRunSystemctlAsync(ct, "disable", UnitName).ConfigureAwait(false);
        try { File.Delete(UnitPath); } catch (Exception ex) { _log.LogDebug(ex, "autostart: deleting old unit failed"); }
        if (hadUnit)
            await TryRunSystemctlAsync(ct, "daemon-reload").ConfigureAwait(false);
        await TryRunSystemctlAsync(ct, "reset-failed", UnitName).ConfigureAwait(false);
    }

    /// <summary>Run <c>systemctl --user …</c>, returning <c>null</c> when systemctl can't be run at all.</summary>
    private async Task<ProcessResult?> TryRunSystemctlAsync(CancellationToken ct, params string[] args)
    {
        var argv = new string[args.Length + 1];
        argv[0] = "--user";
        Array.Copy(args, 0, argv, 1, args.Length);
        try
        {
            return await _runner.RunAsync("systemctl", argv, throwOnNonZero: false, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // ProcessRunner throws PwToolException when the binary itself can't be started.
            _log.LogDebug(ex, "autostart: systemctl {Args} could not run", string.Join(' ', argv));
            return null;
        }
    }

    // ===== XDG autostart ================================================================

    private void WriteXdgEntry(string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DesktopPath)!);
        File.WriteAllText(DesktopPath, BuildDesktopFile(target));
    }

    private void RemoveXdgEntry()
    {
        try { File.Delete(DesktopPath); }
        catch (Exception ex) { _log.LogDebug(ex, "autostart: removing XDG entry failed"); }
    }

    /// <summary>
    /// The generated <c>~/.config/autostart</c> entry. The launcher path is quoted so a path with
    /// spaces survives the desktop-entry <c>Exec</c> parser.
    /// </summary>
    public static string BuildDesktopFile(string target) =>
        $"""
        [Desktop Entry]
        Type=Application
        Name=AutoRoute
        Comment=Wire your PipeWire audio once, and keep it wired
        Exec="{target}" --background
        Terminal=false
        X-GNOME-Autostart-enabled=true

        """;

    // ===== path resolution ==============================================================

    private static string? DefaultLaunchTarget()
    {
        // Set by the AppImage runtime to the absolute path of the .AppImage file itself; its AppRun
        // forwards our arguments, so "<image> --background" is exactly the tray-only launch.
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrEmpty(appImage))
            return appImage;
        return Environment.ProcessPath;
    }

    private static string DefaultConfigHome()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdg))
            return xdg;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
    }
}
