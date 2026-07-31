using Scriblucent.Localization;
using Scriblucent.Models;

namespace Scriblucent.ViewModels;

public sealed class MindMapViewModel : ViewModelBase
{
    private const int MaxPreviewItems = 7;
    private const int MaxHoverPreviewItems = 20;
    private bool _isSelectedInMassSelect;
    private int? _cachedNodeCount;
    private IReadOnlyList<string>? _cachedContentPreviewItems;
    private IReadOnlyList<string>? _cachedHoverContentPreviewItems;
    private string? _cachedTagsDisplay;
    private string? _cachedNextScheduleDisplay;

    public MindMapViewModel(MindMapDocument document)
    {
        Document = document;
    }

    public MindMapDocument Document { get; }

    public string Title => string.IsNullOrWhiteSpace(Document.Title)
        ? LocalizationService.GetString("MindMapUntitled")
        : Document.Title.Trim();

    public int NodeCount
    {
        get
        {
            if (_cachedNodeCount is { } cached)
                return cached;

            _cachedNodeCount = CountNodes(Document.Root);
            return _cachedNodeCount.Value;
        }
    }

    public string NodeCountText => string.Format(LocalizationService.GetString("MindMapNodeCountFormat"), NodeCount);

    public int BranchCount => Document.Root?.Children?.Count ?? 0;

    public string BranchCountText => string.Format(LocalizationService.GetString("MindMapBranchCountFormat"), BranchCount);

    public IReadOnlyList<string> ContentPreviewItems
    {
        get
        {
            if (_cachedContentPreviewItems is not null)
                return _cachedContentPreviewItems;

            _cachedContentPreviewItems = BuildContentPreviewItems(Document.Root, MaxPreviewItems);
            return _cachedContentPreviewItems;
        }
    }

    public IReadOnlyList<string> HoverContentPreviewItems
    {
        get
        {
            if (_cachedHoverContentPreviewItems is not null)
                return _cachedHoverContentPreviewItems;

            _cachedHoverContentPreviewItems = BuildContentPreviewItems(Document.Root, MaxHoverPreviewItems);
            return _cachedHoverContentPreviewItems;
        }
    }

    /// <summary>
    /// Pre-walks and caches node count + content previews eagerly so scrolling the dashboard
    /// never re-walks the mind map tree while cards realize.
    /// </summary>
    internal void WarmContentPreview()
    {
        _ = NodeCount;
        _ = ContentPreviewItems;
        _ = HoverContentPreviewItems;
    }

    public bool HasContentPreview => ContentPreviewItems.Count > 0;

    public bool HasHoverContentPreview => HoverContentPreviewItems.Count > 0;

    public bool HasTags => Document.Tags?.Any(tag => !string.IsNullOrWhiteSpace(tag)) == true;

    public string TagsDisplay => _cachedTagsDisplay ??= HasTags
        ? string.Join("   ", Document.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => $"#{tag.Trim()}"))
        : string.Empty;

    public bool IsPinned => Document.IsPinned;

    public bool IsSelectedInMassSelect
    {
        get => _isSelectedInMassSelect;
        set => SetProperty(ref _isSelectedInMassSelect, value);
    }

    public bool HasSchedule => Document.Schedules?.Any() == true;

    public string NextScheduleDisplay => _cachedNextScheduleDisplay ??= HasSchedule
        ? Document.Schedules
            .OrderBy(schedule => schedule.ScheduledAt)
            .First()
            .ScheduledAt.ToString("yyyy-MM-dd HH:mm")
        : string.Empty;

    public bool IsAiGenerated => !string.IsNullOrWhiteSpace(Document.AiModelDisplayName);

    public string AiGeneratedTooltip => IsAiGenerated
        ? string.Format(LocalizationService.GetString("MindMapGeneratedWithModel"), Document.AiModelDisplayName.Trim())
        : string.Empty;

    public void NotifyChanged()
    {
        _cachedNodeCount = null;
        _cachedContentPreviewItems = null;
        _cachedHoverContentPreviewItems = null;
        _cachedTagsDisplay = null;
        _cachedNextScheduleDisplay = null;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(NodeCount));
        OnPropertyChanged(nameof(NodeCountText));
        OnPropertyChanged(nameof(BranchCount));
        OnPropertyChanged(nameof(BranchCountText));
        OnPropertyChanged(nameof(ContentPreviewItems));
        OnPropertyChanged(nameof(HoverContentPreviewItems));
        OnPropertyChanged(nameof(HasContentPreview));
        OnPropertyChanged(nameof(HasHoverContentPreview));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(TagsDisplay));
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(HasSchedule));
        OnPropertyChanged(nameof(NextScheduleDisplay));
        OnPropertyChanged(nameof(IsAiGenerated));
        OnPropertyChanged(nameof(AiGeneratedTooltip));
    }

    private static int CountNodes(MindMapNode? node)
    {
        if (node is null)
            return 0;

        return 1 + node.Children.Sum(CountNodes);
    }

    private static IReadOnlyList<string> BuildContentPreviewItems(MindMapNode? root, int maxItems)
    {
        if (root is null || maxItems <= 0)
            return Array.Empty<string>();

        var result = new List<string>(maxItems);
        AppendPreviewItems(root, depth: 0, result, maxItems);
        return result;
    }

    private static void AppendPreviewItems(MindMapNode node, int depth, List<string> result, int maxItems)
    {
        if (result.Count >= maxItems)
            return;

        if (!string.IsNullOrWhiteSpace(node.Text))
        {
            var level = Math.Clamp(depth, 0, 3);
            var prefix = level == 0
                ? "• "
                : string.Concat(Enumerable.Repeat("› ", level));

            result.Add($"{prefix}{node.Text.Trim()}");
        }

        foreach (var child in node.Children)
        {
            if (result.Count >= maxItems)
                return;

            AppendPreviewItems(child, depth + 1, result, maxItems);
        }
    }

}
