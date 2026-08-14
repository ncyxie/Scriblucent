using Scriblucent.Models;
using System.Windows;

namespace Scriblucent.Services;

public static class GlobalScrollBarSettingsService
{
    private const double VisibleThickness = 8.0;
    private const double HiddenThickness = 0.0;

    public static void Apply(AppSettings settings)
    {
        var verticalThickness = settings.EnableScrollbar && settings.EnableVerticalScrollbar
            ? VisibleThickness
            : HiddenThickness;
        var horizontalThickness = settings.EnableScrollbar && settings.EnableHorizontalScrollbar
            ? VisibleThickness
            : HiddenThickness;

        SetResource("GlobalVerticalScrollBarWidth", verticalThickness);
        SetResource("GlobalHorizontalScrollBarHeight", horizontalThickness);
        SetResource(SystemParameters.VerticalScrollBarWidthKey, verticalThickness);
        SetResource(SystemParameters.HorizontalScrollBarHeightKey, horizontalThickness);
    }

    private static void SetResource(object key, double value)
    {
        if (Application.Current?.Resources is not { } resources)
            return;

        resources[key] = value;
    }
}
