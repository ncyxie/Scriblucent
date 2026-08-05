using Scriblucent.Services;

namespace Scriblucent.Tests;

public class NoteImageLayoutMathTests
{
    [Fact]
    public void DocumentOriginFromPointer_RoundTripsWithCanvasFromDocument()
    {
        var (left, top) = NoteImageLayoutMath.DocumentOriginFromPointer(
            scrollX: 40,
            scrollY: 80,
            canvasX: 120,
            canvasY: 200,
            paddingLeft: 18,
            paddingTop: 16,
            grabOffsetX: 30,
            grabOffsetY: 40);

        var (canvasLeft, canvasTop) = NoteImageLayoutMath.CanvasFromDocument(
            left, top, 40, 80, 18, 16);

        Assert.Equal(120 - 30, canvasLeft, precision: 5);
        Assert.Equal(200 - 40, canvasTop, precision: 5);
    }

    [Fact]
    public void ResolveDocumentBoundsHeight_IncludesPadAndLiveDrag()
    {
        var height = NoteImageLayoutMath.ResolveDocumentBoundsHeight(
            extentHeight: 500,
            viewportHeight: 400,
            padBaseContentBottom: 480,
            extentPadHeight: 120,
            liveDragBottom: 700);

        Assert.Equal(700, height, precision: 5);
    }

    [Fact]
    public void ResolveDocumentBoundsHeight_UsesExtentWhenLarger()
    {
        var height = NoteImageLayoutMath.ResolveDocumentBoundsHeight(
            extentHeight: 900,
            viewportHeight: 400,
            padBaseContentBottom: 0,
            extentPadHeight: 0,
            liveDragBottom: null);

        Assert.Equal(900, height, precision: 5);
    }

    [Fact]
    public void ResolveExtentPadHeight_GrowsAndShrinks()
    {
        Assert.Equal(100, NoteImageLayoutMath.ResolveExtentPadHeight(500, 400), precision: 5);
        Assert.Equal(0, NoteImageLayoutMath.ResolveExtentPadHeight(300, 400), precision: 5);
    }

    [Fact]
    public void ResolveNeededDocumentBottom_TakesMaxPlusSlack()
    {
        var needed = NoteImageLayoutMath.ResolveNeededDocumentBottom(
            maxImageBottom: 200,
            edgeReserveBottom: 350,
            slack: 24);

        Assert.Equal(374, needed, precision: 5);
    }

    [Fact]
    public void ResolveNeededDocumentBottom_ZeroWhenEmpty()
    {
        var needed = NoteImageLayoutMath.ResolveNeededDocumentBottom(0, 0, 24);
        Assert.Equal(0, needed, precision: 5);
    }
}
