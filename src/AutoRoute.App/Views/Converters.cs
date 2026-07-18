using Avalonia.Data.Converters;

namespace AutoRoute.App.Views;

/// <summary>Small presentation-only value converters used by the board views.</summary>
public static class Converters
{
    /// <summary>Protect-toggle button label: reflects the action the click performs.</summary>
    public static readonly IValueConverter ProtectLabel =
        new FuncValueConverter<bool, string>(isProtected => isProtected ? "Unprotect" : "Protect");
}
