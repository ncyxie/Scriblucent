namespace Scriblucent.ViewModels;

/// <summary>Presentation state shared by rebuilt group view models, independent of document content.</summary>
public sealed class GroupDisclosureState : ViewModelBase
{
    private bool _isExpanded;
    private readonly Action<bool>? _changed;
    public GroupDisclosureState(bool isExpanded = true, Action<bool>? changed = null)
    {
        _isExpanded = isExpanded;
        _changed = changed;
    }
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (SetProperty(ref _isExpanded, value)) _changed?.Invoke(value); }
    }
}
