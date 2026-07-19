using System.Collections.Generic;
using System.Threading.Tasks;
using AutoRoute.Engine.Model;

namespace AutoRoute.App.ViewModels;

/// <summary>
/// What deleting a managed virtual sink would take with it (ADR-0011): the Rules and Suppressions
/// whose criteria reference the sink. Shown in the delete-confirmation flyout so the user decides
/// whether they go too.
/// </summary>
public sealed record SinkDeletionImpact(
    IReadOnlyList<Rule> Rules,
    IReadOnlyList<Suppression> Suppressions)
{
    public bool IsEmpty => Rules.Count == 0 && Suppressions.Count == 0;
}

/// <summary>
/// The action seam child VMs (columns, cards, palette items) call back into. Implemented by
/// <see cref="BoardViewModel"/>, which owns the injected <c>IRuleStore</c>/<c>IPwLinker</c>/graph
/// and turns each intent into rule edits + link ops. Keeping this an interface makes the child VMs
/// unit-testable without the whole board.
/// </summary>
public interface IBoardCoordinator
{
    /// <summary>Drop: connect a Source (by live node id) into a Target Sink → positive Rule + managed Link(s).</summary>
    Task ConnectAsync(int sourceNodeId, int targetNodeId);

    /// <summary>Remove a card: delete its Rule (managed) or write a Suppression (manual/unsaved external).</summary>
    Task RemoveCardAsync(SourceCardViewModel card);

    /// <summary>Save an unsaved external card → a positive Rule from its endpoints.</summary>
    Task SaveCardAsync(SourceCardViewModel card);

    /// <summary>Keep an unsaved external card as a Manual Link (stop offering to save it).</summary>
    void KeepManual(SourceCardViewModel card);

    /// <summary>Toggle "do not touch" (Protected) for the node/app behind a card or palette item.</summary>
    Task ToggleProtectAsync(int nodeId);

    /// <summary>Declare + create a virtual sink (ADR-0011): one rules.json save, then instant pactl load.</summary>
    Task CreateSinkAsync(string name, string description, bool mono);

    /// <summary>What deleting the named managed sink would affect — feeds the confirm flyout.</summary>
    SinkDeletionImpact PreviewDeleteSink(string sinkName);

    /// <summary>
    /// Undeclare the named sink (optionally deleting the affected Rules/Suppressions with it, in
    /// the same atomic save), then unload its module for instant effect.
    /// </summary>
    Task DeleteSinkAsync(string sinkName, bool deleteAffectedPolicy);
}
