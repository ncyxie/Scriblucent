namespace Scriblucent.Models;

public sealed class FlashcardItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Question { get; init; } = string.Empty;
    public string Answer { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public int SetIndex { get; init; } = 1;
    public bool IsKnown { get; set; }
    public bool IsUnknown { get; set; }
    public Guid? LinkedNoteId { get; set; } = null;
}
