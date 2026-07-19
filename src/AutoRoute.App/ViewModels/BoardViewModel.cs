using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AutoRoute.App.Services;
using AutoRoute.Engine;
using AutoRoute.Engine.Model;
using AutoRoute.PipeWire;
using AutoRoute.PipeWire.Models;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AutoRoute.App.ViewModels;

/// <summary>
/// The board root VM and the single <see cref="IBoardCoordinator"/> implementation. Owns the
/// injected Engine/PipeWire seams, subscribes to <c>GraphUpdated</c> (which fires off the UI
/// thread) and marshals to the UI thread before diff-merging into keyed
/// <see cref="ObservableCollection{T}"/>s (columns by Target identity, cards/palette by Source
/// identity) so live graph changes update in place without flicker.
/// </summary>
public partial class BoardViewModel : ViewModelBase, IBoardCoordinator
{
    private readonly IPwGraphService _graph;
    private readonly IPwLinker _linker;
    private readonly IRuleStore _ruleStore;
    private readonly IReconciler _reconciler;
    private readonly IRuleMatcher _matcher;
    private readonly IVirtualSinkController? _sinkController;
    private readonly AppNotices? _notices;
    private readonly ILogger<BoardViewModel>? _log;
    private readonly PulseConfImporter? _importer;

    // Per-session set of external Links the user chose to keep as Manual (not persisted policy).
    private readonly HashSet<string> _keptManual = new();

    public ObservableCollection<SinkColumnViewModel> Columns { get; } = new();
    public SourcesPaletteViewModel Palette { get; }
    public FilterViewModel Filter { get; } = new();

    /// <summary>The "Start at login" toggle VM, or null when no autostart service is wired (mocks/design).</summary>
    public AutostartViewModel? Autostart { get; }

    /// <summary>Gates the autostart toolbar affordance — hidden in mock/design compositions.</summary>
    public bool CanManageAutostart => Autostart is not null;

    /// <summary>The in-app updater VM, or null when no update service is wired (mocks/design).</summary>
    public UpdateViewModel? Update { get; }

    /// <summary>Gates the "Updates" toolbar affordance — hidden in mock/design compositions.</summary>
    public bool CanCheckUpdates => Update is not null;

    /// <summary>"A newer release is available" banner text (empty ⇒ hidden). Follows <see cref="Update"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateNotice))]
    private string _updateNoticeText = string.Empty;

    public bool HasUpdateNotice => UpdateNoticeText.Length > 0;

    [ObservableProperty]
    private bool _hasColumns;

    [ObservableProperty]
    private string _statusText = "Loading graph…";

    /// <summary>Legacy static conf notice (ADR-0011, revised: detect + offer; warn until the file is retired).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLegacyNotice))]
    private string _legacyNoticeText = string.Empty;

    public bool HasLegacyNotice => LegacyNoticeText.Length > 0;

    /// <summary>True while legacy sinks are detected but not imported — shows the banner's Import button.</summary>
    [ObservableProperty]
    private bool _hasPendingLegacyImport;

    public BoardViewModel(
        IPwGraphService graph,
        IPwLinker linker,
        IRuleStore ruleStore,
        IReconciler reconciler,
        IRuleMatcher matcher,
        IVirtualSinkController? sinkController = null,
        AppNotices? notices = null,
        ILogger<BoardViewModel>? log = null,
        PulseConfImporter? importer = null,
        AutostartService? autostart = null,
        UpdateService? update = null)
    {
        _graph = graph;
        _linker = linker;
        _ruleStore = ruleStore;
        _reconciler = reconciler;
        _matcher = matcher;
        _sinkController = sinkController;
        _notices = notices;
        _log = log;
        _importer = importer;
        if (autostart is not null)
            Autostart = new AutostartViewModel(autostart);
        if (update is not null)
            Update = new UpdateViewModel(update, RefreshUpdateNotice);
        Palette = new SourcesPaletteViewModel(this);

        Filter.Changed += (_, _) => ApplyFilter();
        Filter.MonitorsToggled += (_, _) => RebuildFromCurrent();
        if (_notices is not null)
            _notices.Changed += (_, _) => PostToUi(RefreshLegacyNotice);
    }

