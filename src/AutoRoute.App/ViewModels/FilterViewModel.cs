using System;
using AutoRoute.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoRoute.App.ViewModels;

/// <summary>
/// The board-wide filter: a text query over Source/Target names plus an optional Source-kind
/// selector and the "show sink monitors" toggle (monitors are off by default, ADR-0010). Raises
/// <see cref="Changed"/> so the board re-applies the filter / rebuilds when monitors toggle.
/// </summary>
public partial class FilterViewModel : ViewModelBase
{
    /// <summary>Raised whenever any filter facet changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised specifically when <see cref="ShowMonitors"/> flips (requires a rebuild, not just re-filter).</summary>
    public event EventHandler? MonitorsToggled;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private bool _showMonitors;

    partial void OnTextChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnShowMonitorsChanged(bool value)
    {
        MonitorsToggled?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Does the given title/subtitle pass the current text query?</summary>
    public bool MatchesText(params string?[] fields)
    {
        var q = Text?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        foreach (var f in fields)
            if (!string.IsNullOrEmpty(f) && f.Contains(q, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
