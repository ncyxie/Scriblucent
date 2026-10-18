namespace Scriblucent.Services;

/// <summary>View-scoped selection presentation; document commands stay with their owner.</summary>
public interface ICollectionSelectionScope
{
    bool IsActive { get; }
    bool CanSelect { get; }
    int Count { get; }
    int TotalCount { get; }
    int MatchingSelectedCount => Count;
    string StatusText { get; }
    event Action? Changed;
    bool Contains(Guid id);
    void Begin(Guid? initial = null);
    void Toggle(Guid id, bool range = false, bool additive = false);
    void SelectAll();
    void Clear();
    void ClearMatching() => Clear();
    void End();
}
