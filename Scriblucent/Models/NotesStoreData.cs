namespace Scriblucent.Models;

public class NotesStoreData
{
    public List<NoteDocument> Notes { get; set; } = new();

    public List<NoteGroupData> Groups { get; set; } = new();
}
