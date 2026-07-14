using System.Windows;

namespace Scriblucent.Services;

/// <summary>
/// Resolves <c>Type.*</c> token doubles from app/window resources for code-behind UI.
/// </summary>
internal static class TypeScale
{
    public const string Display = "Type.Display";
    public const string PageTitle = "Type.PageTitle";
    public const string DialogTitle = "Type.DialogTitle";
    public const string EmptyState = "Type.EmptyState";
    public const string BlockTitle = "Type.BlockTitle";
    public const string SectionTitle = "Type.SectionTitle";
    public const string CardTitle = "Type.CardTitle";
    public const string Body = "Type.Body";
    public const string Meta = "Type.Meta";
    public const string Caption = "Type.Caption";
    public const string Chip = "Type.Chip";

    public static double Px(string resourceKey, double fallback, FrameworkElement? scope = null)
    {
        if (scope?.TryFindResource(resourceKey) is double scoped)
            return scoped;

        if (Application.Current?.TryFindResource(resourceKey) is double app)
            return app;

        return fallback;
    }
}
