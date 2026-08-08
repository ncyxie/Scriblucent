using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent;
using System.IO;
using System.Text;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;

namespace Scriblucent.ViewModels;

public class NoteCardViewModel : ViewModelBase
{
    private bool _isMenuVisible;
    private bool _isSelectedInMassSelect;
    private string? _cachedContentPreview;
    private string? _cachedTagsDisplay;
    private string? _cachedTagsSearchText;
    private readonly Action<NoteCardViewModel> _deleteAction;
    private readonly Action<NoteCardViewModel>? _removeFromGroupAction;
    private readonly Action<NoteCardViewModel>? _duplicateAction;
    private readonly Action<NoteCardViewModel>? _togglePinAction;

    public NoteCardViewModel(
        NoteDocument document,
        Action<NoteCardViewModel> deleteAction,
        Action<NoteCardViewModel>? removeFromGroupAction = null,
        Action<NoteCardViewModel>? duplicateAction = null,
        Action<NoteCardViewModel>? togglePinAction = null)
    {
        Document = document;
        _deleteAction = deleteAction;
        _removeFromGroupAction = removeFromGroupAction;
        _duplicateAction = duplicateAction;
        _togglePinAction = togglePinAction;
        DeleteCommand = new RelayCommand(DeleteNote);
        RemoveFromGroupCommand = new RelayCommand(RemoveFromGroup, () => IsGrouped);
        DuplicateCommand = new RelayCommand(DuplicateNote);
        TogglePinCommand = new RelayCommand(TogglePin);
    }

    public bool HasTags => Document.Tags?.Count > 0;

    public bool HasSchedule => Document.ScheduledAt.HasValue || (Document.Schedules?.Count ?? 0) > 0;

    public bool IsPinned => Document.IsPinned;

    public DateTime? DeletedAt => Document.DeletedAt;

    public string TagsDisplay
    {
        get
        {
            if (_cachedTagsDisplay is not null)
                return _cachedTagsDisplay;

            _cachedTagsDisplay = !HasTags
                ? string.Empty
                : string.Join("   ", Document.Tags
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .Select(tag => $"#{tag.Trim()}"));
            return _cachedTagsDisplay;
        }
    }

    public string TagsSearchText
    {
        get
        {
            if (_cachedTagsSearchText is not null)
                return _cachedTagsSearchText;

            _cachedTagsSearchText = !HasTags
                ? string.Empty
                : string.Join(" ", Document.Tags
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .Select(tag => tag.Trim()));
            return _cachedTagsSearchText;
        }
    }

    public NoteDocument Document { get; }

    public string Title => Document.Title;
    // Return a plain-text preview for the card. The stored Document.Content
    // may be plain text or Base64-encoded RTF (saved by the editor). If it's
    // RTF we decode and extract the text so the card shows readable content
    // instead of raw Base64/RTF markup.
    public string Content
    {
        get
        {
            if (_cachedContentPreview is not null)
                return _cachedContentPreview;

            _cachedContentPreview = BuildContentPreview();
            return _cachedContentPreview;
        }
    }

    /// <summary>True when the note contains any photo (floating or in-line, current or legacy),
    /// so the card can show a picture indicator instead of looking like an empty note.</summary>
    public bool HasPictures =>
        (Document.Images is { Count: > 0 })
        || (Document.Content?.Contains("[[ScriblucentImage:", StringComparison.Ordinal) ?? false);
    /// <summary>
    /// True once <see cref="Content"/> has decoded and cached its preview at least once. Lets
    /// layout-estimation code (<see cref="Controls.NoteCardItemSizeProvider"/>) avoid forcing that
    /// decode just to size a card during a bulk load, before <see cref="WarmContentPreview"/> has
    /// had a chance to run.
    /// </summary>
    internal bool IsContentPreviewCached => _cachedContentPreview is not null;

    /// <summary>
    /// Builds and caches the preview eagerly so scrolling the dashboard never pays the
    /// RTF decode on the UI thread while cards realize.
    /// </summary>
    internal void WarmContentPreview() => _ = Content;

    /// <summary>Cards render at most a few wrapped lines; formatting the full document text is wasted layout work.</summary>
    private const int ContentPreviewMaxLength = 600;

    private static string CapPreviewLength(string preview)
        => preview.Length <= ContentPreviewMaxLength ? preview : preview[..ContentPreviewMaxLength];

    private string BuildContentPreview()
    {
        var raw = Document.Content;
        if (string.IsNullOrEmpty(raw))
            return AppendFloatingPhotoSentinels(string.Empty);

        try
        {
            var bytes = Convert.FromBase64String(raw);

            var dataFormat = DataFormats.Rtf;
            if (bytes.Length >= 5)
            {
                var hdr = Encoding.ASCII.GetString(bytes, 0, Math.Min(5, bytes.Length));
                if (hdr.StartsWith("{\\rtf"))
                {
                    dataFormat = DataFormats.Rtf;
                }
                else if (bytes.Length >= 2 && bytes[0] == 0x50 && bytes[1] == 0x4B)
                {
                    dataFormat = DataFormats.XamlPackage;
                }
                else
                {
                    throw new FormatException();
                }
            }

            using var ms = new MemoryStream(bytes);
            var flowDoc = new FlowDocument();
            var tr = new TextRange(flowDoc.ContentStart, flowDoc.ContentEnd);
            tr.Load(ms, dataFormat);
            var preview = CapPreviewLength(StripLegacyImageMarkers(BuildOrderedContentPreview(flowDoc)));
            return AppendFloatingPhotoSentinels(preview);
        }
        catch (FormatException)
        {
            return CapPreviewLength(StripLegacyImageMarkers(raw));
        }
        catch (Exception ex)
        {
            Scriblucent.Services.CrashLog.Append("NoteCardViewModel.BuildContentPreview", ex);
            return CapPreviewLength(StripLegacyImageMarkers(raw));
        }
    }

