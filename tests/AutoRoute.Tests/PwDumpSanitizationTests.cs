using AutoRoute.PipeWire;
using Xunit;

namespace AutoRoute.Tests;

/// <summary>
/// App-controlled node properties (application.name, media.name, …) must never carry control
/// characters into the UI or logs — a malicious client could otherwise inject terminal escape
/// sequences via its own stream metadata (they flow verbatim into journald/console output).
/// </summary>
public sealed class PwDumpSanitizationTests
{
    // \u001b (ESC), \u0007 (BEL), \t, \r\n are decoded by the JSON parser into real control chars.
    private const string DumpWithEscapes = """
    [
      {
        "id": 42,
        "type": "PipeWire:Interface:Node",
        "info": {
          "props": {
            "node.name": "evil\u001b[2Jnode",
            "media.class": "Stream/Output/Audio",
            "application.name": "Fake\u0007]0;pwned\u001bApp",
            "media.name": "tab\there\r\nand newline"
          }
        }
      }
    ]
    """;

    [Fact]
    public void Parse_strips_control_characters_from_app_controlled_props()
    {
        var graph = PwDumpReader.Parse(DumpWithEscapes);
        var node = graph.Node(42);

        Assert.NotNull(node);
        Assert.Equal("evil[2Jnode", node!.NodeName);
        Assert.Equal("Fake]0;pwnedApp", node.ApplicationName);
        Assert.Equal("tabhereand newline", node.MediaName);
    }

    [Fact]
    public void Parse_leaves_clean_names_untouched()
    {
        const string dump = """
        [
          {
            "id": 7,
            "type": "PipeWire:Interface:Node",
            "info": { "props": { "node.name": "Spotify — Zen Mode 2.0", "media.class": "Stream/Output/Audio" } }
          }
        ]
        """;

        var graph = PwDumpReader.Parse(dump);
        Assert.Equal("Spotify — Zen Mode 2.0", graph.Node(7)!.NodeName);
    }
}
