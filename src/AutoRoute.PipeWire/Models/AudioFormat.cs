using System.Globalization;

namespace AutoRoute.PipeWire.Models;

/// <summary>
/// The audio format a node is running at — its sample rate in Hz and PipeWire sample format
/// (e.g. <c>S16LE</c>, <c>F32LE</c>), from which the bit depth is derived. Parsed from a node's
/// <c>info.params</c>: the negotiated <c>Format</c> when a stream is active, otherwise the default
/// of the advertised <c>EnumFormat</c> for an idle device (see <c>PwDumpReader</c>).
/// Presentation-only — never part of a node's stable match identity.
/// </summary>
/// <param name="SampleRateHz">Sample rate in Hz (e.g. <c>48000</c>). Null when unknown.</param>
/// <param name="SampleFormat">Raw PipeWire format token (e.g. <c>S24LE</c>, <c>F32P</c>). Null when unknown.</param>
public sealed record AudioFormat(int? SampleRateHz, string? SampleFormat)
{
    /// <summary>
    /// Bit depth implied by <see cref="SampleFormat"/> — the first run of digits after the type
    /// letter (<c>S16LE</c> → 16, <c>F32LE</c> → 32, <c>S24_32LE</c> → 24). Null when unknown.
    /// </summary>
    public int? BitDepth
    {
        get
        {
            if (string.IsNullOrEmpty(SampleFormat)) return null;
            var start = -1;
            for (var i = 0; i < SampleFormat.Length; i++)
            {
                if (char.IsDigit(SampleFormat[i])) { start = i; break; }
            }
            if (start < 0) return null;
            var end = start;
            while (end < SampleFormat.Length && char.IsDigit(SampleFormat[end])) end++;
            return int.TryParse(SampleFormat.AsSpan(start, end - start), out var bits) ? bits : null;
        }
    }

    /// <summary>True for floating-point sample formats (<c>F32LE</c>, <c>F32P</c>, <c>F64LE</c>).</summary>
    public bool IsFloat =>
        SampleFormat is { Length: > 0 } f && (f[0] == 'F' || f[0] == 'f');

    /// <summary>Human sample-rate label in kHz, e.g. <c>48 kHz</c> / <c>44.1 kHz</c>. Null when unknown.</summary>
    public string? RateLabel =>
        SampleRateHz is > 0
            ? (SampleRateHz.Value / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " kHz"
            : null;

    /// <summary>Human bit-depth label, e.g. <c>24-bit</c> / <c>32-bit float</c>. Null when unknown.</summary>
    public string? DepthLabel =>
        BitDepth is { } d ? (IsFloat ? $"{d}-bit float" : $"{d}-bit") : null;

    /// <summary>
    /// One-line summary joining rate and depth (<c>48 kHz · 24-bit</c>). Shows whichever half is
    /// known; empty when neither is.
    /// </summary>
    public string Summary => (RateLabel, DepthLabel) switch
    {
        ({ } r, { } d) => $"{r} · {d}",
        ({ } r, null) => r,
        (null, { } d) => d,
        _ => string.Empty,
    };
}
