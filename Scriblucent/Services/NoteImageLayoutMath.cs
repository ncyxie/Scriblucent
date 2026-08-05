namespace Scriblucent.Services;

/// <summary>
/// Pure layout helpers for floating images (document space ↔ canvas, pad, virtual bounds).
/// Kept free of WPF types so coordinator edge cases stay unit-testable.
/// </summary>
public static class NoteImageLayoutMath
{
    /// <summary>
    /// Document-space origin from overlay pointer + scroll (inverse of canvas projection).
    /// </summary>
    public static (double Left, double Top) DocumentOriginFromPointer(
        double scrollX,
        double scrollY,
        double canvasX,
        double canvasY,
        double paddingLeft,
        double paddingTop,
        double grabOffsetX,
        double grabOffsetY)
        => (scrollX + canvasX - paddingLeft - grabOffsetX,
            scrollY + canvasY - paddingTop - grabOffsetY);

    /// <summary>
    /// Canvas position from document geometry + scroll (matches SyncWidget).
    /// </summary>
    public static (double CanvasLeft, double CanvasTop) CanvasFromDocument(
        double documentLeft,
        double documentTop,
        double scrollX,
        double scrollY,
        double paddingLeft,
        double paddingTop)
        => (documentLeft - scrollX + paddingLeft,
            documentTop - scrollY + paddingTop);

    /// <summary>
    /// Effective document height for clamp: scroll extent, tracked pad, and optional live drag bottom.
    /// </summary>
    public static double ResolveDocumentBoundsHeight(
        double extentHeight,
        double viewportHeight,
        double padBaseContentBottom,
        double extentPadHeight,
        double? liveDragBottom)
    {
        var height = Math.Max(extentHeight, viewportHeight);
        if (padBaseContentBottom > 0 || extentPadHeight > 0)
            height = Math.Max(height, padBaseContentBottom + extentPadHeight);
        if (liveDragBottom is { } bottom)
            height = Math.Max(height, bottom);
        return height;
    }

    /// <summary>
    /// Spacer height so document extent reaches at least <paramref name="neededBottom"/>.
    /// </summary>
    public static double ResolveExtentPadHeight(double neededBottom, double contentBottom)
        => Math.Max(0, neededBottom - Math.Max(0, contentBottom));

    /// <summary>
    /// Needed document bottom from image bottoms, optional edge-scroll reserve, and slack.
    /// </summary>
    public static double ResolveNeededDocumentBottom(
        double maxImageBottom,
        double edgeReserveBottom,
        double slack)
    {
        var needed = Math.Max(maxImageBottom, edgeReserveBottom);
        if (needed > 0)
            needed += Math.Max(0, slack);
        return needed;
    }
}
