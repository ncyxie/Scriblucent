using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace Scriblucent.ViewModels;

public class MindMapGroupViewModel : ViewModelBase
{
    private string _name;
    private Brush _backgroundBrush;

    public MindMapGroupViewModel(Guid groupId, string name, string backgroundColor, IEnumerable<MindMapViewModel> maps)
    {
        GroupId = groupId;
        _name = name;
        BackgroundColor = backgroundColor;
        _backgroundBrush = CreateBrush(backgroundColor);
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

    public ObservableCollection<MindMapViewModel> MindMaps { get; }

    public void SetBackground(string backgroundColor)
    {
        BackgroundColor = backgroundColor;
        BackgroundBrush = CreateBrush(backgroundColor);
    }

    private static Brush CreateBrush(string backgroundColor)
    {
        if (ColorConverter.ConvertFromString(backgroundColor) is Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        if (Application.Current.Resources.Contains("CardBackground"))
            return (Brush)Application.Current.Resources["CardBackground"];

        return Brushes.White;
    }
}
