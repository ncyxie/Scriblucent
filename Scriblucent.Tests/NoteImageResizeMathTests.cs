using Scriblucent.Services;

namespace Scriblucent.Tests;

public class NoteImageResizeMathTests
{
    [Fact]
    public void ResizeFromCorner_Free_GainIsExactlyOneToOne()
    {
        // The amplification regression: a 1px drag must change the size by exactly 1px.
        var (w1, h1) = NoteImageResizeMath.ResizeFromCorner(200, 150, 10, 0, flipX: false, flipY: false, keepRatio: false);
        Assert.Equal(210, w1, precision: 5);
        Assert.Equal(150, h1, precision: 5);

        var (w2, h2) = NoteImageResizeMath.ResizeFromCorner(200, 150, 0, 7, flipX: false, flipY: false, keepRatio: false);
        Assert.Equal(200, w2, precision: 5);
        Assert.Equal(157, h2, precision: 5);

        var (w3, h3) = NoteImageResizeMath.ResizeFromCorner(200, 150, 23, 11, flipX: false, flipY: false, keepRatio: false);
        Assert.Equal(223, w3, precision: 5);
        Assert.Equal(161, h3, precision: 5);
    }

    [Fact]
    public void ResizeFromCorner_Free_NegativeDragShrinksOneToOne()
    {
        var (w, h) = NoteImageResizeMath.ResizeFromCorner(200, 150, -40, -30, flipX: false, flipY: false, keepRatio: false);
        Assert.Equal(160, w, precision: 5);
        Assert.Equal(120, h, precision: 5);
    }

    [Fact]
    public void ResizeFromCorner_Locked_UsesSmallerFactor_AndKeepsRatio()
    {
        // BR drag (80, 100) on 200x150: xFact = 1.4, yFact = 1.667 -> both 1.4 (the smaller).
        var (w, h) = NoteImageResizeMath.ResizeFromCorner(200, 150, 80, 100, flipX: false, flipY: false, keepRatio: true);
        Assert.Equal(280, w, precision: 5);
        Assert.Equal(210, h, precision: 5);
        Assert.Equal(200.0 / 150.0, w / h, precision: 5);
    }

    [Fact]
    public void ResizeFromCorner_Locked_HorizontalOnlyDragDoesNotBalloon()
    {
        // A mostly-horizontal drag: the Y factor (1.0) is the smaller -> the size stays at
        // the start instead of ballooning from a tiny Y movement (the LO "smaller factor" rule).
        var (w, h) = NoteImageResizeMath.ResizeFromCorner(200, 150, 100, 5, flipX: false, flipY: false, keepRatio: true);
        var yFact = 155.0 / 150.0;
        Assert.Equal(200 * yFact, w, precision: 5);
        Assert.Equal(150 * yFact, h, precision: 5);
        Assert.Equal(200.0 / 150.0, w / h, precision: 5);
    }

    [Fact]
    public void ResizeFromCorner_HandleFlips_MapAxes()
    {
        // TL handle: both axes flip (the anchor is the bottom-right corner).
        var (w, h) = NoteImageResizeMath.ResizeFromCorner(200, 150, 10, 10, flipX: true, flipY: true, keepRatio: false);
        Assert.Equal(190, w, precision: 5);
        Assert.Equal(140, h, precision: 5);

        // TR handle: only Y flips.
        var (w2, h2) = NoteImageResizeMath.ResizeFromCorner(200, 150, 10, 10, flipX: false, flipY: true, keepRatio: false);
        Assert.Equal(210, w2, precision: 5);
        Assert.Equal(140, h2, precision: 5);
    }

    [Fact]
    public void ResizeFromCorner_CrossingAnchor_FreezesAtStart()
    {
        // The pointer crossed the anchor (the factor went negative) -> the size freezes
        // at the start (LibreOffice clamps a non-positive factor to 1.0).
        var (w, h) = NoteImageResizeMath.ResizeFromCorner(200, 150, -300, 20, flipX: false, flipY: false, keepRatio: false);
        Assert.Equal(200, w, precision: 5);
        Assert.Equal(170, h, precision: 5);
    }

    [Fact]
    public void ResizeFromCorner_ClampIsStrict_NeverBounces()
    {
        var (w, h) = NoteImageResizeMath.ResizeFromCorner(200, 150, 100, 100, flipX: false, flipY: false, keepRatio: false, minSide: 16, maxSide: 2000);
        Assert.Equal(300, w, precision: 5);
        Assert.Equal(250, h, precision: 5);

        // Shrink to near the floor: the result sits AT the floor, never bounces above it.
        // (A drag past the anchor itself freezes at the start - the LO factor<=0 rule - so
        // the floor is reached by dragging almost back to the anchor, not beyond it.)
        var (w2, h2) = NoteImageResizeMath.ResizeFromCorner(200, 150, -185, -135, flipX: false, flipY: false, keepRatio: false, minSide: 16, maxSide: 2000);
        Assert.Equal(16, w2, precision: 5);
        Assert.Equal(16, h2, precision: 5);

        // A tiny sub-floor result sticks at the floor exactly (no min-side jump to 80).
        var (w3, h3) = NoteImageResizeMath.ResizeFromCorner(30, 30, -18, -18, flipX: false, flipY: false, keepRatio: false, minSide: 16, maxSide: 2000);
        Assert.Equal(16, w3, precision: 5);
        Assert.Equal(16, h3, precision: 5);
    }

    [Fact]
    public void ResizeFromCorner_InvalidStart_UsesMinimum()
    {
        var (w, h) = NoteImageResizeMath.ResizeFromCorner(0, 0, 10, 10, flipX: false, flipY: false, keepRatio: false);
        Assert.Equal(NoteImageResizeMath.MinInlineSideDip, w);
        Assert.Equal(NoteImageResizeMath.MinInlineSideDip, h);
    }

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
