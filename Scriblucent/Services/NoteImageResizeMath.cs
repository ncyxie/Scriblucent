namespace Scriblucent.Services;

/// <summary>
/// Pure geometry helpers for aspect-locked floating image resize (testable without WPF UI).
/// </summary>
public static class NoteImageResizeMath
{
    public const double MinSideDip = 80;
    public const double MaxSideDip = 2000;

    /// <summary>Smallest side of an in-line photo (DIPs). In-line photos are small text elements,
    /// so the floor is far below the floating widget's.</summary>
    public const double MinInlineSideDip = 16;

    /// <summary>
    /// Resizes from a corner handle, replicating LibreOffice's <c>SdrDragResize::MoveSdrDrag</c>:
    /// the size is anchored at the FIXED opposite corner and the per-axis scale factor is the
    /// pointer's distance from that anchor divided by the start distance (so a 1px drag changes
    /// the size by exactly 1px). With <paramref name="keepRatio"/>, both axes take the SMALLER
    /// factor (the axis that moved less limits the scale - the LO "IsBigOrtho=false" rule), so a
    /// tiny movement can never balloon the photo. A factor &lt;= 0 (the pointer crossed the
    /// anchor) freezes the size, and the result is strictly clamped - never bounced.
    /// </summary>
    public static (double Width, double Height) ResizeFromCorner(
        double startWidth,
        double startHeight,
        double deltaX,
        double deltaY,
        bool flipX,
        bool flipY,
        bool keepRatio,
        double minSide = MinInlineSideDip,
        double maxSide = MaxSideDip)
    {
        if (startWidth <= 0 || startHeight <= 0
            || double.IsNaN(startWidth) || double.IsNaN(startHeight)
            || double.IsInfinity(startWidth) || double.IsInfinity(startHeight))
        {
            return (minSide, minSide);
        }

        // The pointer's distance from the fixed opposite corner, as fractions of the start
        // distances (LibreOffice: nXMul/nXDiv and nYMul/nYDiv).
        var xMul = startWidth + (flipX ? -deltaX : deltaX);
        var yMul = startHeight + (flipY ? -deltaY : deltaY);
        var xFact = xMul / startWidth;
        var yFact = yMul / startHeight;

        if (keepRatio)
        {
            // LO: both axes take the smaller factor - the axis that moved less limits the
            // scale, so the photo follows the pointer predictably instead of ballooning.
            xFact = Math.Min(xFact, yFact);
            yFact = xFact;
        }

        // LO: a factor <= 0 (the pointer crossed the anchor) freezes that axis at the start.
        xFact = xFact > 0 ? xFact : 1.0;
        yFact = yFact > 0 ? yFact : 1.0;

        var width = startWidth * xFact;
        var height = startHeight * yFact;

        width = Math.Clamp(width, minSide, maxSide);
        height = Math.Clamp(height, minSide, maxSide);
        return (width, height);
    }

    /// <summary>
    /// Projects a free-form corner size onto the aspect-preserving ray from the opposite corner.
    /// </summary>
    public static (double Width, double Height) ProjectCornerScale(
        double startWidth,
        double startHeight,
        double freeWidth,
        double freeHeight)
    {
        if (startWidth <= 0 || startHeight <= 0)
            return (0, 0);

        var scale = (freeWidth * startWidth + freeHeight * startHeight)
            / (startWidth * startWidth + startHeight * startHeight);
        if (scale < 0)
            scale = 0;

        return (startWidth * scale, startHeight * scale);
    }

    /// <summary>
    /// Clamps display size so the short side is at least <see cref="MinSideDip"/> and neither
    /// side exceeds <see cref="MaxSideDip"/>, always preserving aspect ratio.
    /// </summary>
    public static (double Width, double Height) ClampDisplaySize(double width, double height)
    {
        if (width <= 0 || height <= 0 || double.IsNaN(width) || double.IsNaN(height)
            || double.IsInfinity(width) || double.IsInfinity(height))
        {
            // Unknown aspect — use a square at the minimum floor.
            return (MinSideDip, MinSideDip);
        }

        var ratio = width / height;

        // Uniform scale so the short side reaches MinSideDip when undersized.
        var shortSide = Math.Min(width, height);
        if (shortSide < MinSideDip)
        {
            var grow = MinSideDip / shortSide;
            width *= grow;
            height *= grow;
        }

        // Uniform scale down if either side exceeds MaxSideDip.
        var longSide = Math.Max(width, height);
        if (longSide > MaxSideDip)
        {
            var shrink = MaxSideDip / longSide;
            width *= shrink;
            height *= shrink;
        }

        // After max shrink, short side might fall below min for extreme ratios that cannot
        // satisfy both constraints; prefer min short side and allow long side up to max.
        shortSide = Math.Min(width, height);
        if (shortSide < MinSideDip)
        {
            if (ratio >= 1)
            {
                // Wider than tall: height is short side.
                height = MinSideDip;
                width = height * ratio;
                if (width > MaxSideDip)
                {
                    width = MaxSideDip;
                    height = width / ratio;
                }
            }
            else
            {
                width = MinSideDip;
                height = width / ratio;
                if (height > MaxSideDip)
                {
                    height = MaxSideDip;
                    width = height * ratio;
                }
            }
        }

        return (width, height);
    }

