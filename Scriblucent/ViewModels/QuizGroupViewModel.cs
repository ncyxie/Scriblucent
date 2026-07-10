using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace Scriblucent.ViewModels;

public sealed class QuizGroupViewModel : ViewModelBase
{
    private string _name;
    private Brush _backgroundBrush;

    public QuizGroupViewModel(Guid groupId, string name, string backgroundColor, IEnumerable<QuizViewModel> quizzes)
    {
        GroupId = groupId;
        _name = name;
        BackgroundColor = backgroundColor;
        _backgroundBrush = CreateBrush(backgroundColor);
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

    public ObservableCollection<QuizViewModel> Quizzes { get; }

    public void SetBackground(string backgroundColor)
    {
        BackgroundColor = backgroundColor;
        BackgroundBrush = CreateBrush(backgroundColor);
    }

    private static Brush CreateBrush(string backgroundColor)
    {
        if (ColorConverter.ConvertFromString(backgroundColor) is Color color)
            return new SolidColorBrush(color);

        if (Application.Current.Resources.Contains("CardBackground"))
            return (Brush)Application.Current.Resources["CardBackground"];

        return Brushes.White;
    }
}
