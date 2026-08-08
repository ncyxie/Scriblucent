using Scriblucent.Services;

namespace Scriblucent.Tests;

public class NoteImageResizeMathTests
{
    [Fact]
    public void ProjectCornerScale_PreservesAspect()
    {
        var (w, h) = NoteImageResizeMath.ProjectCornerScale(200, 100, 300, 200);
        Assert.True(w > 0 && h > 0);
        Assert.Equal(2.0, w / h, precision: 5);
    }

    [Fact]
    public void ProjectCornerScale_GrowsMonotonicallyAlongDiagonal()
    {
        var a = NoteImageResizeMath.ProjectCornerScale(200, 100, 220, 110);
        var b = NoteImageResizeMath.ProjectCornerScale(200, 100, 260, 130);
        Assert.True(b.Width > a.Width);
        Assert.True(b.Height > a.Height);
        Assert.Equal(a.Width / a.Height, b.Width / b.Height, precision: 5);
    }

    [Fact]
    public void ProjectCornerScale_NegativeScaleClampsToZero()
    {
        var (w, h) = NoteImageResizeMath.ProjectCornerScale(200, 100, -50, -25);
        Assert.Equal(0, w);
        Assert.Equal(0, h);
    }

    [Fact]
    public void ClampDisplaySize_ZeroInput_UsesMinimumForUnknownAspect()
    {
        var (w, h) = NoteImageResizeMath.ClampDisplaySize(0, 0);
        Assert.Equal(NoteImageResizeMath.MinSideDip, w);
        Assert.Equal(NoteImageResizeMath.MinSideDip, h);
    }

    [Fact]
    public void ClampDisplaySize_TinyWide_PreservesAspect_WithMinShortSide()
    {
        var (w, h) = NoteImageResizeMath.ClampDisplaySize(4, 2); // ratio 2
        Assert.Equal(2.0, w / h, precision: 5);
        Assert.Equal(NoteImageResizeMath.MinSideDip, h, precision: 5);
        Assert.Equal(NoteImageResizeMath.MinSideDip * 2, w, precision: 5);
    }

    [Fact]
    public void ClampDisplaySize_TinyTall_PreservesAspect_WithMinShortSide()
    {
        var (w, h) = NoteImageResizeMath.ClampDisplaySize(2, 4); // ratio 0.5
        Assert.Equal(0.5, w / h, precision: 5);
        Assert.Equal(NoteImageResizeMath.MinSideDip, w, precision: 5);
        Assert.Equal(NoteImageResizeMath.MinSideDip * 2, h, precision: 5);
    }

    [Fact]
    public void ClampDisplaySize_DoesNotForceSquareForNonSquareAspect()
    {
        var (w, h) = NoteImageResizeMath.ClampDisplaySize(0.01, 0.005);
        Assert.NotEqual(w, h);
        Assert.Equal(2.0, w / h, precision: 5);
    }

    [Fact]
    public void MinimumSizeForAspect_Wide()
    {
        var (w, h) = NoteImageResizeMath.MinimumSizeForAspect(2);
        Assert.Equal(NoteImageResizeMath.MinSideDip, h, precision: 5);
        Assert.Equal(NoteImageResizeMath.MinSideDip * 2, w, precision: 5);
    }

    [Fact]
    public void ClampRectToBounds_InsideUnchanged()
    {
        var (left, top, w, h) = NoteImageResizeMath.ClampRectToBounds(40, 50, 100, 80, 400, 300);
        Assert.Equal(40, left, precision: 5);
        Assert.Equal(50, top, precision: 5);
        Assert.Equal(100, w, precision: 5);
        Assert.Equal(80, h, precision: 5);
    }

    [Fact]
    public void ClampRectToBounds_PastEdges_PinnedInside()
    {
        var (left, top, w, h) = NoteImageResizeMath.ClampRectToBounds(350, 280, 100, 80, 400, 300);
        Assert.Equal(300, left, precision: 5); // 400 - 100
        Assert.Equal(220, top, precision: 5);  // 300 - 80
        Assert.Equal(100, w, precision: 5);
        Assert.Equal(80, h, precision: 5);
    }

