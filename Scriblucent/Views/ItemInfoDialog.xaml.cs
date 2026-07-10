using Scriblucent.Animations;
using Scriblucent.Localization;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Scriblucent.Views
{
    public partial class ItemInfoDialog : Window
    {
        private readonly OverlayDialogAnimator _overlayAnimator;
        private Window? _ownerWindow;

        public string TitleText { get; }
        public string AdvancedHeaderText { get; }
        public string OkText { get; }
        public IReadOnlyList<InfoRow> PrimaryRows { get; }
        public IReadOnlyList<InfoRow> AdvancedRows { get; }

        public ItemInfoDialog(
            string title,
            IEnumerable<(string Label, string Value)> primaryRows,
            IEnumerable<(string Label, string Value)> advancedRows)
        {
            TitleText = title;
            AdvancedHeaderText = LocalizationService.GetString("InfoAdvanced");
            OkText = LocalizationService.GetString("Ok");
            PrimaryRows = primaryRows.Select(row => new InfoRow(row.Label, row.Value)).ToList();
            AdvancedRows = advancedRows.Select(row => new InfoRow(row.Label, row.Value)).ToList();

            InitializeComponent();
            _overlayAnimator = OverlayDialogAnimator.Bind(this, OverlayRoot, OverlayDim, PanelMotionHost, BackdropSnapshot, ScrimHost);
            Scriblucent.Services.WindowThemeService.Register(this);
            DataContext = this;
            Loaded += ItemInfoDialog_Loaded;
            Closed += ItemInfoDialog_Closed;
        }

        public sealed record InfoRow(string Label, string Value);

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

        private void ItemInfoDialog_Loaded(object sender, RoutedEventArgs e)
        {
            AttachOwnerHandlers();
            ApplyOwnerBounds();
            UiMotion.ConfigurePopupsForWindow(this);
            _overlayAnimator.BeginOpen();
        }

        private void ItemInfoDialog_Closed(object? sender, EventArgs e)
        {
            DetachOwnerHandlers();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            BeginCloseAnimation();
        }

        private void TrimmedTextBlock_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateTrimmedTextBlockToolTip(sender as TextBlock);
        }

        private void TrimmedTextBlock_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateTrimmedTextBlockToolTip(sender as TextBlock);
        }

        protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                BeginCloseAnimation();
                e.Handled = true;
            }
        }

        private void BeginCloseAnimation()
        {
            _overlayAnimator.BeginClose(() =>
            {
                DialogResult = true;
                Close();
            });
        }

        private static void UpdateTrimmedTextBlockToolTip(TextBlock? textBlock)
        {
            if (textBlock is null)
                return;

            textBlock.ToolTip = IsTextTrimmed(textBlock) ? textBlock.Text : null;
        }

        private static bool IsTextTrimmed(TextBlock textBlock)
        {
            if (string.IsNullOrEmpty(textBlock.Text) || textBlock.ActualWidth <= 0)
                return false;

            var typeface = new Typeface(
                textBlock.FontFamily,
                textBlock.FontStyle,
                textBlock.FontWeight,
                textBlock.FontStretch);
            var dpi = VisualTreeHelper.GetDpi(textBlock);
            var formatted = new FormattedText(
                textBlock.Text,
                CultureInfo.CurrentUICulture,
                textBlock.FlowDirection,
                typeface,
                textBlock.FontSize,
                Brushes.Black,
                dpi.PixelsPerDip);

            return formatted.WidthIncludingTrailingWhitespace > textBlock.ActualWidth + 0.5;
        }
    }
}
