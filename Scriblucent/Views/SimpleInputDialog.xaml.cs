using Scriblucent.Animations;
using System.Windows;

using Scriblucent.Controls;

namespace Scriblucent.Views;

public partial class SimpleInputDialog : Window
{
    private readonly OverlayDialogAnimator _overlayAnimator;
    private readonly Func<string, bool>? _inputValidator;
    private readonly string? _validationMessage;
    private bool _isClosing;

    public SimpleInputDialog(string title, string prompt)
    {
        InitializeComponent();
        _overlayAnimator = ((OverlayDialogShell)Content).BindAnimator(this);
        Scriblucent.Services.WindowThemeService.Register(this);
        Title = title;
        PromptText.Text = prompt;
        Loaded += SimpleInputDialog_Loaded;
        InputBox.TextChanged += (_, _) => ValidateInput();
    }

    public SimpleInputDialog(string title, string prompt, string initialText,
        Func<string, bool>? inputValidator = null, string? validationMessage = null)
        : this(title, prompt)
    {
        _inputValidator = inputValidator;
        _validationMessage = validationMessage;
        InputBox.Text = initialText ?? string.Empty;
        ValidateInput();
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
        if (ValidateInput()) BeginCloseAnimation(true);
    }

    private bool ValidateInput()
    {
        bool valid = _inputValidator?.Invoke(InputText) ?? true;
        ConfirmButton.IsEnabled = valid;
        ValidationText.Text = valid ? string.Empty : _validationMessage ?? string.Empty;
        ValidationText.Visibility = !valid && !string.IsNullOrEmpty(_validationMessage)
            ? Visibility.Visible : Visibility.Collapsed;
        return valid;
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
        if (_isClosing) return;
        _isClosing = true;
        _overlayAnimator.BeginClose(() =>
        {
            DialogResult = accepted;
            Close();
        });
    }
}
