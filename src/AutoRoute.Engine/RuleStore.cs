using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.Engine.Model;
using AutoRoute.PipeWire.Process;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoRoute.Engine;

/// <summary>
/// Owns <c>~/.config/autoroute/rules.json</c> (ADR-0009 auto-persist substrate): the store simply
/// persists whatever <see cref="RulesDocument"/> it is handed, atomically, on every
/// <see cref="SaveAsync"/>. It also hot-reloads the file when it changes on disk (external edit),
/// raising <see cref="Changed"/>.
///
/// <para>Resilience: a missing file loads as <see cref="RulesDocument.Empty"/>; a malformed file is
/// logged and the last-good in-memory document is kept (never throws into the reconcile loop).</para>
///
/// <para>Atomicity: <see cref="SaveAsync"/> writes a sibling temp file then <see cref="File.Move(string,string,bool)"/>s
/// it over the target — a reader never sees a torn write. The <see cref="FileSystemWatcher"/> ignores
/// our own writes by comparing the on-disk text to the last text we persisted, so an atomic save
/// never feedback-loops into a spurious reload.</para>
/// </summary>
public sealed class RuleStore : IRuleStore, IDisposable
{
    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromMilliseconds(150);

    private readonly string _filePath;
    private readonly string _directory;
    private readonly string _fileName;
    private readonly ILogger _log;

    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private Debouncer? _debouncer;
    private bool _disposed;

    /// <summary>The exact JSON text of our most recent write — lets the watcher ignore self-writes.</summary>
    private string? _lastPersistedJson;

    public RuleStore(ILogger<RuleStore>? log = null)
        : this(DefaultPath(), log)
    {
    }

    /// <summary>Testing/host override: point the store at an explicit rules.json path.</summary>
    public RuleStore(string filePath, ILogger<RuleStore>? log = null)
    {
        _filePath = Path.GetFullPath(filePath);
        _directory = Path.GetDirectoryName(_filePath)
                     ?? throw new ArgumentException("rules.json path has no directory", nameof(filePath));
        _fileName = Path.GetFileName(_filePath);
        _log = log ?? NullLogger<RuleStore>.Instance;
    }

    /// <summary><c>$XDG_CONFIG_HOME/autoroute/rules.json</c> (falling back to <c>~/.config</c>).</summary>
    public static string DefaultPath()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            configHome = Path.Combine(home, ".config");
        }
        return Path.Combine(configHome, "autoroute", "rules.json");
    }

    public RulesDocument Current { get; private set; } = RulesDocument.Empty;

    public event EventHandler<RulesDocument>? Changed;

    public Task<RulesDocument> LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnsureDirectory();
        StartWatching();

        RulesDocument doc;
        lock (_gate)
        {
            doc = ReadFromDiskLocked() ?? RulesDocument.Empty;
            Current = doc;
        }
        return Task.FromResult(doc);
    }

    public async Task SaveAsync(RulesDocument document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ct.ThrowIfCancellationRequested();
        EnsureDirectory();

        var json = JsonSerializer.Serialize(document, RulesJsonContext.Default.RulesDocument);
        var tempPath = _filePath + ".tmp-" + Guid.NewGuid().ToString("N");

        // Record the payload BEFORE the file appears so a watcher event that races the move still
        // recognizes it as our own write.
        lock (_gate)
        {
            _lastPersistedJson = json;
        }

        try
        {
            await File.WriteAllTextAsync(tempPath, json, ct).ConfigureAwait(false);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        lock (_gate)
        {
            Current = document;
        }
        Changed?.Invoke(this, document);
    }

    private void EnsureDirectory() => Directory.CreateDirectory(_directory);

    private void StartWatching()
    {
        lock (_gate)
        {
            if (_watcher is not null || _disposed) return;

            _debouncer = new Debouncer(ReloadDebounce, OnDebouncedReload);
            _watcher = new FileSystemWatcher(_directory, _fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            _watcher.Changed += OnWatcherEvent;
            _watcher.Created += OnWatcherEvent;
            _watcher.Renamed += OnWatcherEvent;
            _watcher.EnableRaisingEvents = true;
        }
    }

    private void OnWatcherEvent(object sender, FileSystemEventArgs e) => _debouncer?.Trigger();

    private void OnDebouncedReload()
    {
        RulesDocument? updated = null;
        try
        {
            lock (_gate)
            {
                if (_disposed) return;

                // Read raw text first so we can distinguish our own atomic write from an external edit.
                if (!File.Exists(_filePath)) return;
                var text = File.ReadAllText(_filePath);
                if (text == _lastPersistedJson) return; // self-write — no reload, no feedback loop

                var doc = ParseOrNull(text);
                if (doc is null) return; // malformed external edit — keep last-good (already logged)

                _lastPersistedJson = text;
                Current = doc;
                updated = doc;
            }
        }
        catch (IOException ex)
        {
            // Torn read while an editor is mid-write; the next event will re-trigger us.
            _log.LogDebug(ex, "transient IO reading rules.json during hot-reload; will retry on next change");
            _debouncer?.Trigger();
            return;
        }

        if (updated is not null) Changed?.Invoke(this, updated);
    }

    /// <summary>Reads and parses the file. Returns null if absent; keeps last-good on malformed JSON.</summary>
    private RulesDocument? ReadFromDiskLocked()
    {
        if (!File.Exists(_filePath)) return null;

        try
        {
            var text = File.ReadAllText(_filePath);
            var doc = ParseOrNull(text);
            if (doc is not null) _lastPersistedJson = text;
            return doc; // null (malformed) → caller keeps Current
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "could not read rules.json; keeping last-good policy");
            return null;
        }
    }

    private RulesDocument? ParseOrNull(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return RulesDocument.Empty;
        try
        {
            return JsonSerializer.Deserialize(text, RulesJsonContext.Default.RulesDocument);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "rules.json is malformed; keeping last-good policy");
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnWatcherEvent;
            _watcher.Created -= OnWatcherEvent;
            _watcher.Renamed -= OnWatcherEvent;
            _watcher.Dispose();
        }
        _debouncer?.Dispose();
    }
}
