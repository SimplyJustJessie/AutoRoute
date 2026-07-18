using System;

namespace AutoRoute.App.Hosting;

/// <summary>
/// Command-line options for the always-on host. Parsed once in <c>Program.Main</c> and registered
/// as a singleton so the host, worker and tray see the same choices.
/// </summary>
public sealed record AppOptions
{
    /// <summary>Start hidden (tray only) — the systemd service launch mode (<c>--background</c>).</summary>
    public bool Background { get; init; }

    /// <summary>Use <see cref="PipeWire.PollingGraphMonitor"/> instead of <c>pw-mon</c> (<c>--poll</c>).</summary>
    public bool Poll { get; init; }

    public static AppOptions Parse(string[] args)
    {
        var background = false;
        var poll = false;
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--background":
                    background = true;
                    break;
                case "--poll":
                    poll = true;
                    break;
            }
        }
        return new AppOptions { Background = background, Poll = poll };
    }
}
