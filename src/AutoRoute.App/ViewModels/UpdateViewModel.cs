using System;
using System.Threading.Tasks;
using AutoRoute.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoRoute.App.ViewModels;

/// <summary>
/// Drives the toolbar "Updates" flyout and the "update available" banner. Mirrors
/// <see cref="AutostartViewModel"/>: it reflects the real state from <see cref="UpdateService"/>
/// (current version, whether a newer release exists) and, on the user's click, downloads + installs
/// the update and then restarts into it. It notifies its owner via <c>onStateChanged</c> whenever the
/// "available" / "needs restart" state flips, so the board banner can follow.
/// </summary>
public partial class UpdateViewModel : ViewModelBase
{
    private readonly UpdateService _service;
    private readonly Action? _onStateChanged;

    // The most recent successful check — what Install acts on.
    private UpdateCheck _latest;

    public UpdateViewModel(UpdateService service, Action? onStateChanged = null)
    {
        _service = service;
        _onStateChanged = onStateChanged;
        CurrentVersion = service.CurrentVersion;
        CanSelfUpdate = service.CanSelfUpdate;
        StatusMessage = CanSelfUpdate
            ? string.Empty
            : "Installed via a package manager — update through it.";
    }

    /// <summary>The running app's version, shown in the flyout header.</summary>
    public string CurrentVersion { get; }

    /// <summary>False for package-manager installs — hides the install affordance.</summary>
    public bool CanSelfUpdate { get; }

    /// <summary>True while a check or install is in flight — disables the buttons.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Last user-facing result or hint, shown in the flyout.</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>Download progress 0..1 while installing.</summary>
    [ObservableProperty]
    private double _progress;

    /// <summary>Whether a newer release is available to install.</summary>
    [ObservableProperty]
    private bool _updateAvailable;

    /// <summary>The available release tag (e.g. <c>v0.3.1</c>), when <see cref="UpdateAvailable"/>.</summary>
    [ObservableProperty]
    private string _latestVersion = string.Empty;

    /// <summary>True once an update is installed and the app must be restarted to run it.</summary>
    [ObservableProperty]
    private bool _needsRestart;

    partial void OnUpdateAvailableChanged(bool value) => _onStateChanged?.Invoke();
    partial void OnNeedsRestartChanged(bool value) => _onStateChanged?.Invoke();

    /// <summary>Query Gitea for a newer release and reflect the result. Never throws.</summary>
    [RelayCommand]
    public async Task CheckAsync()
    {
        if (IsBusy || !CanSelfUpdate) return;
        IsBusy = true;
        StatusMessage = "Checking for updates…";
        try
        {
            var check = await _service.CheckAsync().ConfigureAwait(true);
            _latest = check;
            UpdateAvailable = check.UpdateAvailable;
            LatestVersion = check.LatestTag ?? string.Empty;
            StatusMessage = check.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Download + verify + install the latest checked release. Never throws.</summary>
    [RelayCommand]
    public async Task InstallAsync()
    {
        if (IsBusy || !_latest.UpdateAvailable) return;
        IsBusy = true;
        Progress = 0;
        StatusMessage = $"Downloading {_latest.LatestTag}…";
        try
        {
            var progress = new Progress<double>(p => Progress = p);
            var outcome = await _service.DownloadAndApplyAsync(_latest, progress).ConfigureAwait(true);
            StatusMessage = outcome.Message;
            if (outcome.Success)
            {
                UpdateAvailable = false;
                NeedsRestart = outcome.NeedsRestart;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Restart into the just-installed version.</summary>
    [RelayCommand]
    private async Task Restart() => await Task.Run(() => _service.Relaunch()).ConfigureAwait(true);
}
