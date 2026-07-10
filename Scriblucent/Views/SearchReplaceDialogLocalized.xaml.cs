using Scriblucent.Animations;
using Scriblucent.Localization;
using System;
using System.Windows;
using System.Windows.Input;

namespace Scriblucent.Views
{
    public partial class SearchReplaceDialogLocalized : Window
    {
        private readonly NoteEditorWindow _editor;
        private readonly OverlayDialogAnimator _overlayAnimator;

        public SearchReplaceDialogLocalized(
            NoteEditorWindow editor,
            string? initialSearch = null,
            string? initialReplace = null,
            bool matchCase = false,
            bool wholeWord = false,
            bool wrapAround = true,
            bool focusReplace = false)
        {
            _editor = editor;

            InitializeComponent();
            _overlayAnimator = OverlayDialogAnimator.Bind(this, OverlayRoot, OverlayDim, PanelMotionHost, BackdropSnapshot, ScrimHost);
            Scriblucent.Services.WindowThemeService.Register(this);
            Loaded += SearchReplaceDialogLocalized_Loaded;

            SearchBox.Text = initialSearch ?? string.Empty;
            ReplaceBox.Text = initialReplace ?? string.Empty;
            MatchCaseBox.IsChecked = matchCase;
            WholeWordBox.IsChecked = wholeWord;
            WrapAroundBox.IsChecked = wrapAround;

            UpdateButtonState();
            UpdateStatus();

            _focusReplaceOnOpen = focusReplace;
        }

        private readonly bool _focusReplaceOnOpen;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            OverlayDialogBoundsHelper.Apply(this);
        }

        private void SearchReplaceDialogLocalized_Loaded(object? sender, RoutedEventArgs e)
        {
            OverlayDialogBoundsHelper.Apply(this);
            UiMotion.ConfigurePopupsForWindow(this);
            _overlayAnimator.BeginOpen(() =>
            {
                if (_focusReplaceOnOpen)
                {
                    ReplaceBox.SelectAll();
                    ReplaceBox.Focus();
                }
                else
                {
                    SearchBox.SelectAll();
                    SearchBox.Focus();
                }
            });
        }

        public string SearchText => SearchBox.Text ?? string.Empty;

        public string ReplacementText => ReplaceBox.Text ?? string.Empty;

        public bool MatchCase => MatchCaseBox.IsChecked == true;

        public bool WholeWord => WholeWordBox.IsChecked == true;

        public bool WrapAround => WrapAroundBox.IsChecked == true;

        private void FindPreviousBtn_Click(object sender, RoutedEventArgs e)
        {
            var result = _editor.PerformFindPrevious(SearchText, MatchCase, WholeWord, WrapAround);
            UpdateStatus(result);
        }

        private void FindNextBtn_Click(object sender, RoutedEventArgs e)
        {
            var result = _editor.PerformFindNext(SearchText, MatchCase, WholeWord, WrapAround);
            UpdateStatus(result);
        }

        private void ReplaceNextBtn_Click(object sender, RoutedEventArgs e)
        {
            var result = _editor.PerformReplaceNext(SearchText, ReplacementText, MatchCase, WholeWord, WrapAround);
            UpdateStatus(result);
        }

        private void ReplaceAllBtn_Click(object sender, RoutedEventArgs e)
        {
            var result = _editor.PerformReplaceAll(SearchText, ReplacementText, MatchCase, WholeWord);
            UpdateStatus(result);
        }

        private void SearchOptions_Changed(object sender, RoutedEventArgs e)
        {
            UpdateButtonState();
            UpdateStatus();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                BeginCloseAnimation();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                FindNextBtn_Click(sender, e);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                FindPreviousBtn_Click(sender, e);
                e.Handled = true;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            BeginCloseAnimation();
        }

        private void BeginCloseAnimation()
        {
            _overlayAnimator.BeginClose(() =>
            {
                DialogResult = true;
                Close();
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            _editor.ClearFindHighlights();
            base.OnClosed(e);
        }

        private void UpdateButtonState()
        {
            var hasSearch = !string.IsNullOrEmpty(SearchText);
            FindPreviousBtn.IsEnabled = hasSearch;
            FindNextBtn.IsEnabled = hasSearch;
            ReplaceNextBtn.IsEnabled = hasSearch;
            ReplaceAllBtn.IsEnabled = hasSearch;
        }

        private void UpdateStatus(NoteEditorWindow.FindReplaceResult? result = null)
        {
            if (string.IsNullOrEmpty(SearchText))
            {
                StatusText.Text = LocalizationService.GetString("FindReplaceEnterSearch");
                return;
            }

            if (result is null)
            {
                var count = _editor.CountFindMatches(SearchText, MatchCase, WholeWord);
                StatusText.Text = count == 0
                    ? LocalizationService.GetString("FindReplaceNoMatches")
                    : string.Format(LocalizationService.GetString("FindReplaceMatchesFormat"), count);
                return;
            }

            if (result.ReplacedCount > 0 && result.MatchCount == 0)
            {
                StatusText.Text = string.Format(
                    LocalizationService.GetString("FindReplaceReplacedCountFormat"),
                    result.ReplacedCount);
                return;
            }

            var prefix = result.ReplacedCount > 0
                ? string.Format(LocalizationService.GetString("FindReplaceReplacedCountFormat"), result.ReplacedCount) + " - "
                : string.Empty;

            if (result.MatchCount == 0)
            {
                StatusText.Text = prefix + LocalizationService.GetString("FindReplaceNoMatches");
                return;
            }

            if (!result.Found)
            {
                StatusText.Text = prefix + string.Format(
                    LocalizationService.GetString("FindReplaceMatchesFormat"),
                    result.MatchCount);
                return;
            }

            var key = result.Wrapped
                ? "FindReplaceWrappedResultFormat"
                : "FindReplaceResultFormat";

            StatusText.Text = prefix + string.Format(
                LocalizationService.GetString(key),
                result.ActiveIndex + 1,
                result.MatchCount);
        }
    }
}
