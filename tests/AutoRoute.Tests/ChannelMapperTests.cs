using System.Linq;
using AutoRoute.PipeWire;
using AutoRoute.PipeWire.Models;

namespace AutoRoute.Tests;

public class ChannelMapperTests
{
    private static PwPort Out(int id, string? ch) => new(id, 1, PortDirection.Output, $"out_{ch}", ch, 0);
    private static PwPort In(int id, string? ch) => new(id, 2, PortDirection.Input, $"in_{ch}", ch, 0);

    [Fact]
    public void Stereo_pairs_FL_to_FL_and_FR_to_FR()
    {
        var src = new[] { Out(1, "FL"), Out(2, "FR") };
        var tgt = new[] { In(3, "FL"), In(4, "FR") };

        var r = ChannelMapper.Map(src, tgt);

        Assert.Equal(2, r.Pairs.Count);
        Assert.Contains(r.Pairs, p => p.OutPortId == 1 && p.InPortId == 3 && p.Channel == "FL");
        Assert.Contains(r.Pairs, p => p.OutPortId == 2 && p.InPortId == 4 && p.Channel == "FR");
        Assert.False(r.HasWarnings);
    }

    [Fact]
    public void Stereo_pairing_is_channel_correct_even_if_target_ports_reordered()
    {
        var src = new[] { Out(1, "FL"), Out(2, "FR") };
        var tgt = new[] { In(4, "FR"), In(3, "FL") }; // reversed order

        var r = ChannelMapper.Map(src, tgt);

        Assert.Equal(3, r.Pairs.Single(p => p.Channel == "FL").InPortId);
        Assert.Equal(4, r.Pairs.Single(p => p.Channel == "FR").InPortId);
    }

    [Fact]
    public void Mono_source_fans_out_to_FL_and_FR()
    {
        var src = new[] { Out(1, "MONO") };
        var tgt = new[] { In(3, "FL"), In(4, "FR") };

        var r = ChannelMapper.Map(src, tgt);

        Assert.Equal(2, r.Pairs.Count);
        Assert.All(r.Pairs, p => Assert.Equal(1, p.OutPortId));
        Assert.Contains(r.Pairs, p => p.InPortId == 3);
        Assert.Contains(r.Pairs, p => p.InPortId == 4);
        Assert.False(r.HasWarnings);
    }

    [Fact]
    public void Null_channel_single_output_is_treated_as_mono_fan_out()
    {
        var src = new[] { Out(1, null) };
        var tgt = new[] { In(3, "FL"), In(4, "FR") };

        var r = ChannelMapper.Map(src, tgt);

        Assert.Equal(2, r.Pairs.Count);
    }

    [Fact]
    public void Mono_to_mono_target_makes_one_link()
    {
        var src = new[] { Out(1, "MONO") };
        var tgt = new[] { In(3, "MONO") };

        var r = ChannelMapper.Map(src, tgt);

        Assert.Single(r.Pairs);
        Assert.Equal(1, r.Pairs[0].OutPortId);
        Assert.Equal(3, r.Pairs[0].InPortId);
    }

    [Fact]
    public void Surround_source_into_stereo_target_leaves_extra_channels_unmatched()
    {
        var src = new[] { Out(1, "FL"), Out(2, "FR"), Out(3, "FC"), Out(4, "LFE"), Out(5, "RL"), Out(6, "RR") };
        var tgt = new[] { In(10, "FL"), In(11, "FR") };

        var r = ChannelMapper.Map(src, tgt);

        Assert.Equal(2, r.Pairs.Count);
        Assert.True(r.HasWarnings);
        Assert.Equal(new[] { "FC", "LFE", "RL", "RR" }, r.UnmatchedSourceChannels.OrderBy(c => c).ToArray());
    }

    [Fact]
    public void No_ports_yields_empty_pairing()
    {
        var r = ChannelMapper.Map(System.Array.Empty<PwPort>(), new[] { In(3, "FL") });
        Assert.Empty(r.Pairs);
    }
}
