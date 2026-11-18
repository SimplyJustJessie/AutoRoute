using System.Collections.Generic;
using System.Linq;
using AutoRoute.App.Services;

namespace AutoRoute.Tests;

/// <summary>Finding an app's icon name from what PipeWire reports, against entries seen on a dev box.</summary>
public sealed class AppIconsTests
{
    private static DesktopIndex Index() => DesktopIndex.From(new (string, Dictionary<string, string>)[]
    {
        ("legcord", new() { ["Name"] = "Legcord", ["Exec"] = "legcord", ["Icon"] = "legcord" }),
        ("app.legcord.Legcord", new() { ["Name"] = "Legcord", ["Exec"] = "/usr/lib/electron43/electron" }), // no Icon
        ("zen", new() { ["Name"] = "Zen Browser", ["Exec"] = "/opt/zen-browser-bin/zen-bin %u", ["Icon"] = "zen-browser", ["StartupWMClass"] = "zen" }),
        ("mpv", new() { ["Name"] = "mpv Media Player", ["Exec"] = "mpv --player-operation-mode=pseudo-gui -- %U", ["Icon"] = "mpv" }),
        ("com.github.wwmm.easyeffects", new() { ["Name"] = "Easy Effects", ["Icon"] = "com.github.wwmm.easyeffects",
            ["Exec"] = "/usr/bin/flatpak run --branch=stable --command=easyeffects com.github.wwmm.easyeffects" }),
    });

    private static string? First(IconHints h) => AppIcons.CandidateNames(h, Index()).FirstOrDefault();

    [Fact]
    public void Electron_app_is_found_by_its_name_not_its_runtime()
    {
        Assert.Equal("legcord", First(new IconHints(null, null, null, "legcord", "electron")));
        Assert.Null(AppIcons.BinaryName("electron"));
        Assert.Null(AppIcons.BinaryName("/usr/lib/electron43/electron43"));
    }

    [Fact]
    public void Browser_binary_is_matched_through_its_desktop_entry()
    {
        // application.name "Zen", binary "zen-bin": the desktop id "zen" names the themed icon.
        Assert.Equal("zen-browser", First(new IconHints(null, null, null, "Zen", "zen-bin")));
        Assert.Equal("zen", AppIcons.BinaryName("zen-bin"));
    }

    [Fact]
    public void Application_id_and_explicit_icon_name_come_first()
    {
        Assert.Equal("com.github.wwmm.easyeffects", First(new IconHints(null, "com.github.wwmm.easyeffects", null, null, null)));
        Assert.Equal("mpv", First(new IconHints(null, "mpv", null, "mpv", null)));
        Assert.Equal("my-icon", First(new IconHints("my-icon", "mpv", null, "mpv", null)));
    }

    [Fact]
    public void Exec_lines_reduce_to_the_program()
    {
        Assert.Equal("zen-bin", DesktopIndex.ExecBinary("/opt/zen-browser-bin/zen-bin %u"));
        Assert.Equal("easyeffects", DesktopIndex.ExecBinary("/usr/bin/flatpak run --branch=stable --command=easyeffects x"));
        Assert.Equal("app", DesktopIndex.ExecBinary("env FOO=1 app --flag"));
    }
}
