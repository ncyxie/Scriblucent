using Scriblucent.Models;
using Scriblucent.ViewModels;

namespace Scriblucent.Views;

public sealed class QuizLibraryItemViewModel : ViewModelBase
{
    private bool _isVisible = true;
    private bool _isSelected;

    public QuizLibraryItemViewModel(QuizViewModel model)
    {
        Model = model;
    }

    public QuizViewModel Model { get; }
    public QuizDocument Document => Model.Document;
    public string Title => Model.Title;
    public string QuestionCountText => Model.QuestionCountText;
    public bool HasTags => Model.HasTags;
    public string TagsDisplay => Model.TagsDisplay;

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
