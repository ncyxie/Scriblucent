namespace Scriblucent.Models;

public sealed class FlashcardSetStoreData
{
    public List<FlashcardSetDocument> Sets { get; set; } = new();
    public List<FlashcardSetDocument> TrashedFlashcardSets { get; set; } = new();
    public List<FlashcardSetGroupData> Groups { get; set; } = new();
}
