using Scriblucent.Localization;
using Scriblucent.Models;

namespace Scriblucent.Services;

// Editor drafts and dashboard quick additions start with the same document structure.
internal static class NewDocumentDefaults
{
    internal static FlashcardSetDocument Flashcards() => new() { Title = LocalizationService.GetString("FlashcardSetUntitled") };
    internal static QuizDocument Quiz() => new() { Title = LocalizationService.GetString("QuizUntitled") };
    internal static MindMapDocument MindMap() => new()
    {
        Title = LocalizationService.GetString("MindMapUntitled"),
        Root = new MindMapNode { Text = LocalizationService.GetString("NewMindMapCentralTopic"), NodeShape = "Rounded" }
    };
}
