using Scriblucent.Services;

namespace Scriblucent.Tests;

public class NoteWrapLayoutTests
{
    [Fact]
    public void GetWrapRect_ExpandsAllSidesByGap()
    {
        var (l, t, r, b) = NoteWrapMath.GetWrapRect(100, 200, 300, 400, 10);
        Assert.Equal(90, l);
        Assert.Equal(190, t);
        Assert.Equal(310, r);
        Assert.Equal(410, b);
    }

    [Fact]
    public void IntersectsVertical_OverlappingRanges_True()
    {
        Assert.True(NoteWrapMath.IntersectsVertical(50, 150, 100, 300));
        Assert.True(NoteWrapMath.IntersectsVertical(120, 180, 100, 300));
        Assert.True(NoteWrapMath.IntersectsVertical(50, 100.1, 100, 300));
    }

    [Fact]
    public void IntersectsVertical_JustTouchingEdges_False()
    {
        // Line bottom exactly at wrap top, or line top exactly at wrap bottom: no overlap.
        Assert.False(NoteWrapMath.IntersectsVertical(50, 100, 100, 300));
        Assert.False(NoteWrapMath.IntersectsVertical(300, 400, 100, 300));
        Assert.False(NoteWrapMath.IntersectsVertical(50, 80, 100, 300));
    }

    [Fact]
    public void ComputeInsets_MoreRoomOnRight_UsesLeftInset()
    {
        // Image at x 40..140 in a 600 wide column: left lane 40, right lane 460 -> text runs right.
        var (left, right, applies) = NoteWrapMath.ComputeInsets(40, 140, 600, 24);
        Assert.True(applies);
        Assert.Equal(140, left);
        Assert.Equal(0, right);
    }

    [Fact]
    public void ComputeInsets_MoreRoomOnLeft_UsesRightInset()
    {
        // Image at x 460..560: left lane 460, right lane 40 -> text runs left.
        var (left, right, applies) = NoteWrapMath.ComputeInsets(460, 560, 600, 24);
        Assert.True(applies);
        Assert.Equal(0, left);
        Assert.Equal(140, right);
    }

    [Fact]
    public void ComputeInsets_TiedLanes_PrefersRightLane()
    {
        // Image centered: left lane 300, right lane 300 -> tie resolves to the right lane.
        var (left, right, applies) = NoteWrapMath.ComputeInsets(300, 300, 600, 24);
        Assert.True(applies);
        Assert.Equal(300, left);
        Assert.Equal(0, right);
    }

    [Fact]
    public void ComputeInsets_BothLanesTooNarrow_DoesNotApply()
    {
        // Image spanning nearly the whole column: neither lane is usable.
        var (left, right, applies) = NoteWrapMath.ComputeInsets(10, 590, 600, 24);
        Assert.False(applies);
        Assert.Equal(0, left);
        Assert.Equal(0, right);
    }

    [Fact]
    public void ComputeInsets_OneLaneJustBelowMinimum_DoesNotApply()
    {
        var (_, _, applies) = NoteWrapMath.ComputeInsets(20, 580, 600, 24);
        Assert.False(applies);
    }

    [Fact]
    public void ComputeInsets_LaneExactlyAtMinimum_Applies()
    {
        var (left, _, applies) = NoteWrapMath.ComputeInsets(24, 576, 600, 24);
        Assert.True(applies);
        Assert.Equal(576, left);
    }

    [Fact]
    public void ComputeTopPush_MovesParagraphTopToBandBottom()
    {
        Assert.Equal(200, NoteWrapMath.ComputeTopPush(100, 300));
    }

    [Fact]
    public void ComputeTopPush_ParagraphAlreadyBelowBand_Zero()
    {
        Assert.Equal(0, NoteWrapMath.ComputeTopPush(400, 300));
        Assert.Equal(0, NoteWrapMath.ComputeTopPush(300, 300));
    }

    [Fact]
    public void ComputeTopPush_StraddlingParagraph_PushesFullRemainder()
    {
        Assert.Equal(250, NoteWrapMath.ComputeTopPush(50, 300));
    }

    [Fact]
    public void ComputeSpacerHeight_ReachesBandBottomFromSpacerTop()
    {
        Assert.Equal(200, NoteWrapMath.ComputeSpacerHeight(100, 300));
        Assert.Equal(0, NoteWrapMath.ComputeSpacerHeight(400, 300));
        Assert.Equal(0, NoteWrapMath.ComputeSpacerHeight(300, 300));
    }
}
