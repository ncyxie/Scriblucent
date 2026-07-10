using NoteCards.Animations;
using NoteCards.Localization;
using NoteCards.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace NoteCards.Views;

public partial class EditHistoryWindow : Window
{
    public NoteEditHistoryEntry? SelectedVersion { get; private set; }

    public EditHistoryWindow(IEnumerable<NoteEditHistoryEntry> versions)
    {
        InitializeComponent();
        NoteCards.Services.WindowThemeService.Register(this);

        var items = versions
            .OrderByDescending(v => v.Timestamp)
            .Select(v => new EditHistoryListItem(v))
            .ToList();

        HistoryListBox.ItemsSource = items;
        if (items.Count > 0)
            HistoryListBox.SelectedIndex = 0;

        WindowContentEnterHelper.AnimateOnLoaded(this);
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

        public EditHistoryListItem(NoteEditHistoryEntry version)
        {
            Version = version;
            TimestampText = version.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
            PreviewText = BuildPreview(version.Content, version.Images);
        }

        public NoteEditHistoryEntry Version { get; }
        public string TimestampText { get; }
        public string PreviewText { get; }

        private static string BuildPreview(string? encodedContent, IReadOnlyList<NoteImageAttachment>? images)
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
                rawText = new TextRange(flowDoc.ContentStart, flowDoc.ContentEnd).Text ?? string.Empty;
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

            normalized = ReplaceImageMarkers(normalized, images);
            return normalized.Length > PreviewMaxLength
                ? normalized[..PreviewMaxLength] + "..."
                : normalized;
        }

        private static string ReplaceImageMarkers(string text, IReadOnlyList<NoteImageAttachment>? images)
        {
            if (string.IsNullOrEmpty(text) || images == null || images.Count == 0)
                return text;

            var placeholder = LocalizationService.GetString("PicturePlaceholder");
            foreach (var image in images)
            {
                if (image.Id != Guid.Empty)
                    text = text.Replace($"[[NoteCardsImage:{image.Id:D}]]", placeholder, StringComparison.Ordinal);
            }

            return text;
        }
    }
}
