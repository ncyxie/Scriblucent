using Scriblucent.Services;

namespace Scriblucent.Tests;

public class NoteImageLoaderTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "Images", "pixel.png");

    [Fact]
    public void FitWithinBox_PreservesAspect_AndFitsMax()
    {
        var (w, h) = NoteImageLoader.FitWithinBox(800, 600, 400, 300);
        Assert.Equal(400, w, precision: 3);
        Assert.Equal(300, h, precision: 3);
    }

    [Fact]
    public void FitWithinBox_DoesNotUpscale()
    {
        var (w, h) = NoteImageLoader.FitWithinBox(100, 50, 400, 300);
        Assert.Equal(100, w, precision: 3);
        Assert.Equal(50, h, precision: 3);
    }

    [Fact]
    public void ResolveDecodePixelWidth_AppliesHeadroomAndCapsAtNatural()
    {
        var budget = NoteImageLoader.ResolveDecodePixelWidth(
            targetDisplayWidthDip: 100,
            dpiScale: 1,
            naturalPixelWidth: 200);
        Assert.Equal(150, budget); // 100 * 1.5 headroom = 150, below the natural cap

        var smallNatural = NoteImageLoader.ResolveDecodePixelWidth(100, 1, 50);
        Assert.Equal(50, smallNatural);

        // The headroom is kept low: 1.5x display width, so a photo note never holds ~9x its
        // rendered pixels (the old 3.0 headroom) - the zoom re-decodes at the new size instead.
        Assert.Equal(150, NoteImageLoader.ResolveDecodePixelWidth(100, 1, 1000));
    }

    [Fact]
    public void TryLoadFromBytes_LoadsFrozenBitmap()
    {
        Assert.True(File.Exists(FixturePath), $"Missing fixture: {FixturePath}");
        var bytes = File.ReadAllBytes(FixturePath);

        Assert.True(NoteImageLoader.TryLoadFromBytes(bytes, 64, 1, out var result, out var error), error);
        Assert.NotNull(result);
        Assert.True(result!.DisplaySource.IsFrozen);
        Assert.Equal(1, result.NaturalPixelWidth);
        Assert.Equal(1, result.NaturalPixelHeight);
        Assert.NotEmpty(result.Bytes);
    }

    [Fact]
    public async Task TryLoadFromFileAsync_LoadsFixture()
    {
        Assert.True(File.Exists(FixturePath), $"Missing fixture: {FixturePath}");

        var (ok, result, error) = await NoteImageLoader.TryLoadFromFileAsync(FixturePath, 64, 1);
        Assert.True(ok, error);
        Assert.NotNull(result);
        Assert.True(result!.DisplaySource.IsFrozen);
    }
}
