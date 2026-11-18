using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AutoRoute.PipeWire.Models;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Svg.Skia;

namespace AutoRoute.App.Services;

/// <summary>What PipeWire tells us about an app that can lead to its icon.</summary>
public sealed record IconHints(string? IconName, string? AppId, string? FlatpakId, string? AppName, string? Binary)
{
    public static IconHints? For(PwNode n)
    {
        var h = new IconHints(n.Info.IconName, n.Info.ApplicationId, n.Info.FlatpakId, n.ApplicationName,
            n.ProcessBinary);
        return h is { IconName: null, AppId: null, FlatpakId: null, AppName: null, Binary: null } ? null : h;
    }
}

/// <summary>
/// Finds an app's icon the way a desktop does: its .desktop entry names the icon, the user's icon
/// theme (then the themes it inherits, then hicolor, then pixmaps) supplies the file. SVGs — what
/// most themes ship — are rasterised with Svg.Skia. Everything is cached and done off the UI thread;
/// nothing runs until a tile asks, so the tray app never loads it.
/// </summary>
public static class AppIcons
{
    /// <summary>Pixel size icons are rendered at (tiles are at most 42 px; this leaves room for HiDPI).</summary>
    public const int RenderSize = 64;

    private static readonly ConcurrentDictionary<IconHints, Task<Bitmap?>> Cache = new();
    private static readonly Lazy<DesktopIndex> Desktop = new(DesktopIndex.Build);
    private static readonly Lazy<IconTheme> Theme = new(IconTheme.Build);

    public static Task<Bitmap?> GetAsync(IconHints hints) =>
        Cache.GetOrAdd(hints, h => Task.Run(() => Load(h)));

    private static Bitmap? Load(IconHints h)
    {
        try
        {
            foreach (var name in CandidateNames(h))
                if (Theme.Value.Find(name) is { } path && Render(path) is { } bmp)
                    return bmp;
        }
        catch (Exception)
        {
            // A broken theme or SVG just means no icon: the letter tile stays.
        }
        return null;
    }

    /// <summary>Icon names to try, most specific first.</summary>
    public static IEnumerable<string> CandidateNames(IconHints h) =>
        CandidateNames(h, Desktop.Value).Distinct(StringComparer.Ordinal);

    internal static IEnumerable<string> CandidateNames(IconHints h, DesktopIndex desktop)
    {
        if (h.IconName is { Length: > 0 } icon) yield return icon;
        if (desktop.IconFor(h) is { } fromDesktop) yield return fromDesktop;
        if (h.AppId is { Length: > 0 } id) yield return id;
        if (h.FlatpakId is { Length: > 0 } fp) yield return fp;
        if (h.AppName is { Length: > 0 } name) yield return name.ToLowerInvariant();
        if (BinaryName(h.Binary) is { } bin) yield return bin;
    }

    /// <summary><c>zen-bin</c> → <c>zen</c>; generic runtimes (electron, python…) say nothing about the app.</summary>
    internal static string? BinaryName(string? binary)
    {
        if (string.IsNullOrEmpty(binary)) return null;
        var b = Path.GetFileName(binary).ToLowerInvariant();
        foreach (var suffix in new[] { "-bin", ".bin", "-wrapped" })
            if (b.EndsWith(suffix, StringComparison.Ordinal)) b = b[..^suffix.Length];
        return b is "electron" or "python" or "python3" or "java" or "wine" or "wine64" or "node" or "bwrap"
            or "flatpak" || b.StartsWith("electron", StringComparison.Ordinal) ? null : b;
    }

    private static Bitmap? Render(string path)
    {
        if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            using var svg = new SKSvg();
            if (svg.Load(path) is not { } picture) return null;
            var rect = picture.CullRect;
            if (rect.Width <= 0 || rect.Height <= 0) return null;
            var scale = RenderSize / Math.Max(rect.Width, rect.Height);
            using var surface = SKSurface.Create(new SKImageInfo(RenderSize, RenderSize, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Translate((RenderSize - rect.Width * scale) / 2, (RenderSize - rect.Height * scale) / 2);
            canvas.Scale(scale);
            canvas.Translate(-rect.Left, -rect.Top);
            canvas.DrawPicture(picture);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = data.AsStream();
            return new Bitmap(stream);
        }
        using var file = File.OpenRead(path);
        return Bitmap.DecodeToWidth(file, RenderSize, BitmapInterpolationMode.HighQuality);
    }
}

/// <summary>.desktop entries from the XDG data dirs (user first), indexed for icon lookups.</summary>
internal sealed class DesktopIndex
{
    private readonly Dictionary<string, string> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _byWmClass = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _byExec = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _byName = new(StringComparer.OrdinalIgnoreCase);