    /// <summary>Load policy, start the graph service, render the first board, then go live.</summary>
    public async Task InitializeAsync()
    {
        await _ruleStore.LoadAsync().ConfigureAwait(true);
        await _graph.StartAsync().ConfigureAwait(true);

        _graph.GraphUpdated += OnGraphUpdated;
        _ruleStore.Changed += OnRulesChanged;

        RefreshLegacyNotice();
        RebuildFromCurrent();

        if (Autostart is not null)
            await Autostart.RefreshAsync().ConfigureAwait(true);

        // Fire-and-forget: a background update check that never blocks startup. It resumes on the UI
        // thread (ConfigureAwait(true) inside), catches its own failures, and lights the banner via
        // RefreshUpdateNotice if a newer release is out.
        if (Update is not null)
            _ = Update.CheckAsync();
    }

    private void RefreshUpdateNotice()
    {
        UpdateNoticeText = Update is { UpdateAvailable: true }
            ? $"AutoRoute {Update.LatestVersion} is available — you're on {Update.CurrentVersion}."
            : string.Empty;
    }

    /// <summary>Banner action: install the available update (delegates to <see cref="Update"/>).</summary>
    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (Update is not null)
            await Update.InstallAsync().ConfigureAwait(true);
    }

    private void RefreshLegacyNotice()
    {
        var files = _notices?.LegacySinkFiles ?? Array.Empty<string>();
        var pending = _notices?.PendingLegacySinks ?? Array.Empty<string>();

        HasPendingLegacyImport = pending.Count > 0 && _importer is not null;
        LegacyNoticeText = (files.Count, pending.Count) switch
        {
            (0, _) => string.Empty,
            // Detect + offer: nothing was written — the Import button is the only way in.
            (_, > 0) =>
                $"{string.Join(", ", files)} declares {pending.Count} sink(s) not managed by AutoRoute: {string.Join(", ", pending)}.",
            // Everything's imported; the static file is the last thing left to retire.
            _ =>
                $"Sinks are also created statically by {string.Join(", ", files)} — remove the file(s) to let AutoRoute own them.",
        };
    }

    /// <summary>Banner action: import the detected legacy sinks (the ONLY path that writes them).</summary>
    [RelayCommand]
    private async Task ImportLegacySinks()
    {
        if (_importer is null) return;
        try
        {
            var result = await _importer.ImportAsync(_ruleStore).ConfigureAwait(true);
            _notices?.SetLegacyState(result.LegacyFilesStillPresent, Array.Empty<string>());
            _log?.LogInformation("imported {Count} legacy sink(s) on request", result.Imported.Count);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "legacy sink import failed");
        }
        RefreshLegacyNotice();
        RebuildFromCurrent();
    }

    // GraphUpdated fires on a background thread — marshal before touching ObservableCollections.
    private void OnGraphUpdated(object? sender, PwGraph graph) => PostToUi(() => Rebuild(graph));

    private void OnRulesChanged(object? sender, RulesDocument doc) => PostToUi(RebuildFromCurrent);

    /// <summary>Rebuild the board from the latest snapshot (synchronous; safe for headless/VM tests).</summary>
    public void RebuildFromCurrent() => Rebuild(_graph.Current);

    private void Rebuild(PwGraph graph)
    {
        // A rebuild runs inside a Dispatcher callback — an escaped exception there aborts the
        // whole process (seen live: SIGABRT during a PipeWire restart). Whatever goes wrong,
        // keep the previous board and recover on the next snapshot.
        try
        {
            var snapshot = BoardModelBuilder.Build(
                graph, _ruleStore.Current, _matcher, _keptManual, Filter.ShowMonitors);

            MergeColumns(snapshot.Columns);
            Palette.Merge(snapshot.Palette);
            HasColumns = Columns.Count > 0;
            StatusText = Columns.Count == 0
                ? "No Target Sinks in the graph"
                : $"{Columns.Count} Target Sinks · {Palette.Items.Count} Sources";
            ApplyFilter();
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "board rebuild failed; keeping the previous board");
            StatusText = "Board update failed — will retry on the next change";
        }
    }

    private void MergeColumns(IReadOnlyList<ColumnModel> models)
    {
        // TryAdd, not ToDictionary: duplicate keys must degrade gracefully, never throw.
        var byKey = new Dictionary<string, SinkColumnViewModel>(Columns.Count);
        foreach (var c in Columns) byKey.TryAdd(c.Key, c);
        var wanted = new HashSet<string>(models.Count);

        // Insert/update in the model's order, keeping existing instances (diff-merge).
        for (var i = 0; i < models.Count; i++)
        {
            var m = models[i];
            if (!wanted.Add(m.Key)) continue; // duplicate key within one snapshot — merge first only
            if (byKey.TryGetValue(m.Key, out var existing))
            {
                existing.Apply(m);
                var current = Columns.IndexOf(existing);
                if (current != i && i < Columns.Count) Columns.Move(current, i);
            }
            else
            {
                var vm = new SinkColumnViewModel(this, m);
                if (i <= Columns.Count) Columns.Insert(Math.Min(i, Columns.Count), vm);
                else Columns.Add(vm);
            }
        }

        for (var i = Columns.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Columns[i].Key))
                Columns.RemoveAt(i);
    }

    private void ApplyFilter()
    {
        foreach (var item in Palette.Items)
            item.IsVisible = Filter.MatchesText(item.Title, item.Subtitle, item.KindLabel);

        foreach (var column in Columns)
        {
            // A column that matches the filter shows ALL its cards: searching for a sink means
            // "show me this sink and what feeds it" — hiding non-matching cards left a matching
            // column looking empty while links exist. Card-level filtering only applies when the
            // column itself isn't what was searched for.
            var columnMatches = Filter.MatchesText(column.Title, column.Subtitle);
            var anyCardVisible = false;
            foreach (var card in column.Cards)
            {
                card.IsVisible = columnMatches || Filter.MatchesText(card.Title, card.Subtitle);
                anyCardVisible |= card.IsVisible;
            }
            column.IsVisible = columnMatches || anyCardVisible;
        }
    }

    // ===== IBoardCoordinator ============================================================

    public async Task ConnectAsync(int sourceNodeId, int targetNodeId)
    {
        var g = _graph.Current;
        var source = g.Node(sourceNodeId);
        var target = g.Node(targetNodeId);
        if (source is null || target is null) return;

        // Protected overrides all — never create a Link touching a "do not touch" node (ADR-0008).
        var protectedMatches = _ruleStore.Current.Protected;
        if (protectedMatches.Any(pm => _matcher.Matches(pm.Match, source) || _matcher.Matches(pm.Match, target)))
            return;

        var rule = RuleFactory.PositiveRule(source, target);

        await SaveDocAsync(doc =>
        {
            // Connecting clears a Suppression for this pair (latest action wins).
            var suppressions = doc.Suppressions
                .Where(s => !(_matcher.Matches(s.Source, source) && _matcher.Matches(s.Target, target)))
                .ToList();

            var alreadyRuled = doc.Rules.Any(r =>
                r.Enabled && _matcher.Matches(r.Source, source) && _matcher.Matches(r.Target, target));

            var rules = alreadyRuled ? doc.Rules.ToList() : doc.Rules.Append(rule).ToList();
            return doc with { Rules = rules, Suppressions = suppressions };
        }).ConfigureAwait(true);

        // Honour the reconcile seam, then optimistically create the managed Link(s) now.
        await SafeReconcile().ConfigureAwait(true);
        await ConnectLinksAsync(g, rule.Source, rule.Target, rule.Id).ConfigureAwait(true);

        RebuildFromCurrent();
    }

    public async Task RemoveCardAsync(SourceCardViewModel card)
    {
        if (card.State == CardState.Protected) return;
        var g = _graph.Current;
        var target = g.Node(card.TargetNodeId);
        var source = g.Node(card.RepresentativeNodeId);

        if (card.State == CardState.Managed)
        {
            // Managed → delete the Rule and tear down its Links.
            await SaveDocAsync(doc =>
            {
                var rules = doc.Rules.Where(r =>
                    r.Id != card.RuleId &&
                    !(source is not null && target is not null &&
                      _matcher.Matches(r.Source, source) && _matcher.Matches(r.Target, target)))
                    .ToList();
                return doc with { Rules = rules };
            }).ConfigureAwait(true);

            if (source is not null && target is not null)
                await DisconnectLinksAsync(g, card).ConfigureAwait(true);
        }
        else
        {
            // Manual/Unsaved external → write a Suppression (keep it un-linked) and drop the live Link.
            if (source is null || target is null) return;
            var suppression = RuleFactory.SuppressionFor(source, target);
            await SaveDocAsync(doc =>
            {
                var suppressions = doc.Suppressions.Any(s =>
                        _matcher.Matches(s.Source, source) && _matcher.Matches(s.Target, target))
                    ? doc.Suppressions.ToList()
                    : doc.Suppressions.Append(suppression).ToList();
                // Disconnecting overrides any positive rule for this pair.
                var rules = doc.Rules.Where(r =>
                    !(_matcher.Matches(r.Source, source) && _matcher.Matches(r.Target, target))).ToList();
                return doc with { Rules = rules, Suppressions = suppressions };
            }).ConfigureAwait(true);

            await DisconnectLinksAsync(g, card).ConfigureAwait(true);
        }

        _keptManual.Remove(BoardModelBuilder.CardKey(card.TargetNodeId, card.Key));
        RebuildFromCurrent();
    }

    public async Task SaveCardAsync(SourceCardViewModel card)
    {
        if (card.State != CardState.Unsaved) return;
        var g = _graph.Current;
        var source = g.Node(card.RepresentativeNodeId);
        var target = g.Node(card.TargetNodeId);
        if (source is null || target is null) return;

        var rule = RuleFactory.PositiveRule(source, target);
        await SaveDocAsync(doc =>
        {
            var alreadyRuled = doc.Rules.Any(r =>
                r.Enabled && _matcher.Matches(r.Source, source) && _matcher.Matches(r.Target, target));
            var rules = alreadyRuled ? doc.Rules.ToList() : doc.Rules.Append(rule).ToList();
            return doc with { Rules = rules };
        }).ConfigureAwait(true);

        _keptManual.Remove(BoardModelBuilder.CardKey(card.TargetNodeId, card.Key));
        await SafeReconcile().ConfigureAwait(true);
        RebuildFromCurrent();
    }

    public void KeepManual(SourceCardViewModel card)
    {
        if (card.State != CardState.Unsaved) return;
        _keptManual.Add(BoardModelBuilder.CardKey(card.TargetNodeId, card.Key));
        RebuildFromCurrent();
    }

    public async Task ToggleProtectAsync(int nodeId)
    {
        var node = _graph.Current.Node(nodeId);
        if (node is null) return;

        var isProtected = _ruleStore.Current.Protected.Any(pm => _matcher.Matches(pm.Match, node));
        await SaveDocAsync(doc =>
        {
            if (isProtected)
            {
                var kept = doc.Protected.Where(pm => !_matcher.Matches(pm.Match, node)).ToList();
                return doc with { Protected = kept };
            }
            return doc with { Protected = doc.Protected.Append(RuleFactory.ProtectedFor(node)).ToList() };
        }).ConfigureAwait(true);

        RebuildFromCurrent();
    }

    // ===== Virtual sinks (ADR-0011) =====================================================

    /// <summary>True when a sink controller is wired — gates the whole sink-management UI.</summary>
    public bool CanManageSinks => _sinkController is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewSinkValidationMessage))]
    [NotifyCanExecuteChangedFor(nameof(SubmitNewSinkCommand))]
    private string _newSinkName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewSinkValidationMessage))]
    [NotifyCanExecuteChangedFor(nameof(SubmitNewSinkCommand))]
    private string _newSinkDescription = string.Empty;

    [ObservableProperty]
    private bool _newSinkMono;

    public string? NewSinkValidationMessage => ValidateNewSink();

    private string? ValidateNewSink()
    {
        var name = NewSinkName.Trim();
        if (name.Length == 0) return null; // pristine form — no scolding, just a disabled button

        if (!SinkNameValidator.IsValidName(name))
            return "Name can only use letters, digits, '.', '_' and '-'.";
        if (_ruleStore.Current.VirtualSinks.Any(s => s.Name == name))
            return $"A sink named {name} is already declared.";
        if (_graph.Current.Nodes.Any(n => n.NodeName == name && NodeRoles.IsAudio(n)))
            return $"An audio node named {name} already exists.";

        var description = NewSinkDescription.Trim();
        if (description.Length > 0 && !SinkNameValidator.IsValidDescription(description))
            return "Description cannot contain quotes.";
        return null;
    }

    private bool CanSubmitNewSink => NewSinkName.Trim().Length > 0 && ValidateNewSink() is null;

    [RelayCommand(CanExecute = nameof(CanSubmitNewSink))]
    private async Task SubmitNewSink()
    {
        var name = NewSinkName.Trim();
        var description = NewSinkDescription.Trim();
        await CreateSinkAsync(name, description.Length > 0 ? description : name, NewSinkMono)
            .ConfigureAwait(true);
        NewSinkName = string.Empty;
        NewSinkDescription = string.Empty;
        NewSinkMono = false;
    }

    public async Task CreateSinkAsync(string name, string description, bool mono)
    {
        if (_sinkController is null) return;
        if (!SinkNameValidator.IsValidName(name) || !SinkNameValidator.IsValidDescription(description)) return;
        if (_ruleStore.Current.VirtualSinks.Any(s => s.Name == name)) return;

        var spec = new VirtualSinkSpec(Guid.NewGuid().ToString("N"), name, description,
            mono ? SinkChannels.Mono : SinkChannels.Stereo);
        await SaveDocAsync(doc => doc with { VirtualSinks = doc.VirtualSinks.Append(spec).ToList() })
            .ConfigureAwait(true);

        // Instant effect (the always-on SinkReconciler pass would also catch up, but this makes the
        // column appear within one snapshot — mirrors ConnectAsync's optimistic linking).
        try { await _sinkController.LoadAsync(new NullSinkRequest(name, description, mono)).ConfigureAwait(true); }
        catch { /* transient — SinkReconciler self-heals next pass */ }

        RebuildFromCurrent();
    }

    public SinkDeletionImpact PreviewDeleteSink(string sinkName)
    {
        var doc = _ruleStore.Current;
        var node = _graph.Current.Nodes.FirstOrDefault(n =>
            n.NodeName == sinkName && NodeRoles.IsTargetSink(n));

        // A criterion references the sink when it matches the live node, or — for robustness while
        // the node is absent — when it names it textually.
        bool Refs(MatchCriteria c) =>
            (node is not null && _matcher.Matches(c, node)) ||
            c.Predicates.Any(p => p.Field == Field.NodeName && p.Op == Op.Equals && p.Value == sinkName);

        return new SinkDeletionImpact(
            doc.Rules.Where(r => Refs(r.Target) || Refs(r.Source)).ToList(),
            doc.Suppressions.Where(s => Refs(s.Target) || Refs(s.Source)).ToList());
    }

    public async Task DeleteSinkAsync(string sinkName, bool deleteAffectedPolicy)
    {
        if (_sinkController is null) return;
        var impact = PreviewDeleteSink(sinkName);

        // One atomic save: the sink and (if chosen) everything referencing it go together.
        await SaveDocAsync(doc =>
        {
            var next = doc with { VirtualSinks = doc.VirtualSinks.Where(s => s.Name != sinkName).ToList() };
            if (!deleteAffectedPolicy) return next;

            var ruleIds = impact.Rules.Select(r => r.Id).ToHashSet();
            var suppressionIds = impact.Suppressions.Select(s => s.Id).ToHashSet();
            return next with
            {
                Rules = next.Rules.Where(r => !ruleIds.Contains(r.Id)).ToList(),
                Suppressions = next.Suppressions.Where(s => !suppressionIds.Contains(s.Id)).ToList(),
            };
        }).ConfigureAwait(true);

        // Instant effect: unload OUR modules for this sink_name — tagged only. A sink that was
        // imported from a still-present legacy conf has an untagged module owned by that file;
        // un-declaring it must not kill the user's live sink. Our own creations are always tagged,
        // so their teardown is unchanged. (SinkReconciler's stale pass is the safety net.)
        try
        {
            var modules = await _sinkController.ListNullSinkModulesAsync().ConfigureAwait(true);
            foreach (var module in modules.Where(m => m.SinkName == sinkName && m.IsAutoRouteTagged))
                await _sinkController.UnloadAsync(module.ModuleIndex).ConfigureAwait(true);
        }
        catch { /* transient — SinkReconciler self-heals next pass */ }

        RebuildFromCurrent();
    }

    // ===== helpers ======================================================================

    private async Task SaveDocAsync(Func<RulesDocument, RulesDocument> edit)
    {
        var next = edit(_ruleStore.Current);
        await _ruleStore.SaveAsync(next).ConfigureAwait(true);
    }

    private async Task ConnectLinksAsync(PwGraph g, MatchCriteria src, MatchCriteria tgt, string ruleId)
    {
        foreach (var s in _matcher.Resolve(src, g).Where(NodeRoles.IsAudioSource))
        foreach (var t in _matcher.Resolve(tgt, g).Where(NodeRoles.IsTargetSink))
        foreach (var pair in ChannelMapper.Map(s, t).Pairs)
        {
            try { await _linker.ConnectAsync(pair.OutPortId, pair.InPortId, ruleId).ConfigureAwait(true); }
            catch { /* transient (port vanished) → reconciler self-heals next snapshot */ }
        }
    }

    private async Task DisconnectLinksAsync(PwGraph g, SourceCardViewModel card)
    {
        var target = g.Node(card.TargetNodeId);
        if (target is null) return;

        foreach (var nodeId in card.AllSourceNodeIds)
        {
            var s = g.Node(nodeId);
            if (s is null) continue;
            foreach (var pair in ChannelMapper.Map(s, target).Pairs)
            {
                try { await _linker.DisconnectAsync(pair.OutPortId, pair.InPortId).ConfigureAwait(true); }
                catch { /* already gone → fine */ }
            }
        }
    }

    private async Task SafeReconcile()
    {
        try { await _reconciler.ReconcileAsync(_graph.Current, _ruleStore.Current).ConfigureAwait(true); }
        catch { /* reconcile is best-effort from the UI; graph service remains source of truth */ }
    }

    private static void PostToUi(Action action)
    {
        // Marshal onto the UI thread. In a headless/VM context (no Avalonia dispatcher running)
        // fall back to running inline so the smoke check and design-time init still work.
        try
        {
            var dispatcher = Dispatcher.UIThread;
            if (dispatcher.CheckAccess()) action();
            else dispatcher.Post(action);
        }
        catch
        {
            action();
        }
    }
}
