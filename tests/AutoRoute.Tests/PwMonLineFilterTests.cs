using System.IO;
using System.Linq;
using AutoRoute.PipeWire;
using AutoRoute.Tests.TestSupport;

namespace AutoRoute.Tests;

/// <summary>
/// The pw-mon line filter is the first CPU gate: only Node/Port/Link records may cost a pw-dump.
/// Client/Device/Metadata churn — which a busy desktop emits constantly — must be ignored, and
/// anything the filter doesn't understand must fail open (extra reloads are cheap; missed changes
/// are not).
/// </summary>
public class PwMonLineFilterTests
{
    [Fact]
    public void Node_port_and_link_records_trigger()
    {
        var filter = new PwMonLineFilter();
        foreach (var iface in new[] { "Node", "Port", "Link" })
        {
            Assert.False(filter.ShouldTrigger("added:"));
            Assert.False(filter.ShouldTrigger("\tid: 42"));
            Assert.False(filter.ShouldTrigger("\tpermissions: rwxm-"));
            Assert.True(filter.ShouldTrigger($"\ttype: PipeWire:Interface:{iface} (version 3)"));
        }
    }

    [Fact]
    public void Client_metadata_and_profiler_records_do_not_trigger()
    {
        var filter = new PwMonLineFilter();
        foreach (var iface in new[] { "Client", "Metadata", "Profiler", "Device", "Factory" })
        {
            Assert.False(filter.ShouldTrigger("changed:"));
            Assert.False(filter.ShouldTrigger("\tid: 136"));
            Assert.False(filter.ShouldTrigger($"\ttype: PipeWire:Interface:{iface} (version 3)"));
            Assert.False(filter.ShouldTrigger("*\tproperties:"));
            Assert.False(filter.ShouldTrigger("*\t\tpipewire.protocol = \"protocol-native\""));
        }
    }

    [Fact]
    public void Removed_records_always_trigger()
    {
        // Removal records don't reliably carry a type line — never risk missing one.
        var filter = new PwMonLineFilter();
        Assert.True(filter.ShouldTrigger("removed:"));
        Assert.True(filter.ShouldTrigger("removed:"));
    }

    [Fact]
    public void A_record_without_a_type_line_fails_open_on_the_next_header()
    {
        var filter = new PwMonLineFilter();
        Assert.False(filter.ShouldTrigger("added:"));
        Assert.False(filter.ShouldTrigger("\tid: 42"));
        // Format drift: the next record starts before we ever saw a type line.
        Assert.True(filter.ShouldTrigger("added:"));
    }

    [Fact]
    public void The_pw_mon_preamble_does_not_trigger()
    {
        // pw-mon opens with an un-headed Core info block; its type line must be ignored.
        var filter = new PwMonLineFilter();
        Assert.False(filter.ShouldTrigger("\ttype: PipeWire:Interface:Core"));
        Assert.False(filter.ShouldTrigger("\tcookie: 2083166090"));
    }

    [Fact]
    public void Real_capture_triggers_on_some_but_not_all_records()
    {
        var filter = new PwMonLineFilter();
        var lines = File.ReadAllLines(Fixtures.PwMonSamplePath);
        var headers = lines.Count(l =>
            l.StartsWith("added:") || l.StartsWith("changed:") || l.StartsWith("removed:"));
        var triggers = lines.Count(filter.ShouldTrigger);

        Assert.True(triggers > 0, "a real capture contains Node/Port/Link records");
        Assert.True(triggers < headers,
            $"filtering must drop irrelevant records (headers={headers}, triggers={triggers})");
    }
}
