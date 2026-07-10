namespace Scriblucent.ViewModels;

public sealed class TagFilterItemViewModel : ViewModelBase
{
    private bool _isSelected;
    private readonly Action<string, bool> _selectionChanged;

    public TagFilterItemViewModel(string tag, bool isSelected, Action<string, bool> selectionChanged)
    {
        Tag = tag;
        _isSelected = isSelected;
        _selectionChanged = selectionChanged;
    }

    public string Tag { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value))
                return;

            _selectionChanged(Tag, value);
        }
    }
}
