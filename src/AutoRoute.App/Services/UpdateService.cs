using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.App.Hosting;
using AutoRoute.PipeWire.Process;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoRoute.App.Services;

/// <summary>The result of a "is there a newer release?" check.</summary>
public readonly record struct UpdateCheck(
    bool UpdateAvailable,
    string CurrentVersion,
    string? LatestTag,
    string? DownloadUrl,
    string? ChecksumsUrl,
    string? AssetName,
    long AssetSize,
    string Message);

/// <summary>The result of a download+install attempt, with a user-facing message.</summary>
public readonly record struct UpdateOutcome(bool Success, bool NeedsRestart, string Message);

/// <summary>
/// The in-app updater: it looks at the AutoRoute Gitea repo's latest release, and — when the running
/// app is an AppImage older than it — downloads that release's AppImage, verifies it (SHA256 against
/// the release's <c>SHA256SUMS</c> asset, then a boot smoke-test), and atomically swaps it in over
/// the running <c>$APPIMAGE</c> file. A final <see cref="Relaunch"/> restarts into the new version.
///
/// <para>Only AppImage installs self-update (<see cref="AppImageInfo"/>); a package-manager install
/// updates through its package manager, so <see cref="CanSelfUpdate"/> is false and the UI says so.
/// Every failure path is caught and surfaced as an <see cref="UpdateOutcome"/> / <see cref="UpdateCheck"/>
/// message — nothing here throws into the UI.</para>
/// </summary>
public sealed class UpdateService
{
    // The Gitea REST base for this repo. Public (anonymous) release reads, HTTPS.
    private const string ApiBase = "https://git.bussy.cloud/api/v1/repos/jessie/AutoRoute";
    private const string ChecksumsAssetName = "SHA256SUMS";

