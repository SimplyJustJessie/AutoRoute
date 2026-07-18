namespace AutoRoute.App.ViewModels;

/// <summary>
/// The four Source-card states from the board spec (ADR-0010 / CONTEXT.md), encoded so views can
/// switch styling on them. Precedence when classifying is Protected &gt; Managed &gt; Unsaved/Manual.
/// </summary>
public enum CardState
{
    /// <summary>Created by a positive Rule (carries the <c>autoroute.managed</c> tag). Accent border + rule tooltip + "×".</summary>
    Managed,

    /// <summary>An untagged Link the user keeps as-is; not reproduced automatically. Neutral styling.</summary>
    Manual,

    /// <summary>An external/unowned Link mirrored on first launch; not persisted. Badge + "Save".</summary>
    Unsaved,

    /// <summary>A "do not touch" node — locked/pinned, not editable.</summary>
    Protected,
}
