using Scriblucent.Services;
using Scriblucent.Services.AI;

namespace Scriblucent.Tests;

public sealed class AiGenerationProgressAdapterTests
{
    [Theory]
    [InlineData("ConvertToFlashcardsStatusPreparingAssets", AiGenerationStatus.PreparingAssets)]
    [InlineData("ConvertToFlashcardsStatusAssetsReady", AiGenerationStatus.AssetsReady)]
    [InlineData("ConvertToFlashcardsStatusProcessing", AiGenerationStatus.Processing)]
    [InlineData("ConvertToFlashcardsStatusRepairing", AiGenerationStatus.Repairing)]
    [InlineData("ConvertToFlashcardsStatusFinalizing", AiGenerationStatus.Finalizing)]
    [InlineData("ConvertToMindMapStatusProcessing", AiGenerationStatus.Processing)]
    [InlineData("ConvertToTestStatusRepairing", AiGenerationStatus.Repairing)]
    [InlineData("SomeUnknownStatus", AiGenerationStatus.Processing)]
    public void FromFlashcardProgress_MapsStatusKeysCorrectly(string statusKey, AiGenerationStatus expected)
    {
        var flashcardProgress = new BundledModelHostService.FlashcardProgress(statusKey);

        var result = AiGenerationProgressAdapter.FromFlashcardProgress(flashcardProgress);

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public void FromFlashcardProgress_PreservesChunkInfo()
    {
        var flashcardProgress = new BundledModelHostService.FlashcardProgress(
            "ConvertToFlashcardsStatusProcessing",
            ChunkIndex: 3,
            ChunkCount: 5);

        var result = AiGenerationProgressAdapter.FromFlashcardProgress(flashcardProgress);

        Assert.Equal(3, result.ChunkIndex);
        Assert.Equal(5, result.ChunkCount);
    }

    [Fact]
    public void FromFlashcardProgress_PreservesPercentAndChars()
    {
        var flashcardProgress = new BundledModelHostService.FlashcardProgress(
            "ConvertToFlashcardsStatusProcessing",
            Percent: 75,
            GeneratedChars: 1234);

        var result = AiGenerationProgressAdapter.FromFlashcardProgress(flashcardProgress);

        Assert.Equal(75, result.Percent);
        Assert.Equal(1234, result.GeneratedChars);
    }
}
