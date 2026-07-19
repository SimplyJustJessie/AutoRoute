using System;
using System.Net.Http;
using AutoRoute.App.Services;
using AutoRoute.PipeWire.Process;

namespace AutoRoute.App.Hosting;

/// <summary>
/// Window-free update check (run via <c>--check-update</c>): the CLI counterpart to the in-app
/// "Check for updates" button. Asks the Gitea repo for its latest release and prints whether the
/// running build is behind it, plus the resolved (https) download URL. Lets a headless/systemd user
/// script "am I up to date?" and lets a dev confirm the check path without a display.
/// </summary>
public static class UpdateCheckDiagnostic
{
    public static int Run(AppOptions options)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AutoRoute-Updater");

        var service = new UpdateService(http, new ProcessRunner(), new AppVersion(), options);

        Console.WriteLine($"[check-update] current={service.CurrentVersion} canSelfUpdate={service.CanSelfUpdate}");

        var check = service.CheckAsync().GetAwaiter().GetResult();
        Console.WriteLine($"  message      : {check.Message}");
        Console.WriteLine($"  updateAvailable: {check.UpdateAvailable}");
        if (check.LatestTag is not null)
            Console.WriteLine($"  latestTag    : {check.LatestTag}");
        if (check.DownloadUrl is not null)
            Console.WriteLine($"  downloadUrl  : {check.DownloadUrl}");
        if (check.ChecksumsUrl is not null)
            Console.WriteLine($"  checksumsUrl : {check.ChecksumsUrl}");

        // Exit 0 whether or not an update exists — a reachable check succeeded. A non-zero exit is
        // reserved for a check that could not run (e.g. offline), signalled by an empty tag + a
        // "Couldn't check" message.
        var reachable = check.UpdateAvailable || check.Message.StartsWith("You're on", StringComparison.Ordinal)
            || !service.CanSelfUpdate;
        return reachable ? 0 : 1;
    }
}
