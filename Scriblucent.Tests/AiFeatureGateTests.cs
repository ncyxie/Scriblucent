using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;

namespace Scriblucent.Tests;

public sealed class AiFeatureGateTests
{
    [Fact]
    public void EnableAiFeatures_DefaultsOn()
    {
        Assert.True(new AppSettings().EnableAiFeatures);
    }

    [Fact]
    public void ShowAiGeneratedChrome_HidesWhenGateOff()
    {
        using (AiFeatureGate.PushEnabled(false))
        {
            var flashcards = new FlashcardSetViewModel(new FlashcardSetDocument { AiModelDisplayName = "Qwen3.5-2B" });
            var quiz = new QuizViewModel(new QuizDocument { AiModelDisplayName = "Qwen3.5-2B" });
            var mindMap = new MindMapViewModel(new MindMapDocument { AiModelDisplayName = "Qwen3.5-2B" });

            Assert.True(flashcards.IsAiGenerated);
            Assert.False(flashcards.ShowAiGeneratedChrome);
            Assert.True(quiz.IsAiGenerated);
            Assert.False(quiz.ShowAiGeneratedChrome);
            Assert.True(mindMap.IsAiGenerated);
            Assert.False(mindMap.ShowAiGeneratedChrome);
        }
    }

    [Fact]
    public void ShowAiGeneratedChrome_ShowsWhenGateOn()
    {
        using (AiFeatureGate.PushEnabled(true))
        {
            var flashcards = new FlashcardSetViewModel(new FlashcardSetDocument { AiModelDisplayName = "Qwen3.5-2B" });
            Assert.True(flashcards.ShowAiGeneratedChrome);
        }
    }

    [Fact]
    public void ShowAiGeneratedChrome_StaysOffWhenNotGenerated()
    {
        using (AiFeatureGate.PushEnabled(true))
        {
            var flashcards = new FlashcardSetViewModel(new FlashcardSetDocument());
            Assert.False(flashcards.IsAiGenerated);
            Assert.False(flashcards.ShowAiGeneratedChrome);
        }
    }

    [Fact]
    public async Task EnsureModelAvailable_ThrowsWhenDisabled()
    {
        using (AiFeatureGate.PushEnabled(false))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => BundledModelHostService.Instance.EnsureModelAvailableAsync("Qwen3.5-0.8B"));
            Assert.Equal(AiFeatureGate.DisabledMessage, ex.Message);
        }
    }
}
