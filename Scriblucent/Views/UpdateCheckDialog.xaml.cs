using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Icons;
using Scriblucent.Localization;
using Scriblucent.Services;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Controls;
using System.Windows.Input;

using Scriblucent.Controls;

namespace Scriblucent.Views
{
    public partial class UpdateCheckDialog : Window
    {
        private readonly string? _releasesUrl;
        private readonly OverlayDialogAnimator _overlayAnimator;

        public string TitleText { get; }
        public string MessageText { get; }
        public AppIconKind IconKind { get; }
        public string CurrentVersionText { get; }
        public string LatestVersionText { get; }
        public string PublishedText { get; }
        public FlowDocument ReleaseNotesDocument { get; }
        public string CloseText { get; }
        public string OpenReleasesText { get; }
        public Visibility PublishedVisibility { get; }
        public Visibility ReleaseNotesVisibility { get; }
        public Visibility OpenReleasesVisibility { get; }

        public UpdateCheckDialog(GitHubUpdateResult result)
        {
            var latest = result.LatestRelease;
            var latestVersion = latest?.TagName ?? LocalizationService.GetString("UpdateNoReleaseVersion");

            TitleText = result.IsUpdateAvailable
                ? LocalizationService.GetString("UpdateAvailableTitle")
                : LocalizationService.GetString("AppUpdate");
            MessageText = BuildMessage(result);
            IconKind = result.IsUpdateAvailable ? AppIconKind.ArrowUp : AppIconKind.Checkmark;
            CurrentVersionText = string.Format(LocalizationService.GetString("UpdateCurrentVersionFormat"), result.CurrentVersion);
            LatestVersionText = string.Format(LocalizationService.GetString("UpdateLatestVersionFormat"), latestVersion);
            PublishedText = latest?.PublishedAt is null
                ? string.Empty
                : string.Format(LocalizationService.GetString("UpdatePublishedFormat"), latest.PublishedAt.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
            PublishedVisibility = string.IsNullOrWhiteSpace(PublishedText) ? Visibility.Collapsed : Visibility.Visible;
            ReleaseNotesDocument = ReleaseNotesDocumentBuilder.Build(latest?.Body);
            ReleaseNotesVisibility = ReleaseNotesDocument.Blocks.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            CloseText = LocalizationService.GetString("Close");
            OpenReleasesText = result.IsUpdateAvailable
                ? LocalizationService.GetString("OpenRelease")
                : LocalizationService.GetString("OpenReleases");
            OpenReleasesVisibility = Visibility.Visible;
            _releasesUrl = result.IsUpdateAvailable ? latest?.HtmlUrl : result.ReleasesUrl;

            InitializeComponent();
            _overlayAnimator = ((OverlayDialogShell)Content).BindAnimator(this);
            Scriblucent.Services.WindowThemeService.Register(this);
            DataContext = this;
            Loaded += UpdateCheckDialog_Loaded;
        }

        public UpdateCheckDialog(string title, string message, string? releasesUrl = null)
        {
            TitleText = title;
            MessageText = message;
            IconKind = AppIconKind.Warning;
            CurrentVersionText = string.Format(LocalizationService.GetString("UpdateCurrentVersionFormat"), AppVersionInfo.DisplayVersion);
            LatestVersionText = LocalizationService.GetString("UpdateLatestVersionUnavailable");
            PublishedText = string.Empty;
            ReleaseNotesDocument = ReleaseNotesDocumentBuilder.Build(null);
            CloseText = LocalizationService.GetString("Close");
            OpenReleasesText = LocalizationService.GetString("OpenReleases");
            PublishedVisibility = Visibility.Collapsed;
            ReleaseNotesVisibility = Visibility.Collapsed;
            OpenReleasesVisibility = string.IsNullOrWhiteSpace(releasesUrl) ? Visibility.Collapsed : Visibility.Visible;
            _releasesUrl = releasesUrl;

            InitializeComponent();
            _overlayAnimator = ((OverlayDialogShell)Content).BindAnimator(this);
            Scriblucent.Services.WindowThemeService.Register(this);
            DataContext = this;
            Loaded += UpdateCheckDialog_Loaded;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplyOwnerBounds();
        }

        private static string BuildMessage(GitHubUpdateResult result)
        {
            if (result.LatestRelease is null)
                return LocalizationService.GetString("UpdateNoReleasesMessage");

            if (!result.HasComparableVersions)
                return LocalizationService.GetString("UpdateVersionCompareUnavailableMessage");

            if (result.IsUpdateAvailable)
            {
                return string.Format(
                    LocalizationService.GetString("UpdateAvailableMessageFormat"),
                    result.LatestRelease.TagName);
            }

            return LocalizationService.GetString("LatestVersion");
        }

        private void ApplyOwnerBounds()
        {
            OverlayDialogBoundsHelper.Apply(this);
        }

        private void UpdateCheckDialog_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyOwnerBounds();
            UiMotion.ConfigurePopupsForWindow(this);
            _overlayAnimator.BeginOpen();
        }

        private void ReleaseNotesViewer_Loaded(object sender, RoutedEventArgs e)
        {
            // Like the note editor, this document view only draws its current viewport.
            // Commit each eased position instead of translating it between commits.
            if (ReleaseNotesViewer.Template?.FindName("PART_ContentHost", ReleaseNotesViewer) is ScrollViewer scroller)
                SmoothScrollCompositor.SetDirectOffsetMode(scroller, true);
        }

        private void ReleaseNotesViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ReleaseNotesViewer.Template?.FindName("PART_ContentHost", ReleaseNotesViewer) is ScrollViewer scroller)
                SmoothScrollBehavior.HandleMouseWheel(scroller, e);
        }

        private void OpenReleasesButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_releasesUrl))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _releasesUrl,
                        UseShellExecute = true
                    });
                }
                catch
                {
                }
            }

            BeginCloseAnimation();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            BeginCloseAnimation();
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
    }
}
