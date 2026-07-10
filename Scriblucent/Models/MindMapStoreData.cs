namespace Scriblucent.Models;

public class MindMapStoreData
{
    public List<MindMapDocument> Maps { get; set; } = new();
    public List<MindMapGroupData> Groups { get; set; } = new();
}