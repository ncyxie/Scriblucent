using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Scriblucent.Animations;
using Scriblucent.Localization;
using Scriblucent.Services;

namespace Scriblucent.Views;

public partial class SpellCheckWordsDialog : Window
{
    private readonly HashSet<string> _original, _words;
    private readonly bool _ignored;
    private readonly OverlayDialogAnimator _animator;
    private bool _ready, _closing, _allowClose;
    internal IReadOnlyList<string> Words => _words.OrderBy(w => w, StringComparer.CurrentCultureIgnoreCase).ToArray();

    internal SpellCheckWordsDialog(IEnumerable<string> words, bool ignored = false)
    {
        _original = new HashSet<string>(words, StringComparer.OrdinalIgnoreCase);
        _words = new HashSet<string>(_original, StringComparer.OrdinalIgnoreCase);
        _ignored = ignored;
        InitializeComponent();
        Title = HeadingText.Text = LocalizationService.GetString(ignored ? "SpellCheckIgnoredWords" : "SpellCheckCustomDictionary");
        DescriptionText.Text = LocalizationService.GetString(ignored ? "SpellCheckIgnoredWordsDescription" : "SpellCheckCustomWordsDescription");
        AddRow.Visibility = ignored ? Visibility.Collapsed : Visibility.Visible;
        ResetIgnoredButton.Visibility = ignored ? Visibility.Visible : Visibility.Collapsed;
        _animator = DialogShell.BindAnimator(this); WindowThemeService.Register(this);
        _ready = true; RefreshWords();
        Loaded += (_, _) =>
        {
            OverlayDialogBoundsHelper.Apply(this); UiMotion.ConfigurePopupsForWindow(this);
            _animator.BeginOpen(() => SearchBox.Focus());
        };
    }

    internal static void ManageCustomDictionary(Window owner)
    {
        var dialog = new SpellCheckWordsDialog(AppSettingsService.Load().SpellCheckCustomWords) { Owner = owner };
        if (dialog.ShowDialog() != true) return;
        var settings = AppSettingsService.Load(); settings.SpellCheckCustomWords = dialog.Words.ToList();
        AppSettingsService.Save(settings); NoteEditorWindow.RefreshOpenSpellCheckEditors();
    }

    private void RefreshWords()
    {
        var search = SearchBox.Text.Trim();
        var visible = Words.Where(w => w.Contains(search, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        WordsList.ItemsSource = visible;
        CountText.Text = string.Format(LocalizationService.GetString("SpellCheckWordCount"), visible.Length, _words.Count);
        EmptyText.Text = LocalizationService.GetString(search.Length > 0 ? "SpellCheckNoMatchingWords"
            : _ignored ? "SpellCheckNoIgnoredWords" : "SpellCheckNoCustomWords");
        EmptyText.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResetIgnoredButton.IsEnabled = _words.Count > 0;
        SaveButton.IsEnabled = !_original.SetEquals(_words);
        RemoveButton.IsEnabled = WordsList.SelectedItems.Count > 0;
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) { if (_ready) RefreshWords(); }
    private void NewWord_Changed(object sender, TextChangedEventArgs e)
    { if (!_ready) return; AddButton.IsEnabled = !string.IsNullOrWhiteSpace(NewWordBox.Text); Validation.Hide(); }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var word = NewWordBox.Text.Trim();
        if (!SpellCheckService.IsDictionaryWord(word)) { Validation.Show(LocalizationService.GetString("SpellCheckWordInvalid")); return; }
        if (!_words.Add(word)) { Validation.Show(LocalizationService.GetString("SpellCheckWordExists")); return; }
        NewWordBox.Clear(); SearchBox.Clear(); RefreshWords(); WordsList.SelectedItem = word; WordsList.ScrollIntoView(word); NewWordBox.Focus();
    }
    private void NewWord_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { Add_Click(sender, e); e.Handled = true; } }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    { if (_ready) RemoveButton.IsEnabled = WordsList.SelectedItems.Count > 0; }
    private void Remove_Click(object sender, RoutedEventArgs e)
    { foreach (var word in WordsList.SelectedItems.Cast<string>().ToArray()) _words.Remove(word); RefreshWords(); }
    private void ResetIgnored_Click(object sender, RoutedEventArgs e) { _words.Clear(); RefreshWords(); }
    private void Words_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { Remove_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { WordsList.SelectAll(); e.Handled = true; }
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    { base.OnPreviewKeyDown(e); if (e.Key == Key.Escape) { CloseDialog(false); e.Handled = true; } }
    protected override void OnSourceInitialized(EventArgs e) { base.OnSourceInitialized(e); OverlayDialogBoundsHelper.Apply(this); }
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e); if (_allowClose) return;
        e.Cancel = true; Dispatcher.BeginInvoke(() => { if (IsVisible) CloseDialog(false); });
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseDialog(false);
    private void Save_Click(object sender, RoutedEventArgs e) { if (SaveButton.IsEnabled) CloseDialog(true); }
    private void CloseDialog(bool accepted)
    {
        if (_closing) return; _closing = true; DialogShell.IsEnabled = false;
        _animator.BeginClose(() =>
        {
            _allowClose = true;
            if (System.Windows.Interop.ComponentDispatcher.IsThreadModal) DialogResult = accepted;
            Close();
        });
    }
}
