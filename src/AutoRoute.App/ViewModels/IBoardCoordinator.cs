using System.Threading.Tasks;

namespace AutoRoute.App.ViewModels;

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
}
