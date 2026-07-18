using System;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.Engine.Model;

namespace AutoRoute.Engine;

/// <summary>
/// Owns <c>~/.config/autoroute/rules.json</c>: load, atomic save (auto-persist on every edit,
/// ADR-0009), and <see cref="Changed"/> hot-reload when the file changes on disk.
/// </summary>
/// <remarks>Wave 2 (Engine) implements the behavior; the public surface here is frozen.</remarks>
public interface IRuleStore
{
    /// <summary>The in-memory document. <see cref="RulesDocument.Empty"/> until first load.</summary>
    RulesDocument Current { get; }

    /// <summary>Raised when the document changes (external edit hot-reload, or after a save).</summary>
    event EventHandler<RulesDocument>? Changed;

    /// <summary>Load the document from disk (creating an empty one if absent).</summary>
    Task<RulesDocument> LoadAsync(CancellationToken ct = default);

    /// <summary>Atomically persist the document (temp file + File.Move).</summary>
    Task SaveAsync(RulesDocument document, CancellationToken ct = default);
}