    /// <summary>Floating photos have no text position. A sentinel trails the preview when the
    /// photo actually sits near the note text - a photo deep in the empty pad region below the
    /// text is far from the card's preview and would just add noise. A photo-only note (no
    /// previewable text) still signals its photo: the photo IS the content there.</summary>
    private string AppendFloatingPhotoSentinels(string preview)
    {
        if (Document.Images is null)
            return preview;

        var hasFloating = Document.Images.Any(image => image.WrapMode != NoteImageWrapMode.Inline);
        if (!hasFloating)
            return preview;

        if (string.IsNullOrWhiteSpace(preview))
            return FloatingImageCoordinator.InlineMarkerChar.ToString();

        var previewHeight = EstimatePreviewHeight(preview.Length);
        var hasNearPhoto = Document.Images.Any(image =>
            image.WrapMode != NoteImageWrapMode.Inline && image.Top <= previewHeight);

        return hasNearPhoto ? preview + FloatingImageCoordinator.InlineMarkerChar : preview;
    }

    /// <summary>Rough vertical extent of the card's preview text (the flow document is not laid
    /// out here, so the estimate stands in for the character-rect measurements).</summary>
    private static double EstimatePreviewHeight(int characterCount)
    {
        const double charactersPerLine = 95;
        const double lineHeightDip = 22;
        return Math.Ceiling(characterCount / charactersPerLine) * lineHeightDip;
    }

    public bool IsMenuVisible
    {
        get => _isMenuVisible;
        set => SetProperty(ref _isMenuVisible, value);
    }

    public bool IsSelectedInMassSelect
    {
        get => _isSelectedInMassSelect;
        set => SetProperty(ref _isSelectedInMassSelect, value);
    }

    /// <summary>No confirmation prompt - deleting a note moves it to Trash rather than destroying
    /// it, so there's nothing to lose by acting immediately. Permanently removing a note (from
    /// within Trash) is the irreversible step and confirms separately - see TrashPanel.</summary>
    private void DeleteNote() => _deleteAction(this);

    public bool IsGrouped => Document.GroupId.HasValue;

    public ICommand DeleteCommand { get; }

    public ICommand RemoveFromGroupCommand { get; }

    public ICommand DuplicateCommand { get; }

    public ICommand TogglePinCommand { get; }

    private void DuplicateNote() => _duplicateAction?.Invoke(this);

    public void NotifyContentChanged()
    {
        _cachedContentPreview = null;
        _cachedTagsDisplay = null;
        _cachedTagsSearchText = null;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Content));
        OnPropertyChanged(nameof(HasPictures));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(HasSchedule));
        OnPropertyChanged(nameof(TagsDisplay));
        OnPropertyChanged(nameof(TagsSearchText));
        OnPropertyChanged(nameof(IsGrouped));
        CommandManager.InvalidateRequerySuggested();
    }

    public void NotifyGroupChanged()
    {
        OnPropertyChanged(nameof(IsGrouped));
        CommandManager.InvalidateRequerySuggested();
    }

    public void NotifyPinChanged()
    {
        OnPropertyChanged(nameof(IsPinned));
        CommandManager.InvalidateRequerySuggested();
    }

    private void RemoveFromGroup()
    {
        _removeFromGroupAction?.Invoke(this);
    }

    private void TogglePin()
    {
        _togglePinAction?.Invoke(this);
    }

    private static string BuildOrderedContentPreview(FlowDocument document)
    {
        var preview = new StringBuilder();
        var navigator = document.ContentStart;

        while (navigator != null && navigator.CompareTo(document.ContentEnd) < 0)
        {
            var context = navigator.GetPointerContext(LogicalDirection.Forward);
            if (context == TextPointerContext.Text)
            {
                // In-line photo markers (\uFFFC) stay in the preview as position sentinels -
                // the card renders a picture icon where each one sits (the photo's place in
                // the text), instead of an "obj" glyph or a leading icon.
                preview.Append(navigator.GetTextInRun(LogicalDirection.Forward));
            }
            else if (context == TextPointerContext.ElementEnd
                && navigator.Parent is Paragraph or ListItem or BlockUIContainer)
            {
                AppendSeparator(preview);
            }

            navigator = navigator.GetNextContextPosition(LogicalDirection.Forward);
        }

        return NormalizePreviewWhitespace(preview.ToString());
    }

    private static string StripLegacyImageMarkers(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("[[ScriblucentImage:", StringComparison.Ordinal))
            return text;

        // Legacy in-line photos also become position sentinels (the card renders the icon there).
        return System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\[\[ScriblucentImage:[^\]]+\]\]",
            FloatingImageCoordinator.InlineMarkerChar.ToString());
    }

    private static void AppendSeparator(StringBuilder preview)
    {
        if (preview.Length > 0 && !char.IsWhiteSpace(preview[^1]))
            preview.Append(' ');
    }

    private static string NormalizePreviewWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = new StringBuilder(text.Length);
        var previousWasWhitespace = true;

        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                    normalized.Append(' ');

                previousWasWhitespace = true;
                continue;
            }

            normalized.Append(character);
            previousWasWhitespace = false;
        }

        return normalized.ToString().Trim();
    }
}
