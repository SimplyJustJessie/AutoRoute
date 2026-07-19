using AutoRoute.PipeWire.Models;

namespace AutoRoute.Tests;

/// <summary>
/// Unit tests for <see cref="AudioFormat"/> — the derivation of bit depth, float-ness, and the
/// human labels from a raw PipeWire format token + sample rate.
/// </summary>
public class AudioFormatTests
{
    [Theory]
    [InlineData("S16LE", 16, false)]
    [InlineData("S24LE", 24, false)]
    [InlineData("S32LE", 32, false)]
    [InlineData("S8", 8, false)]
    [InlineData("U8", 8, false)]
    [InlineData("F32LE", 32, true)]
    [InlineData("F32P", 32, true)]
    [InlineData("F64LE", 64, true)]
    [InlineData("S24_32LE", 24, false)] // 24-bit sample in a 32-bit container => the meaningful depth is 24
    public void Derives_bit_depth_and_floatness_from_format_token(string token, int bits, bool isFloat)
    {
        var f = new AudioFormat(48000, token);
        Assert.Equal(bits, f.BitDepth);
        Assert.Equal(isFloat, f.IsFloat);
    }

    [Theory]
    [InlineData(48000, "48 kHz")]
    [InlineData(44100, "44.1 kHz")]
    [InlineData(96000, "96 kHz")]
    [InlineData(192000, "192 kHz")]
    [InlineData(88200, "88.2 kHz")]
    public void Formats_rate_as_kHz(int hz, string expected)
    {
        Assert.Equal(expected, new AudioFormat(hz, "S16LE").RateLabel);
    }

    [Fact]
    public void Depth_label_marks_float_formats()
    {
        Assert.Equal("24-bit", new AudioFormat(48000, "S24LE").DepthLabel);
        Assert.Equal("32-bit float", new AudioFormat(48000, "F32LE").DepthLabel);
    }

    [Fact]
    public void Summary_shows_whichever_half_is_known()
    {
        Assert.Equal("48 kHz · 16-bit", new AudioFormat(48000, "S16LE").Summary);
        Assert.Equal("48 kHz", new AudioFormat(48000, null).Summary);
        Assert.Equal("16-bit", new AudioFormat(null, "S16LE").Summary);
        Assert.Equal("", new AudioFormat(null, null).Summary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("UNKNOWN")]
    public void Unknown_format_token_yields_no_bit_depth(string token)
    {
        var f = new AudioFormat(48000, token);
        Assert.Null(f.BitDepth);
        Assert.Null(f.DepthLabel);
    }
}
