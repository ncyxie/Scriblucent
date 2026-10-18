using Microsoft.Win32;
using Scriblucent.Animations;
using Scriblucent.Localization;
using Scriblucent.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using Scriblucent.Controls;

namespace Scriblucent.Views
{
    public partial class EditFlashcardDialog : Window
    {
        private readonly OverlayDialogAnimator _overlayAnimator;
        private bool _pendingDialogResult;
        private string? _questionImageFileName;
        private string? _questionAudioFileName;
        private string? _answerImageFileName;
        private string? _answerAudioFileName;
        private AudioClipPlayer? _audioPlayer;
        private string? _activePlaybackFileName;

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

        public string Hint
        {
            get => HintTextBox.Text?.Trim() ?? string.Empty;
            set => HintTextBox.Text = value?.Trim() ?? string.Empty;
        }

        public string? QuestionImageFileName
        {
            get => _questionImageFileName;
            set
            {
                _questionImageFileName = value;
                UpdateImagePanelState(QuestionAttachmentsHost, QuestionImageEmptyPanel, QuestionImageAttachedPanel, QuestionImagePreview, value);
            }
        }

        public string? QuestionAudioFileName
        {
            get => _questionAudioFileName;
            set
            {
                _questionAudioFileName = value;
                UpdateAudioPanelState(QuestionAttachmentsHost, QuestionAudioEmptyPanel, QuestionAudioAttachedPanel, value);
            }
        }

        public string? AnswerImageFileName
        {
            get => _answerImageFileName;
            set
            {
                _answerImageFileName = value;
                UpdateImagePanelState(AnswerAttachmentsHost, AnswerImageEmptyPanel, AnswerImageAttachedPanel, AnswerImagePreview, value);
            }
        }

        public string? AnswerAudioFileName
        {
            get => _answerAudioFileName;
            set
            {
                _answerAudioFileName = value;
                UpdateAudioPanelState(AnswerAttachmentsHost, AnswerAudioEmptyPanel, AnswerAudioAttachedPanel, value);
            }
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
            QuestionTextBox.TextChanged += (_, _) => { if (!string.IsNullOrWhiteSpace(Question)) QuestionValidation.Hide(); };
            AnswerTextBox.TextChanged += (_, _) => { if (!string.IsNullOrWhiteSpace(Answer)) AnswerValidation.Hide(); };
            Loaded += EditFlashcardDialog_Loaded;
            Closed += (_, _) => _audioPlayer?.Dispose();
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
            bool missingQuestion = string.IsNullOrWhiteSpace(Question);
            bool missingAnswer = string.IsNullOrWhiteSpace(Answer);
            QuestionValidation.IsError = missingQuestion;
            AnswerValidation.IsError = missingAnswer;
            if (missingQuestion || missingAnswer)
            {
                (missingQuestion ? QuestionTextBox : AnswerTextBox).Focus();
                return;
            }

            BeginCloseAnimation(true);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            BeginCloseAnimation(false);
        }

        private void QuestionAddImageButton_Click(object sender, RoutedEventArgs e) => AddImage(fileName => QuestionImageFileName = fileName);
        private void AnswerAddImageButton_Click(object sender, RoutedEventArgs e) => AddImage(fileName => AnswerImageFileName = fileName);
        private void QuestionRemoveImageButton_Click(object sender, RoutedEventArgs e) => QuestionImageFileName = null;
        private void AnswerRemoveImageButton_Click(object sender, RoutedEventArgs e) => AnswerImageFileName = null;

        private void QuestionAddAudioButton_Click(object sender, RoutedEventArgs e) => AddAudio(fileName => QuestionAudioFileName = fileName);
        private void AnswerAddAudioButton_Click(object sender, RoutedEventArgs e) => AddAudio(fileName => AnswerAudioFileName = fileName);
        private void QuestionRemoveAudioButton_Click(object sender, RoutedEventArgs e) => QuestionAudioFileName = null;
        private void AnswerRemoveAudioButton_Click(object sender, RoutedEventArgs e) => AnswerAudioFileName = null;

        private void QuestionPlayAudioButton_Click(object sender, RoutedEventArgs e) => TogglePlayPauseAudio(_questionAudioFileName);
        private void AnswerPlayAudioButton_Click(object sender, RoutedEventArgs e) => TogglePlayPauseAudio(_answerAudioFileName);

        private void QuestionRestartAudioButton_Click(object sender, RoutedEventArgs e) => RestartAudio(_questionAudioFileName);
        private void AnswerRestartAudioButton_Click(object sender, RoutedEventArgs e) => RestartAudio(_answerAudioFileName);

        private void AddImage(Action<string> onSaved)
        {
            var dialog = new OpenFileDialog
            {
                Filter = LocalizationService.GetString("ImageOpenFileDialogFilter")
            };

            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                onSaved(FlashcardMediaStorageService.SaveImage(dialog.FileName));
            }
            catch
            {
                ShowMediaAttachError();
            }
        }