    [Fact]
    public void ClampRectToBounds_NegativeOrigin_PinnedToZero()
    {
        var (left, top, w, h) = NoteImageResizeMath.ClampRectToBounds(-40, -20, 100, 80, 400, 300);
        Assert.Equal(0, left, precision: 5);
        Assert.Equal(0, top, precision: 5);
        Assert.Equal(100, w, precision: 5);
        Assert.Equal(80, h, precision: 5);
    }

    [Fact]
    public void ClampRectToBounds_Oversized_ShrinksToFitAndClampsPosition()
    {
        // Shrink-to-fit preserves the intended position as much as the reachable area allows -
        // never a blind (0,0) reset. 800x400 -> 200x100 in a 200x150 box: left pins to 0,
        // top stays at 10 (there is 50px of vertical slack).
        var (left, top, w, h) = NoteImageResizeMath.ClampRectToBounds(10, 10, 800, 400, 200, 150);
        Assert.Equal(0, left, precision: 5);
        Assert.Equal(10, top, precision: 5);
        Assert.True(w <= 200 + 0.001);
        Assert.True(h <= 150 + 0.001);
        Assert.Equal(2.0, w / h, precision: 4);
    }

    [Fact]
    public void ClampRectToBounds_RestoreMode_KeepsSizeAndClampsPosition()
    {
        // resizeToFit:false (the restore path) - a saved size is authoritative and is never
        // shrunk; only the position is clamped to the nearest reachable spot.
        var (left, top, w, h) = NoteImageResizeMath.ClampRectToBounds(950, 300, 400, 250, 1000, 800, lockAspectRatio: true, resizeToFit: false);
        Assert.Equal(600, left, precision: 3); // clamped into the reachable width, NOT reset to 0
        Assert.Equal(300, top, precision: 3);
        Assert.Equal(400, w, precision: 3); // size untouched
        Assert.Equal(250, h, precision: 3);

        // Oversized in a small bounds: position clamps to the origin (nowhere else fits),
        // but the size is still preserved instead of being shrunk into the box.
        var (l2, t2, w2, h2) = NoteImageResizeMath.ClampRectToBounds(200, 300, 400, 250, 300, 200, lockAspectRatio: true, resizeToFit: false);
        Assert.Equal(0, l2, precision: 3);
        Assert.Equal(0, t2, precision: 3);
        Assert.Equal(400, w2, precision: 3);
        Assert.Equal(250, h2, precision: 3);
    }

    [Fact]
    public void ClampFreeformSize_ClampsEachSideIndependently()
    {
        var (w, h) = NoteImageResizeMath.ClampFreeformSize(40, 3000);
        Assert.Equal(NoteImageResizeMath.MinSideDip, w);
        Assert.Equal(NoteImageResizeMath.MaxSideDip, h);
    }

    [Fact]
    public void ClampFreeformSize_InvalidInput_UsesMinimum()
    {
        var (w, h) = NoteImageResizeMath.ClampFreeformSize(0, double.NaN);
        Assert.Equal(NoteImageResizeMath.MinSideDip, w);
        Assert.Equal(NoteImageResizeMath.MinSideDip, h);
    }

    [Fact]
    public void ClampRectToBounds_Freeform_ClampsSidesIndependentlyWithoutPreservingAspect()
    {
        var (left, top, w, h) = NoteImageResizeMath.ClampRectToBounds(
            10, 10, 40, 3000, 400, 500, lockAspectRatio: false);
        Assert.Equal(10, left, precision: 5); // preserved - only the position is clamped
        Assert.Equal(0, top, precision: 5);   // pins to the bottom of the reachable area
        Assert.Equal(NoteImageResizeMath.MinSideDip, w, precision: 5);
        Assert.Equal(500, h, precision: 5); // shrunk to fit bounds height, not aspect
    }
}
