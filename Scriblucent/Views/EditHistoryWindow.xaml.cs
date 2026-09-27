using Scriblucent.Animations;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Scriblucent.Views;

public partial class EditHistoryWindow : Window
{
    public NoteEditHistoryEntry? SelectedVersion { get; private set; }

    public EditHistoryWindow(IEnumerable<NoteEditHistoryEntry> versions)
    {
        InitializeComponent();
        Scriblucent.Services.WindowThemeService.Register(this);

        var source = versions.ToArray();
        if (source.Length < 10_000)
        {
            BindVersions(BuildItems(source));
        }
        else
        {
            var loadStarted = false;
            Loaded += async (_, _) =>
            {
                if (loadStarted)
                    return;

                loadStarted = true;
                var items = await Task.Run(() => BuildItems(source));
                if (IsVisible)
                    BindVersions(items);
            };
        }

        WindowContentEnterHelper.AnimateOnLoaded(this);
    }

    private static List<EditHistoryListItem> BuildItems(NoteEditHistoryEntry[] versions)
        => versions.OrderByDescending(v => v.Timestamp)
            .Select(v => new EditHistoryListItem(v))
            .ToList();

    private void BindVersions(List<EditHistoryListItem> items)
    {
        HistoryListBox.ItemsSource = items;
        if (items.Count > 0)
            HistoryListBox.SelectedIndex = 0;
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryListBox.SelectedItem is not EditHistoryListItem item)
            return;

        SelectedVersion = item.Version;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void HistoryListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        RestoreButton_Click(sender, e);
    }

    private sealed class EditHistoryListItem
    {
        private const int PreviewMaxLength = 180;

        private string? _previewText;

        public EditHistoryListItem(NoteEditHistoryEntry version)
        {
            Version = version;
            TimestampText = version.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
        }

        public NoteEditHistoryEntry Version { get; }
        public string TimestampText { get; }

        // Decoded lazily: the binding only evaluates for realized containers, so with a
        // virtualized ListBox only visible rows pay the FlowDocument decode cost (once each).
        public string PreviewText => _previewText ??= BuildPreview(Version.Content);

        private static string BuildPreview(string? encodedContent)
        {
            if (string.IsNullOrWhiteSpace(encodedContent))
                return LocalizationService.GetString("EditHistoryEmptyPreview");

            string rawText;
            try
            {
                var bytes = Convert.FromBase64String(encodedContent);
                if (bytes.Length >= 5)
                {
                    var header = Encoding.ASCII.GetString(bytes, 0, Math.Min(5, bytes.Length));
                    if (header.StartsWith("{\\rtf", StringComparison.Ordinal))
                    {
                        // RTF is the legacy rich-text format.
                    }
                    else if (bytes.Length >= 2 && bytes[0] == 0x50 && bytes[1] == 0x4B)
                    {
                        // XamlPackage is the current rich-text format.
                    }
                    else
                    {
                        throw new FormatException();
                    }
                }

                var flowDoc = new FlowDocument();
                using var stream = new MemoryStream(bytes);
                var range = new TextRange(flowDoc.ContentStart, flowDoc.ContentEnd);
                range.Load(stream, bytes.Length >= 2 && bytes[0] == 0x50 && bytes[1] == 0x4B
                    ? DataFormats.XamlPackage
                    : DataFormats.Rtf);
                // In-line photo markers (object replacement chars) would read as "obj" - the
                // photo is a picture, not text.
                rawText = (new TextRange(flowDoc.ContentStart, flowDoc.ContentEnd).Text ?? string.Empty)
                    .Replace(FloatingImageCoordinator.InlineMarkerChar.ToString(), " ");
            }
            catch (FormatException)
            {
                rawText = encodedContent;
            }
            catch
            {
                return LocalizationService.GetString("EditHistoryUnreadablePreview");
            }

            var normalized = rawText.Replace("\r", " ").Replace("\n", " ").Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                return LocalizationService.GetString("EditHistoryEmptyPreview");

            if (normalized.Contains("[[ScriblucentImage:", StringComparison.Ordinal))
            {
                normalized = System.Text.RegularExpressions.Regex.Replace(
                    normalized,
                    @"\[\[ScriblucentImage:[^\]]+\]\]",
                    string.Empty).Trim();
            }

            if (string.IsNullOrWhiteSpace(normalized))
                return LocalizationService.GetString("EditHistoryEmptyPreview");

            return normalized.Length > PreviewMaxLength
                ? normalized[..PreviewMaxLength] + "..."
                : normalized;
        }
    }
}