        private void AddAudio(Action<string> onSaved)
        {
            var dialog = new OpenFileDialog
            {
                Filter = LocalizationService.GetString("AudioOpenFileDialogFilter")
            };

            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                onSaved(FlashcardMediaStorageService.SaveAudio(dialog.FileName));
            }
            catch
            {
                ShowMediaAttachError();
            }
        }

        private void ShowMediaAttachError()
        {
            ModernMessageBox.Show(
                LocalizationService.GetString("FlashcardMediaAttachError"),
                LocalizationService.GetString("EditFlashcard"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        private void TogglePlayPauseAudio(string? fileName)
        {
            if (!TryResolveExistingAudioPath(fileName, out var path))
                return;

            _activePlaybackFileName = fileName;
            _audioPlayer ??= CreateAudioPlayer();
            _audioPlayer.Volume = ReadFlashcardAudioVolume();
            _audioPlayer.TogglePlayPause(path);
        }

        private void RestartAudio(string? fileName)
        {
            if (!TryResolveExistingAudioPath(fileName, out var path))
                return;

            _activePlaybackFileName = fileName;
            _audioPlayer ??= CreateAudioPlayer();
            _audioPlayer.Volume = ReadFlashcardAudioVolume();
            _audioPlayer.Restart(path);
        }

        private static float ReadFlashcardAudioVolume()
            => AppSettingsService.Load().FlashcardAudioVolumePercent / 100f;

        private bool TryResolveExistingAudioPath(string? fileName, out string path)
        {
            path = FlashcardMediaStorageService.ResolvePath(fileName) ?? string.Empty;
            if (path.Length == 0)
                return false;

            if (!System.IO.File.Exists(path))
            {
                ShowAudioPlaybackError("File not found on disk.");
                return false;
            }

            return true;
        }

        private AudioClipPlayer CreateAudioPlayer()
        {
            var player = new AudioClipPlayer();
            player.PlaybackFailed += (_, ex) =>
            {
                CrashLog.Append("FlashcardAudioPlayback", ex);
                ShowAudioPlaybackError(ex.Message);
            };
            player.PlaybackStateChanged += (_, _) => UpdatePlayPauseButtons();
            return player;
        }

        private void UpdatePlayPauseButtons()
        {
            var isQuestionPlaying = (_audioPlayer?.IsPlaying ?? false) && _activePlaybackFileName == _questionAudioFileName;
            var isAnswerPlaying = (_audioPlayer?.IsPlaying ?? false) && _activePlaybackFileName == _answerAudioFileName;

            QuestionPlayButton.Visibility = isQuestionPlaying ? Visibility.Collapsed : Visibility.Visible;
            QuestionPauseButton.Visibility = isQuestionPlaying ? Visibility.Visible : Visibility.Collapsed;
            AnswerPlayButton.Visibility = isAnswerPlaying ? Visibility.Collapsed : Visibility.Visible;
            AnswerPauseButton.Visibility = isAnswerPlaying ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowAudioPlaybackError(string reason)
        {
            ModernMessageBox.Show(
                string.Format(LocalizationService.GetString("FlashcardAudioPlaybackError"), reason),
                LocalizationService.GetString("FlashcardPlayAudio"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        private static void UpdateImagePanelState(LayoutRevealHost row, StackPanel emptyPanel, StackPanel attachedPanel, Image previewImage, string? fileName)
        {
            var hasImage = !string.IsNullOrWhiteSpace(fileName);
            var source = hasImage ? FlashcardMediaStorageService.LoadImage(fileName, 144) : null;
            SwapAttachmentPanels(row, emptyPanel, attachedPanel, hasImage, () => previewImage.Source = source);
        }

        private static void UpdateAudioPanelState(LayoutRevealHost row, StackPanel emptyPanel, StackPanel attachedPanel, string? fileName)
            => SwapAttachmentPanels(row, emptyPanel, attachedPanel, !string.IsNullOrWhiteSpace(fileName));

        private static void SwapAttachmentPanels(LayoutRevealHost row, StackPanel emptyPanel, StackPanel attachedPanel,
            bool attached, Action? updatePreview = null)
        {
            bool changed = (attachedPanel.Visibility == Visibility.Visible) != attached;
            UiMotion.Stop(emptyPanel);
            UiMotion.Stop(attachedPanel);
            var incoming = attached ? attachedPanel : emptyPanel;
            row.ChangeContent(() =>
            {
                updatePreview?.Invoke();
                emptyPanel.Visibility = attached ? Visibility.Collapsed : Visibility.Visible;
                attachedPanel.Visibility = attached ? Visibility.Visible : Visibility.Collapsed;
                incoming.Opacity = changed && row.IsLoaded ? 0 : 1;
            });
            if (changed) UiMotion.Fade(incoming, 1, MotionTokens.InteractionIn, UiMotion.InOutSine);
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
