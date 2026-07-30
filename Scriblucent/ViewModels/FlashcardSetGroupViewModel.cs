using System.Collections.ObjectModel;
using System.Windows.Media;

namespace Scriblucent.ViewModels;

public sealed class FlashcardSetGroupViewModel : ViewModelBase
{
    private string _name;
    private Brush _backgroundBrush;

    public FlashcardSetGroupViewModel(Guid groupId, string name, string backgroundColor, IEnumerable<FlashcardSetViewModel> sets)
    {
        GroupId = groupId;
        _name = name;
        BackgroundColor = backgroundColor;
        _backgroundBrush = GroupBrushFactory.Create(backgroundColor);
        Sets = new ObservableCollection<FlashcardSetViewModel>(sets);
    }

    public Guid GroupId { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string BackgroundColor { get; private set; }

    public Brush BackgroundBrush
    {
        get => _backgroundBrush;
        private set => SetProperty(ref _backgroundBrush, value);
    }

    public ObservableCollection<FlashcardSetViewModel> Sets { get; }

    public void SetBackground(string backgroundColor)
    {
        BackgroundColor = backgroundColor;
        BackgroundBrush = GroupBrushFactory.Create(backgroundColor);
    }
}
