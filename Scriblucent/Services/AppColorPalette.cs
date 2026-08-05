using System.Windows.Media;

namespace Scriblucent.Services;

/// <summary>
/// Shared swatch colors for sketch / color-picker UI (ink + paper backgrounds).
/// </summary>
public static class AppColorPalette
{
    /// <summary>Muted ink pigments used by the sketch pen strip.</summary>
    public static readonly Color[] Ink =
    [
        Color.FromRgb(0x1A, 0x1A, 0x1A),
        Color.FromRgb(0x5C, 0x5C, 0x5C),
        Color.FromRgb(0x9E, 0x9E, 0x9E),
        Color.FromRgb(0xE8, 0xE8, 0xE8),
        Colors.White,
        Color.FromRgb(0xC4, 0x47, 0x3A),
        Color.FromRgb(0xD4, 0x78, 0x3A),
        Color.FromRgb(0xC4, 0xA0, 0x35),
        Color.FromRgb(0x6B, 0x8F, 0x5A),
        Color.FromRgb(0x3D, 0x7A, 0x6A),
        Color.FromRgb(0x4A, 0x7C, 0x9B),
        Color.FromRgb(0x4A, 0x5F, 0x8F),
        Color.FromRgb(0x7A, 0x5F, 0x8F),
        Color.FromRgb(0xB8, 0x5A, 0x7A),
        Color.FromRgb(0x8B, 0x65, 0x40)
    ];

    /// <summary>Paper / canvas background solids shown beside transparent.</summary>
    public static readonly Color[] Paper =
    [
        Colors.White,
        Color.FromRgb(0xF7, 0xF4, 0xEF), // warm paper
        Color.FromRgb(0xF3, 0xF4, 0xF6), // cool paper
        Color.FromRgb(0xE5, 0xE7, 0xEB), // soft gray
        Color.FromRgb(0x2A, 0x2A, 0x2A), // charcoal
        Color.FromRgb(0x1A, 0x1A, 0x1A)  // near black
    ];
}
