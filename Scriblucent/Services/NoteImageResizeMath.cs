namespace Scriblucent.Services;

/// <summary>
/// Pure geometry helpers for aspect-locked floating image resize (testable without WPF UI).
/// </summary>
public static class NoteImageResizeMath
{
    public const double MinSideDip = 80;
    public const double MaxSideDip = 2000;

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
        bool lockAspectRatio = true)
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

        var shrunkToFit = false;
        if (width > boundsWidth || height > boundsHeight)
        {
            if (lockAspectRatio)
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
            else
            {
                width = Math.Min(width, boundsWidth);
                height = Math.Min(height, boundsHeight);
            }

            shrunkToFit = true;
        }

        var maxLeft = Math.Max(0, boundsWidth - width);
        var maxTop = Math.Max(0, boundsHeight - height);

        if (shrunkToFit || double.IsNaN(left) || double.IsInfinity(left))
            left = 0;
        if (shrunkToFit || double.IsNaN(top) || double.IsInfinity(top))
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
