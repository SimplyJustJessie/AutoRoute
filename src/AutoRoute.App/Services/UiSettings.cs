using System;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoRoute.App.Services;

/// <summary>
/// Per-user UI preferences, separate from routing policy (rules.json): today just the board
/// density. Stored at <c>$XDG_CONFIG_HOME/autoroute/ui.json</c>; a missing or unreadable file means
/// defaults, and a failed save is ignored — a lost preference must never break routing.
/// Shared by the Audio and Video boards so the toggle applies to both.
/// </summary>
public sealed partial class UiSettings : ObservableObject
{
    private readonly string? _path;
    private bool _loading;

    /// <summary>Compact layout: one-line rows and a sources tray instead of the sidebar.</summary>
    [ObservableProperty]
    private bool _isCompact;

    /// <summary>In-memory only (tests, design data).</summary>
    public UiSettings() { }

    public UiSettings(string path)
    {
        _path = path;
        Load();
    }

    public static string DefaultPath()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(configHome, "autoroute", "ui.json");
    }

    partial void OnIsCompactChanged(bool value) => Save();

    private sealed record Dto(bool Compact);

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(_path));
            if (dto is null) return;
            _loading = true;
            IsCompact = dto.Compact;
        }
        catch (Exception) { /* defaults */ }
        finally { _loading = false; }
    }

    private void Save()
    {
        if (_loading || _path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Dto(IsCompact)));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception) { /* preference only */ }
    }
}
