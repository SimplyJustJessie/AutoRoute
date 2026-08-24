using System;
using System.Threading.Tasks;
using AutoRoute.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace AutoRoute.App.ViewModels;

/// <summary>
/// Drives the toolbar "Start at login" toggle. Reflects the real autostart state (queried from
/// <see cref="AutostartService"/>) and, when the user flips the switch, installs or removes it —
/// then re-reads the truth so the switch can never lie about what's on disk.
/// </summary>
public partial class AutostartViewModel : ViewModelBase
{
    private readonly AutostartService _service;
    private readonly ILogger<AutostartViewModel>? _log;

    // Guards the toggle: set while we assign IsEnabled from a query so the change handler doesn't
    // treat our own refresh as a user request and loop.
    private bool _suppress;

    public AutostartViewModel(AutostartService service, ILogger<AutostartViewModel>? log = null)
    {
        _service = service;
        _log = log;
    }

    /// <summary>Whether AutoRoute starts at login. Flipping it applies the change.</summary>
    [ObservableProperty]
    private bool _isEnabled;

    /// <summary>True while an enable/disable is in flight — disables the toggle in the UI.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Last user-facing result or hint, shown under the toggle.</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>The path autostart will launch — shown so the user can see exactly what gets wired.</summary>
    public string LaunchTarget => _service.LaunchTarget ?? "(unknown)";

    /// <summary>
    /// Read the current state and set the toggle without triggering an apply. Repairs a stale entry
    /// first (see <see cref="AutostartService.RepairStaleEntry"/>) so an AppImage upgraded by
    /// filename doesn't leave autostart silently pointing at a launcher that no longer exists — this
    /// runs on every start, window or <c>--background</c>, since the board initializes either way.
    /// </summary>
    public async Task RefreshAsync()
    {
        try
        {
            var repair = _service.RepairStaleEntry();
            if (repair.Repaired)
            {
                StatusMessage =
                    "Autostart pointed at a version that's no longer installed — repaired to launch this one.";
                _log?.LogInformation("autostart: repaired stale entry {Old} -> {New}",
                    repair.OldTarget, repair.NewTarget);
            }

            var state = await _service.GetStateAsync().ConfigureAwait(true);
            SetEnabledQuietly(state.Enabled);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "autostart: state query failed");
        }
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (_suppress) return;
        _ = ApplyAsync(value);
    }

    private async Task ApplyAsync(bool enable)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var outcome = enable
                ? await _service.EnableAsync().ConfigureAwait(true)
                : await _service.DisableAsync().ConfigureAwait(true);
            StatusMessage = outcome.Message;

            // Snap the toggle back to what actually happened (e.g. an enable that wholly failed).
            if (!outcome.Success)
                await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "autostart: apply failed");
            StatusMessage = $"Couldn't change autostart: {ex.Message}";
            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetEnabledQuietly(bool value)
    {
        _suppress = true;
        try { IsEnabled = value; }
        finally { _suppress = false; }
    }
}
