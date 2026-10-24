namespace Scriblucent.Models;

internal interface IDashboardGroupMetadata
{
    Guid GroupId { get; }
    string Name { get; set; }
    int? SortOrder { get; }
    int NameVersion { get; set; }
}
