using System;
using System.Windows;

namespace Scriblucent.Services;

/// <summary>
/// Corner radius for a dialog's full-window scrim/backdrop, matched to what the OS actually does
/// to the window frame underneath it rather than an app-drawn constant.
/// </summary>
internal static class OverlayScrimRounding
{
    // No DWM API reports the actual radius DWM applies for its default (DWMWCP_ROUND) corner
    // preference - this app never overrides it, so every native-rounded window here gets that
    // default. 8px @ 100% DPI is the best publicly-known approximation. Windows 10 and earlier
    // never round top-level windows at all, so the scrim must stay square there to match.
    private const double Windows11CornerRadiusDip = 8;

    public static CornerRadius WindowMatchRadius { get; } =
        new CornerRadius(Environment.OSVersion.Version.Build >= 22000 ? Windows11CornerRadiusDip : 0);
}
