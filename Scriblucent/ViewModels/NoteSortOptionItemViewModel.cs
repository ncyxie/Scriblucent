namespace Scriblucent.ViewModels;

public sealed class NoteSortOptionItemViewModel : ViewModelBase
{
    private bool _isSelected;
    private readonly Action<string, bool> _selectionChanged;

    public NoteSortOptionItemViewModel(string key, string displayName, bool isSelected, Action<string, bool> selectionChanged)
    {
        Key = key;
        _isSelected = isSelected;
        _selectionChanged = selectionChanged;
        _displayName = displayName;
    }

    public string Key { get; }

    private string _displayName;
    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value))
                return;

            _selectionChanged(Key, value);
        }
    }
}