    // The release asset that is our AppImage, e.g. AutoRoute-v0.3.0-x86_64.AppImage.
    private static readonly Regex AppImageAsset =
        new(@"^AutoRoute-.*-x86_64\.AppImage$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _http;
    private readonly IProcessRunner _runner;
    private readonly AppVersion _version;
    private readonly AppOptions _options;
    private readonly ILogger _log;
    private readonly Action? _requestShutdown;
    private readonly string? _appImagePath;
    private readonly string _socketPath;

    public UpdateService(
        HttpClient http,
        IProcessRunner runner,
        AppVersion version,
        AppOptions options,
        ILogger<UpdateService>? log = null,
        Action? requestShutdown = null,
        string? appImagePath = null,
        string? socketPath = null)
    {
        _http = http;
        _runner = runner;
        _version = version;
        _options = options;
        _log = log ?? NullLogger<UpdateService>.Instance;
        _requestShutdown = requestShutdown;
        _appImagePath = appImagePath ?? AppImageInfo.Path;
        _socketPath = socketPath ?? SingleInstanceGuard.DefaultSocketPath();
    }

    /// <summary>Whether an in-app update is even possible here (i.e. we are an AppImage).</summary>
    public bool CanSelfUpdate => _appImagePath is not null;

    /// <summary>The running app's version string (e.g. <c>0.3.0</c>, or <c>dev</c>).</summary>
    public string CurrentVersion => _version.Current;

    /// <summary>
    /// Ask Gitea for the latest release and decide whether it's newer than us. Never throws — a
    /// network/parse failure comes back as <c>UpdateAvailable=false</c> with an explanatory message.
    /// </summary>
    public async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        var current = _version.Current;

        if (!CanSelfUpdate)
            return NoUpdate(current, "Installed via a package manager — update through it.");

        try
        {
            var json = await _http.GetStringAsync($"{ApiBase}/releases/latest", ct).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize(json, GiteaJsonContext.Default.GiteaRelease);
            if (release?.TagName is null || release.Assets is null)
                return NoUpdate(current, "The latest release could not be read.");

            GiteaAsset? appImage = null;
            GiteaAsset? checksums = null;
            foreach (var asset in release.Assets)
            {
                if (asset.Name is null) continue;
                if (AppImageAsset.IsMatch(asset.Name)) appImage = asset;
                else if (asset.Name == ChecksumsAssetName) checksums = asset;
            }

            if (appImage?.BrowserDownloadUrl is null)
                return NoUpdate(current, $"Release {release.TagName} has no AppImage asset.");

            if (!_version.IsNewer(release.TagName))
                return NoUpdate(current, $"You're on the latest version ({current}).");

            _log.LogInformation("update available: {Latest} (current {Current})", release.TagName, current);
            return new UpdateCheck(
                UpdateAvailable: true,
                CurrentVersion: current,
                LatestTag: release.TagName,
                DownloadUrl: ForceHttps(appImage.BrowserDownloadUrl),
                ChecksumsUrl: checksums?.BrowserDownloadUrl is { } cu ? ForceHttps(cu) : null,
                AssetName: appImage.Name,
                AssetSize: appImage.Size,
                Message: $"Version {release.TagName} is available.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "update check failed");
            return NoUpdate(current, $"Couldn't check for updates: {ex.Message}");
        }
    }

    /// <summary>
    /// Download the release AppImage into the same directory as the running one, verify it (checksum
    /// when the release publishes one, then a <c>--smoke</c> boot test), and atomically swap it in.
    /// On any failure the partial download is deleted and the running image is left untouched.
    /// </summary>
    public async Task<UpdateOutcome> DownloadAndApplyAsync(
        UpdateCheck check, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (_appImagePath is null)
            return new UpdateOutcome(false, false, "Not running as an AppImage — nothing to update.");
        if (check.DownloadUrl is null || check.AssetName is null)
            return new UpdateOutcome(false, false, "No download is available for this release.");

        // Same directory as $APPIMAGE ⇒ same filesystem ⇒ the final rename is atomic.
        var tmp = _appImagePath + ".new";
        try
        {
            var hash = await DownloadAsync(check.DownloadUrl, tmp, check.AssetSize, progress, ct)
                .ConfigureAwait(false);

            // Checksum gate — verify against the release's SHA256SUMS when it publishes one. Older
            // releases without it fall through to the boot smoke-test as the sole integrity gate.
            if (check.ChecksumsUrl is not null)
            {
                var sums = await _http.GetStringAsync(check.ChecksumsUrl, ct).ConfigureAwait(false);
                var expected = FindChecksum(sums, check.AssetName);
                if (expected is null)
                {
                    _log.LogWarning("update: {Asset} absent from SHA256SUMS — relying on smoke test", check.AssetName);
                }
                else if (!expected.Equals(hash, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(tmp);
                    _log.LogWarning("update: checksum mismatch for {Asset}", check.AssetName);
                    return new UpdateOutcome(false, false,
                        "Download failed verification (checksum mismatch) — update aborted.");
                }
            }
            else
            {
                _log.LogWarning("update: release published no SHA256SUMS — relying on smoke test");
            }

            MakeExecutable(tmp);

            // Boot the freshly downloaded image window-free; a non-zero exit means it won't run here,
            // so don't swap it in. --appimage-extract-and-run works without FUSE (matches CI).
            var smoke = await _runner.RunAsync(
                tmp, new[] { "--appimage-extract-and-run", "--smoke" }, throwOnNonZero: false, ct)
                .ConfigureAwait(false);
            if (!smoke.Succeeded)
            {
                TryDelete(tmp);
                _log.LogWarning("update: downloaded image failed --smoke (exit {Code})", smoke.ExitCode);
                return new UpdateOutcome(false, false,
                    "The downloaded update failed its self-test — keeping the current version.");
            }

            // Atomic swap. Overwriting the file of a running, FUSE-mounted AppImage is safe: the mount
            // holds the old inode until this process exits; the new bytes are what the NEXT launch runs.
            File.Move(tmp, _appImagePath, overwrite: true);

            _log.LogInformation("update installed: now {Tag}", check.LatestTag);
            return new UpdateOutcome(true, true,
                $"Updated to {check.LatestTag}. Restart to finish.");
        }
        catch (OperationCanceledException)
        {
            TryDelete(tmp);
            return new UpdateOutcome(false, false, "Update cancelled.");
        }
        catch (Exception ex)
        {
            TryDelete(tmp);
            _log.LogWarning(ex, "update install failed");
            return new UpdateOutcome(false, false, $"Update failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Restart into the just-installed image. Spawns a detached helper that waits for our
    /// single-instance socket to disappear (so the relaunch doesn't bounce off the guard) and then
    /// execs the AppImage in the same run mode, then requests our own graceful shutdown.
    /// </summary>
    public bool Relaunch()
    {
        if (_appImagePath is null) return false;
        try
        {
            // The child polls for the socket file to vanish (our teardown unlinks it), then relaunches.
            // Paths are passed via env vars so no shell-quoting of the paths is needed.
            var argSuffix = _options.Background ? " --background" : string.Empty;
            var script =
                "while [ -S \"$ARU_SOCK\" ]; do sleep 0.2; done; exec \"$ARU_IMG\"" + argSuffix;

            var psi = new ProcessStartInfo
            {
                FileName = "setsid",
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("sh");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);
            psi.Environment["ARU_SOCK"] = _socketPath;
            psi.Environment["ARU_IMG"] = _appImagePath;

            Process.Start(psi);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "update: could not spawn relaunch helper");
            return false;
        }

        // Tear ourselves down; the detached child is watching the socket and starts the new version.
        (_requestShutdown ?? App.RequestShutdown)();
        return true;
    }

    // ===== helpers ======================================================================

    private async Task<string> DownloadAsync(
        string url, string destPath, long knownSize, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? knownSize;
        await using var netStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fileStream = new FileStream(
            destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var sha = SHA256.Create();

        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await netStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            sha.TransformBlock(buffer, 0, read, null, 0);
            readTotal += read;
            if (total > 0) progress?.Report(Math.Clamp((double)readTotal / total, 0, 1));
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>Find the hex hash for <paramref name="assetName"/> in a <c>sha256sum</c>-format listing.</summary>
    public static string? FindChecksum(string sha256sums, string assetName)
    {
        foreach (var raw in sha256sums.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            // "<hash>␠␠<name>" or "<hash>␠*<name>" (binary marker). Split off the leading hash.
            var sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            var hash = line[..sp];
            var name = line[(sp + 1)..].TrimStart(' ', '*');
            // Compare on the basename so a "./AutoRoute-…" prefix still matches.
            if (Path.GetFileName(name) == assetName)
                return hash;
        }
        return null;
    }

    private static string ForceHttps(string url)
    {
        try
        {
            var b = new UriBuilder(url);
            if (b.Scheme == Uri.UriSchemeHttp)
            {
                b.Scheme = Uri.UriSchemeHttps;
                if (b.Port == 80) b.Port = -1; // drop the default http port so it doesn't leak into the URL
            }
            return b.Uri.ToString();
        }
        catch
        {
            return url;
        }
    }

    private static void MakeExecutable(string path) =>
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }

    private UpdateCheck NoUpdate(string current, string message) =>
        new(false, current, null, null, null, null, 0, message);
}

// ===== Gitea release JSON (source-generated, no reflection) ==============================

internal sealed class GiteaRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("assets")] public GiteaAsset[]? Assets { get; set; }
}

internal sealed class GiteaAsset
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GiteaRelease))]
internal partial class GiteaJsonContext : JsonSerializerContext
{
}
