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
/// Installs and removes "start AutoRoute at login" on the user's own machine, from inside the app —
/// so an AppImage user never has to hand-write a systemd unit or a <c>.desktop</c> file.
///
/// It prefers a **systemd user service** (restart-on-failure + journald, matching
/// <c>dist/systemd/autoroute.service</c>); if the systemd user manager isn't usable it falls back to
/// an XDG <c>~/.config/autostart</c> entry, which needs no <c>systemctl</c> at all. Either way the
/// launcher it points at is AutoRoute's own path — the <c>$APPIMAGE</c> file when running from an
/// AppImage (its in-mount executable path is ephemeral), otherwise the running executable.
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

    /// <summary>Report whether AutoRoute is set to start at login, and by which mechanism.</summary>
    public async Task<AutostartState> GetStateAsync(CancellationToken ct = default)
    {
        if (await SystemdIsEnabledAsync(ct).ConfigureAwait(false))
            return new AutostartState(true, AutostartMechanism.Systemd);
        if (File.Exists(DesktopPath))
            return new AutostartState(true, AutostartMechanism.Xdg);
        return new AutostartState(false, AutostartMechanism.None);
    }

    /// <summary>
    /// Install autostart: try the systemd user unit first, fall back to an XDG desktop entry. Any
    /// leftover from the other mechanism is cleared so the two never both fire at login.
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

        if (await TryEnableSystemdAsync(target, ct).ConfigureAwait(false))
        {
            RemoveXdgEntry(); // don't let a stale .desktop double-launch alongside the unit
            return new AutostartOutcome(true, AutostartMechanism.Systemd,
                "AutoRoute will start automatically at your next login (systemd user service).");
        }

        // systemd wasn't usable (no user manager, systemctl missing, enable refused) — XDG works
        // anywhere a desktop session honours ~/.config/autostart.
        try
        {
            WriteXdgEntry(target);
            _log.LogInformation("autostart: enabled via XDG desktop entry {Path}", DesktopPath);
            return new AutostartOutcome(true, AutostartMechanism.Xdg,
                "AutoRoute will start at login via a desktop autostart entry (systemd user service wasn't available).");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "autostart: XDG fallback failed");
            return new AutostartOutcome(false, AutostartMechanism.None,
                $"Couldn't enable autostart: {ex.Message}");
        }
    }

    /// <summary>Remove autostart from both mechanisms; disabling something that isn't there is a no-op.</summary>
    public async Task<AutostartOutcome> DisableAsync(CancellationToken ct = default)
    {
        // Best-effort on both fronts so state is unambiguous afterwards.
        await TryRunSystemctlAsync(ct, "disable", UnitName).ConfigureAwait(false);
        RemoveXdgEntry();
        _log.LogInformation("autostart: disabled");
        return new AutostartOutcome(true, AutostartMechanism.None,
            "AutoRoute will no longer start automatically at login.");
    }

    // ===== systemd ======================================================================

    private async Task<bool> SystemdIsEnabledAsync(CancellationToken ct)
    {
        var result = await TryRunSystemctlAsync(ct, "is-enabled", UnitName).ConfigureAwait(false);
        // is-enabled prints the state on stdout; exit 0 only for enabled/enabled-runtime.
        return result is { ExitCode: 0 } r && r.StdOut.Trim().StartsWith("enabled", StringComparison.Ordinal);
    }

    private async Task<bool> TryEnableSystemdAsync(string target, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UnitPath)!);
            File.WriteAllText(UnitPath, BuildUnitFile(target));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "autostart: writing the systemd unit failed — trying XDG");
            return false;
        }

        // daemon-reload so the manager sees a freshly written/updated unit; enable (not --now).
        if (await TryRunSystemctlAsync(ct, "daemon-reload").ConfigureAwait(false) is null)
            return false; // no systemd user manager here
        // Clear any latched failure (e.g. a prior broken unit that hit the start limit) so this
        // enable — and the next login's start — begins from a clean slate. Harmless if not failed.
        await TryRunSystemctlAsync(ct, "reset-failed", UnitName).ConfigureAwait(false);
        var enable = await TryRunSystemctlAsync(ct, "enable", UnitName).ConfigureAwait(false);
        if (enable is { ExitCode: 0 })
        {
            _log.LogInformation("autostart: enabled systemd unit {Path}", UnitPath);
            return true;
        }

        _log.LogWarning("autostart: systemctl enable failed ({Err}) — trying XDG",
            enable?.StdErr.Trim() ?? "systemctl unavailable");
        return false;
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

    /// <summary>
    /// The generated systemd user unit, with the concrete absolute <paramref name="target"/> baked
    /// into <c>ExecStart</c> (quoted, so a home directory with spaces still parses).
    ///
    /// Two things are deliberately different from a hand-written server unit, because this one must
    /// launch a <b>GUI/tray app</b> that may be an <b>AppImage</b>:
    /// <list type="bullet">
    /// <item>It orders after (and is <c>WantedBy</c>) <c>graphical-session.target</c>, not
    /// <c>default.target</c> — so the display, session DBus and tray already exist when it starts.</item>
    /// <item>It sets <b>no</b> sandboxing (<c>NoNewPrivileges</c>, <c>RestrictNamespaces</c>,
    /// <c>RestrictSUIDSGID</c>, …). Those block the <b>setuid <c>fusermount</c></b> an AppImage uses to
    /// mount itself, so every start would fail non-zero and trip systemd's start limit.</item>
    /// </list>
    /// </summary>
    public static string BuildUnitFile(string target) =>
        $"""
        [Unit]
        Description=AutoRoute — automated PipeWire routing manager
        Documentation=https://github.com/SimplyJustJessie/AutoRoute
        # After graphical-session so the display, session bus and tray are up; after the audio graph.
        After=graphical-session.target pipewire.service wireplumber.service
        Wants=pipewire.service wireplumber.service
        PartOf=graphical-session.target

        [Service]
        Type=simple
        ExecStart="{target}" --background
        Restart=on-failure
        RestartSec=3
        # No NoNewPrivileges / RestrictNamespaces / RestrictSUIDSGID: they break the setuid fusermount
        # an AppImage relies on to mount itself, so every start would fail and hit the start limit.

        [Install]
        WantedBy=graphical-session.target

        """;

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
