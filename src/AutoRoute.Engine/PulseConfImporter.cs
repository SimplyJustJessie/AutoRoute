using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.Engine.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoRoute.Engine;

/// <summary>A <c>module-null-sink</c> entry recovered from a legacy pipewire-pulse conf file.</summary>
public sealed record ImportedSink(string Name, string? Description, bool Mono);

/// <summary>
/// Outcome of a startup import: the specs actually appended to the declared set, and the legacy
/// files that still declare null sinks — surfaced as a warning until the user retires them
/// (ADR-0011: import + warn only; AutoRoute never edits or removes the user's files).
/// </summary>
public sealed record ImportResult(
    IReadOnlyList<VirtualSinkSpec> Imported,
    IReadOnlyList<string> LegacyFilesStillPresent)
{
    public static ImportResult Empty { get; } = new(Array.Empty<VirtualSinkSpec>(), Array.Empty<string>());
}

/// <summary>
/// One-shot startup import of the user's static virtual-sink declarations (e.g. the historical
/// <c>virtual-sinks.conf</c>): scans every <c>*.conf</c> in the pipewire-pulse conf.d directory —
/// except AutoRoute's own generated drop-in — for <c>load-module module-null-sink</c> entries and
/// appends any sink not already declared to <c>rules.json</c>. The parser is deliberately tolerant:
/// anything unparseable is skipped with a log line; startup is never blocked.
/// </summary>
public sealed class PulseConfImporter
{
    private readonly string _confDDirectory;
    private readonly string _ownFileName;
    private readonly ILogger _log;

    public PulseConfImporter(string confDDirectory, string ownFileName, ILogger<PulseConfImporter>? log = null)
    {
        _confDDirectory = confDDirectory;
        _ownFileName = ownFileName;
        _log = log ?? NullLogger<PulseConfImporter>.Instance;
    }

    /// <summary>The directory AutoRoute's own drop-in lives in: <c>…/pipewire/pipewire-pulse.conf.d</c>.</summary>
    public static string DefaultConfDDirectory() =>
        Path.GetDirectoryName(SinkDropInWriter.DefaultPath())!;

    // { cmd = "load-module" args = "module-null-sink …" } — args value with \" escapes.
    private static readonly Regex LoadModuleEntry = new(
        "cmd\\s*=\\s*\"load-module\"[^}]*?args\\s*=\\s*\"((?:[^\"\\\\]|\\\\.)*)\"",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // Runs on the raw args string, so the unquoted branch must stop at any quote as well as
    // whitespace (e.g. a bare description that ends right before sink_properties' closing quote).
    private static readonly Regex DescriptionProp = new(
        "device\\.description=(?:'([^']*)'|\"([^\"]*)\"|([^\\s\"']+))",
        RegexOptions.Compiled);

    /// <summary>Pure, tolerant scan of one conf file's text for null-sink declarations.</summary>
    public static IReadOnlyList<ImportedSink> Parse(string confText)
    {
        var sinks = new List<ImportedSink>();
        foreach (Match entry in LoadModuleEntry.Matches(confText))
        {
            // Un-escape the SPA-JSON string value (\" and \\).
            var args = entry.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
            var tokens = SplitRespectingQuotes(args);
            if (tokens.Count == 0 || tokens[0] != "module-null-sink") continue;

            string? name = null;
            var mono = false;
            foreach (var token in tokens.Skip(1))
            {
                if (token.StartsWith("sink_name=", StringComparison.Ordinal))
                    name = token["sink_name=".Length..];
                else if (token is "channels=1" or "channel_map=mono")
                    mono = true;
            }

            if (name is null) continue; // no stable identity — nothing to import

            // Description is extracted from the RAW args, not a tokenized value: real configs use
            // both sink_properties="device.description='X Y' …" (our drop-in) and the bare
            // sink_properties=device.description='X Y' (the user's legacy file, per the live gate
            // capture) — tokenization strips the quotes in the bare shape, which would truncate a
            // description with spaces to its first word.
            string? description = null;
            var m = DescriptionProp.Match(args);
            if (m.Success)
                description = m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value;
            sinks.Add(new ImportedSink(name, description, mono));
        }
        return sinks;
    }

    /// <summary>
    /// Scans the conf.d directory and appends every not-yet-declared legacy sink to the store in
    /// one save. Idempotent: a second run finds everything already declared and appends nothing.
    /// </summary>
    public async Task<ImportResult> ImportAsync(IRuleStore store, CancellationToken ct = default)
    {
        if (!Directory.Exists(_confDDirectory)) return ImportResult.Empty;

        var imported = new List<VirtualSinkSpec>();
        var legacyFiles = new List<string>();
        var declaredNames = store.Current.VirtualSinks.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(_confDDirectory, "*.conf").OrderBy(f => f, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(Path.GetFileName(file), _ownFileName, StringComparison.Ordinal)) continue;

            IReadOnlyList<ImportedSink> found;
            try
            {
                found = Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "could not read {File}; skipping it", file);
                continue;
            }

            if (found.Count == 0) continue;
            legacyFiles.Add(file); // still creating sinks statically — warn until the user retires it

            foreach (var sink in found)
            {
                if (!SinkNameValidator.IsValidName(sink.Name))
                {
                    _log.LogWarning("skipping legacy sink with unusable name {Name} in {File}", sink.Name, file);
                    continue;
                }
                if (!declaredNames.Add(sink.Name)) continue; // already declared (or duplicated across files)

                var description = SinkNameValidator.IsValidDescription(sink.Description) ? sink.Description! : sink.Name;
                imported.Add(new VirtualSinkSpec(
                    Guid.NewGuid().ToString("N"), sink.Name, description,
                    sink.Mono ? SinkChannels.Mono : SinkChannels.Stereo));
            }
        }

        if (imported.Count > 0)
        {
            var doc = store.Current;
            await store.SaveAsync(doc with { VirtualSinks = doc.VirtualSinks.Concat(imported).ToList() }, ct)
                .ConfigureAwait(false);
            _log.LogInformation("imported {Count} legacy sink(s): {Names}",
                imported.Count, string.Join(", ", imported.Select(s => s.Name)));
        }

        return new ImportResult(imported, legacyFiles);
    }

    /// <summary>
    /// Splits module args on whitespace, keeping quoted regions (either quote style) intact —
    /// outer quotes are stripped, inner content (including the other quote style) is preserved.
    /// </summary>
    private static List<string> SplitRespectingQuotes(string s)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        char? quote = null;
        foreach (var c in s)
        {
            if (quote is char q)
            {
                if (c == q) quote = null;
                else sb.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }
}
