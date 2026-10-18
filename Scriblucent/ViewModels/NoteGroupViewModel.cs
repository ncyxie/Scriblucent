using System.Collections.ObjectModel;
using System.Windows.Media;

namespace Scriblucent.ViewModels;

public class NoteGroupViewModel : ViewModelBase
{
    private string _name;
    private Brush _backgroundBrush;
    private Brush _foregroundBrush;

    public NoteGroupViewModel(Guid groupId, string name, string backgroundColor, IEnumerable<NoteCardViewModel> notes, GroupDisclosureState? disclosure = null)
    {
        Disclosure = disclosure ?? new GroupDisclosureState();
        GroupId = groupId;
        _name = name;
        BackgroundColor = backgroundColor;
        _backgroundBrush = GroupBrushFactory.Create(backgroundColor);
        _foregroundBrush = GroupBrushFactory.CreateForeground(backgroundColor);
        Notes = new BulkObservableCollection<NoteCardViewModel>(notes);
    }

    public Guid GroupId { get; }
    public GroupDisclosureState Disclosure { get; }

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

    public ObservableCollection<NoteCardViewModel> Notes { get; }

    public void SetBackground(string backgroundColor)
    {
        BackgroundColor = backgroundColor;
        BackgroundBrush = GroupBrushFactory.Create(backgroundColor);
        ForegroundBrush = GroupBrushFactory.CreateForeground(backgroundColor);
    }
}