    /// <summary>
    /// Keeps a display rect fully inside [0, boundsWidth] × [0, boundsHeight].
    /// When <paramref name="lockAspectRatio"/> is true, size is aspect-clamped and
    /// oversized rects shrink uniformly; when false, sides clamp independently.
    /// </summary>
    public static (double Left, double Top, double Width, double Height) ClampRectToBounds(
        double left,
        double top,
        double width,
        double height,
        double boundsWidth,
        double boundsHeight,
        bool lockAspectRatio = true,
        bool resizeToFit = true)
    {
        if (boundsWidth <= 0 || boundsHeight <= 0
            || double.IsNaN(boundsWidth) || double.IsNaN(boundsHeight)
            || double.IsInfinity(boundsWidth) || double.IsInfinity(boundsHeight))
        {
            var size = lockAspectRatio
                ? ClampDisplaySize(width, height)
                : ClampFreeformSize(width, height);
            return (0, 0, size.Width, size.Height);
        }

        var clamped = lockAspectRatio
            ? ClampDisplaySize(width, height)
            : ClampFreeformSize(width, height);
        width = clamped.Width;
        height = clamped.Height;

        if (width > boundsWidth || height > boundsHeight)
        {
            if (lockAspectRatio && resizeToFit)
            {
                var scale = Math.Min(boundsWidth / width, boundsHeight / height);
                if (scale > 0 && !double.IsNaN(scale) && !double.IsInfinity(scale))
                {
                    width *= scale;
                    height *= scale;
                }
                else
                {
                    width = Math.Min(width, boundsWidth);
                    height = Math.Min(height, boundsHeight);
                }
            }
            else if (resizeToFit)
            {
                width = Math.Min(width, boundsWidth);
                height = Math.Min(height, boundsHeight);
            }
        }

        var maxLeft = Math.Max(0, boundsWidth - width);
        var maxTop = Math.Max(0, boundsHeight - height);

        // NaN / infinity positions collapse to the origin; a size shrunk to fit keeps its
        // intended position clamped to the nearest reachable spot - never a blind (0,0) reset.
        if (double.IsNaN(left) || double.IsInfinity(left))
            left = 0;
        if (double.IsNaN(top) || double.IsInfinity(top))
            top = 0;

        left = Math.Clamp(left, 0, maxLeft);
        top = Math.Clamp(top, 0, maxTop);

        return (left, top, width, height);
    }

    /// <summary>
    /// Clamps width and height independently to <see cref="MinSideDip"/>..<see cref="MaxSideDip"/>.
    /// </summary>
    public static (double Width, double Height) ClampFreeformSize(double width, double height)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
            width = MinSideDip;
        if (double.IsNaN(height) || double.IsInfinity(height) || height <= 0)
            height = MinSideDip;

        width = Math.Clamp(width, MinSideDip, MaxSideDip);
        height = Math.Clamp(height, MinSideDip, MaxSideDip);
        return (width, height);
    }

    /// <summary>
    /// Minimum width/height box for a given aspect ratio (short side = <see cref="MinSideDip"/>).
    /// </summary>
    public static (double Width, double Height) MinimumSizeForAspect(double widthOverHeight)
    {
        if (widthOverHeight <= 0 || double.IsNaN(widthOverHeight) || double.IsInfinity(widthOverHeight))
            return (MinSideDip, MinSideDip);

        if (widthOverHeight >= 1)
            return (MinSideDip * widthOverHeight, MinSideDip);

        return (MinSideDip, MinSideDip / widthOverHeight);
    }
}
