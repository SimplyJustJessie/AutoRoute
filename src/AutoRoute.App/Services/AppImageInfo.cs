using System;

namespace AutoRoute.App.Services;

/// <summary>
/// Where AutoRoute is running from, as far as it can tell. The AppImage runtime exports
/// <c>$APPIMAGE</c> as the absolute path of the <c>.AppImage</c> file itself (its in-mount
/// <see cref="Environment.ProcessPath"/> lives under an ephemeral <c>/tmp/.mount_*</c>), so a set
/// <c>$APPIMAGE</c> is the one reliable signal that we are an AppImage — the thing both autostart
/// (which path to launch) and the updater (which file to replace) key off.
/// </summary>
public static class AppImageInfo
{
    /// <summary>The <c>.AppImage</c> file path when running from one, otherwise <c>null</c>.</summary>
    public static string? Path
    {
        get
        {
            var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
            return string.IsNullOrEmpty(appImage) ? null : appImage;
        }
    }

    /// <summary>True when running from an AppImage (self-update / self-relaunch apply here).</summary>
    public static bool IsAppImage => Path is not null;
}
