using NoteCards.Animations;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NoteCards.Views;

public partial class ColorPickerDialog : Window
{
    private readonly OverlayDialogAnimator _overlayAnimator;

    public Color? SelectedColor { get; private set; }

    public ColorPickerDialog()
    {
        InitializeComponent();
        _overlayAnimator = OverlayDialogAnimator.Bind(this, OverlayRoot, OverlayDim, PanelMotionHost, BackdropSnapshot, ScrimHost);
        NoteCards.Services.WindowThemeService.Register(this);
        Loaded += ColorPickerDialog_Loaded;
        BuildSwatches();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        OverlayDialogBoundsHelper.Apply(this);
    }

    private void ColorPickerDialog_Loaded(object sender, RoutedEventArgs e)
    {
        OverlayDialogBoundsHelper.Apply(this);
        UiMotion.ConfigurePopupsForWindow(this);
        _overlayAnimator.BeginOpen();
    }

    private void BuildSwatches()
    {
        var colors = new[]
        {
            Colors.White, Colors.Black,
            Color.FromRgb(239, 68, 68), Color.FromRgb(249, 115, 22),
            Color.FromRgb(245, 158, 11), Color.FromRgb(34, 197, 94),
            Color.FromRgb(59, 130, 246), Color.FromRgb(139, 92, 246),
            Color.FromRgb(236, 72, 153), Color.FromRgb(244, 63, 94),
            Color.FromRgb(209, 213, 219), Color.FromRgb(107, 114, 128)
        };

        foreach (var color in colors)
        {
            var button = new Button
            {
                Style = (Style)FindResource("ColorSwatchButtonStyle"),
                Background = new SolidColorBrush(color),
                Tag = color
            };

            button.Click += (_, _) =>
            {
                SelectedColor = color;
                BeginCloseAnimation(true);
            };

            ColorSwatchPanel.Children.Add(button);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => BeginCloseAnimation(false);

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            BeginCloseAnimation(false);
            e.Handled = true;
        }
    }

    private void BeginCloseAnimation(bool accepted)
    {
        _overlayAnimator.BeginClose(() =>
        {
            DialogResult = accepted;
            Close();
        });
    }
}