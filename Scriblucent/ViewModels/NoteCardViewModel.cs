using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent;
using System.Globalization;
using System.Windows.Input;

namespace Scriblucent.ViewModels;

public class NoteCardViewModel : ViewModelBase
{
    private bool _isMenuVisible;
    private bool _isSelectedInMassSelect;
    private int _contentGeneration;
    private string? _cachedContentPreview;
    private NotePreviewSnapshot? _cachedPreview;
    private bool _previewNeedsValidation;
    private string? _cachedTagsDisplay;
    private string? _cachedTagsSearchText;
    private string? _searchHaystack;
    private bool _searchHaystackIncludesBody;
    private CultureInfo? _searchHaystackCulture;
    private readonly Action<NoteCardViewModel> _deleteAction;
    private readonly Action<NoteCardViewModel>? _removeFromGroupAction;
    private readonly Action<NoteCardViewModel>? _duplicateAction;
    private readonly Action<NoteCardViewModel>? _togglePinAction;
    private RelayCommand? _deleteCommand;
    private RelayCommand? _removeFromGroupCommand;
    private RelayCommand? _duplicateCommand;
    private RelayCommand? _togglePinCommand;

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
        if (document.CardPreview is { Version: NotePreviewService.Version } preview)
        { _cachedPreview = preview; _cachedContentPreview = preview.Text; _previewNeedsValidation = true; }
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

    internal void NotifyTitleChanged() => OnPropertyChanged(nameof(Title));

    public string Title => Document.Title;

    /// <summary>Bumped when the note body changes so tile signatures invalidate without reading
    /// <see cref="Content"/> (that getter parses a FlowDocument).</summary>
    internal int ContentGeneration => _contentGeneration;

    /// <summary>The validated cached excerpt. Does not parse or validate an unrequested note.</summary>
    internal bool TryGetCachedContentPreview(out string preview)
    {
        if (_cachedContentPreview is null || _previewNeedsValidation)
        {
            preview = string.Empty;
            return false;
        }

        preview = _cachedContentPreview;
        return true;
    }
    // Return a plain-text preview for the card. The stored Document.Content
    // may be plain text or Base64-encoded RTF (saved by the editor). If it's
    // RTF we decode and extract the text so the card shows readable content
    // instead of raw Base64/RTF markup.
    public string Content
    {
        get
        {
            if (_cachedContentPreview is not null && !_previewNeedsValidation)
                return _cachedContentPreview;

            var startedAt = Scriblucent.Services.ScrollPerfDiag.Enabled
                ? System.Diagnostics.Stopwatch.GetTimestamp()
                : 0;
            _cachedPreview = NotePreviewService.Build(Document);
            _previewNeedsValidation = false;
            Document.CardPreview = _cachedPreview;
            _cachedContentPreview = _cachedPreview.Text;
            Document.SearchablePreview = null;
            if (startedAt != 0)
            {
                var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                if (elapsedMs >= 20)
                    Scriblucent.Services.ScrollPerfDiag.Log($"notePreviewDecode\tms={elapsedMs:F2}\tcontentLength={Document.Content?.Length ?? 0}\timages={Document.Images?.Count ?? 0}");
            }
            return _cachedContentPreview;
        }
    }

    /// <summary>True when the note contains any photo (floating or in-line, current or legacy),
    /// so the card can show a picture indicator instead of looking like an empty note.</summary>
    public bool HasPictures =>
        (_cachedPreview?.Text.Contains('\uFFFC') ?? false) || (Document.Images is { Count: > 0 })
        || (Document.Content?.Contains("[[ScriblucentImage:", StringComparison.Ordinal) ?? false);
    /// <summary>
    /// True once <see cref="Content"/> has decoded and cached its preview at least once. Lets
    /// layout-estimation code (<see cref="Controls.NoteCardItemSizeProvider"/>) avoid forcing that
    /// decode just to size a card during a bulk load, before <see cref="WarmContentPreview"/> has
    /// had a chance to run.
    /// </summary>
    internal bool IsContentPreviewCached => _cachedContentPreview is not null && !_previewNeedsValidation;

    /// <summary>Duplicates share content text and equivalent image layout. Reuse a preview
    /// already decoded by the source card when the duplicate is first realized.</summary>
    internal void CopyCachedContentPreviewFrom(NoteCardViewModel source)
    {
        if (!ReferenceEquals(Document.Content, source.Document.Content))
            return;

        _cachedContentPreview = source._cachedContentPreview;
        _cachedPreview = source._cachedPreview;
        _previewNeedsValidation = source._previewNeedsValidation;
        Document.CardPreview = source.Document.CardPreview;
        Document.PreviewLayoutWidth = source.Document.PreviewLayoutWidth;
        Document.SearchablePreview = source.Document.SearchablePreview;
    }

    internal bool TryGetSearchHaystack(bool includeBody, out string haystack)
    {
        if (_searchHaystack is not null
            && _searchHaystackIncludesBody == includeBody
            && ReferenceEquals(_searchHaystackCulture, CultureInfo.CurrentCulture))
        {
            haystack = _searchHaystack;
            return true;
        }

        haystack = string.Empty;
        return false;
    }

    internal void SetSearchHaystack(string haystack, bool includeBody)
    {
        _searchHaystack = haystack;
        _searchHaystackIncludesBody = includeBody;
        _searchHaystackCulture = CultureInfo.CurrentCulture;
    }

    internal void InvalidateSearchHaystack() => _searchHaystack = null;

    /// <summary>
    /// Builds and caches the preview eagerly so scrolling the dashboard never pays the
    /// RTF decode on the UI thread while cards realize.
    /// </summary>
    internal void WarmContentPreview() => _ = Content;

    internal NotePreviewSnapshot Preview { get { _ = Content; return _cachedPreview!; } }

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

    public ICommand DeleteCommand => _deleteCommand ??= new RelayCommand(DeleteNote);

    public ICommand RemoveFromGroupCommand => _removeFromGroupCommand ??=
        new RelayCommand(RemoveFromGroup, () => IsGrouped);

    public ICommand DuplicateCommand => _duplicateCommand ??= new RelayCommand(DuplicateNote);

    public ICommand TogglePinCommand => _togglePinCommand ??= new RelayCommand(TogglePin);

    private void DuplicateNote() => _duplicateAction?.Invoke(this);

    public void NotifyContentChanged()
    {
        _contentGeneration++;
        _cachedContentPreview = null;
        _cachedPreview = null;
        Document.SearchablePreview = null;
        _cachedTagsDisplay = null;
        _cachedTagsSearchText = null;
        InvalidateSearchHaystack();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Content));
        OnPropertyChanged(nameof(HasPictures));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(HasSchedule));
        OnPropertyChanged(nameof(TagsDisplay));
        OnPropertyChanged(nameof(TagsSearchText));
        OnPropertyChanged(nameof(IsGrouped));
        CommandManager.InvalidateRequerySuggested();
        // The editor saves immediately after this. Rebuild now so the snapshot keeps the
        // same preview string the card shows, and the next launch does not parse again.
        WarmContentPreview();
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

}
