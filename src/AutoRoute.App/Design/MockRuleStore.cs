using System;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.Engine;
using AutoRoute.Engine.Model;

namespace AutoRoute.App.Design;

/// <summary>
/// In-memory <see cref="IRuleStore"/> for standalone UI development: holds the document in memory
/// and raises <see cref="Changed"/> on save (mimics auto-persist + hot-reload, ADR-0009). Wave 3
/// replaces this with the real <c>RuleStore</c> writing <c>~/.config/autoroute/rules.json</c>.
/// </summary>
public sealed class MockRuleStore : IRuleStore
{
    public MockRuleStore(RulesDocument? seed = null) => Current = seed ?? RulesDocument.Empty;

    public RulesDocument Current { get; private set; }

    public event EventHandler<RulesDocument>? Changed;

    public Task<RulesDocument> LoadAsync(CancellationToken ct = default) => Task.FromResult(Current);

    public Task SaveAsync(RulesDocument document, CancellationToken ct = default)
    {
        Current = document;
        Changed?.Invoke(this, document);
        return Task.CompletedTask;
    }
}
