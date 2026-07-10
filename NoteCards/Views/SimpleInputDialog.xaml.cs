using NoteCards.Animations;
using System.Windows;

namespace NoteCards.Views;

public partial class SimpleInputDialog : Window
{
    private readonly OverlayDialogAnimator _overlayAnimator;

    public SimpleInputDialog(string title, string prompt)
    {
        InitializeComponent();
        _overlayAnimator = OverlayDialogAnimator.Bind(this, OverlayRoot, OverlayDim, PanelMotionHost, BackdropSnapshot, ScrimHost);
        NoteCards.Services.WindowThemeService.Register(this);
        Title = title;
        PromptText.Text = prompt;
        Loaded += SimpleInputDialog_Loaded;
    }

    public SimpleInputDialog(string title, string prompt, string initialText)
        : this(title, prompt)
    {
        InputBox.Text = initialText ?? string.Empty;
        InputBox.SelectAll();
        InputBox.Focus();
    }

    public string InputText => InputBox.Text ?? string.Empty;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        OverlayDialogBoundsHelper.Apply(this);
    }

    private void SimpleInputDialog_Loaded(object sender, RoutedEventArgs e)
    {
        OverlayDialogBoundsHelper.Apply(this);
        UpdateLayout();
        UiMotion.ConfigurePopupsForWindow(this);
        _overlayAnimator.BeginOpen(() => InputBox.Focus());
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        BeginCloseAnimation(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        BeginCloseAnimation(false);
    }

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