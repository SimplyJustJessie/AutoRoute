using System;
using System.Collections.Generic;

namespace AutoRoute.App.Services;

/// <summary>
/// Shared app-level notices, raised by the always-on worker and rendered by whatever UI is open
/// (banner in the board; tray-only mode still gets them in the journal via the worker's logs).
/// Currently one notice: legacy conf files that still create null sinks statically — shown until
/// the user retires those files (ADR-0011: warn only, never edit the user's config).
/// </summary>
public sealed class AppNotices
{
    private readonly object _gate = new();
    private IReadOnlyList<string> _legacySinkFiles = Array.Empty<string>();

    public event EventHandler? Changed;

    public IReadOnlyList<string> LegacySinkFiles
    {
        get { lock (_gate) return _legacySinkFiles; }
    }

    public void SetLegacySinkFiles(IReadOnlyList<string> files)
    {
        lock (_gate) _legacySinkFiles = files;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
