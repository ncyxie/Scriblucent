namespace Scriblucent.Models;

public sealed class FlashcardStudySession
{
    public bool IsStudyMode { get; set; }
    public int CurrentSetIndex { get; set; } = 1;
    public Guid? CurrentCardId { get; set; }
    public List<Guid> History { get; set; } = new();
    public int HistoryPosition { get; set; } = -1;
}
