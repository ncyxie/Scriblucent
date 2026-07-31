using System.Collections.ObjectModel;
using System.Windows.Media;

namespace Scriblucent.ViewModels;

public class MindMapGroupViewModel : ViewModelBase
{
    private string _name;
    private Brush _backgroundBrush;
    private Brush _foregroundBrush;

    public MindMapGroupViewModel(Guid groupId, string name, string backgroundColor, IEnumerable<MindMapViewModel> maps)
    {
        GroupId = groupId;
        _name = name;
        BackgroundColor = backgroundColor;
        _backgroundBrush = GroupBrushFactory.Create(backgroundColor);
        _foregroundBrush = GroupBrushFactory.CreateForeground(backgroundColor);
        MindMaps = new ObservableCollection<MindMapViewModel>(maps);
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

    public Brush ForegroundBrush
    {
        get => _foregroundBrush;
        private set => SetProperty(ref _foregroundBrush, value);
    }

    public ObservableCollection<MindMapViewModel> MindMaps { get; }

    public void SetBackground(string backgroundColor)
    {
        BackgroundColor = backgroundColor;
        BackgroundBrush = GroupBrushFactory.Create(backgroundColor);
        ForegroundBrush = GroupBrushFactory.CreateForeground(backgroundColor);
    }
}
