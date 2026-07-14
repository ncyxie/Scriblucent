using Scriblucent.Animations;
using Scriblucent.Localization;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using Scriblucent.Controls;

namespace Scriblucent.Views
{
    public partial class QuizStartOptionsDialog : Window
    {
        private const int MaxCustomTimeLimitMinutes = 1440;

        private readonly OverlayDialogAnimator _overlayAnimator;
        private bool _pendingDialogResult;
        private Window? _ownerWindow;

        public int TimeLimitSeconds { get; private set; }
        public int PassingScorePercent { get; private set; }

        public QuizStartOptionsDialog(int? initialTimeLimitSeconds, int initialPassingScorePercent)
        {
            InitializeComponent();
            _overlayAnimator = ((OverlayDialogShell)Content).BindAnimator(this);
            Scriblucent.Services.WindowThemeService.Register(this);
            Loaded += QuizStartOptionsDialog_Loaded;
            Closed += QuizStartOptionsDialog_Closed;

            SelectTimeLimit(initialTimeLimitSeconds.GetValueOrDefault());
            PassingScorePercent = Math.Clamp(initialPassingScorePercent, 0, 100);
            PassingScoreTextBox.Text = PassingScorePercent.ToString(CultureInfo.InvariantCulture);
            PassingScoreTextBox.SelectAll();
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

        private void AttachOwnerHandlers()
        {
            if (Owner is null)
                return;

            if (ReferenceEquals(_ownerWindow, Owner))
                return;

            DetachOwnerHandlers();

            _ownerWindow = Owner;
            _ownerWindow.LocationChanged += OwnerWindow_BoundsChanged;
            _ownerWindow.SizeChanged += OwnerWindow_BoundsChanged;
            _ownerWindow.StateChanged += OwnerWindow_BoundsChanged;
        }

        private void DetachOwnerHandlers()
        {
            if (_ownerWindow is null)
                return;

            _ownerWindow.LocationChanged -= OwnerWindow_BoundsChanged;
            _ownerWindow.SizeChanged -= OwnerWindow_BoundsChanged;
            _ownerWindow.StateChanged -= OwnerWindow_BoundsChanged;
            _ownerWindow = null;
        }

        private void OwnerWindow_BoundsChanged(object? sender, EventArgs e)
        {
            ApplyOwnerBounds();
        }

        private void QuizStartOptionsDialog_Loaded(object sender, RoutedEventArgs e)
        {
            UiMotion.ConfigurePopupsForWindow(this);
            AttachOwnerHandlers();
            ApplyOwnerBounds();
            _overlayAnimator.BeginOpen(() =>
            {
                if (CustomTimePanel.Visibility == Visibility.Visible)
                {
                    CustomTimeTextBox.Focus();
                    CustomTimeTextBox.SelectAll();
                }
                else
                {
                    PassingScoreTextBox.Focus();
                }
            });
        }

        private void QuizStartOptionsDialog_Closed(object? sender, EventArgs e)
        {
            DetachOwnerHandlers();
        }

        private void SelectTimeLimit(int seconds)
        {
            var normalized = seconds switch
            {
                60 or 300 or 600 or 900 or 1800 => seconds,
                _ => 0
            };

            if (seconds > 0 && normalized == 0)
            {
                foreach (var item in TimeLimitComboBox.Items.OfType<ComboBoxItem>())
                {
                    if (string.Equals(item.Tag?.ToString(), "custom", StringComparison.Ordinal))
                    {
                        TimeLimitComboBox.SelectedItem = item;
                        CustomTimeTextBox.Text = Math.Clamp((int)Math.Ceiling(seconds / 60.0), 1, MaxCustomTimeLimitMinutes)
                            .ToString(CultureInfo.InvariantCulture);
                        return;
                    }
                }
            }

            foreach (var item in TimeLimitComboBox.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag?.ToString() == normalized.ToString(CultureInfo.InvariantCulture))
                {
                    TimeLimitComboBox.SelectedItem = item;
                    return;
                }
            }

            TimeLimitComboBox.SelectedIndex = 0;
        }

        private void TimeLimitComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var isCustom = TimeLimitComboBox.SelectedItem is ComboBoxItem item
                && string.Equals(item.Tag?.ToString(), "custom", StringComparison.Ordinal);

            CustomTimePanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            UiMotion.AnimateInlineReveal(TimeValidationText, false);

            if (isCustom && string.IsNullOrWhiteSpace(CustomTimeTextBox.Text))
                CustomTimeTextBox.Text = "20";
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryCommitValues())
                return;

            BeginCloseAnimation(true);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            BeginCloseAnimation(false);
        }

        private bool TryCommitValues()
        {
            if (!int.TryParse(PassingScoreTextBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var passingScore)
                || passingScore < 0
                || passingScore > 100)
            {
                UiMotion.AnimateInlineReveal(ValidationText, true);
                PassingScoreTextBox.Focus();
                PassingScoreTextBox.SelectAll();
                return false;
            }

            UiMotion.AnimateInlineReveal(ValidationText, false);
            PassingScorePercent = passingScore;

            if (TimeLimitComboBox.SelectedItem is ComboBoxItem { Tag: string tag }
                && string.Equals(tag, "custom", StringComparison.Ordinal))
            {
                if (!int.TryParse(CustomTimeTextBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var customMinutes)
                    || customMinutes <= 0
                    || customMinutes > MaxCustomTimeLimitMinutes)
                {
                    UiMotion.AnimateInlineReveal(TimeValidationText, true);
                    CustomTimeTextBox.Focus();
                    CustomTimeTextBox.SelectAll();
                    return false;
                }

                UiMotion.AnimateInlineReveal(TimeValidationText, false);
                TimeLimitSeconds = customMinutes * 60;
            }
            else if (TimeLimitComboBox.SelectedItem is ComboBoxItem { Tag: string presetTag }
                && int.TryParse(presetTag, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                TimeLimitSeconds = seconds;
            }
            else
            {
                TimeLimitSeconds = 0;
            }

            return true;
        }

        private void NumberTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = e.Text.Any(ch => !char.IsDigit(ch));
        }

        private void NumberTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space)
                e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.Key == Key.Escape)
            {
                BeginCloseAnimation(false);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                if (TryCommitValues())
                    BeginCloseAnimation(true);

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
