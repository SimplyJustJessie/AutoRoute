using System;
using System.Collections.Generic;

namespace AutoRoute.App.Services;

/// <summary>
/// Shared app-level notices, raised by the always-on worker and rendered by whatever UI is open
/// (banner in the board; tray-only mode still gets them in the journal via the worker's logs).
/// Currently the legacy static-conf situation (ADR-0011, revised to detect + offer): which files
/// still create null sinks statically, and which of their sinks are not yet imported — nothing is
/// written to rules.json until the user explicitly imports.
/// </summary>
public sealed class AppNotices
{
    private readonly object _gate = new();
    private IReadOnlyList<string> _legacySinkFiles = Array.Empty<string>();
    private IReadOnlyList<string> _pendingLegacySinks = Array.Empty<string>();

    public event EventHandler? Changed;

    /// <summary>Legacy conf files still creating sinks statically (warn until the user removes them).</summary>
    public IReadOnlyList<string> LegacySinkFiles
    {
        get { lock (_gate) return _legacySinkFiles; }
    }

    /// <summary>Legacy sink names available to import but not yet declared (drives the offer banner).</summary>
    public IReadOnlyList<string> PendingLegacySinks
    {
        get { lock (_gate) return _pendingLegacySinks; }
    }

    public void SetLegacyState(IReadOnlyList<string> files, IReadOnlyList<string> pendingSinks)
    {
        lock (_gate)
        {
            _legacySinkFiles = files;
            _pendingLegacySinks = pendingSinks;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
