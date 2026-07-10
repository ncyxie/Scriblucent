namespace Scriblucent.Models;

public sealed class FlashcardSetGroupData
{
    public Guid GroupId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string BackgroundColor { get; set; } = "#F8FAFF";
    public int? SortOrder { get; set; }
}
