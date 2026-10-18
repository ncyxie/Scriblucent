namespace Scriblucent.Models;

public class NotesStoreData
{
    public List<Guid> ManualOrder { get; set; } = new();

    public List<NoteDocument> Notes { get; set; } = new();

    public List<NoteDocument> TrashedNotes { get; set; } = new();

    public List<NoteGroupData> Groups { get; set; } = new();
}