    public static DesktopIndex Build()
    {
        var index = new DesktopIndex();
        foreach (var dir in XdgDirs.Data().Select(d => Path.Combine(d, "applications")).Distinct())
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.desktop", SearchOption.AllDirectories))
                index.Add(Path.GetFileNameWithoutExtension(file), ReadEntry(file));
        }
        return index;
    }

    /// <summary>For tests: build from in-memory entries (id → key/value pairs).</summary>
    internal static DesktopIndex From(IEnumerable<(string Id, Dictionary<string, string> Entry)> entries)
    {
        var index = new DesktopIndex();
        foreach (var (id, entry) in entries) index.Add(id, entry);
        return index;
    }

    // First one wins: user entries come before system ones.
    private void Add(string id, Dictionary<string, string> e)
    {
        if (!e.TryGetValue("Icon", out var icon) || icon.Length == 0) return;
        _byId.TryAdd(id, icon);
        if (e.TryGetValue("StartupWMClass", out var wm) && wm.Length > 0) _byWmClass.TryAdd(wm, icon);
        if (e.TryGetValue("Exec", out var exec) && ExecBinary(exec) is { } bin) _byExec.TryAdd(bin, icon);
        if (e.TryGetValue("Name", out var name) && name.Length > 0) _byName.TryAdd(name, icon);
    }

    public string? IconFor(IconHints h)
    {
        var appName = h.AppName?.ToLowerInvariant();
        var bin = AppIcons.BinaryName(h.Binary);
        foreach (var id in new[] { h.AppId, h.FlatpakId, appName, bin })
            if (id is { Length: > 0 } && _byId.TryGetValue(id, out var icon)) return icon;
        foreach (var wm in new[] { appName, bin })
            if (wm is { Length: > 0 } && _byWmClass.TryGetValue(wm, out var icon)) return icon;
        if (h.Binary is { Length: > 0 } b && _byExec.TryGetValue(Path.GetFileName(b), out var byExec)) return byExec;
        if (h.AppName is { Length: > 0 } n)
        {
            if (_byName.TryGetValue(n, out var exact)) return exact;
            // "Zen" → "Zen Browser": a display name that starts with the app's name.
            var prefix = _byName.FirstOrDefault(kv => kv.Key.StartsWith(n + " ", StringComparison.OrdinalIgnoreCase));
            if (prefix.Value is { } p) return p;
        }
        return null;
    }

    /// <summary>The program an Exec line runs: <c>/opt/zen/zen-bin %u</c> → <c>zen-bin</c>; flatpak's <c>--command=</c>.</summary>
    internal static string? ExecBinary(string exec)
    {
        var parts = exec.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var cmd = parts.FirstOrDefault(p => p.StartsWith("--command=", StringComparison.Ordinal));
        if (cmd is not null) return cmd["--command=".Length..];
        var first = parts[0] == "env" ? parts.Skip(1).FirstOrDefault(p => !p.Contains('=')) : parts[0];
        return first is null ? null : Path.GetFileName(first.Trim('"'));
    }

    private static Dictionary<string, string> ReadEntry(string file)
    {
        var e = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var inEntry = false;
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    if (inEntry) break; // only [Desktop Entry]
                    inEntry = line == "[Desktop Entry]";
                    continue;
                }
                if (!inEntry) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                if (key is "Icon" or "Name" or "Exec" or "StartupWMClass")
                    e.TryAdd(key, line[(eq + 1)..].Trim());
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return e;
    }
}

/// <summary>The user's icon theme and what it inherits, ending at hicolor and pixmaps.</summary>
internal sealed class IconTheme
{
    private readonly List<(string Dir, Lazy<Dictionary<string, List<string>>> Files)> _themes = new();

