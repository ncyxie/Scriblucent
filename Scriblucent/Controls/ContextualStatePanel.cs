using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Icons;

namespace Scriblucent.Controls;

public enum StateIllustrationKind { Trash, EditHistory, StudyHistory, QuizHistory, Selection, Search, FlashcardSelection }

/// <summary>Compact native artwork and a delayed loading presentation; never delays the underlying data.</summary>
public sealed class ContextualStatePanel : StackPanel
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(ContextualStatePanel), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(ContextualStatePanel), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty IllustrationKindProperty = DependencyProperty.Register(nameof(IllustrationKind), typeof(StateIllustrationKind), typeof(ContextualStatePanel), new PropertyMetadata(StateIllustrationKind.Search, Changed));
    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.Register(nameof(IsLoading), typeof(bool), typeof(ContextualStatePanel), new PropertyMetadata(false, Changed));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public StateIllustrationKind IllustrationKind { get => (StateIllustrationKind)GetValue(IllustrationKindProperty); set => SetValue(IllustrationKindProperty, value); }
    public bool IsLoading { get => (bool)GetValue(IsLoadingProperty); set => SetValue(IsLoadingProperty, value); }
    private readonly Grid _art = new() { Width = 112, Height = 90, Margin = new(0, 0, 0, 12) };
    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _description = new() { FontSize = 12, Margin = new(0, 6, 0, 0), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 };
    private readonly AppIcon _icon = new() { Width = 28, Height = 28 };
    private readonly List<FrameworkElement> _decoration = new();
    private readonly AppIcon _sealIcon = new() { Width = 12, Height = 12, Foreground = Brushes.White };
    private readonly DispatcherTimer _loadingDelay;
    public ContextualStatePanel()
    {
        HorizontalAlignment = HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Center; Margin = new(16); MaxWidth = 380;
        _loadingDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _loadingDelay.Tick += (_, _) => { _loadingDelay.Stop(); if (IsLoaded && IsVisible && IsLoading) Present(); };
        var halo = new Ellipse { Width = 92, Height = 68, Opacity = .65, Margin = new(0, 18, 0, 0) };
        halo.SetResourceReference(Shape.FillProperty, "ControlHoverSurfaceWash"); _art.Children.Add(halo); _decoration.Add(halo);
        var rear = new Border { Width = 62, Height = 64, CornerRadius = new(10), RenderTransformOrigin = new(.5, .5), RenderTransform = new RotateTransform(-12), Margin = new(-24, -8, 0, 0), BorderThickness = new(1) };
        rear.SetResourceReference(Border.BackgroundProperty, "InputSurface"); rear.SetResourceReference(Border.BorderBrushProperty, "ControlHoverSurfaceEdge"); _art.Children.Add(rear); _decoration.Add(rear);
        var paper = new Grid { Width = 66, Height = 70, Margin = new(16, 0, 0, 0), RenderTransformOrigin = new(.5, .5), RenderTransform = new RotateTransform(5) };
        paper.Children.Add(new BakedElevationLayer { Kind = BakedElevationLayer.ElevationKind.ButtonCard, IsHitTestVisible = false, Opacity = .20 });
        var face = new Border { CornerRadius = new(11), BorderThickness = new(1) };
        face.SetResourceReference(Border.BackgroundProperty, "CardBackground"); face.SetResourceReference(Border.BorderBrushProperty, "ControlHoverSurfaceEdge");
        var marks = new StackPanel { Margin = new(12, 9, 12, 9) }; marks.Children.Add(_icon);
        foreach (var width in new[] { 36d, 25d })
        { var line = new Border { Height = 3, Width = width, Margin = new(0, 5, 0, 0), CornerRadius = new(1.5), HorizontalAlignment = HorizontalAlignment.Left }; line.SetResourceReference(Border.BackgroundProperty, "ControlHoverSurfaceEdge"); marks.Children.Add(line); }
        face.Child = marks; paper.Children.Add(face); _art.Children.Add(paper);
        var seal = new Border { Width = 22, Height = 22, CornerRadius = new(11), BorderThickness = new(2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(0, 0, 9, 7) };
        seal.SetResourceReference(Border.BackgroundProperty, "AccentPrimary"); seal.SetResourceReference(Border.BorderBrushProperty, "CardBackground");
        seal.Child = _sealIcon;
        _art.Children.Add(seal); _decoration.Add(seal);
        _icon.SetResourceReference(Control.ForegroundProperty, "AccentPrimary");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextColor"); _description.SetResourceReference(TextBlock.ForegroundProperty, "TextColorSecondary");
        Children.Add(_art); Children.Add(_title); Children.Add(_description);
        Loaded += (_, _) => Refresh(); IsVisibleChanged += (_, _) => { if (IsLoaded) Refresh(); };
        Unloaded += (_, _) => { _loadingDelay.Stop(); MotionPolicy.Changed -= PolicyChanged; Stop(); };
        Loaded += (_, _) => { MotionPolicy.Changed -= PolicyChanged; MotionPolicy.Changed += PolicyChanged; };
        _title.SetResourceReference(TextBlock.FontSizeProperty, "Type.SectionTitle");
        _description.SetResourceReference(TextBlock.FontSizeProperty, "Type.Control.Helper");
        _title.SetResourceReference(TextBlock.FontFamilyProperty, "FontFamily.UI");
        _description.SetResourceReference(TextBlock.FontFamilyProperty, "FontFamily.UI");
        UpdateText();
    }
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    { var panel = (ContextualStatePanel)target; panel.UpdateText(); if (e.Property == IsLoadingProperty && panel.IsLoaded) panel.Refresh(); }
    private void UpdateText()
    {
        _title.Text = Title; _description.Text = Description;
        _description.Visibility = string.IsNullOrWhiteSpace(Description) ? Visibility.Collapsed : Visibility.Visible;
        _icon.Kind = IllustrationKind switch { StateIllustrationKind.Trash => AppIconKind.Trash, StateIllustrationKind.StudyHistory => AppIconKind.Flashcards, StateIllustrationKind.QuizHistory => AppIconKind.Quizzes, StateIllustrationKind.Selection => AppIconKind.Quizzes, StateIllustrationKind.FlashcardSelection => AppIconKind.Flashcards, StateIllustrationKind.EditHistory => AppIconKind.Notes, _ => AppIconKind.Search };
        _sealIcon.Kind = IllustrationKind switch { StateIllustrationKind.Trash => AppIconKind.Undo,
            StateIllustrationKind.Selection or StateIllustrationKind.FlashcardSelection => AppIconKind.Plus, StateIllustrationKind.Search => AppIconKind.Search, _ => AppIconKind.History };
    }
    private void Refresh()
    {
        _loadingDelay.Stop(); Stop();
        if (!IsVisible) return;
        if (IsLoading) { foreach (UIElement child in Children) child.Opacity = 0; _loadingDelay.Start(); }
        else Present();
    }
    private void Present()
    {
        PolicyChanged();
        var move = UiMotion.ChannelTranslate(_art, MotionChannel.Presence); var scale = UiMotion.EnsureScale(_art); _art.RenderTransformOrigin = new(.5, .75);
        move.Y = MotionPolicy.Spatial ? 10 : 0; scale.ScaleX = scale.ScaleY = MotionPolicy.Spatial ? .92 : 1;
        _art.Opacity = MotionPolicy.Quality == MotionQuality.Off ? 1 : 0;
        SurfaceMotion.AnimateTracks(_art, MotionChannel.Presence,
            [new(_art, OpacityProperty, 1, Profile: new(UiMotion.OutSine, 120)), new(move, TranslateTransform.YProperty, 0, true),
             new(scale, ScaleTransform.ScaleXProperty, 1, true, 1.015, 1), new(scale, ScaleTransform.ScaleYProperty, 1, true, 1.015, 1)], 320, UiMotion.InOutSine);
        int delay = 40;
        foreach (var text in new[] { _title, _description })
        {
            text.Opacity = MotionPolicy.ExpressiveExtras ? .35 : 1;
            if (MotionPolicy.ExpressiveExtras) SurfaceMotion.Arrive(text, 180, startDelayMs: delay);
            delay += 30;
        }
    }
    private void PolicyChanged()
    { foreach (var item in _decoration) item.Visibility = MotionPolicy.Spatial ? Visibility.Visible : Visibility.Hidden; }
    private void Stop() { foreach (UIElement child in Children) SurfaceMotion.Stop(child); }
}
