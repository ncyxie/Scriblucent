namespace Scriblucent.Models;

public class MindMapStoreData
{
    public List<Guid> ManualOrder { get; set; } = new();

    public List<MindMapDocument> Maps { get; set; } = new();
    public List<MindMapDocument> TrashedMindMaps { get; set; } = new();
    public List<MindMapGroupData> Groups { get; set; } = new();
}