    public static IconTheme Build()
    {
        var bases = new List<string>
        {
            Path.Combine(XdgDirs.Home, ".local", "share", "icons"),
            Path.Combine(XdgDirs.Home, ".icons"),
        };
        bases.AddRange(XdgDirs.Data().Select(d => Path.Combine(d, "icons")));
        bases = bases.Distinct().ToList();

        var theme = new IconTheme();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void AddTheme(string name)
        {
            if (!seen.Add(name)) return;
            var inherits = new List<string>();
            foreach (var b in bases)
            {
                var dir = Path.Combine(b, name);
                if (!Directory.Exists(dir)) continue;
                theme._themes.Add((dir, new Lazy<Dictionary<string, List<string>>>(() => IndexFiles(dir))));
                var index = Path.Combine(dir, "index.theme");
                if (File.Exists(index))
                    foreach (var line in File.ReadLines(index))
                        if (line.StartsWith("Inherits=", StringComparison.Ordinal))
                            inherits.AddRange(line["Inherits=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            foreach (var parent in inherits) AddTheme(parent);
        }
        AddTheme(CurrentThemeName() ?? "hicolor");
        AddTheme("hicolor");
        if (Directory.Exists("/usr/share/pixmaps"))
            theme._themes.Add(("/usr/share/pixmaps", new Lazy<Dictionary<string, List<string>>>(() => IndexFiles("/usr/share/pixmaps"))));
        return theme;
    }

    /// <summary>The best file for an icon name: an absolute path as-is, else the first theme that has it.</summary>
    public string? Find(string name)
    {
        if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
        foreach (var (_, files) in _themes)
            if (files.Value.TryGetValue(name, out var paths) && Best(paths) is { } best)
                return best;
        return null;
    }

    // Scalable wins; else the PNG nearest 64 px that isn't tiny.
    private static string? Best(List<string> paths)
    {
        var svg = paths.Where(p => p.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(SizeOf).FirstOrDefault();
        if (svg is not null) return svg;
        return paths.Where(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => SizeOf(p) >= 48 ? Math.Abs(SizeOf(p) - 64) : 1000 - SizeOf(p))
            .FirstOrDefault();
    }

    // "…/48x48/apps/x.png", "…/apps/48/x.svg" → 48; unknown → 0.
    private static int SizeOf(string path)
    {
        foreach (var part in path.Split('/'))
        {
            var p = part.Split('x')[0].Split('@')[0];
            if (int.TryParse(p, out var n) && n is > 0 and <= 1024) return n;
        }
        return 0;
    }

    private static Dictionary<string, List<string>> IndexFiles(string dir)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.*", new EnumerationOptions
                     { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                if (!f.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    continue;
                var key = Path.GetFileNameWithoutExtension(f);
                if (!map.TryGetValue(key, out var list)) map[key] = list = new List<string>(2);
                list.Add(f);
            }
        }
        catch (IOException) { }
        return map;
    }

    /// <summary>KDE's [Icons] Theme, else GTK's gtk-icon-theme-name.</summary>
    private static string? CurrentThemeName()
    {
        var config = XdgDirs.Config;
        return ReadKey(Path.Combine(config, "kdeglobals"), "[Icons]", "Theme")
               ?? ReadKey(Path.Combine(config, "gtk-3.0", "settings.ini"), "[Settings]", "gtk-icon-theme-name")
               ?? ReadKey(Path.Combine(config, "gtk-4.0", "settings.ini"), "[Settings]", "gtk-icon-theme-name");
    }

    private static string? ReadKey(string file, string section, string key)
    {
        if (!File.Exists(file)) return null;
        var inSection = false;
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inSection = line == section; continue; }
            if (inSection && line.StartsWith(key + "=", StringComparison.Ordinal))
                return line[(key.Length + 1)..].Trim() is { Length: > 0 } v ? v : null;
        }
        return null;
    }
}

internal static class XdgDirs
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string Config =>
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(Home, ".config");

    /// <summary>XDG_DATA_HOME then XDG_DATA_DIRS (with the spec's defaults), plus Flatpak's exports.</summary>
    public static IEnumerable<string> Data()
    {
        yield return Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } h ? h : Path.Combine(Home, ".local", "share");
        yield return Path.Combine(Home, ".local", "share", "flatpak", "exports", "share");
        var dirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } d ? d : "/usr/local/share:/usr/share";
        foreach (var dir in dirs.Split(':', StringSplitOptions.RemoveEmptyEntries)) yield return dir;
        yield return "/var/lib/flatpak/exports/share";
    }
}
