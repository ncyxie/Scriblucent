using System.Collections.ObjectModel;
using System.Windows.Media;

namespace Scriblucent.ViewModels;

public sealed class QuizGroupViewModel : ViewModelBase
{
    private string _name;
    private Brush _backgroundBrush;
    private Brush _foregroundBrush;

    public QuizGroupViewModel(Guid groupId, string name, string backgroundColor, IEnumerable<QuizViewModel> quizzes)
    {
        GroupId = groupId;
        _name = name;
        BackgroundColor = backgroundColor;
        _backgroundBrush = GroupBrushFactory.Create(backgroundColor);
        _foregroundBrush = GroupBrushFactory.CreateForeground(backgroundColor);
        Quizzes = new ObservableCollection<QuizViewModel>(quizzes);
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

    public ObservableCollection<QuizViewModel> Quizzes { get; }

    public void SetBackground(string backgroundColor)
    {
        BackgroundColor = backgroundColor;
        BackgroundBrush = GroupBrushFactory.Create(backgroundColor);
        ForegroundBrush = GroupBrushFactory.CreateForeground(backgroundColor);
    }
}
