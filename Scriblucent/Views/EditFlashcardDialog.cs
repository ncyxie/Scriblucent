using Scriblucent.Animations;
using Scriblucent.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

using Scriblucent.Controls;

namespace Scriblucent.Views
{
    public partial class EditFlashcardDialog : Window
    {
        private readonly OverlayDialogAnimator _overlayAnimator;
        private bool _pendingDialogResult;

        public string DialogTitle { get; }
        public string DialogHint { get; }
        public IReadOnlyList<string> CategoryOptions { get; }

        public string Question
        {
            get => QuestionTextBox.Text;
            set => QuestionTextBox.Text = value;
        }

        public string Answer
        {
            get => AnswerTextBox.Text;
            set => AnswerTextBox.Text = value;
        }

        public string Category
        {
            get => CategoryComboBox.Text?.Trim() ?? string.Empty;
            set => CategoryComboBox.Text = value?.Trim() ?? string.Empty;
        }

        public EditFlashcardDialog(bool isNew = false, IEnumerable<string>? categoryOptions = null)
        {
            DialogTitle = LocalizationService.GetString(isNew ? "AddFlashcard" : "EditFlashcard");
            DialogHint = LocalizationService.GetString(isNew ? "AddFlashcardDialogHint" : "EditFlashcardDialogHint");
            CategoryOptions = categoryOptions is null
                ? Array.Empty<string>()
                : categoryOptions
                    .Select(category => category.Trim())
                    .Where(category => !string.IsNullOrWhiteSpace(category))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(category => category, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            InitializeComponent();
            _overlayAnimator = ((OverlayDialogShell)Content).BindAnimator(this);
            Scriblucent.Services.WindowThemeService.Register(this);
            DataContext = this;
            Loaded += EditFlashcardDialog_Loaded;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplyOwnerBounds();
        }

        private void ApplyOwnerBounds()
        {
            OverlayDialogBoundsHelper.Apply(this);
        }

        private void EditFlashcardDialog_Loaded(object sender, RoutedEventArgs e)
        {
            UiMotion.ConfigurePopupsForWindow(this);
            ApplyOwnerBounds();
            _overlayAnimator.BeginOpen();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Question) || string.IsNullOrWhiteSpace(Answer))
            {
                ModernMessageBox.Show(
                    LocalizationService.GetString("FlashcardEditEmptyError"),
                    LocalizationService.GetString("EditFlashcard"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            BeginCloseAnimation(true);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
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

        private void BeginCloseAnimation(bool dialogResult)
        {
            _pendingDialogResult = dialogResult;
            _overlayAnimator.BeginClose(() =>
            {
                DialogResult = _pendingDialogResult;
                Close();
            });
        }
    }
}
