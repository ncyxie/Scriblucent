using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Scriblucent.Views;

public partial class InsertHyperlinkDialog : Window
{
    public readonly record struct NoteLinkOption(Guid Id, string Title);

    private const string InternalNoteScheme = "scriblucent-note";

    private readonly OverlayDialogAnimator _overlayAnimator;
    private readonly IReadOnlyList<NoteLinkOption> _noteOptions;
    private readonly ICollectionView _noteResultsView;

    public string LinkText { get; private set; } = string.Empty;
    public Uri? ResultUri { get; private set; }

    public InsertHyperlinkDialog(string initialDisplayText, IReadOnlyList<NoteLinkOption> noteOptions)
    {
        InitializeComponent();
        _overlayAnimator = ((OverlayDialogShell)Content).BindAnimator(this);
        Scriblucent.Services.WindowThemeService.Register(this);
        Loaded += InsertHyperlinkDialog_Loaded;

        _noteOptions = noteOptions;
        _noteResultsView = CollectionViewSource.GetDefaultView(_noteOptions);
        NoteResultsListBox.ItemsSource = _noteResultsView;
        DisplayTextBox.Text = initialDisplayText;

        if (_noteOptions.Count == 0)
            NoteModeRadioButton.IsEnabled = false;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        OverlayDialogBoundsHelper.Apply(this);
    }

    private void InsertHyperlinkDialog_Loaded(object sender, RoutedEventArgs e)
    {
        OverlayDialogBoundsHelper.Apply(this);
        UiMotion.ConfigurePopupsForWindow(this);
        _overlayAnimator.BeginOpen();
        UrlTextBox.Focus();
    }

    private void ModeRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        if (WebModePanel is null || NoteModePanel is null)
            return;

        var isWebMode = ReferenceEquals(sender, WebModeRadioButton);
        WebModePanel.Visibility = isWebMode ? Visibility.Visible : Visibility.Collapsed;
        NoteModePanel.Visibility = isWebMode ? Visibility.Collapsed : Visibility.Visible;
        HideValidationMessage();
    }

    private void NoteSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = NoteSearchTextBox.Text?.Trim() ?? string.Empty;
        _noteResultsView.Filter = query.Length == 0
            ? null
            : option => option is NoteLinkOption noteLink
                        && noteLink.Title.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void NoteResultsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NoteResultsListBox.SelectedItem is not NoteLinkOption option)
            return;

        if (string.IsNullOrWhiteSpace(DisplayTextBox.Text))
            DisplayTextBox.Text = option.Title;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => BeginCloseAnimation(false);

    private void Insert_Click(object sender, RoutedEventArgs e)
    {
        var isWebMode = WebModeRadioButton.IsChecked == true;
        Uri? uri;

        if (isWebMode)
        {
            var rawUrl = UrlTextBox.Text?.Trim() ?? string.Empty;
            if (!TryNormalizeWebUrl(rawUrl, out uri))
            {
                ShowValidationMessage(LocalizationService.GetString("InsertLinkInvalidUrl"));
                return;
            }
        }
        else
        {
            if (NoteResultsListBox.SelectedItem is not NoteLinkOption option)
            {
                ShowValidationMessage(LocalizationService.GetString("InsertLinkNoNoteSelected"));
                return;
            }

            uri = new Uri($"{InternalNoteScheme}://{option.Id:D}");
        }

        var displayText = DisplayTextBox.Text?.Trim();
        LinkText = string.IsNullOrEmpty(displayText) ? uri!.ToString() : displayText;
        ResultUri = uri;
        BeginCloseAnimation(true);
    }

    private static bool TryNormalizeWebUrl(string rawUrl, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(rawUrl))
            return false;

        // Let people type "example.com" without a scheme, same leniency Word's own Insert
        // Hyperlink dialog gives - default to https rather than rejecting it outright.
        var candidate = rawUrl.Contains("://", StringComparison.Ordinal) ? rawUrl : $"https://{rawUrl}";

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed))
            return false;

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeMailto)
            return false;

        uri = parsed;
        return true;
    }

    private void ShowValidationMessage(string message)
    {
        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
    }

    private void HideValidationMessage()
    {
        ValidationText.Visibility = Visibility.Collapsed;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
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
