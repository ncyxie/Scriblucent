using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Icons;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent;
using Scriblucent.Services;
using MahApps.Metro.IconPacks;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using ShapePath = System.Windows.Shapes.Path;
using ShapeLine = System.Windows.Shapes.Line;

namespace Scriblucent.Views;

public partial class MindMapPreviewWindow : Window, INotifyPropertyChanged, Scriblucent.Services.IHostedDocumentEditor
{
    private enum UnsavedCloseDecision
    {
        Cancel,
        LeaveWithoutSaving,
        SaveAndClose
    }

    private const double BaseNodeWidth = 190;
    private const double MaxNodeWidth = 420;
    // Shared column width targets ~PreferredTitleLines of wrap; titles beyond MaxTitleLines
    // ellipsize with a tooltip (see FitTitleForDisplay).
    private const int PreferredTitleLines = 2;
    private const int MaxTitleLines = 4;
    // Every layout algorithm (balanced/left/right tree, top-down, radial) treats this as a single
    // shared column width, not a per-node one - reworking all four to place variable-width boxes
    // without overlapping neighbors is a much bigger change than this needs. Instead the whole
    // map uses one width, recomputed once per RebuildMapCore pass (see ComputeEffectiveNodeWidth)
    // to fit whatever node currently needs the most room at its own font size. Each retained
    // map has its own layout context, so opening another tab cannot change its measurements.
    private double NodeWidth = BaseNodeWidth;
    private MindMapNode? LayoutRoot;
    // TextBlock.Measure matches what DrawNodes paints; keep a little slack for layout rounding.
    private const double NodeHeightSlack = 6;
    private const double HorizontalGap = 150;
    private const double VerticalGap = 28;
    private const double VerticalLayoutLevelGap = 118;
    private const double VerticalLayoutNodeGap = 34;
    private const double CanvasPadding = 52;
    // Must mirror DrawNodes card padding / chevron / meta row for measure.
    private const double CardPaddingX = 14;
    private const double CardPaddingY = 12;
    private const double NodeTextHorizontalPadding = CardPaddingX * 2;
    private const double NodeTextVerticalPadding = CardPaddingY * 2;
    private const double ChevronHitSize = 20;
    private const double ChevronGlyphSize = 14;
    private const double ChevronTitleGap = 8;
    private const double MetaRowHeight = 18;
    private const double TitleMetaGap = 8;
    private const double MetaGlyphSize = 13;
    private const double MetaItemGap = 8;
    private const int MaxVisibleNodeIcons = 3;
    private const double MinNodeHeight = 48;
    private const double MaxNodeHeight = 400;
    private static readonly FontFamily DefaultNodeFontFamily =
        Application.Current?.TryFindResource("FontFamily.UI") as FontFamily ?? new FontFamily("Segoe UI");
    private static readonly double DefaultNodeFontSize = TypeScale.Px(TypeScale.Body, 14);
    private readonly MindMapNode _root;
    private readonly Dictionary<MindMapNode, NodeLayout> _layouts = new();
    private readonly ObservableCollection<MindMapNoteLinkOption> _noteLinkOptions = new();
    private Action<Guid>? _openNoteAction;
    private string _modelDisplayName = string.Empty;
    private bool _hasCenteredOnRootInitially;
    private MindMapNode? _selectedNode;
    private readonly HashSet<MindMapNode> _selectedNodes = new();
    private readonly List<MindMapNode> _searchMatches = [];
    private string _searchQuery = string.Empty;
    private DispatcherTimer? _searchDebounceTimer;
    private int _currentSearchMatchIndex = -1;
    private HashSet<MindMapNode>? _renderOnlyNodes;
    private MindMapLayoutMode _selectedLayoutMode = MindMapLayoutMode.BalancedTree;
    private bool _suppressLayoutModeSelectionChanged;
    private bool _suppressManualPositioningToggleChanged;
    private bool _suppressZoomSliderViewportRecenter;
    private const double ManualDragThreshold = 4;
    private const int ConnectionZIndex = 0;
    private const int NodeZIndex = 1;
    private const int DraggingNodeZIndex = 2;

    private bool _isDraggingNode;
    private bool _isManualDragPending;
    private bool _isLinkDragGesture;
    private bool _connectionRedrawQueued;
    private readonly Dictionary<Guid, string> _motionNodeText = new();
    private bool _mapViewportRefreshQueued;
    private MindMapNode? _draggedNode;
    private MindMapNode? _pendingDragNode;
    private MindMapNode? _dropTargetHoverNode;
    private Point _dragStartMousePos;
    private Point _dragStartNodePos;
    private Border? _draggedBorder;
    private Border? _pendingDragBorder;
    private bool _isManualPositioningMode;
    private bool _suppressManualPositionCapture;
    private bool _isViewPanning;

    // Zoom-session Skia snapshots: during zoom gestures the live canvas collapses behind a cached
    // layer rendered off-thread, masking the LayoutTransform
    // re-raster cost on large maps. The canvas returns ~350ms after the last zoom change.
    private int _tileRevision;
    private readonly Guid _tileMapId = Guid.NewGuid();
    private int _snapshotRequest;
    private int _snapshotSourceRevision = -1;
    private string? _snapshotSourceTheme;
    private bool _snapshotWarmQueued, _mapClosed;
    private DispatcherTimer? _tileSettleTimer;
    private bool _tileSessionActive;
    
    private Point _viewPanLastPos;
    private readonly Dictionary<MindMapNode, Point> _manualPositions = new();
    private string _lastSavedSnapshot = string.Empty;
    private bool _isInitializing = true;
    private bool _allowCloseWithoutPrompt;
    private MindMapNoteLinkOption? _selectedLinkedNote;

    // Collapsible chrome panels (Arrange & View / Search) - session-only, not persisted, mutually
    // exclusive (opening one closes the other) so they never stack. Mirrors NoteEditorWindow's
    // FontPanel show/hide animation.
    private bool _isArrangePanelOpen;
    private bool _isSearchPanelOpen;
    private bool _isFontPanelOpen;

    // Tags panel is independent of the group above (different row, no visual overlap) so it does
    // not participate in their mutual exclusion, and its state is persisted per-document.
    private bool _isTagsPanelOpen;
    private bool _lastSavedTagsPanelOpen;
    private bool _suppressFontPanelChangeHandlers;

    // Snapshot-based undo/redo, scoped to content edits only (add/delete/reparent/edit text/
    // restyle/reposition/import-merge) - expand/collapse and zoom are view state, not tracked.
    // Whole-tree clone per step rather than paired command objects: DeleteNodeWithConfirmation's
    // children-promotion-to-grandparent behavior makes a clean inverse non-trivial to hand-write,
    // and mind maps are small enough (tens to low hundreds of nodes) that cloning the whole root
    // per edit is cheap. _root is readonly, so restoring a snapshot copies fields into the
    // existing _root instance (RestoreRootFrom) rather than reassigning the reference.
    private readonly List<MindMapNode> _undoStack = new();
    private readonly List<MindMapNode> _redoStack = new();
    private const int MaxUndoDepth = 50;

    // Clipboard for cut/copy/paste and copy/paste style. Static (not per-window) because
    // The shared clipboard stores clones, and paste clones again. A static field means
    // the clipboard survives closing one map and opening another, matching how copy/paste behaves
    // in every other app instead of resetting on close.

    private static CopiedNodeStyle? _copiedStyle;
    private bool _aiReviewNoticeDismissed;

    private sealed record CopiedNodeStyle(
        string? BackgroundColor,
        string? BorderColor,
        double BorderThickness,
        string? NodeShape,
        List<string> Icons,
        string? IconBadgeColor,
        bool IsBold,
        bool IsItalic,
        string? TextColor,
        string? FontFamily,
        double? FontSize);

    public sealed class MindMapNoteLinkOption
    {
        public MindMapNoteLinkOption(Guid id, string title)
        {
            Id = id;
            Title = string.IsNullOrWhiteSpace(title) ? LocalizationService.GetString("QuizLinkedNoteNone") : title.Trim();
        }

        public Guid Id { get; }
        public string Title { get; }
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public MindMapPreviewWindow(
        MindMapNode root,
        IEnumerable<MindMapNoteLinkOption>? noteOptions = null,
        string? modelDisplayName = null,
        string? title = null,
        IEnumerable<string>? tags = null,
        string? layoutMode = null,
        bool useManualPositions = false,
        Guid? sourceNoteId = null,
        Action<Guid>? openNoteAction = null,
        bool? isTagsPanelOpen = null,
        bool aiReviewNoticeDismissed = false)
    {
        _root = root;
        MindMapNodeIconCatalog.NormalizeTreeIcons(_root);
        MindMapFileService.SoftMigrateLegacyRectangleDefault(_root);
        _openNoteAction = openNoteAction;
        if (noteOptions != null)
        {
            foreach (var option in noteOptions)
                _noteLinkOptions.Add(option);
        }
        _selectedLinkedNote = _noteLinkOptions.FirstOrDefault(option => option.Id == sourceNoteId);
        _selectedLayoutMode = ParseLayoutMode(layoutMode);
        LoadManualPositionsFromNodes(_root);
        _isManualPositioningMode = useManualPositions || _manualPositions.Count > 0;
        InitializeComponent();
        InitializeNodeSelection();
        Scriblucent.Services.WindowThemeService.Register(this);
        PopulateFontFamilyBox();

        TitleTextBox.Text = string.IsNullOrWhiteSpace(title)
            ? (string.IsNullOrWhiteSpace(root.Text) ? LocalizationService.GetString("MindMapUntitled") : root.Text.Trim())
            : title.Trim();
        TagsTextBox.Text = tags is null
            ? string.Empty
            : string.Join(", ", tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()));
        SetTagsPanelOpen(isTagsPanelOpen ?? false, animate: false);
        _lastSavedTagsPanelOpen = _isTagsPanelOpen;
        _aiReviewNoticeDismissed = aiReviewNoticeDismissed;
        ConfigureAiGeneratedIndicator(modelDisplayName);
        AiFeatureGate.Changed += MindMapPreviewWindow_AiFeatureGateChanged;
        Closed += (_, _) => AiFeatureGate.Changed -= MindMapPreviewWindow_AiFeatureGateChanged;
        ThemeManager.ThemeChanged += MindMapPreviewWindow_ThemeChanged;
        Closed += (_, _) => ThemeManager.ThemeChanged -= MindMapPreviewWindow_ThemeChanged;
        Closed += (_, _) =>
        {
            _mapClosed = true;
            _snapshotRequest++;
            _tileSettleTimer?.Stop();
            Scriblucent.Rendering.SkiaMindMapSnapshotCache.Release(_tileMapId);
            UiMotion.Cleanup(MapCanvas);
            foreach (var node in MapCanvas.Children.OfType<Border>()) UiMotion.Cleanup(node);
        };
        UpdateSearchResultsText();

        _suppressLayoutModeSelectionChanged = true;
        SelectLayoutComboBoxItem(_selectedLayoutMode);
        _suppressLayoutModeSelectionChanged = false;
        _suppressManualPositioningToggleChanged = true;
        ManualPositioningToggle.IsChecked = _isManualPositioningMode;
        _suppressManualPositioningToggleChanged = false;
        UpdateManualPositioningVisualState();
        UpdateUndoRedoButtonStates();
        OnPropertyChanged(nameof(NoteLinkOptions));
        OnPropertyChanged(nameof(LinkedNoteButtonText));
        OnPropertyChanged(nameof(LinkedNoteButtonToolTip));

        Loaded += (_, _) => InitializeWorkspaceMap();
        WindowContentEnterHelper.AnimateOnLoaded(this);
        SizeChanged += (_, _) => QueueMapViewportRefresh();
        StateChanged += (_, _) => QueueMapViewportRefresh();
    }

    private bool _workspaceMapInitialized;
    private void MindMapPreviewWindow_ThemeChanged(object? sender, EventArgs e)
    {
        // Generated canvas brushes and raster previews must move to the same theme together.
        if (_workspaceMapInitialized && !_mapClosed)
            RebuildMap();
    }

    private void InitializeWorkspaceMap()
    {
        if (_workspaceMapInitialized) return;
        _workspaceMapInitialized = true;
        MapCanvas.PreviewMouseMove += MapCanvas_PreviewMouseMove;
        MapCanvas.MouseMove += MapCanvas_MouseMove;
        MapCanvas.PreviewMouseLeftButtonUp += MapCanvas_PreviewMouseLeftButtonUp;
        MapCanvas.LostMouseCapture += MapCanvas_LostMouseCapture;
        MapScrollViewer.PreviewMouseLeftButtonDown += MapScrollViewer_PreviewMouseLeftButtonDown;
        MapScrollViewer.PreviewMouseDown += MapScrollViewer_PreviewMouseDownForPan;
        MapScrollViewer.PreviewMouseMove += MapScrollViewer_PreviewMouseMoveForPan;
        MapScrollViewer.PreviewMouseLeftButtonUp += MapScrollViewer_PreviewMouseButtonUpForPan;
        MapScrollViewer.PreviewMouseUp += MapScrollViewer_PreviewMouseUpForPan;
        MapScrollViewer.LostMouseCapture += MapScrollViewer_LostMouseCaptureForPan;
        RebuildMap(animate: false);
        MarkCurrentStateSaved();
        _isInitializing = false;
        UpdateEditedIndicator();
        }

    public string EditorTitle => TitleTextBox.Text.Trim();

    public IReadOnlyList<string> Tags => ParseTags(TagsTextBox.Text);

    public string AiModelDisplayName => _modelDisplayName;

    public ObservableCollection<MindMapNoteLinkOption> NoteLinkOptions => _noteLinkOptions;

    public string LinkedNoteButtonText => _selectedLinkedNote?.Title ?? LocalizationService.GetString("QuizLinkedNoteNone");

    public string LinkedNoteButtonToolTip => _selectedLinkedNote is null
        ? LocalizationService.GetString("QuizLinkedNoteNoneTooltip")
        : string.Format(LocalizationService.GetString("QuizLinkedNoteOpenTooltip"), _selectedLinkedNote.Title);

    public MindMapNoteLinkOption? SelectedLinkedNote
    {
        get => _selectedLinkedNote;
        set
        {
            if (ReferenceEquals(_selectedLinkedNote, value))
                return;

            _selectedLinkedNote = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LinkedNoteButtonText));
            OnPropertyChanged(nameof(LinkedNoteButtonToolTip));
            UpdateEditedIndicator();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void EditorField_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateEditedIndicator();
    }

    private void LinkedNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLinkedNote is null)
            return;

        _openNoteAction?.Invoke(_selectedLinkedNote.Id);
    }

    private void OpenNodeLink(MindMapNode node)
    {
        if (node.LinkedNoteId is { } noteId)
            _openNoteAction?.Invoke(noteId);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void MarkCurrentStateSaved()
    {
        _lastSavedSnapshot = GetEditorSnapshot();
        _lastSavedTagsPanelOpen = _isTagsPanelOpen;
        UpdateEditedIndicator();
    }

    // Excluded from GetEditorSnapshot/HasUnsavedChanges (toggling it shouldn't prompt a save on
    // close, same as Font/Arrange/Search), but still needs to reach disk eventually - piggybacking
    // on the next natural autosave tick instead of triggering an immediate full-collection rewrite
    // (and its visible "saved" flash) on every single toggle click.
    public bool HasPendingAutoSaveChanges() => HasUnsavedChanges() || _isTagsPanelOpen != _lastSavedTagsPanelOpen;

    public void MarkCurrentStateAutoSaved()
    {
        _lastSavedSnapshot = GetEditorSnapshot();
        _lastSavedTagsPanelOpen = _isTagsPanelOpen;
        ShowAutoSaveIndicator();
    }

    private void ShowAutoSaveIndicator()
    {
        EditedIndicatorText?.ShowSaved(UpdateEditedIndicator);
    }

    public void ReportWorkspaceSaveFailure() => EditedIndicatorText?.ShowFailure();

    private bool HasUnsavedChanges()
    {
        if (_isInitializing)
            return false;

        return !string.Equals(GetEditorSnapshot(), _lastSavedSnapshot, StringComparison.Ordinal);
    }

    private void UpdateEditedIndicator()
    {
        WorkspaceStateChanged?.Invoke();
        if (_isInitializing || EditedIndicatorText is null)
            return;

        EditedIndicatorText.SetEdited(HasUnsavedChanges());
    }

    private string GetEditorSnapshot()
    {
        return string.Join(
            '\u001F',
            TitleTextBox.Text,
            string.Join('\u001E', Tags),
            GetLayoutModeKey(_selectedLayoutMode),
            _isManualPositioningMode.ToString(),
            SerializeNodeSnapshot(_root));
    }

    private static string SerializeNodeSnapshot(MindMapNode node)
    {
        var childrenSnapshot = string.Join('\u001C', node.Children.Select(SerializeNodeSnapshot));

        return string.Join(
            '\u001D',
            node.Text ?? string.Empty,
            node.IsExpanded.ToString(),
            node.BackgroundColor ?? string.Empty,
            node.BorderColor ?? string.Empty,
            node.BorderThickness.ToString(CultureInfo.InvariantCulture),
            node.NodeShape ?? string.Empty,
            string.Join('', node.Icons),
            node.IconBadgeColor ?? string.Empty,
            FormatNullableDouble(node.ManualX),
            FormatNullableDouble(node.ManualY),
            childrenSnapshot);
    }

    private static string FormatNullableDouble(double? value)
        => value.HasValue && IsFinite(value.Value)
            ? value.Value.ToString("R", CultureInfo.InvariantCulture)
            : string.Empty;

    private UnsavedCloseDecision GetCloseDecision()
    {
        if (!HasUnsavedChanges())
            return UnsavedCloseDecision.LeaveWithoutSaving;

        var documentTitle = ResolveEditorTitleForPrompt();
        var result = ModernDialog.Show(
            WorkspaceOwner,
            LocalizationService.GetString("UnsavedChanges"),
            string.Format(LocalizationService.GetString("UnsavedChangesConfirmationFormat"), documentTitle),
            ModernDialogTone.Warning,
            LocalizationService.GetString("LeaveWithoutSaving"),
            LocalizationService.GetString("Cancel"),
            LocalizationService.GetString("SaveAndExit"),
            primaryStyle: ModernDialogButtonStyle.Danger,
            secondaryStyle: ModernDialogButtonStyle.Primary);

        return result switch
        {
            ModernDialogResult.Primary => UnsavedCloseDecision.LeaveWithoutSaving,
            ModernDialogResult.Secondary => UnsavedCloseDecision.SaveAndClose,
            _ => UnsavedCloseDecision.Cancel
        };
    }

    private string ResolveEditorTitleForPrompt()
    {
        var title = EditorTitle;
        return string.IsNullOrWhiteSpace(title)
            ? LocalizationService.GetString("MindMapUntitled")
            : title;
    }

    private void SaveAndClose()
    {
        if (_workspaceSave is not null) { _workspaceSave(); return; }
        MarkCurrentStateSaved();
        _allowCloseWithoutPrompt = true;
        WindowContentEnterHelper.CloseWithDialogResult(this, true);
    }

    private void SelectLayoutComboBoxItem(MindMapLayoutMode mode)
    {
        var targetKey = GetLayoutModeKey(mode);
        foreach (var item in LayoutModeComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, targetKey, StringComparison.OrdinalIgnoreCase))
            {
                LayoutModeComboBox.SelectedItem = item;
                return;
            }
        }

        LayoutModeComboBox.SelectedIndex = 0;
    }

    private static MindMapLayoutMode ParseLayoutMode(string? value)
    {
        return value?.Trim() switch
        {
            "Radial" => MindMapLayoutMode.Radial,
            "RightTree" => MindMapLayoutMode.RightTree,
            "LeftTree" => MindMapLayoutMode.LeftTree,
            "TopDown" => MindMapLayoutMode.TopDown,
            _ => MindMapLayoutMode.BalancedTree
        };
    }

    private static string GetLayoutModeKey(MindMapLayoutMode mode)
    {
        return mode switch
        {
            MindMapLayoutMode.Radial => "Radial",
            MindMapLayoutMode.RightTree => "RightTree",
            MindMapLayoutMode.LeftTree => "LeftTree",
            MindMapLayoutMode.TopDown => "TopDown",
            _ => "BalancedTree"
        };
    }

    public MindMapDocument ToDocument(MindMapDocument? existingDocument = null, bool captureLayout = true)
    {
        if (captureLayout)
        {
            if (_isManualPositioningMode) CaptureVisibleLayoutAsManualPositions();
            else ClearManualPositions(_root);
        }

        return new MindMapDocument
        {
            Id = existingDocument?.Id ?? Guid.NewGuid(),
            Title = string.IsNullOrWhiteSpace(EditorTitle)
                ? LocalizationService.GetString("MindMapUntitled")
                : EditorTitle,
            Tags = Tags.ToList(),
            IsEditorTagsPanelOpen = _isTagsPanelOpen,
            Root = _root,
            LayoutMode = GetLayoutModeKey(_selectedLayoutMode),
            UseManualPositions = _isManualPositioningMode,
            CreatedAt = existingDocument?.CreatedAt ?? DateTime.UtcNow,
            LastModified = DateTime.Now,
            AiModelDisplayName = string.IsNullOrWhiteSpace(_modelDisplayName)
                ? existingDocument?.AiModelDisplayName ?? string.Empty
                : _modelDisplayName,
            SourceNoteId = _selectedLinkedNote?.Id ?? existingDocument?.SourceNoteId,
            GroupId = existingDocument?.GroupId,
            Schedules = existingDocument?.Schedules?.ToList() ?? new List<NoteScheduleEntry>(),
            IsPinned = existingDocument?.IsPinned ?? false,
            AiReviewNoticeDismissed = _aiReviewNoticeDismissed || existingDocument?.AiReviewNoticeDismissed == true
        };
    }

    private void LoadManualPositionsFromNodes(MindMapNode node)
    {
        if (node.ManualX is { } x && node.ManualY is { } y && IsFinite(x) && IsFinite(y))
            _manualPositions[node] = new Point(x, y);

        foreach (var child in node.Children)
            LoadManualPositionsFromNodes(child);
    }

    private void ApplyManualPositionsToLayouts()
    {
        foreach (var (node, layout) in _layouts.ToList())
        {
            if (_manualPositions.TryGetValue(node, out var position))
                _layouts[node] = layout with { X = position.X, Y = position.Y };
        }
    }

    private void CaptureVisibleLayoutAsManualPositions()
    {
        foreach (var (node, layout) in _layouts)
            SetManualPosition(node, new Point(layout.X, layout.Y));
    }

    private void SetManualPosition(MindMapNode node, Point position)
    {
        if (!IsFinite(position.X) || !IsFinite(position.Y))
            return;

        _manualPositions[node] = position;
        node.ManualX = position.X;
        node.ManualY = position.Y;
    }

    private static void ClearManualPositions(MindMapNode node)
    {
        node.ManualX = null;
        node.ManualY = null;

        foreach (var child in node.Children)
            ClearManualPositions(child);
    }

    private static bool IsFinite(double value)
        => !double.IsNaN(value) && !double.IsInfinity(value);

    private void UpdateManualPositioningVisualState()
    {
        if (ManualPositioningToggle is null
            || ManualPositioningStatusDot is null
            || ManualPositioningStateBadge is null
            || ManualPositioningStateText is null)
        {
            return;
        }

        if (_isManualPositioningMode)
        {
            ManualPositioningStateText.Text = LocalizationService.GetString("MindMapManualPositioningOn");
            ManualPositioningStatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentPrimaryForeground");
            ManualPositioningStateBadge.SetResourceReference(Border.BackgroundProperty, "AccentPrimarySubtle");
            ManualPositioningStateBadge.BorderBrush = Brushes.Transparent;
            ManualPositioningStateText.SetResourceReference(TextBlock.ForegroundProperty, "AccentPrimarySubtleForeground");
            ManualPositioningToggle.ToolTip = LocalizationService.GetString("MindMapManualPositioningOnHint");
            return;
        }

        ManualPositioningStateText.Text = LocalizationService.GetString("MindMapManualPositioningOff");
        ManualPositioningStatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextColorSecondary");
        ManualPositioningStateBadge.SetResourceReference(Border.BackgroundProperty, "IconButtonBackground");
        ManualPositioningStateBadge.BorderBrush = Brushes.Transparent;
        ManualPositioningStateText.SetResourceReference(TextBlock.ForegroundProperty, "TextColorSecondary");
        ManualPositioningToggle.ToolTip = LocalizationService.GetString("MindMapManualPositioningOffHint");
    }

    private void ConfigureAiGeneratedIndicator(string? modelDisplayName)
    {
        _modelDisplayName = modelDisplayName?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(_modelDisplayName) || !AiFeatureGate.IsEnabled)
        {
            AiGeneratedInfoBadge.ToolTip = null;
            if (_isInitializing)
            {
                UiMotion.AnimateLayoutReveal(AiGeneratedInfoBadge, false, axis: Orientation.Horizontal, animate: false);
                UiMotion.AnimateLayoutReveal(AiReviewNotice, false, animate: false);
                return;
            }

            UiMotion.AnimateLayoutReveal(AiGeneratedInfoBadge, false, axis: Orientation.Horizontal);
            UiMotion.AnimateLayoutReveal(AiReviewNotice, false);
            return;
        }

        AiGeneratedInfoBadge.ToolTip = LocalizationService.FormatAiGeneratedTooltip(
            "MindMapGeneratedWithModel",
            _modelDisplayName,
            includeReviewDisclaimer: !_aiReviewNoticeDismissed);

        if (_isInitializing)
        {
            UiMotion.AnimateLayoutReveal(AiGeneratedInfoBadge, true, axis: Orientation.Horizontal, animate: false);
            UiMotion.AnimateLayoutReveal(AiReviewNotice, !_aiReviewNoticeDismissed, animate: false);
            return;
        }

        UiMotion.AnimateLayoutReveal(AiGeneratedInfoBadge, true, axis: Orientation.Horizontal);
        UiMotion.AnimateLayoutReveal(AiReviewNotice, !_aiReviewNoticeDismissed);
    }

    public event EventHandler? AiReviewNoticeDismissedPersistRequested;

    private void AiReviewNotice_Dismissed(object sender, RoutedEventArgs e)
    {
        if (_aiReviewNoticeDismissed)
            return;

        _aiReviewNoticeDismissed = true;
        ConfigureAiGeneratedIndicator(_modelDisplayName);
        AiReviewNoticeDismissedPersistRequested?.Invoke(this, EventArgs.Empty);
    }

    private void MindMapPreviewWindow_AiFeatureGateChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() => ConfigureAiGeneratedIndicator(_modelDisplayName));
    }

    private void RebuildMap(bool animate = true, bool moveNodes = false)
    {
        UiMotion.Stop(UiMotion.Channel(MapCanvas, MotionChannel.Layout));
        UiMotion.Stop(MapCanvas);
        MapCanvas.Opacity = 1;
        var before = moveNodes && _layouts.Count <= 32 ? new Dictionary<MindMapNode, NodeLayout>(_layouts) : null;
        RebuildMapCore();
        var nodes = MapCanvas.Children.OfType<Border>().Where(b => b.Tag is MindMapNode).ToArray();
        var changed = 0;
        foreach (var border in nodes)
        {
            var node = (MindMapNode)border.Tag;
            if (animate && !_isInitializing && ContentRoot.IsLoaded && changed < 8
                && (!_motionNodeText.TryGetValue(node.Id, out var text) || text != node.Text))
            {
                var newNode = !_motionNodeText.ContainsKey(node.Id);
                border.Opacity = 0;
                var rise = UiMotion.ChannelTranslate(border, MotionChannel.Presence);
                rise.Y = newNode && MotionPolicy.Spatial ? 12 : 0;
                SurfaceMotion.Arrive(border, newNode ? 220 : 160, translate: rise);
                if (newNode)
                    foreach (var edge in MapCanvas.Children.OfType<FrameworkElement>().Where(e => e.Tag is ValueTuple<MindMapNode, MindMapNode> pair && ReferenceEquals(pair.Item2, node)))
                    { edge.Opacity = 0; UiMotion.Fade(edge, 1, 160); }
                changed++;
            }
        }
        _motionNodeText.Clear();
        foreach (var border in nodes) { var node = (MindMapNode)border.Tag; _motionNodeText[node.Id] = node.Text; }
        if (before is null || !animate || !MotionPolicy.Spatial || nodes.Length > 32
            || MapCanvas.Children.OfType<ShapePath>().Count() + MapCanvas.Children.OfType<ShapeLine>().Count() > 64) return;
        var tracks = new List<RenderTransitionClock.Track>();
        foreach (var border in nodes)
        {
            var node = (MindMapNode)border.Tag;
            if (!before.TryGetValue(node, out var old) || !_layouts.TryGetValue(node, out var target)) continue;
            var translate = UiMotion.ChannelTranslate(border, MotionChannel.Layout);
            translate.X = old.X - target.X; translate.Y = old.Y - target.Y;
            tracks.Add(UiMotion.Track(220, UiMotion.InOutCubic, translate.X, 0, v => translate.X = v));
            tracks.Add(UiMotion.Track(220, UiMotion.InOutCubic, translate.Y, 0, v => translate.Y = v));
        }
        foreach (var path in MapCanvas.Children.OfType<ShapePath>())
        {
            if (path.Tag is not ValueTuple<MindMapNode, MindMapNode> pair
                || !before.TryGetValue(pair.Item1, out var oldParent) || !before.TryGetValue(pair.Item2, out var oldChild)
                || path.Data is not PathGeometry targetGeometry) continue;
            var geometry = targetGeometry.Clone(); path.Data = geometry;
            var figure = geometry.Figures[0]; var segment = (BezierSegment)figure.Segments[0];
            var oldFigure = oldChild.Direction == 2 ? CreateVerticalConnectionFigure(oldParent, oldChild)
                : CreateHorizontalConnectionFigure(oldParent, oldChild);
            var oldSegment = (BezierSegment)oldFigure.Segments[0];
            var endStart = figure.StartPoint; var end1 = segment.Point1; var end2 = segment.Point2; var end3 = segment.Point3;
            void Apply(double t)
            {
                figure.StartPoint = BlendPoint(oldFigure.StartPoint, endStart, t);
                segment.Point1 = BlendPoint(oldSegment.Point1, end1, t);
                segment.Point2 = BlendPoint(oldSegment.Point2, end2, t);
                segment.Point3 = BlendPoint(oldSegment.Point3, end3, t);
            }
            Apply(0); tracks.Add(UiMotion.Track(220, UiMotion.InOutCubic, 0, 1, Apply));
        }
        foreach (var line in MapCanvas.Children.OfType<ShapeLine>())
        {
            if (line.Tag is not ValueTuple<MindMapNode, MindMapNode> pair
                || !before.TryGetValue(pair.Item1, out var a) || !before.TryGetValue(pair.Item2, out var b)) continue;
            var x1 = line.X1; var y1 = line.Y1; var x2 = line.X2; var y2 = line.Y2;
            void Apply(double t)
            {
                line.X1 = UiMotion.Lerp(a.X + a.Width / 2, x1, t); line.Y1 = UiMotion.Lerp(a.Y + a.Height / 2, y1, t);
                line.X2 = UiMotion.Lerp(b.X + b.Width / 2, x2, t); line.Y2 = UiMotion.Lerp(b.Y + b.Height / 2, y2, t);
            }
            Apply(0); tracks.Add(UiMotion.Track(220, UiMotion.InOutCubic, 0, 1, Apply));
        }
        UiMotion.Run(MapCanvas, MotionChannel.Layout, tracks);
    }

    private static Point BlendPoint(Point from, Point to, double t)
        => new(UiMotion.Lerp(from.X, to.X, t), UiMotion.Lerp(from.Y, to.Y, t));


    private void RebuildMapCore()
    {
        RefreshSearchMatches(resetIndex: false);

        _layouts.Clear();
        MapCanvas.Children.Clear();
        _tileRevision++;
        _snapshotSourceRevision = -1;
        LayoutRoot = _root;
        NodeWidth = ComputeEffectiveNodeWidth(_root);

        switch (_selectedLayoutMode)
        {
            case MindMapLayoutMode.Radial:
                BuildRadialLayout();
                break;
            case MindMapLayoutMode.RightTree:
                BuildDirectionalTreeLayout(direction: 1);
                break;
            case MindMapLayoutMode.LeftTree:
                BuildDirectionalTreeLayout(direction: -1);
                break;
            case MindMapLayoutMode.TopDown:
                BuildTopDownLayout();
                break;
            default:
                BuildBalancedTreeLayout();
                break;
        }

        // Nodes imported from a real FreeMind/Freeplane file may carry a pending HGAP/VSHIFT
        // offset - resolve it against OUR OWN just-computed auto-layout position (the only layout
        // we can know), then let it flow into the same manual-position capture below like any
        // other manually-placed node. See MindMapNode.PendingFreeMindOffsetX/Y and
        // ApplyPendingFreeMindOffsets for why this can only happen here, not at import time.
        if (ApplyPendingFreeMindOffsets(_root) && !_isManualPositioningMode)
        {
            _isManualPositioningMode = true;
            _suppressManualPositioningToggleChanged = true;
            ManualPositioningToggle.IsChecked = true;
            _suppressManualPositioningToggleChanged = false;
            UpdateManualPositioningVisualState();
        }

        if (_isManualPositioningMode)
            ApplyManualPositionsToLayouts();

        NormalizeLayoutOffset();

        if (_isManualPositioningMode && !_suppressManualPositionCapture)
            CaptureVisibleLayoutAsManualPositions();

        UpdateMapCanvasSize();

        DrawConnections(_root);
        DrawNodes(_root);
        DrawCrossLinks();

        if (CurrentSearchMatch is not null)
            CenterViewOnCurrentSearchMatch();

        QueueInitialCenterOnRoot();
        UpdateEditedIndicator();
        RefreshFontPanelFromSelection();
        SyncNodeSelection();

        // The visuals were rebuilt above - warm the zoom-tile cache for the visible region so
        // the next zoom session starts with tiles ready (deferred past the layout pass).
        Dispatcher.BeginInvoke(new Action(WarmSnapshot), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Resolves any pending FreeMind HGAP/VSHIFT offsets (set by MindMapFileService on import)
    /// against the auto-layout position _layouts just computed for that same node in this pass -
    /// the only layout we can ever know, since we don't run Freeplane's own layout engine. Returns
    /// true if any offset was resolved, so the caller can force manual-positioning mode on for
    /// this pass (the resolved position then gets captured into a real ManualX/ManualY below, the
    /// same way any other manually-placed node's position does).
    /// </summary>
    private bool ApplyPendingFreeMindOffsets(MindMapNode node)
    {
        var resolved = false;

        if ((node.PendingFreeMindOffsetX is not null || node.PendingFreeMindOffsetY is not null)
            && _layouts.TryGetValue(node, out var layout))
        {
            _layouts[node] = layout with
            {
                X = layout.X + (node.PendingFreeMindOffsetX ?? 0),
                Y = layout.Y + (node.PendingFreeMindOffsetY ?? 0)
            };
            node.PendingFreeMindOffsetX = null;
            node.PendingFreeMindOffsetY = null;
            resolved = true;
        }

        foreach (var child in node.Children)
        {
            if (ApplyPendingFreeMindOffsets(child))
                resolved = true;
        }

        return resolved;
    }

    private void BuildBalancedTreeLayout()
    {
        var step = NodeWidth + HorizontalGap;
        var rootHeight = MeasureNodeHeight(_root);
        IReadOnlyList<MindMapNode> visibleRootChildren = _root.IsExpanded
            ? _root.Children
            : Array.Empty<MindMapNode>();
        var rightChildren = visibleRootChildren
            .Where((_, index) => index % 2 == 0)
            .ToList();
        var leftChildren = visibleRootChildren
            .Where((_, index) => index % 2 == 1)
            .ToList();

        var leftDepth = leftChildren.Count == 0 ? 0 : leftChildren.Max(GetMaxVisibleDepth);
        var rootX = CanvasPadding + leftDepth * step;
        var leftHeight = MeasureChildrenHeight(leftChildren);
        var rightHeight = MeasureChildrenHeight(rightChildren);
        var totalHeight = Math.Max(rootHeight, Math.Max(leftHeight, rightHeight));
        var rootY = CanvasPadding + (totalHeight - rootHeight) / 2;

        _layouts[_root] = new NodeLayout(rootX, rootY, NodeWidth, rootHeight, Direction: 0, Depth: 0);

        AssignChildrenLayout(
            leftChildren,
            rootX,
            direction: -1,
            depth: 1,
            top: CanvasPadding + Math.Max(0, (totalHeight - leftHeight) / 2));
        AssignChildrenLayout(
            rightChildren,
            rootX,
            direction: 1,
            depth: 1,
            top: CanvasPadding + Math.Max(0, (totalHeight - rightHeight) / 2));
    }

    private void BuildDirectionalTreeLayout(int direction)
    {
        direction = direction < 0 ? -1 : 1;

        var step = NodeWidth + HorizontalGap;
        var rootHeight = MeasureNodeHeight(_root);
        IReadOnlyList<MindMapNode> visibleRootChildren = _root.IsExpanded
            ? _root.Children
            : Array.Empty<MindMapNode>();

        var childDepth = visibleRootChildren.Count == 0 ? 0 : visibleRootChildren.Max(GetMaxVisibleDepth);
        var rootX = direction > 0
            ? CanvasPadding
            : CanvasPadding + childDepth * step;
        var childrenHeight = MeasureChildrenHeight(visibleRootChildren);
        var totalHeight = Math.Max(rootHeight, childrenHeight);
        var rootY = CanvasPadding + (totalHeight - rootHeight) / 2;

        _layouts[_root] = new NodeLayout(rootX, rootY, NodeWidth, rootHeight, Direction: 0, Depth: 0);

        AssignChildrenLayout(
            visibleRootChildren,
            rootX,
            direction,
            depth: 1,
            top: CanvasPadding + Math.Max(0, (totalHeight - childrenHeight) / 2));
    }

    private void BuildTopDownLayout()
    {
        var totalWidth = MeasureVerticalSubtree(_root);
        var rootHeight = MeasureNodeHeight(_root);
        var rootX = CanvasPadding + (totalWidth - NodeWidth) / 2;
        var rootY = CanvasPadding;

        _layouts[_root] = new NodeLayout(rootX, rootY, NodeWidth, rootHeight, Direction: 2, Depth: 0);

        if (!_root.IsExpanded || _root.Children.Count == 0)
            return;

        AssignVerticalChildrenLayout(_root.Children, CanvasPadding, rootY + rootHeight + VerticalLayoutLevelGap, totalWidth, depth: 1);
    }

    private void BuildRadialLayout()
    {
        var levels = GetVisibleLevels(_root);
        var rootHeight = MeasureNodeHeight(_root);
        var centerX = CanvasPadding + 520;
        var centerY = CanvasPadding + 420;

        _layouts[_root] = new NodeLayout(
            centerX - NodeWidth / 2,
            centerY - rootHeight / 2,
            NodeWidth,
            rootHeight,
            Direction: 0,
            Depth: 0);

        var baseRadius = Math.Max(NodeWidth + 70, rootHeight + 90);
        var minRingGap = Math.Max(130, NodeWidth * 0.75);
        var previousRadius = 0d;

        for (var depth = 1; depth < levels.Count; depth++)
        {
            var levelNodes = levels[depth];
            if (levelNodes.Count == 0)
                continue;

            var angleStep = (Math.PI * 2) / levelNodes.Count;
            var minArcSpacing = NodeWidth + 36;
            var minRadiusByArc = levelNodes.Count <= 1
                ? baseRadius + (depth - 1) * minRingGap
                : minArcSpacing / angleStep;
            var radius = Math.Max(baseRadius + (depth - 1) * minRingGap, minRadiusByArc);
            radius = Math.Max(radius, previousRadius + minRingGap);
            previousRadius = radius;

            for (var i = 0; i < levelNodes.Count; i++)
            {
                var node = levelNodes[i];
                var angle = -Math.PI / 2 + i * angleStep;
                var nodeHeight = MeasureNodeHeight(node);
                var x = centerX + Math.Cos(angle) * radius - NodeWidth / 2;
                var y = centerY + Math.Sin(angle) * radius - nodeHeight / 2;
                var direction = Math.Cos(angle) < 0 ? -1 : 1;

                _layouts[node] = new NodeLayout(x, y, NodeWidth, nodeHeight, direction, depth);
            }
        }
    }

    private static List<List<MindMapNode>> GetVisibleLevels(MindMapNode root)
    {
        var levels = new List<List<MindMapNode>> { new() { root } };
        var currentLevel = new List<MindMapNode> { root };

        while (currentLevel.Count > 0)
        {
            var nextLevel = new List<MindMapNode>();
            foreach (var node in currentLevel)
            {
                if (!node.IsExpanded)
                    continue;

                foreach (var child in node.Children)
                    nextLevel.Add(child);
            }

            if (nextLevel.Count == 0)
                break;

            levels.Add(nextLevel);
            currentLevel = nextLevel;
        }

        return levels;
    }

    private MindMapNode? CurrentSearchMatch
        => _currentSearchMatchIndex >= 0 && _currentSearchMatchIndex < _searchMatches.Count
            ? _searchMatches[_currentSearchMatchIndex]
            : null;

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchQuery = SearchTextBox.Text ?? string.Empty;

        // RebuildMap() recreates every node/connection visual and recomputes the whole tree layout -
        // real work, so debounce it instead of running on every keystroke.
        _searchDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Tick -= SearchDebounceTimer_Tick;
        _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;
        _searchDebounceTimer.Start();
    }

    private void SearchDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _searchDebounceTimer?.Stop();
        RefreshSearchMatches(resetIndex: true);
        RebuildMap();
    }

    private void SearchPreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (_searchMatches.Count == 0)
            return;

        _currentSearchMatchIndex = (_currentSearchMatchIndex - 1 + _searchMatches.Count) % _searchMatches.Count;
        UpdateSearchResultsText();
        if (CurrentSearchMatch is { } match)
            ExpandAncestorsOf(match);
        CenterViewOnCurrentSearchMatch();
        RebuildMap();
    }

    private void SearchNextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_searchMatches.Count == 0)
            return;

        _currentSearchMatchIndex = (_currentSearchMatchIndex + 1) % _searchMatches.Count;
        UpdateSearchResultsText();
        if (CurrentSearchMatch is { } match)
            ExpandAncestorsOf(match);
        CenterViewOnCurrentSearchMatch();
        RebuildMap();
    }

    private void SearchReplaceButton_Click(object sender, RoutedEventArgs e)
    {
        var find = GetReplaceFindText();
        if (string.IsNullOrEmpty(find) || CurrentSearchMatch is not { } node)
            return;

        var replacement = ReplaceTextBox?.Text ?? string.Empty;
        PushUndoSnapshot();
        var replaced = ReplaceInNode(node, find, replacement);
        if (replaced == 0)
        {
            UpdateSearchReplaceButtonStates();
            return;
        }

        SelectSingleNode(node);
        RefreshSearchMatches(resetIndex: false);
        if (CurrentSearchMatch is { } match)
            ExpandAncestorsOf(match);
        CenterViewOnCurrentSearchMatch();
        RebuildMap();
        ShowSearchReplaceStatus(replaced);
    }

    private void SearchReplaceAllButton_Click(object sender, RoutedEventArgs e)
    {
        var find = GetReplaceFindText();
        if (string.IsNullOrEmpty(find))
            return;

        var replacement = ReplaceTextBox?.Text ?? string.Empty;
        var targets = EnumerateAllNodes(_root)
            .Where(node => NodeContainsFindText(node, find))
            .ToList();
        if (targets.Count == 0)
            return;

        PushUndoSnapshot();
        var total = 0;
        foreach (var node in targets)
            total += ReplaceInNode(node, find, replacement);

        if (total == 0)
        {
            UpdateSearchReplaceButtonStates();
            return;
        }

        RefreshSearchMatches(resetIndex: true);
        if (CurrentSearchMatch is { } match)
            ExpandAncestorsOf(match);
        CenterViewOnCurrentSearchMatch();
        RebuildMap();
        ShowSearchReplaceStatus(total);
    }

    private string GetReplaceFindText()
        => (SearchTextBox?.Text ?? string.Empty).Trim();

    private static bool NodeContainsFindText(MindMapNode node, string find)
        => (!string.IsNullOrEmpty(node.Text) && node.Text.Contains(find, StringComparison.CurrentCultureIgnoreCase))
           || (!string.IsNullOrEmpty(node.Note) && node.Note.Contains(find, StringComparison.CurrentCultureIgnoreCase));

    // Case-insensitive literal replace in title + note. Title is re-normalized so length/whitespace
    // rules stay consistent with EditNodeText.
    private static int ReplaceInNode(MindMapNode node, string find, string replacement)
    {
        var count = 0;

        var text = node.Text ?? string.Empty;
        var (newText, textCount) = ReplaceIgnoreCase(text, find, replacement);
        if (textCount > 0)
        {
            node.Text = NormalizeNodeText(newText);
            count += textCount;
        }

        var note = node.Note ?? string.Empty;
        var (newNote, noteCount) = ReplaceIgnoreCase(note, find, replacement);
        if (noteCount > 0)
        {
            node.Note = string.IsNullOrWhiteSpace(newNote) ? null : newNote;
            count += noteCount;
        }

        return count;
    }

    private static (string Result, int Count) ReplaceIgnoreCase(string input, string find, string replacement)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(find))
            return (input, 0);

        var comparison = StringComparison.CurrentCultureIgnoreCase;
        var count = 0;
        var index = 0;
        var builder = new System.Text.StringBuilder(input.Length);
        while (index < input.Length)
        {
            var match = input.IndexOf(find, index, comparison);
            if (match < 0)
            {
                builder.Append(input, index, input.Length - index);
                break;
            }

            builder.Append(input, index, match - index);
            builder.Append(replacement);
            count++;
            index = match + find.Length;
        }

        return (builder.ToString(), count);
    }

    private void ShowSearchReplaceStatus(int replacedCount)
    {
        if (SearchResultsTextBlock is null)
            return;

        SearchResultsTextBlock.Text = string.Format(
            CultureInfo.CurrentCulture,
            LocalizationService.GetString("FindReplaceReplacedCountFormat"),
            replacedCount);
        UpdateSearchReplaceButtonStates();
    }

    private void UpdateSearchReplaceButtonStates()
    {
        var canReplace = !string.IsNullOrEmpty(GetReplaceFindText()) && _searchMatches.Count > 0;
        if (SearchReplaceButton is not null)
            SearchReplaceButton.IsEnabled = canReplace && CurrentSearchMatch is not null;
        if (SearchReplaceAllButton is not null)
            SearchReplaceAllButton.IsEnabled = canReplace;
    }

    private void RefreshSearchMatches(bool resetIndex)
    {
        var previousCurrent = CurrentSearchMatch;

        _searchMatches.Clear();
        var tokens = GetSearchTokens(_searchQuery);
        if (tokens.Count > 0)
        {
            // Every node, not just currently-visible ones - a match hidden inside a collapsed
            // branch is exactly the case find needs to help with, not one it should skip.
            foreach (var node in EnumerateAllNodes(_root))
            {
                if (IsSearchMatch(node, tokens))
                    _searchMatches.Add(node);
            }
        }

        if (_searchMatches.Count == 0)
        {
            _currentSearchMatchIndex = -1;
        }
        else if (resetIndex)
        {
            _currentSearchMatchIndex = 0;
        }
        else if (previousCurrent is not null && _searchMatches.IndexOf(previousCurrent) is var existingIndex && existingIndex >= 0)
        {
            _currentSearchMatchIndex = existingIndex;
        }
        else
        {
            _currentSearchMatchIndex = Math.Clamp(_currentSearchMatchIndex, 0, _searchMatches.Count - 1);
        }

        UpdateSearchResultsText();
        UpdateSearchReplaceButtonStates();

        if (CurrentSearchMatch is { } match)
            ExpandAncestorsOf(match);
    }

    // Reveals a node found by search that's inside a collapsed branch by expanding every
    // ancestor on the path down to it (not the node itself - only what's hiding it).
    private void ExpandAncestorsOf(MindMapNode node)
        => ExpandPathTo(_root, node);

    private static bool ExpandPathTo(MindMapNode current, MindMapNode target)
    {
        if (ReferenceEquals(current, target))
            return true;

        foreach (var child in current.Children)
        {
            if (ExpandPathTo(child, target))
            {
                current.IsExpanded = true;
                return true;
            }
        }

        return false;
    }

    private static List<string> GetSearchTokens(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        return query
            .Split([' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToList();
    }

    private static bool IsSearchMatch(MindMapNode node, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
            return false;

        var text = node.Text ?? string.Empty;
        var note = node.Note ?? string.Empty;
        return tokens.All(token =>
            text.Contains(token, StringComparison.CurrentCultureIgnoreCase) ||
            note.Contains(token, StringComparison.CurrentCultureIgnoreCase));
    }

    private static IEnumerable<MindMapNode> EnumerateVisibleNodes(MindMapNode root)
    {
        yield return root;

        if (!root.IsExpanded)
            yield break;

        foreach (var child in root.Children)
        {
            foreach (var descendant in EnumerateVisibleNodes(child))
                yield return descendant;
        }
    }

    private void UpdateSearchResultsText()
    {
        if (SearchResultsTextBlock is null)
        {
            UpdateSearchReplaceButtonStates();
            return;
        }

        if (string.IsNullOrWhiteSpace(_searchQuery))
        {
            SearchResultsTextBlock.Text = LocalizationService.GetString("MindMapSearchNoResults");
            UpdateSearchReplaceButtonStates();
            return;
        }

        if (_searchMatches.Count == 0)
        {
            SearchResultsTextBlock.Text = string.Format(
                CultureInfo.CurrentCulture,
                LocalizationService.GetString("MindMapSearchResultsFormat"),
                0,
                0);
            UpdateSearchReplaceButtonStates();
            return;
        }

        SearchResultsTextBlock.Text = string.Format(
            CultureInfo.CurrentCulture,
            LocalizationService.GetString("MindMapSearchResultsFormat"),
            _currentSearchMatchIndex + 1,
            _searchMatches.Count);
        UpdateSearchReplaceButtonStates();
    }

    private void CenterViewOnCurrentSearchMatch()
    {
        if (CurrentSearchMatch is null)
            return;

        if (_layouts.TryGetValue(CurrentSearchMatch, out var layout))
            CenterViewOnLayout(layout);
    }

    private void QueueMapViewportRefresh()
    {
        if (_mapViewportRefreshQueued)
            return;

        _mapViewportRefreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _mapViewportRefreshQueued = false;
            if (!ContentRoot.IsLoaded)
                return;

            RefreshMapViewportSize();
        }, DispatcherPriority.Loaded);
    }

    private void MapScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        => QueueMapViewportRefresh();

    private void RefreshMapViewportSize()
    {
        MapScrollViewer?.UpdateLayout();
        UpdateMapCanvasSize();
    }

    private void UpdateMapCanvasSize()
    {
        if (_layouts.Count == 0 || MapScrollViewer is null || MapCanvas is null)
            return;

        // Zoom-independent canvas: fitted to the viewport at the MINIMUM zoom so the scrollable
        // extent (and the scrollbars, and the clamping window) never change during a zoom
        // gesture. Pointer-anchored zoom math is then exact on every step - the content under
        // the cursor stays pinned, no cumulative clamp drift from a shifting extent.
        const double minZoom = 0.45;
        var maxRight = _layouts.Values.Max(layout => layout.X + layout.Width);
        var maxBottom = _layouts.Values.Max(layout => layout.Y + layout.Height);

        var viewportWidth = MapScrollViewer.ViewportWidth;
        var viewportHeight = MapScrollViewer.ViewportHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            viewportWidth = MapScrollViewer.ActualWidth;
            viewportHeight = MapScrollViewer.ActualHeight;
        }

        MapCanvas.Width = Math.Max(viewportWidth / minZoom, maxRight + CanvasPadding);
        MapCanvas.Height = Math.Max(viewportHeight / minZoom, maxBottom + CanvasPadding + NodeSelectionDock.ScrollClearance / Math.Max(.1, MapScaleTransform.ScaleY));

        // Keep the transformed wrapper at the canvas size directly: during a tile session the
        // canvas is collapsed, so its SizeChanged never fires and the wrapper (hence the
        // scrollable extent) would go stale - which made every zoom ScrollTo land wrong and
        // the viewport "reshift" once the canvas returned.
        if (MapViewport is not null)
        {
            MapViewport.MinWidth = MapCanvas.Width;
            MapViewport.MinHeight = MapCanvas.Height;
        }
    }

    private void QueueInitialCenterOnRoot()
    {
        if (_hasCenteredOnRootInitially)
            return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_hasCenteredOnRootInitially || !_layouts.TryGetValue(_root, out var rootLayout))
                return;

            if (MapScrollViewer.ViewportWidth <= 0 || MapScrollViewer.ViewportHeight <= 0)
                return;

            CenterViewOnLayout(rootLayout);
            _hasCenteredOnRootInitially = true;
        }), DispatcherPriority.Loaded);
    }

    private void CenterViewOnRoot()
    {
        if (_layouts.TryGetValue(_root, out var rootLayout))
            CenterViewOnLayout(rootLayout);
    }

    private void CenterViewOnLayout(NodeLayout layout)
    {
        var zoom = Math.Max(ZoomSlider.Value, 0.1);
        var targetX = (layout.X + layout.Width / 2) * zoom - MapScrollViewer.ViewportWidth / 2;
        var targetY = (layout.Y + layout.Height / 2) * zoom - MapScrollViewer.ViewportHeight / 2;

        MapScrollViewer.ScrollToHorizontalOffset(Math.Clamp(targetX, 0, Math.Max(0, MapScrollViewer.ScrollableWidth)));
        MapScrollViewer.ScrollToVerticalOffset(Math.Clamp(targetY, 0, Math.Max(0, MapScrollViewer.ScrollableHeight)));
    }

    private double MeasureChildrenHeight(IReadOnlyList<MindMapNode> children)
    {
        if (children.Count == 0)
            return 0;

        return children.Sum(MeasureSubtree)
            + VerticalGap * Math.Max(0, children.Count - 1);
    }

    private double MeasureSubtree(MindMapNode node)
    {
        var ownHeight = MeasureNodeHeight(node);
        if (!node.IsExpanded || node.Children.Count == 0)
            return ownHeight;

        var childrenHeight = node.Children.Sum(MeasureSubtree)
            + VerticalGap * Math.Max(0, node.Children.Count - 1);

        return Math.Max(ownHeight, childrenHeight);
    }

    private double MeasureVerticalSubtree(MindMapNode node)
    {
        if (!node.IsExpanded || node.Children.Count == 0)
            return NodeWidth;

        var childrenWidth = node.Children.Sum(MeasureVerticalSubtree)
            + VerticalLayoutNodeGap * Math.Max(0, node.Children.Count - 1);

        return Math.Max(NodeWidth, childrenWidth);
    }

    private Typeface ResolveNodeTypeface(MindMapNode node, out double fontSize)
    {
        fontSize = node.FontSize ?? DefaultNodeFontSize;
        var fontFamily = !string.IsNullOrWhiteSpace(node.FontFamily)
            ? new FontFamily(node.FontFamily)
            : DefaultNodeFontFamily;

        // DrawNodes paints the root SemiBold when not explicitly bold — measure must match or the
        // title wraps taller than reserved height and pushes the meta/icon row under the clip.
        var weight = node.IsBold
            ? FontWeights.Bold
            : ReferenceEquals(node, LayoutRoot)
                ? FontWeights.SemiBold
                : FontWeights.Normal;

        return new Typeface(
            fontFamily,
            node.IsItalic ? FontStyles.Italic : FontStyles.Normal,
            weight,
            FontStretches.Normal);
    }

    // Recomputes the whole map's shared node width from visible nodes. Prefers a width where each
    // title wraps to about PreferredTitleLines (not single-line width), so large fonts / longer
    // phrases widen the column modestly instead of stacking into a tall narrow tower. Collapsed
    // nodes are ignored; expand/collapse rebuilds and re-measures.
    private double ComputeEffectiveNodeWidth(MindMapNode root)
    {
        var maxWidth = BaseNodeWidth;

        foreach (var node in EnumerateVisibleNodes(root))
        {
            var typeface = ResolveNodeTypeface(node, out var fontSize);
            var contentWidth = Math.Max(MeasurePreferredContentWidth(node.Text, typeface, fontSize),
                MeasureMetaContentWidth(node));
            var neededWidth = contentWidth
                + NodeTextHorizontalPadding
                + EstimateChevronReserve(node)
                + GetContentBorderThickness(node) * 2;
            maxWidth = Math.Max(maxWidth, Math.Min(neededWidth, MaxNodeWidth));
        }

        return maxWidth;
    }

    private double MeasureMetaContentWidth(MindMapNode node)
    {
        // Dense combinations (badges, folded count, note and link) must fit the wider gaps.
        var icons = MindMapNodeIconCatalog.NormalizeStoredIcons(node.Icons);
        var visible = Math.Min(icons.Count, MaxVisibleNodeIcons);
        var overflow = icons.Count - visible;
        var width = visible > 0 ? visible * (MetaGlyphSize + MetaItemGap) + MetaItemGap : 0;
        double LabelWidth(string text) => MeasureWrappedText(text,
            new Typeface(DefaultNodeFontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            TypeScale.Px(TypeScale.Chip, 11), double.PositiveInfinity).Width;
        if (overflow > 0)
            width += LabelWidth(string.Format(CultureInfo.InvariantCulture,
                LocalizationService.GetString("MindMapIconOverflowFormat"), overflow));
        if (node.HasChildren && !node.IsExpanded)
            width += LabelWidth((EnumerateAllNodes(node).Count() - 1).ToString(CultureInfo.InvariantCulture)) + MetaItemGap;
        if (!string.IsNullOrWhiteSpace(node.Note)) width += MetaRowHeight + MetaItemGap;
        if (node.LinkedNoteId.HasValue) width += MetaRowHeight;
        return width;
    }

    // TextBlock.Measure matches what DrawNodes actually paints; FormattedText often under-reports
    // wrapped height (especially with custom fonts / DPI), leaving cards one line short.
    private static Size MeasureWrappedText(string? text, Typeface typeface, double fontSize, double maxWidth)
    {
        var block = new TextBlock
        {
            Text = string.IsNullOrEmpty(text) ? " " : text,
            FontFamily = typeface.FontFamily,
            FontSize = fontSize,
            FontWeight = typeface.Weight,
            FontStyle = typeface.Style,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Left,
            Padding = new Thickness(0),
            Margin = new Thickness(0)
        };
        block.Measure(new Size(Math.Max(20, maxWidth), double.PositiveInfinity));
        return block.DesiredSize;
    }

    private static double MeasureLineHeight(Typeface typeface, double fontSize)
        => Math.Max(1, MeasureWrappedText("X", typeface, fontSize, double.PositiveInfinity).Height);

    // Smallest content width where the title fits in PreferredTitleLines (binary search). Short
    // titles return near their single-line width so the shared column stays dense.
    private static double MeasurePreferredContentWidth(string? text, Typeface typeface, double fontSize)
    {
        var source = string.IsNullOrEmpty(text) ? " " : text;
        var oneLineWidth = Math.Max(20, MeasureWrappedText(source, typeface, fontSize, double.PositiveInfinity).Width);
        var oneLineHeight = MeasureLineHeight(typeface, fontSize);
        var targetHeight = oneLineHeight * PreferredTitleLines + 0.5;

        var lo = Math.Max(20, oneLineWidth / (PreferredTitleLines * 2.0));
        var hi = oneLineWidth;
        for (var i = 0; i < 18; i++)
        {
            var mid = (lo + hi) * 0.5;
            var probeHeight = MeasureWrappedText(source, typeface, fontSize, mid).Height;
            if (probeHeight <= targetHeight)
                hi = mid;
            else
                lo = mid;
        }

        return Math.Clamp(hi, 20, oneLineWidth);
    }

    private double GetTitleContentWidth(MindMapNode node)
        => Math.Max(
            20,
            NodeWidth
            - NodeTextHorizontalPadding
            - EstimateChevronReserve(node)
            - GetContentBorderThickness(node) * 2);

    // Truncates wrapped title to MaxTitleLines with an ellipsis; full text belongs in a tooltip.
    private static string FitTitleForDisplay(
        string? text,
        Typeface typeface,
        double fontSize,
        double maxWidth,
        out bool truncated)
    {
        truncated = false;
        var source = text ?? string.Empty;
        if (source.Length == 0)
            return source;

        // Slightly tighter than layout width so live wrap cannot spill past reserved height.
        var fitWidth = Math.Max(20, maxWidth - 2);
        var maxHeight = MeasureLineHeight(typeface, fontSize) * MaxTitleLines + 0.5;

        if (MeasureWrappedText(source, typeface, fontSize, fitWidth).Height <= maxHeight)
            return source;

        truncated = true;
        const string ellipsis = "…";
        var lo = 0;
        var hi = source.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            var candidate = source.AsSpan(0, mid).TrimEnd().ToString() + ellipsis;
            if (MeasureWrappedText(candidate, typeface, fontSize, fitWidth).Height <= maxHeight)
                lo = mid;
            else
                hi = mid - 1;
        }

        if (lo <= 0)
            return ellipsis;

        return source.AsSpan(0, lo).TrimEnd().ToString() + ellipsis;
    }

    // WPF BorderThickness is inset: it eats the fixed Width/Height. Measure must reserve it or
    // title/meta sit under the stroke when the user picks a thick border (or selection bumps to 2).
    private static double GetContentBorderThickness(MindMapNode node)
    {
        var painted = node.BorderThickness > 0 ? node.BorderThickness : 1.0;
        return Math.Max(painted, 2.0);
    }

    // Height tracks the real wrapped TextBlock (same typeface/DPI/ellipsize as DrawNodes) so a
    // second+ line grows the card instead of clipping inside a one-line shell.
    private double MeasureNodeHeight(MindMapNode node)
    {
        var typeface = ResolveNodeTypeface(node, out var fontSize);
        var borderReserve = GetContentBorderThickness(node) * 2;
        var availableWidth = GetTitleContentWidth(node);

        var displayTitle = FitTitleForDisplay(node.Text, typeface, fontSize, availableWidth, out _);
        var titleHeight = Math.Ceiling(MeasureWrappedText(displayTitle, typeface, fontSize, availableWidth).Height);

        var height = titleHeight + NodeTextVerticalPadding + borderReserve + NodeHeightSlack;
        if (HasMetaRow(node))
            height += TitleMetaGap + MetaRowHeight;

        return Math.Clamp(height, MinNodeHeight + borderReserve, MaxNodeHeight);
    }

    private static int GetMaxVisibleDepth(MindMapNode node)
    {
        if (!node.IsExpanded || node.Children.Count == 0)
            return 1;

        return 1 + node.Children.Max(GetMaxVisibleDepth);
    }

    private void AssignChildrenLayout(
        IReadOnlyList<MindMapNode> children,
        double rootX,
        int direction,
        int depth,
        double top)
    {
        foreach (var child in children)
        {
            var childHeight = MeasureSubtree(child);
            AssignLayout(child, rootX, direction, depth, top, childHeight);
            top += childHeight + VerticalGap;
        }
    }

    private void AssignLayout(MindMapNode node, double rootX, int direction, int depth, double top, double subtreeHeight)
    {
        var ownHeight = MeasureNodeHeight(node);
        var x = rootX + direction * depth * (NodeWidth + HorizontalGap);
        var y = top + (subtreeHeight - ownHeight) / 2;
        _layouts[node] = new NodeLayout(x, y, NodeWidth, ownHeight, direction, depth);

        if (!node.IsExpanded || node.Children.Count == 0)
            return;

        var childrenHeight = MeasureChildrenHeight(node.Children);
        var childTop = top + Math.Max(0, (subtreeHeight - childrenHeight) / 2);

        foreach (var child in node.Children)
        {
            var childHeight = MeasureSubtree(child);
            AssignLayout(child, rootX, direction, depth + 1, childTop, childHeight);
            childTop += childHeight + VerticalGap;
        }
    }

    private void AssignVerticalChildrenLayout(
        IReadOnlyList<MindMapNode> children,
        double left,
        double top,
        double availableWidth,
        int depth)
    {
        if (children.Count == 0)
            return;

        var childrenWidth = children.Sum(MeasureVerticalSubtree)
            + VerticalLayoutNodeGap * Math.Max(0, children.Count - 1);
        var childLeft = left + Math.Max(0, (availableWidth - childrenWidth) / 2);

        foreach (var child in children)
        {
            var childWidth = MeasureVerticalSubtree(child);
            AssignVerticalLayout(child, childLeft, top, childWidth, depth);
            childLeft += childWidth + VerticalLayoutNodeGap;
        }
    }

    private void AssignVerticalLayout(MindMapNode node, double left, double top, double subtreeWidth, int depth)
    {
        var ownHeight = MeasureNodeHeight(node);
        var x = left + (subtreeWidth - NodeWidth) / 2;
        var y = top;

        _layouts[node] = new NodeLayout(x, y, NodeWidth, ownHeight, Direction: 2, Depth: depth);

        if (!node.IsExpanded || node.Children.Count == 0)
            return;

        AssignVerticalChildrenLayout(
            node.Children,
            left,
            top + ownHeight + VerticalLayoutLevelGap,
            subtreeWidth,
            depth + 1);
    }

    private void NormalizeLayoutOffset()
    {
        var minX = _layouts.Values.Min(layout => layout.X);
        var minY = _layouts.Values.Min(layout => layout.Y);
        var offsetX = minX < CanvasPadding ? CanvasPadding - minX : 0;
        var offsetY = minY < CanvasPadding ? CanvasPadding - minY : 0;

        if (offsetX <= 0 && offsetY <= 0)
            return;

        foreach (var (node, layout) in _layouts.ToList())
            _layouts[node] = layout with { X = layout.X + offsetX, Y = layout.Y + offsetY };
    }

    private void RedrawConnections()
    {
        foreach (var path in MapCanvas.Children.OfType<ShapePath>().ToList())
            MapCanvas.Children.Remove(path);
        foreach (var line in MapCanvas.Children.OfType<ShapeLine>().ToList())
            MapCanvas.Children.Remove(line);

        DrawConnections(_root);
        DrawCrossLinks();
    }

    // Second connector pass, alongside DrawConnections' tree edges - draws a visually distinct
    // (dashed) straight line center-to-center between a node and each of its cross-link targets.
    // Iterates _layouts directly rather than walking the tree, since that's already exactly the
    // set of currently-visible nodes; a target hidden inside a collapsed branch (or deleted, in
    // which case FindNodeById returns null) is silently skipped rather than drawn as a dangling
    // stub - it reappears automatically once that branch is expanded again.
    private void DrawCrossLinks()
    {
        foreach (var (node, sourceLayout) in _layouts)
        {
            if (node.CrossLinkTargetIds.Count == 0)
                continue;

            foreach (var targetId in node.CrossLinkTargetIds)
            {
                var target = FindNodeById(_root, targetId);
                if (target is null || !_layouts.TryGetValue(target, out var targetLayout))
                    continue;

                var line = new ShapeLine
                {
                    Tag = (node, target),
                    X1 = sourceLayout.X + sourceLayout.Width / 2,
                    Y1 = sourceLayout.Y + sourceLayout.Height / 2,
                    X2 = targetLayout.X + targetLayout.Width / 2,
                    Y2 = targetLayout.Y + targetLayout.Height / 2,
                    Stroke = GetThemeBrush("MindMapCrossLinkBrush", Color.FromRgb(168, 85, 247)),
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                    Opacity = 0.85,
                    IsHitTestVisible = false
                };

                Panel.SetZIndex(line, ConnectionZIndex);
                MapCanvas.Children.Add(line);
            }
        }
    }

    private void QueueConnectionRedraw()
    {
        if (_connectionRedrawQueued)
            return;

        _connectionRedrawQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _connectionRedrawQueued = false;
            RedrawConnections();
            if (_isDraggingNode)
                UpdateMapCanvasSize();
        }, DispatcherPriority.Render);
    }

    private void BeginManualNodeDrag(MindMapNode node, Border border)
    {
        // Ctrl held at drag-start means "link, don't move" - checked here (not at mouse-down)
        // so it reflects whatever's held right when the drag actually begins.
        _isLinkDragGesture = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        PrepareGroupDrag(node);

        if (_isManualPositioningMode && !_isLinkDragGesture)
        {
            // Manual mode live-updates ManualX/Y on every mouse-move for the rest of the gesture
            // (see UpdateManualNodeDragPosition), so the pre-drag position has to be captured now,
            // before anything moves - by FinalizeManualNodeDrag it would already be gone.
            // Auto-layout-mode drags (and any Ctrl+drag link gesture, regardless of mode) don't
            // persist a position at all - the only mutation they can produce is a reparent (or
            // cross-link) at drop time, so that snapshot is pushed lazily inside ReparentNode/
            // AddCrossLink instead, right before it happens. That keeps a plain "drag then drop
            // on empty space" from leaving a no-op entry on the undo stack.
            PushUndoSnapshot();
        }

        _isManualDragPending = false;
        _pendingDragNode = null;
        _pendingDragBorder = null;
        _isDraggingNode = true;
        _draggedNode = node;
        _draggedBorder = border;

        MapCanvas.CaptureMouse();
        Panel.SetZIndex(border, DraggingNodeZIndex);
        ApplyNodeDragChrome(border, isDragging: true);
        if (!_selectedNodes.Contains(node) || _selectedNodes.Count < 2) SelectSingleNode(node);
        ApplyNodeSelectionVisuals();
    }

    private void UpdateManualNodeDragPosition(Point currentCanvasPos)
    {
        if (!_isDraggingNode || _draggedNode is null || _draggedBorder is null)
            return;

        if (MoveGroupDrag(currentCanvasPos)) return;
        if (!_layouts.TryGetValue(_draggedNode, out var layout))
            return;

        var dx = currentCanvasPos.X - _dragStartMousePos.X;
        var dy = currentCanvasPos.Y - _dragStartMousePos.Y;
        var newX = Math.Max(CanvasPadding, _dragStartNodePos.X + dx);
        var newY = Math.Max(CanvasPadding, _dragStartNodePos.Y + dy);

        // Only a plain manual-mode drag persists the position as it moves. Auto-layout mode (and
        // any Ctrl+drag link gesture, regardless of mode) still needs the node to visually follow
        // the cursor for drag feedback, but that offset is transient - RebuildMap (always called
        // at drop, whether reparented/linked or snapped back) recomputes real layouts from
        // scratch and discards it.
        if (_isManualPositioningMode && !_isLinkDragGesture)
            SetManualPosition(_draggedNode, new Point(newX, newY));

        _layouts[_draggedNode] = layout with { X = newX, Y = newY };
        Canvas.SetLeft(_draggedBorder, newX);
        Canvas.SetTop(_draggedBorder, newY);
        QueueConnectionRedraw();

        if (!ReferenceEquals(_draggedNode, _root))
            UpdateDropTargetHover(FindDropTargetNode(_draggedNode, currentCanvasPos));
    }

    private static void ApplyNodeDragChrome(Border border, bool isDragging)
    {
        border.Cursor = isDragging ? Cursors.SizeAll : Cursors.Hand;
    }

    private void CancelManualDragPending()
    {
        _isManualDragPending = false;
        _pendingDragNode = null;
        _pendingDragBorder = null;
    }

    private void MapCanvas_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingNode)
            return;

        if (!_isManualDragPending || _pendingDragNode is null || _pendingDragBorder is null)
            return;

        var currentPos = e.GetPosition(MapCanvas);
        var dx = currentPos.X - _dragStartMousePos.X;
        var dy = currentPos.Y - _dragStartMousePos.Y;
        if (Math.Abs(dx) < ManualDragThreshold && Math.Abs(dy) < ManualDragThreshold)
            return;

        BeginManualNodeDrag(_pendingDragNode, _pendingDragBorder);
        UpdateManualNodeDragPosition(currentPos);
        e.Handled = true;
    }

    private void MapCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingNode)
            return;

        UpdateManualNodeDragPosition(e.GetPosition(MapCanvas));
        e.Handled = true;
    }

    private void MapCanvas_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingNode && _draggedNode is not null && _draggedBorder is not null)
        {
            FinalizeManualNodeDrag(_draggedNode, _draggedBorder, e.GetPosition(MapCanvas));
            CancelManualDragPending();
            e.Handled = true;
            return;
        }

        CancelManualDragPending();
    }

    private void MapCanvas_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_isDraggingNode || _draggedNode is null || _draggedBorder is null)
        {
            CancelManualDragPending();
            return;
        }

        FinalizeManualNodeDrag(_draggedNode, _draggedBorder, e.GetPosition(MapCanvas));
        CancelManualDragPending();
    }

    private void ApplyNodeBorderChrome(Border border, MindMapNode node, bool animate = true)
    {
        var isRoot = ReferenceEquals(node, _root);
        var defaultThickness = node.BorderThickness > 0
            ? node.BorderThickness
            : isRoot ? 1.5 : 1;
        border.BorderThickness = new Thickness(defaultThickness);
        border.BorderBrush = !string.IsNullOrWhiteSpace(node.BorderColor)
            ? FrozenBrush((Color)ColorConverter.ConvertFromString(node.BorderColor))
            : isRoot
                ? GetThemeBrush("MindMapNodeRootBorderBrush", Color.FromRgb(47, 92, 208))
                : GetThemeBrush("MindMapNodeBorderBrush", Color.FromRgb(194, 203, 220));
        border.Effect = null;

        if (border.Child is null) return;
        var (resource, thickness) = ResolveNodeRim(node);
        AccentMotion.Set(border, resource is not null, resource is null ? null : TryFindResource(resource) as Brush,
            thickness: thickness, animate: animate);
    }

    private (string? Resource, double Thickness) ResolveNodeRim(MindMapNode node)
    {
        if (ReferenceEquals(node, _dropTargetHoverNode)) return ("AccentRimBrush", 2.5);
        if (_selectedNodes.Contains(node)) return ("AccentRimBrush", 2);
        if (ReferenceEquals(node, CurrentSearchMatch)) return ("MindMapSearchActiveBorderBrush", 2);
        return _searchMatches.Contains(node) ? ("MindMapSearchMatchBorderBrush", 2) : (null, 0d);
    }

    private void ApplyNodeSelectionVisuals()
    {
        foreach (var border in MapCanvas.Children.OfType<Border>())
        {
            if (border.Tag is MindMapNode node)
                ApplyNodeBorderChrome(border, node);
        }

        RefreshFontPanelFromSelection();
        SyncNodeSelection();
        InvalidateMapSnapshot();
    }

    private void InvalidateMapSnapshot()
    {
        _tileRevision++;
        _snapshotSourceRevision = -1;
        if (_tileSessionActive) RestoreCanvas();
        if (_snapshotWarmQueued || _mapClosed) return;
        _snapshotWarmQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _snapshotWarmQueued = false;
            if (!_mapClosed && !_tileSessionActive && !_isDraggingNode) WarmSnapshot();
        }, DispatcherPriority.Loaded);
    }

    private void FinalizeManualNodeDrag(MindMapNode node, Border border, Point dropPosition)
    {
        _isDraggingNode = false;
        _draggedNode = null;
        _draggedBorder = null;
        UpdateDropTargetHover(null);
        var isLinkGesture = _isLinkDragGesture;
        _isLinkDragGesture = false;

        if (MapCanvas.IsMouseCaptured)
            MapCanvas.ReleaseMouseCapture();

        if (_groupDragStarts.Count > 1)
        {
            _groupDragStarts.Clear(); Panel.SetZIndex(border, NodeZIndex); ApplyNodeDragChrome(border, false);
            ApplyNodeSelectionVisuals(); UpdateMapCanvasSize(); UpdateEditedIndicator(); return;
        }
        var dropTarget = ReferenceEquals(node, _root) ? null : FindDropTargetNode(node, dropPosition);

        if (isLinkGesture)
        {
            if (dropTarget is not null)
                AddCrossLink(node, dropTarget);

            // A link gesture never repositions anything in any mode - always rebuild from the
            // real tree so the node snaps back to wherever it actually belongs.
            RebuildMap();
            return;
        }

        var reparented = _isManualPositioningMode
            && dropTarget is not null
            && ReparentNode(node, dropTarget);

        if (reparented)
        {
            SelectSingleNode(node);
            RebuildMap();
            return;
        }

        if (!_isManualPositioningMode)
        {
            // Nothing structural happened and auto layout never persisted the transient drag
            // offset - rebuild from the real tree so the node lands back on its computed
            // position instead of staying stranded wherever the cursor let go.
            RebuildMap();
            return;
        }

        Panel.SetZIndex(border, NodeZIndex);
        ApplyNodeDragChrome(border, isDragging: false);
        SelectSingleNode(node);
        ApplyNodeBorderChrome(border, node);
        RedrawConnections();
        UpdateMapCanvasSize();
        UpdateEditedIndicator();
    }

    // Finds the smallest-area node whose current layout bounds contain the given canvas point,
    // excluding the node being dragged and anything inside its own subtree (dropping a node onto
    // its own descendant would create a cycle). Smallest-area wins so that a drop over a nested
    // node's bounds targets that node rather than an ancestor whose bounds happen to overlap too.
    private MindMapNode? FindDropTargetNode(MindMapNode draggedNode, Point canvasPos)
    {
        MindMapNode? best = null;
        var bestArea = double.MaxValue;

        foreach (var (candidate, layout) in _layouts)
        {
            if (ReferenceEquals(candidate, draggedNode) || IsAncestorNode(draggedNode, candidate))
                continue;

            if (canvasPos.X < layout.X || canvasPos.X > layout.X + layout.Width ||
                canvasPos.Y < layout.Y || canvasPos.Y > layout.Y + layout.Height)
                continue;

            var area = layout.Width * layout.Height;
            if (area < bestArea)
            {
                best = candidate;
                bestArea = area;
            }
        }

        return best;
    }

    // Moves node under newParent. Returns false (no-op, no undo entry) if newParent is already
    // node's current parent - matching FreeMind, where dropping a node back onto the branch it
    // already belongs to doesn't do anything.
    private bool ReparentNode(MindMapNode node, MindMapNode newParent)
    {
        var oldParent = FindParentNode(_root, node);
        if (oldParent is null || ReferenceEquals(oldParent, newParent))
            return false;

        // Manual-mode drags already pushed their snapshot in BeginManualNodeDrag; auto-mode drags
        // never did (see the comment there), so this is where that deferred push happens.
        if (!_isManualPositioningMode)
            PushUndoSnapshot();

        oldParent.Children.Remove(node);
        newParent.Children.Add(node);
        newParent.IsExpanded = true;
        return true;
    }

    // Adds a graphical cross-link from source to target (Ctrl+drag one node onto another).
    // Stored one-directional on source only; checks both directions for an existing link so
    // dragging A onto B when B->A already exists doesn't create a visually-redundant second line.
    private bool AddCrossLink(MindMapNode source, MindMapNode target)
    {
        if (ReferenceEquals(source, target))
            return false;

        if (source.CrossLinkTargetIds.Contains(target.Id) || target.CrossLinkTargetIds.Contains(source.Id))
            return false;

        PushUndoSnapshot();
        source.CrossLinkTargetIds.Add(target.Id);
        SelectSingleNode(source);
        return true;
    }

    // Clears all of this node's outgoing cross-links at once, rather than offering removal of one
    // specific link at a time - simpler, and covers the common case (a node usually has at most
    // one or two cross-links) without a submenu picker for something this narrow.
    private void RemoveCrossLinks(MindMapNode node)
    {
        if (node.CrossLinkTargetIds.Count == 0)
            return;

        PushUndoSnapshot();
        node.CrossLinkTargetIds.Clear();
        RebuildMap();
    }

    private static MindMapNode? FindNodeById(MindMapNode root, Guid id)
    {
        if (root.Id == id)
            return root;

        foreach (var child in root.Children)
        {
            var found = FindNodeById(child, id);
            if (found is not null)
                return found;
        }

        return null;
    }

    private void UpdateDropTargetHover(MindMapNode? candidate)
    {
        if (ReferenceEquals(_dropTargetHoverNode, candidate))
            return;

        var previous = _dropTargetHoverNode;
        _dropTargetHoverNode = candidate;
        if (previous is not null)
        {
            var previousBorder = FindNodeBorder(previous);
            if (previousBorder is not null)
                ApplyNodeBorderChrome(previousBorder, previous);
        }

        if (candidate is not null)
        {
            var border = FindNodeBorder(candidate);
            if (border is not null)
            {
                ApplyNodeBorderChrome(border, candidate);
            }
        }
        InvalidateMapSnapshot();
    }

    private Border? FindNodeBorder(MindMapNode node)
    {
        foreach (var border in MapCanvas.Children.OfType<Border>())
        {
            if (ReferenceEquals(border.Tag, node))
                return border;
        }

        return null;
    }

    // Two-Border shadow-plate pattern matching the app's other floating chrome (e.g. context
    // menus in Themes/Controls/Chrome.xaml) - the DropShadowEffect goes on a separate
    // non-hit-testable sibling instead of directly on the text-bearing border, since a shadow
    // effect on the same element that renders text rasterizes (and blurs) the text too.
    private Popup BuildNotePopup(FrameworkElement placementTarget, string noteText)
    {
        var shadowBorder = new Border
        {
            Background = GetThemeBrush("CardBackground", Color.FromRgb(255, 255, 255)),
            BorderBrush = GetThemeBrush("PopupBorder", Color.FromRgb(208, 208, 220)),
            BorderThickness = new Thickness(1),
            CornerRadius = ResolveCornerRadiusToken("Radius.Control", 10),
            Effect = TryFindResource("Elevation.Popup") as Effect,
            IsHitTestVisible = false
        };

        var contentBorder = new SurfaceMaterialBorder
        {
            Background = GetThemeBrush("CardBackground", Color.FromRgb(255, 255, 255)),
            BorderBrush = GetThemeBrush("PopupBorder", Color.FromRgb(208, 208, 220)),
            BorderThickness = new Thickness(1),
            CornerRadius = ResolveCornerRadiusToken("Radius.Control", 10),
            Padding = new Thickness(12),
            MaxWidth = 320,
            Child = new ScrollViewer
            {
                Style = TryFindResource("PopupListScrollViewerStyle") as Style,
                Padding = new Thickness(4),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 280,
                Content = new TextBlock
                {
                    Text = noteText,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = DefaultNodeFontFamily,
                    FontSize = TypeScale.Px(TypeScale.Body, 14)
                }
            }
        };
        SurfaceMaterial.SetKind(contentBorder, SurfaceMaterialKind.Raised);
        contentBorder.SetResourceReference(Border.BackgroundProperty, "CardBackground");
        contentBorder.SetResourceReference(Border.BorderBrushProperty, "PopupBorder");
        shadowBorder.SetResourceReference(Border.BackgroundProperty, "CardBackground");
        shadowBorder.SetResourceReference(Border.BorderBrushProperty, "PopupBorder");
        ((TextBlock)((ScrollViewer)contentBorder.Child).Content).SetResourceReference(TextBlock.ForegroundProperty, "TextColor");

        // Padding-only outer border, matching Chrome.xaml's own popups - gives the shadow's blur
        // and offset room to render instead of getting clipped at the popup's own edge.
        var shell = new Grid { Margin = new Thickness(32), HorizontalAlignment = HorizontalAlignment.Left };
        shell.Children.Add(shadowBorder);
        shell.Children.Add(contentBorder);

        var popup = new Popup
        {
            PlacementTarget = placementTarget,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -32,
            VerticalOffset = -26,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None,
            Child = shell
        };
        popup.Opened += (_, _) =>
        {
            var workArea = FeatureWindowBounds.GetWorkArea(popup.Child);
            contentBorder.MaxWidth = Math.Max(1, Math.Min(320, workArea.Width - 64));
            ((ScrollViewer)contentBorder.Child).MaxHeight = Math.Max(1, Math.Min(280, workArea.Height - 88));
        };
        placementTarget.Unloaded += (_, _) => popup.IsOpen = false;
        UiMotion.AttachPopupOpenAnimation(popup);
        return popup;
    }

    private static double EstimateChevronReserve(MindMapNode node)
        => node.HasChildren ? ChevronHitSize + ChevronTitleGap : 0;

    private static bool HasMetaRow(MindMapNode node)
        => node.Icons.Count > 0
           || !string.IsNullOrWhiteSpace(node.Note)
           || node.LinkedNoteId.HasValue
           || (node.HasChildren && !node.IsExpanded);

    private CornerRadius ResolveCornerRadiusToken(string resourceKey, double fallback)
        => TryFindResource(resourceKey) is CornerRadius radius
            ? radius
            : new CornerRadius(fallback);

    private Brush GetCardSurfaceBrush(bool isRoot)
    {
        if (isRoot)
            return GetThemeBrush("MindMapNodeRootBackground", Color.FromRgb(245, 247, 251));

        return GetThemeBrush("CardBackground", Color.FromRgb(255, 255, 255));
    }

    private Brush GetCardTitleBrush()
        => GetThemeBrush("TextColor", Color.FromRgb(28, 28, 28));

    private Brush GetCardMetaBrush()
        => GetThemeBrush("TextColorSecondary", Color.FromRgb(92, 92, 92));

    private Button CreateFoldChevron(MindMapNode node)
    {
        var chevron = new Button
        {
            Style = TryFindResource("CompactGlyphButtonStyle") as Style,
            Width = ChevronHitSize,
            Height = ChevronHitSize,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, ChevronTitleGap, 0),
            ToolTip = LocalizationService.GetString(node.IsExpanded ? "MindMapCollapseThisNode" : "MindMapExpandThisNode"),
            Content = new AppIcon
            {
                Kind = node.IsExpanded ? AppIconKind.ChevronDown : AppIconKind.ChevronRight,
                Width = ChevronGlyphSize,
                Height = ChevronGlyphSize,
                StrokeThickness = 1.75,
                Foreground = GetCardMetaBrush(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        System.Windows.Automation.AutomationProperties.SetName(chevron, (string)chevron.ToolTip);
        chevron.Click += (_, e) =>
        {
            ToggleNodeExpansion(node);
            e.Handled = true;
        };

        return chevron;
    }

    private Button CreateMetaGlyphButton(AppIconKind kind, string toolTip, RoutedEventHandler? onClick = null)
    {
        var hit = new Button
        {
            Style = TryFindResource("CompactGlyphButtonStyle") as Style,
            Width = MetaRowHeight,
            Height = MetaRowHeight,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = toolTip,
            Content = new AppIcon
            {
                Kind = kind,
                Width = MetaGlyphSize,
                Height = MetaGlyphSize,
                StrokeThickness = 1.6,
                Foreground = GetCardMetaBrush(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        if (onClick is not null)
            hit.Click += onClick;
        System.Windows.Automation.AutomationProperties.SetName(hit, toolTip);

        return hit;
    }

    private UIElement? BuildMetaRow(MindMapNode node)
    {
        if (!HasMetaRow(node))
            return null;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(EstimateChevronReserve(node), TitleMetaGap, 0, 0)
        };

        if (node.Icons.Count > 0)
        {
            var normalized = MindMapNodeIconCatalog.NormalizeStoredIcons(node.Icons);
            var visible = normalized.Take(MaxVisibleNodeIcons).ToList();
            var overflow = normalized.Count - visible.Count;
            var iconBrush = !string.IsNullOrWhiteSpace(node.IconBadgeColor)
                ? FrozenBrush((Color)ColorConverter.ConvertFromString(node.IconBadgeColor))
                : GetCardMetaBrush();

            var iconsHost = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, MetaItemGap, 0),
                ToolTip = string.Join(", ", normalized)
            };

            foreach (var iconId in visible)
            {
                if (!MindMapNodeIconCatalog.TryParseKind(iconId, out var kind))
                    continue;

                iconsHost.Children.Add(new AppIcon
                {
                    Kind = kind,
                    Width = MetaGlyphSize,
                    Height = MetaGlyphSize,
                    StrokeThickness = 1.6,
                    Foreground = iconBrush,
                    Margin = new Thickness(0, 0, MetaItemGap, 0),
                    IsHitTestVisible = false
                });
            }

            if (overflow > 0)
            {
                iconsHost.Children.Add(new TextBlock
                {
                    Text = string.Format(CultureInfo.InvariantCulture, LocalizationService.GetString("MindMapIconOverflowFormat"), overflow),
                    FontSize = TypeScale.Px(TypeScale.Chip, 11),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = GetCardMetaBrush(),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            if (iconsHost.Children.Count > 0)
                row.Children.Add(iconsHost);
        }

        if (node.HasChildren && !node.IsExpanded)
        {
            var hiddenCount = EnumerateAllNodes(node).Count() - 1;
            row.Children.Add(new TextBlock
            {
                Text = hiddenCount.ToString(CultureInfo.InvariantCulture),
                FontSize = TypeScale.Px(TypeScale.Chip, 11),
                FontWeight = FontWeights.SemiBold,
                Foreground = GetCardMetaBrush(),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, MetaItemGap, 0),
                ToolTip = string.Format(LocalizationService.GetString("MindMapHiddenNodesCountFormat"), hiddenCount)
            });
        }

        if (!string.IsNullOrWhiteSpace(node.Note))
        {
            var noteHit = CreateMetaGlyphButton(AppIconKind.Notes, LocalizationService.GetString("MindMapClickToViewNote"));
            var notePopup = BuildNotePopup(noteHit, node.Note!);
            noteHit.Click += (_, e) =>
            {
                notePopup.IsOpen = !notePopup.IsOpen;
                e.Handled = true;
            };

            var noteHost = new Grid
            {
                Width = MetaRowHeight,
                Height = MetaRowHeight,
                Margin = new Thickness(0, 0, MetaItemGap, 0)
            };
            noteHost.Children.Add(notePopup);
            noteHost.Children.Add(noteHit);
            row.Children.Add(noteHost);
        }

        if (node.LinkedNoteId.HasValue)
        {
            row.Children.Add(CreateMetaGlyphButton(
                AppIconKind.Link,
                LocalizationService.GetString("MindMapOpenLinkedNote"),
                (_, e) =>
                {
                    OpenNodeLink(node);
                    e.Handled = true;
                }));
        }

        return row;
    }

    private void DrawConnections(MindMapNode node)
    {
        if (!node.IsExpanded || node.Children.Count == 0)
            return;

        var includeNode = _renderOnlyNodes is null || _renderOnlyNodes.Contains(node);
        var hasParentLayout = _layouts.TryGetValue(node, out var parentLayout);

        foreach (var child in node.Children)
        {
            var includeChild = _renderOnlyNodes is null || _renderOnlyNodes.Contains(child);

            if (includeNode && includeChild && hasParentLayout && parentLayout is not null && _layouts.TryGetValue(child, out var childLayout) && childLayout is not null)
            {
                var figure = childLayout.Direction == 2
                    ? CreateVerticalConnectionFigure(parentLayout, childLayout)
                    : CreateHorizontalConnectionFigure(parentLayout, childLayout);

                var geometry = new PathGeometry();
                geometry.Figures.Add(figure);
                geometry.Freeze();

                var path = new ShapePath
                {
                    Tag = (node, child),
                    Data = geometry,
                    Stroke = GetThemeBrush("MindMapConnectionBrush", Color.FromRgb(145, 158, 183)),
                    StrokeThickness = 2,
                    Opacity = 0.78
                };

                Panel.SetZIndex(path, ConnectionZIndex);
                MapCanvas.Children.Add(path);
            }

            DrawConnections(child);
        }
    }

    private static PathFigure CreateHorizontalConnectionFigure(NodeLayout parentLayout, NodeLayout childLayout)
    {
        var direction = childLayout.Direction < 0 ? -1 : 1;
        var start = direction < 0
            ? new Point(parentLayout.X, parentLayout.Y + parentLayout.Height / 2)
            : new Point(parentLayout.X + parentLayout.Width, parentLayout.Y + parentLayout.Height / 2);
        var end = direction < 0
            ? new Point(childLayout.X + childLayout.Width, childLayout.Y + childLayout.Height / 2)
            : new Point(childLayout.X, childLayout.Y + childLayout.Height / 2);
        var horizontalDistance = Math.Abs(end.X - start.X);
        var midOffset = Math.Max(42, horizontalDistance * 0.45);

        var figure = new PathFigure { StartPoint = start };
        figure.Segments.Add(direction < 0
            ? new BezierSegment(
                new Point(start.X - midOffset, start.Y),
                new Point(end.X + midOffset, end.Y),
                end,
                isStroked: true)
            : new BezierSegment(
                new Point(start.X + midOffset, start.Y),
                new Point(end.X - midOffset, end.Y),
                end,
                isStroked: true));

        return figure;
    }

    private static PathFigure CreateVerticalConnectionFigure(NodeLayout parentLayout, NodeLayout childLayout)
    {
        var start = new Point(parentLayout.X + parentLayout.Width / 2, parentLayout.Y + parentLayout.Height);
        var end = new Point(childLayout.X + childLayout.Width / 2, childLayout.Y);
        var verticalDistance = Math.Abs(end.Y - start.Y);
        var midOffset = Math.Max(42, verticalDistance * 0.45);

        var figure = new PathFigure { StartPoint = start };
        figure.Segments.Add(new BezierSegment(
            new Point(start.X, start.Y + midOffset),
            new Point(end.X, end.Y - midOffset),
            end,
            isStroked: true));

        return figure;
    }

    private void DrawNodes(MindMapNode node)
    {
        if (!_layouts.TryGetValue(node, out var layout))
            return;

        var includeNode = _renderOnlyNodes is null || _renderOnlyNodes.Contains(node);

        if (includeNode)
        {
        var isRoot = ReferenceEquals(node, _root);
        var nodeBackground = GetNodeBackground(node, layout.Depth);
        var border = new Border
        {
            Tag = node,
            Width = layout.Width,
            Height = layout.Height,
            CornerRadius = GetCornerRadius(node.NodeShape, isRoot),
            Background = nodeBackground,
            Padding = new Thickness(CardPaddingX, CardPaddingY, CardPaddingX, CardPaddingY),
            Cursor = Cursors.Hand
        };

        ApplyNodeBorderChrome(border, node);

        // Star title + Auto meta: if wrap still disagrees with measure, the icon row keeps its
        // space at the bottom instead of being pushed under the card clip.
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleRow = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };

        if (node.HasChildren)
        {
            var fold = CreateFoldChevron(node);
            DockPanel.SetDock(fold, Dock.Left);
            titleRow.Children.Add(fold);
        }

        var titleTypeface = ResolveNodeTypeface(node, out var titleFontSize);
        var titleContentWidth = GetTitleContentWidth(node);
        var displayTitle = FitTitleForDisplay(
            node.Text,
            titleTypeface,
            titleFontSize,
            titleContentWidth,
            out var titleTruncated);

        var lineHeight = MeasureLineHeight(titleTypeface, titleFontSize);
        var text = new TextBlock
        {
            Text = displayTitle,
            FontSize = titleFontSize,
            FontFamily = titleTypeface.FontFamily,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxHeight = lineHeight * MaxTitleLines + 1,
            FontWeight = titleTypeface.Weight,
            FontStyle = titleTypeface.Style,
            Foreground = !string.IsNullOrWhiteSpace(node.TextColor)
                ? FrozenBrush((Color)ColorConverter.ConvertFromString(node.TextColor))
                : GetNodeForeground(nodeBackground, isRoot, node),
            ToolTip = titleTruncated ? node.Text : null
        };

        titleRow.Children.Add(text);

        var titleHost = new Border
        {
            Child = titleRow,
            ClipToBounds = true,
            Background = Brushes.Transparent
        };
        Grid.SetRow(titleHost, 0);
        content.Children.Add(titleHost);

        if (BuildMetaRow(node) is { } metaRow)
        {
            Grid.SetRow(metaRow, 1);
            content.Children.Add(metaRow);
        }

        border.Child = content;
        ApplyNodeBorderChrome(border, node, animate: false);
        // One local light group per node keeps hit testing bounded on large maps and lets its
        // nested commands own the light instead of stacking a second wash behind them.
        CommandLightBehavior.SetIsScope(border, true);
        CommandLightBehavior.EnsureControlLight(border, CommandLightRole.Item);

            border.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2)
                {
                    EditNodeText(node);
                    e.Handled = true;
                    return;
                }

                // Manual mode: drag to reposition (and drop-to-reparent). Auto layout: nodes stay
                // put — structure changes use add/cut/paste, not free dragging. Ctrl+drag still
                // starts a gesture so cross-links remain available without moving the card.
                var wantsLinkGesture = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
                if (_isManualPositioningMode || wantsLinkGesture)
                {
                    _isManualDragPending = true;
                    _pendingDragNode = node;
                    _pendingDragBorder = border;
                    _dragStartMousePos = e.GetPosition(MapCanvas);
                    _dragStartNodePos = new Point(_layouts[node].X, _layouts[node].Y);
                    e.Handled = true;
                }
            };

            border.MouseLeftButtonUp += (_, e) =>
            {
                if (_isDraggingNode)
                    return;

                CancelManualDragPending();

                if (_nodeSelectionMode || Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    _nodeSelectionMode = true;
                    ToggleNodeSelection(node);
                    ApplyNodeSelectionVisuals();
                }
                else if (!ReferenceEquals(_selectedNode, node) || _selectedNodes.Count != 1)
                {
                    SelectSingleNode(node);
                    ApplyNodeSelectionVisuals();
                }

                e.Handled = true;
            };

            border.ContextMenu = new ContextMenu();
        border.ContextMenuOpening += (_, _) => UseContextSelection(node);
        var selectItem = new MenuItem { Header = LocalizationService.GetString("MassSelectModeTitle") };
        selectItem.Click += (_, _) => BeginNodeSelection(node);
        border.ContextMenu.Items.Add(selectItem);
        var renameItem = new MenuItem { Header = LocalizationService.GetString("RenameSelected") };
        renameItem.Click += (_, _) => RenameSelectedNodes();
        border.ContextMenu.Items.Add(renameItem);
        border.ContextMenu.Items.Add(new Separator());

        if (node.HasChildren)
        {
            var toggleExpansionMenuItem = new MenuItem
            {
                Header = LocalizationService.GetString(node.IsExpanded ? "MindMapCollapseThisNode" : "MindMapExpandThisNode"),
                Tag = node
            };
            toggleExpansionMenuItem.Click += (s, e) => ToggleNodeExpansion(node);
            border.ContextMenu.Items.Add(toggleExpansionMenuItem);

            var toggleBranchMenuItem = new MenuItem
            {
                Header = LocalizationService.GetString(node.IsExpanded ? "MindMapCollapseNode" : "MindMapExpandNode"),
                Tag = node
            };
            toggleBranchMenuItem.Click += (s, e) => ToggleNodeBranchExpansion(node);
            border.ContextMenu.Items.Add(toggleBranchMenuItem);

            border.ContextMenu.Items.Add(new Separator());
        }

        var addChildMenuItem = new MenuItem
        {
            Header = LocalizationService.GetString("MindMapAddChild"),
            Tag = node
        };
        addChildMenuItem.Click += (s, e) => AddChildNode(node);
        border.ContextMenu.Items.Add(addChildMenuItem);

        var addSiblingMenuItem = new MenuItem
        {
            Header = LocalizationService.GetString("MindMapAddSibling"),
            Tag = node,
            IsEnabled = !ReferenceEquals(node, _root)
        };
        addSiblingMenuItem.Click += (s, e) => AddSiblingNode(node);
        border.ContextMenu.Items.Add(addSiblingMenuItem);

        var editMenuItem = new MenuItem
        {
            Header = LocalizationService.GetString("Edit"),
            Tag = node
        };
        editMenuItem.Click += (s, e) => EditNodeText(node);
        border.ContextMenu.Items.Add(editMenuItem);

        var styleMenuItem = new MenuItem
        {
            Header = LocalizationService.GetString("MindMapStyleOrganize"),
            Tag = node
        };
        styleMenuItem.Click += (s, e) => StyleNode(node);
        border.ContextMenu.Items.Add(styleMenuItem);

        border.ContextMenu.Items.Add(new Separator());

        var cutMenuItem = new MenuItem
        {
            Header = LocalizationService.GetString("Cut"),
            Tag = node,
            IsEnabled = !ReferenceEquals(node, _root)
        };
        cutMenuItem.Click += (s, e) => CutNode(node);
        border.ContextMenu.Items.Add(cutMenuItem);

        var copyMenuItem = new MenuItem
        {
            Header = LocalizationService.GetString("Copy"),
            Tag = node
        };
        copyMenuItem.Click += (s, e) => CopyNode(node);
        border.ContextMenu.Items.Add(copyMenuItem);

        var pasteMenuItem = new MenuItem
        {
            Header = LocalizationService.GetString("Paste"),
            Tag = node
        };
        pasteMenuItem.Click += (s, e) => PasteNodeAsChild(node);
        border.ContextMenu.Items.Add(pasteMenuItem);

        var pasteAsOutlineMenuItem = new MenuItem
        {
            Header = LocalizationService.GetString("MindMapPasteAsOutline"),
            Tag = node
        };
        pasteAsOutlineMenuItem.Click += (s, e) => PasteAsOutline(node);
        border.ContextMenu.Items.Add(pasteAsOutlineMenuItem);

        var copyStyleMenuItem = new MenuItem
        {
            Header = LocalizationService.GetString("MindMapCopyStyle"),
            Tag = node
        };
        copyStyleMenuItem.Click += (s, e) => CopyNodeStyle(node);
        border.ContextMenu.Items.Add(copyStyleMenuItem);

        var pasteStyleMenuItem = new MenuItem
        {
            Header = LocalizationService.GetString("MindMapPasteStyle"),
            Tag = node
        };
        pasteStyleMenuItem.Click += (s, e) => PasteNodeStyle(node);
        border.ContextMenu.Items.Add(pasteStyleMenuItem);

        // _clipboardNodes/_copiedStyle are static and can change between when this menu was built
        // (during the last RebuildMap) and when it's actually opened - refresh enabled state right
        // before showing rather than relying on stale values captured at construction time. The OS
        // clipboard can change from outside the app entirely, so it needs the same live check.
        border.ContextMenu.Opened += (_, _) =>
        {
            pasteMenuItem.IsEnabled = _clipboardNodes.Count > 0 && _selectedNodes.Count == 1;
            cutMenuItem.IsEnabled = CanDeleteSelection();
            editMenuItem.IsEnabled = styleMenuItem.IsEnabled = copyStyleMenuItem.IsEnabled = _selectedNodes.Count == 1;
            addChildMenuItem.IsEnabled = _selectedNodes.Count == 1;
            addSiblingMenuItem.IsEnabled = _selectedNodes.Count == 1 && node != _root;
            pasteStyleMenuItem.IsEnabled = _copiedStyle is not null;
            pasteAsOutlineMenuItem.IsEnabled = _selectedNodes.Count == 1 && TryGetClipboardText(out _);
        };

        if (node.LinkedNoteId.HasValue)
        {
            border.ContextMenu.Items.Add(new Separator());

            var openLinkMenuItem = new MenuItem
            {
                Header = LocalizationService.GetString("MindMapOpenLinkedNote"),
                Tag = node
            };
            openLinkMenuItem.Click += (s, e) => OpenNodeLink(node);
            border.ContextMenu.Items.Add(openLinkMenuItem);
            border.ContextMenu.Opened += (_, _) => openLinkMenuItem.IsEnabled = _selectedNodes.Count == 1;
        }

        if (node.CrossLinkTargetIds.Count > 0)
        {
            var removeCrossLinksMenuItem = new MenuItem
            {
                Header = LocalizationService.GetString("MindMapRemoveCrossLinks"),
                Tag = node
            };
            removeCrossLinksMenuItem.Click += (s, e) => RemoveCrossLinks(node);
            border.ContextMenu.Items.Add(removeCrossLinksMenuItem);
            border.ContextMenu.Opened += (_, _) => removeCrossLinksMenuItem.IsEnabled = _selectedNodes.Count == 1;
        }

        border.ContextMenu.Items.Add(new Separator());

        var deleteMenuItem = new MenuItem
        {
            Header = LocalizationService.GetString("Delete"),
            Tag = node
        };
        deleteMenuItem.Click += (s, e) => DeleteNodeWithConfirmation(node);
        border.ContextMenu.Items.Add(deleteMenuItem);
        border.ContextMenu.Opened += (_, _) => deleteMenuItem.IsEnabled = CanDeleteSelection();

        Canvas.SetLeft(border, layout.X);
        Canvas.SetTop(border, layout.Y);
        Panel.SetZIndex(border, NodeZIndex);
        MapCanvas.Children.Add(border);
        }

        if (!node.IsExpanded)
            return;

        foreach (var child in node.Children)
            DrawNodes(child);
    }

    private void ToggleNodeExpansion(MindMapNode node)
    {
        if (!node.HasChildren)
            return;

        node.IsExpanded = !node.IsExpanded;
        SelectSingleNode(node);
        RebuildMap(moveNodes: true);
    }

    // Unlike ToggleNodeExpansion (folds/unfolds only this one level, leaving descendants'
    // own fold state untouched underneath), this forces this node and every descendant to the
    // same state - "collapse the whole branch" vs. "collapse just this node".
    private void ToggleNodeBranchExpansion(MindMapNode node)
    {
        if (!node.HasChildren)
            return;

        SetExpanded(node, !node.IsExpanded);
        SelectSingleNode(node);
        RebuildMap(moveNodes: true);
    }

    private void SelectSingleNode(MindMapNode node)
    {
        _selectedNodes.Clear();
        _selectedNodes.Add(node);
        _selectedNode = node;

        // Keyboard shortcuts (MindMap_KeyDown) only fire while focus is on the canvas itself - selecting
        // a node (Border isn't focusable, so a plain click wouldn't otherwise move focus here) is
        // the one point where "the user is now interacting with a node" is unambiguous, so this is
        // where focus should follow. Harmless/idempotent when already-focused (e.g. from the arrow-
        // key navigation methods, which can only run once focus is already here).
        MapScrollViewer.Focus();
    }

    /// <summary>
    /// Keyboard shortcuts for the canvas: undo/redo, delete, rename, add child/sibling, and
    /// arrow-key navigation between nodes. Deliberately an allow-list, not a deny-list: Tab/Enter/
    /// arrows are heavily-overloaded keys (Tab cycles focus between toolbar controls, Enter/arrows
    /// drive ComboBox/Slider), so this only fires while focus is on the canvas itself (or nowhere
    /// in particular) rather than trying to enumerate every control type that should be left alone
    /// - a plain TextBox-only guard would still swallow Tab/arrow keys meant for the toolbar
    /// whenever a node happens to be selected. SelectSingleNode moves focus to MapScrollViewer on
    /// every selection, so clicking a node is what opts back into these shortcuts. Node text itself
    /// is edited through a modal dialog (EditNodeText), which already owns keyboard input while
    /// open, so no separate guard is needed for that case.
    /// </summary>
    private void MindMap_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled)
            return;

        var focused = Keyboard.FocusedElement;
        var isCanvasFocused = focused is null
            || ReferenceEquals(focused, this)
            || ReferenceEquals(focused, MapScrollViewer)
            || ReferenceEquals(focused, MapCanvas);

        var isIslandFocused = NodeSelectionDock.IsKeyboardFocusWithin;
        if (!isCanvasFocused && !isIslandFocused)
            return;

        if (HandleNodeSelectionKey(e)) return;
        var isCtrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var isShift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (isCtrl && e.Key == Key.Z)
        {
            if (isShift)
                Redo();
            else
                Undo();
            e.Handled = true;
            return;
        }

        if (isCtrl && e.Key == Key.Y)
        {
            Redo();
            e.Handled = true;
            return;
        }

        if (isCtrl && e.Key == Key.X && _selectedNode is not null)
        {
            CutNode(_selectedNode);
            e.Handled = true;
            return;
        }

        if (isCtrl && e.Key == Key.C && _selectedNode is not null)
        {
            CopyNode(_selectedNode);
            e.Handled = true;
            return;
        }

        if (isCtrl && isShift && e.Key == Key.V && _selectedNode is not null)
        {
            PasteAsOutline(_selectedNode);
            e.Handled = true;
            return;
        }

        if (isCtrl && e.Key == Key.V && _selectedNode is not null)
        {
            PasteNodeAsChild(_selectedNode);
            e.Handled = true;
            return;
        }

        // Selection mode leaves Tab/Enter to focus/command navigation; ordinary canvas
        // navigation retains the add-child/sibling shortcuts. F2 works from the island too.
        if (_selectedNode is null || (_nodeSelectionMode && e.Key is Key.Tab or Key.Enter)
            || (isIslandFocused && e.Key is not (Key.Delete or Key.F2)))
            return;

        switch (e.Key)
        {
            case Key.Delete:
                DeleteNodeWithConfirmation(_selectedNode);
                e.Handled = true;
                break;
            case Key.F2:
                if (_selectedNodes.Count > 1) RenameSelectedNodes(); else EditNodeText(_selectedNode);
                e.Handled = true;
                break;
            case Key.Tab:
                AddChildNode(_selectedNode);
                e.Handled = true;
                break;
            case Key.Enter:
                AddSiblingNode(_selectedNode);
                e.Handled = true;
                break;
            case Key.Up:
                SelectSibling(-1);
                e.Handled = true;
                break;
            case Key.Down:
                SelectSibling(1);
                e.Handled = true;
                break;
            case Key.Left:
                SelectParentOfCurrent();
                e.Handled = true;
                break;
            case Key.Right:
                SelectFirstChildOfCurrent();
                e.Handled = true;
                break;
        }
    }

    private void SelectSibling(int direction)
    {
        if (_selectedNode is null || ReferenceEquals(_selectedNode, _root))
            return;

        var parent = FindParentNode(_root, _selectedNode);
        if (parent is null)
            return;

        var index = parent.Children.IndexOf(_selectedNode);
        var newIndex = index + direction;
        if (index < 0 || newIndex < 0 || newIndex >= parent.Children.Count)
            return;

        SelectSingleNode(parent.Children[newIndex]);
        ApplyNodeSelectionVisuals();
    }

    private void SelectParentOfCurrent()
    {
        if (_selectedNode is null)
            return;

        var parent = FindParentNode(_root, _selectedNode);
        if (parent is null)
            return;

        SelectSingleNode(parent);
        ApplyNodeSelectionVisuals();
    }

    private void SelectFirstChildOfCurrent()
    {
        if (_selectedNode is null || !_selectedNode.HasChildren || !_selectedNode.IsExpanded)
            return;

        SelectSingleNode(_selectedNode.Children[0]);
        ApplyNodeSelectionVisuals();
    }

    private void ToggleNodeSelection(MindMapNode node)
    {
        _nodeSelectionMode = true;
        MapScrollViewer.Focus();
        if (_selectedNodes.Remove(node))
        {
            if (ReferenceEquals(_selectedNode, node))
                _selectedNode = _selectedNodes.FirstOrDefault();
            return;
        }

        _selectedNodes.Add(node);
        _selectedNode = node;
    }

    private void EnsureNodeSelected(MindMapNode node)
    {
        _selectedNodes.Add(node);
        _selectedNode = node;
    }

    private void ClearSelectedNodes()
    {
        _nodeSelectionMode = false;
        _selectedNodes.Clear();
        _selectedNode = null;
    }

    private bool HasSelectedNodes() => _selectedNodes.Count > 0;

    private IReadOnlyList<MindMapNode> GetSelectedExportRoots() => MindMapSelectionOperations.Roots(_root, _selectedNodes);

    private MindMapNode BuildExportRoot(IReadOnlyList<MindMapNode> selectedRoots)
    {
        if (selectedRoots.Count == 0)
            return _root;

        if (selectedRoots.Count == 1)
            return selectedRoots[0];

        var exportRoot = new MindMapNode
        {
            Text = _root.Text
        };

        exportRoot.Children.AddRange(MindMapSelectionOperations.CloneForest(selectedRoots, keepExternalLinks: false));

        return exportRoot;
    }

    private CornerRadius GetCornerRadius(string? nodeShape, bool isRoot)
    {
        var cardRadius = ResolveCornerRadiusToken("Radius.Card", 14);
        return nodeShape switch
        {
            "Circle" => new CornerRadius(95),
            "Ellipse" => new CornerRadius(26),
            "Rectangle" => new CornerRadius(0),
            // Rounded + null/unknown: card language (serious default).
            _ => cardRadius
        };
    }

    private Brush GetNodeBackground(MindMapNode node, int depth)
    {
        if (!string.IsNullOrWhiteSpace(node.BackgroundColor))
            return FrozenBrush((Color)ColorConverter.ConvertFromString(node.BackgroundColor));

        return GetCardSurfaceBrush(ReferenceEquals(node, _root) || depth <= 0);
    }

    private void StyleNode(MindMapNode node)
    {
        if (_selectedNodes.Count > 1) return;
        var dialog = new StyleNodeDialog { Owner = WorkspaceOwner };
        dialog.SetNoteLinkOptions(NoteLinkOptions);
        dialog.LoadFromNode(node);

        if (dialog.ShowDialog() == true)
        {
            PushUndoSnapshot();
            dialog.ApplyToNode(node);
            RebuildMap();
        }
    }

    private void AddChildNode(MindMapNode parentNode)
    {
        if (_selectedNodes.Count > 1) return;
        var dialog = new SimpleInputDialog(
            LocalizationService.GetString("MindMapAddChild"),
            LocalizationService.GetString("MindMapAddChildPrompt"),
            string.Empty)
        {
            Owner = WorkspaceOwner
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
        {
            PushUndoSnapshot();
            var newNode = new MindMapNode
            {
                Text = NormalizeNodeText(dialog.InputText),
                NodeShape = "Rounded"
            };

            parentNode.Children.Add(newNode);
            parentNode.IsExpanded = true;
            SelectSingleNode(newNode);
            RebuildMap();
        }
    }

    private void AddSiblingNode(MindMapNode node)
    {
        if (_selectedNodes.Count > 1) return;
        if (ReferenceEquals(node, _root))
        {
            ModernMessageBox.Show(
                LocalizationService.GetString("MindMapCannotAddSiblingToRoot"),
                LocalizationService.GetString("MindMapAddSibling"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var parent = FindParentNode(_root, node);
        if (parent is null)
            return;

        var dialog = new SimpleInputDialog(
            LocalizationService.GetString("MindMapAddSibling"),
            LocalizationService.GetString("MindMapAddSiblingPrompt"),
            string.Empty)
        {
            Owner = WorkspaceOwner
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
        {
            PushUndoSnapshot();
            var newNode = new MindMapNode
            {
                Text = NormalizeNodeText(dialog.InputText),
                NodeShape = "Rounded"
            };

            parent.Children.Add(newNode);
            parent.IsExpanded = true;
            SelectSingleNode(newNode);
            RebuildMap();
        }
    }


    private void EditNodeText(MindMapNode node)
    {
        if (_selectedNodes.Count > 1) return;
        var dialog = new SimpleInputDialog(
            LocalizationService.GetString("Edit"),
            LocalizationService.GetString("MindMapEditNodePrompt"),
            node.Text)
        {
            Owner = WorkspaceOwner
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
        {
            PushUndoSnapshot();
            node.Text = NormalizeNodeText(dialog.InputText);
            RebuildMap();
        }
    }

    private void DeleteNodeWithConfirmation(MindMapNode node) { UseContextSelection(node); DeleteSelectedNodes(); }
    private void CutNode(MindMapNode node) { UseContextSelection(node); CutSelectedNodes(); }
    private void CopyNode(MindMapNode node) { UseContextSelection(node); CopySelectedNodes(); }
    private void PasteNodeAsChild(MindMapNode targetNode) => PasteSelectedBranches(targetNode);

    // Reads plain text from the OS clipboard (not the app's own node clipboard _clipboardNodes
    // above - a deliberately separate action/shortcut so the two don't get confused) and parses
    // it as an indented outline, adding the resulting nodes as children of targetNode. Lets you
    // paste a bulleted list or a plain tab-indented outline copied from anywhere and get a real
    // branch out of it instead of one node containing the whole blob of text.
    private void PasteAsOutline(MindMapNode targetNode)
    {
        if (_selectedNodes.Count > 1) return;
        if (!TryGetClipboardText(out var clipboardText))
            return;

        var parsedNodes = MindMapConversionService.ParseOutlineText(clipboardText);
        if (parsedNodes.Count == 0)
            return;

        PushUndoSnapshot();
        foreach (var node in parsedNodes)
            targetNode.Children.Add(node);

        targetNode.IsExpanded = true;
        SelectSingleNode(targetNode);
        RebuildMap();
    }

    private static bool TryGetClipboardText(out string text)
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                text = Clipboard.GetText();
                return !string.IsNullOrWhiteSpace(text);
            }
        }
        catch (COMException)
        {
            // Clipboard can be transiently locked by another process - nothing to recover here.
        }

        text = string.Empty;
        return false;
    }

    private void CopyNodeStyle(MindMapNode node)
        => _copiedStyle = new CopiedNodeStyle(
            node.BackgroundColor,
            node.BorderColor,
            node.BorderThickness,
            node.NodeShape,
            new List<string>(node.Icons),
            node.IconBadgeColor,
            node.IsBold,
            node.IsItalic,
            node.TextColor,
            node.FontFamily,
            node.FontSize);

    private void PasteNodeStyle(MindMapNode node)
    {
        if (_copiedStyle is null)
            return;

        PushUndoSnapshot();
        foreach (var selected in _selectedNodes.Count > 1 ? _selectedNodes.ToArray() : new[] { node })
        {
            selected.BackgroundColor = _copiedStyle.BackgroundColor;
            selected.BorderColor = _copiedStyle.BorderColor;
            selected.BorderThickness = _copiedStyle.BorderThickness;
            selected.NodeShape = _copiedStyle.NodeShape;
            selected.Icons = new List<string>(_copiedStyle.Icons);
            selected.IconBadgeColor = _copiedStyle.IconBadgeColor;
            selected.IsBold = _copiedStyle.IsBold;
            selected.IsItalic = _copiedStyle.IsItalic;
            selected.TextColor = _copiedStyle.TextColor;
            selected.FontFamily = _copiedStyle.FontFamily;
            selected.FontSize = _copiedStyle.FontSize;
        }
        RebuildMap();
    }

    private void AddNodeMenuButton_Click(object sender, RoutedEventArgs e)
    {
        // One combined "+" button popping out Add Child / Add Sibling instead of two separate
        // always-visible Plus buttons - opened on left-click rather than the default right-click,
        // matching how the button reads as a primary action, not a context menu.
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            if (menu.Items.OfType<MenuItem>().Skip(1).FirstOrDefault() is { } sibling)
                sibling.IsEnabled = _selectedNodes.Count == 1 && _selectedNode != _root;
            menu.IsOpen = true;
        }
    }

    private void AddChildButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is null)
        {
            ModernMessageBox.Show(
                LocalizationService.GetString("MindMapSelectNodeToAddChild"),
                LocalizationService.GetString("MindMapAddChild"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        AddChildNode(_selectedNode);
    }

    private void AddSiblingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is null)
        {
            ModernMessageBox.Show(
                LocalizationService.GetString("MindMapSelectNodeToAddSibling"),
                LocalizationService.GetString("MindMapAddSibling"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        AddSiblingNode(_selectedNode);
    }

    private void DeleteNodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is null)
        {
            ModernMessageBox.Show(
                LocalizationService.GetString("MindMapSelectNodeToDelete"),
                LocalizationService.GetString("Delete"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        DeleteNodeWithConfirmation(_selectedNode);
    }

    private MindMapNode? FindParentNode(MindMapNode root, MindMapNode target)
    {
        if (ReferenceEquals(root, target))
            return null;

        foreach (var child in root.Children)
        {
            if (ReferenceEquals(child, target))
                return root;

            var found = FindParentNode(child, target);
            if (found is not null)
                return found;
        }

        return null;
    }

    private static string NormalizeNodeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim();
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\s+", " ");

        const int maxNodeTextLength = 110;
        if (normalized.Length > maxNodeTextLength)
            normalized = normalized[..maxNodeTextLength].TrimEnd() + "...";

        return normalized;
    }

    private void ExpandAllButton_Click(object sender, RoutedEventArgs e)
    {
        SetExpanded(_root, true);
        RebuildMap(moveNodes: true);
        Dispatcher.BeginInvoke(CenterViewOnRoot, DispatcherPriority.Loaded);
    }

    private void CollapseAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var node in EnumerateAllNodes(_root))
        {
            if (!ReferenceEquals(node, _root))
                node.IsExpanded = false;
        }

        _root.IsExpanded = false;
        RebuildMap(moveNodes: true);
        Dispatcher.BeginInvoke(CenterViewOnRoot, DispatcherPriority.Loaded);
    }

    private void LayoutModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLayoutModeSelectionChanged)
            return;

        if (LayoutModeComboBox.SelectedItem is not ComboBoxItem selectedItem)
            return;

        _selectedLayoutMode = ParseLayoutMode(selectedItem.Tag as string);
        _hasCenteredOnRootInitially = false;
        RebuildMap(moveNodes: true);
        Dispatcher.BeginInvoke(new Action(CenterViewOnRoot), DispatcherPriority.Loaded);
    }

    private void AutoLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        _isManualPositioningMode = false;
        _manualPositions.Clear();
        ClearManualPositions(_root);
        _suppressManualPositioningToggleChanged = true;
        ManualPositioningToggle.IsChecked = false;
        _suppressManualPositioningToggleChanged = false;
        UpdateManualPositioningVisualState();

        RebuildMap(moveNodes: true);
        Dispatcher.BeginInvoke(new Action(CenterViewOnRoot), DispatcherPriority.Loaded);
    }
    private void ManualPositioningToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressManualPositioningToggleChanged)
            return;

        _isManualPositioningMode = true;
        CaptureVisibleLayoutAsManualPositions();
        UpdateManualPositioningVisualState();
        RebuildMap(animate: false);
    }

    private void ManualPositioningToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressManualPositioningToggleChanged)
            return;

        _isManualPositioningMode = false;
        _manualPositions.Clear();
        ClearManualPositions(_root);
        UpdateManualPositioningVisualState();
        RebuildMap(animate: false);
    }

    private void ArrangePanelToggleButton_Click(object sender, RoutedEventArgs e)
        => SetArrangePanelOpen(!_isArrangePanelOpen);

    private void SearchPanelToggleButton_Click(object sender, RoutedEventArgs e)
        => SetSearchPanelOpen(!_isSearchPanelOpen);

    private void FontPanelToggleButton_Click(object sender, RoutedEventArgs e)
        => SetFontPanelOpen(!_isFontPanelOpen);

    private void SetArrangePanelOpen(bool isOpen)
    {
        _isArrangePanelOpen = isOpen;
        if (isOpen)
        {
            if (_isSearchPanelOpen) SetSearchPanelOpen(false);
            if (_isFontPanelOpen) SetFontPanelOpen(false);
        }
        UiMotion.AnimateLayoutReveal(ArrangePanel, isOpen);
    }

    private void SetSearchPanelOpen(bool isOpen)
    {
        _isSearchPanelOpen = isOpen;
        if (isOpen)
        {
            if (_isArrangePanelOpen) SetArrangePanelOpen(false);
            if (_isFontPanelOpen) SetFontPanelOpen(false);
        }
        UiMotion.AnimateLayoutReveal(SearchPanel, isOpen);
        if (isOpen)
            SearchTextBox.Focus();
    }

    private void SetFontPanelOpen(bool isOpen)
    {
        _isFontPanelOpen = isOpen;
        if (isOpen)
        {
            if (_isArrangePanelOpen) SetArrangePanelOpen(false);
            if (_isSearchPanelOpen) SetSearchPanelOpen(false);
        }
        UiMotion.AnimateLayoutReveal(FontPanel, isOpen);
        if (isOpen)
            RefreshFontPanelFromSelection();
        SyncNodeSelection();
    }

    private void TagsPanelToggleButton_Click(object sender, RoutedEventArgs e)
        => SetTagsPanelOpen(!_isTagsPanelOpen);

    private void SetTagsPanelOpen(bool isOpen, bool animate = true)
    {
        _isTagsPanelOpen = isOpen;
        UiMotion.AnimateLayoutReveal(TagsPanel, isOpen, animate: animate);
    }

    // FreeMind's own font toolbar lists every font the OS has installed, not a curated handful -
    // matches that instead of a fixed list so anything actually available on this machine shows
    // up. Populated once at construction (installed fonts don't change mid-session) rather than
    // on every RebuildMap.
    private void PopulateFontFamilyBox()
    {
        var defaultItem = new ComboBoxItem
        {
            Content = LocalizationService.GetString("MindMapFontDefault"),
            Tag = string.Empty
        };
        FontFamilyBox.Items.Add(defaultItem);
        FontFamilyBox.Items.Add(new ComboBoxItem { Content = LocalizationService.GetString("MixedSelection"), Tag = "__mixed", IsEnabled = false });

        var familyNames = Fonts.SystemFontFamilies
            .Select(family => family.Source)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

        foreach (var name in familyNames)
            FontFamilyBox.Items.Add(new ComboBoxItem { Content = name, Tag = name });
    }

    /// <summary>
    /// Syncs the font panel's controls to whatever's currently selected. Called whenever the
    /// panel opens and from the two chokepoints that already run after every selection change
    /// (ApplyNodeSelectionVisuals, for clicks/arrow-key nav/drag-finalize) or content edit
    /// (RebuildMapCore, for add/delete/undo/redo/import/reparent) - cheaper and far less
    /// error-prone than hooking every individual SelectSingleNode/ToggleNodeSelection call site
    /// by hand. Mixed values remain explicit; changing one control edits only that property
    /// across the selected nodes in one undo transaction.
    /// </summary>
    private void RefreshFontPanelFromSelection()
    {
        if (!_isFontPanelOpen)
            return;

        var nodes = _selectedNodes.ToArray();
        bool enabled = nodes.Length > 0;
        FontFamilyBox.IsEnabled = FontSizeBox.IsEnabled = FontBoldToggleButton.IsEnabled = FontItalicToggleButton.IsEnabled = FontColorButton.IsEnabled = FontColorClearButton.IsEnabled = enabled;
        FontPanelHintText.Visibility = nodes.Length == 1 ? Visibility.Collapsed : Visibility.Visible;
        FontPanelHintText.Text = nodes.Length > 1 ? string.Format(LocalizationService.GetString("MindMapFontSelectionHint"), nodes.Length) : LocalizationService.GetString("MindMapFontNoSelectionHint");
        _suppressFontPanelChangeHandlers = true;
        try
        {
            var families = nodes.Select(node => node.FontFamily ?? string.Empty).Distinct().ToArray();
            var sizes = nodes.Select(node => node.FontSize).Distinct().ToArray();
            var bold = nodes.Select(node => node.IsBold).Distinct().ToArray();
            var italic = nodes.Select(node => node.IsItalic).Distinct().ToArray();
            var colors = nodes.Select(node => node.TextColor).Distinct().ToArray();
            FontFamilyBox.SelectedValue = families.Length > 1 ? "__mixed" : families.FirstOrDefault() ?? string.Empty;
            var sizeValue = sizes.Length > 1 ? "__mixed" : sizes.FirstOrDefault()?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            if (sizeValue != "__mixed" && !FontSizeBox.Items.OfType<ComboBoxItem>().Any(item => Equals(item.Tag, sizeValue)))
                FontSizeBox.Items.Add(new ComboBoxItem { Content = sizeValue, Tag = sizeValue });
            FontSizeBox.SelectedValue = sizeValue;
            FontBoldToggleButton.IsChecked = bold.Length > 1 ? null : bold.FirstOrDefault();
            FontItalicToggleButton.IsChecked = italic.Length > 1 ? null : italic.FirstOrDefault();
            FontBoldToggleButton.ToolTip = LocalizationService.GetString("Bold") + (bold.Length > 1 ? " · " + LocalizationService.GetString("MixedSelection") : "");
            FontItalicToggleButton.ToolTip = LocalizationService.GetString("Italic") + (italic.Length > 1 ? " · " + LocalizationService.GetString("MixedSelection") : "");
            FontColorButton.Background = colors.Length == 1 && !string.IsNullOrWhiteSpace(colors[0]) ? FrozenBrush((Color)ColorConverter.ConvertFromString(colors[0]!)) : Brushes.Transparent;
            FontColorButton.ToolTip = LocalizationService.GetString(colors.Length > 1 ? "MixedSelection" : "SelectColor");
        }
        finally { _suppressFontPanelChangeHandlers = false; }
    }

    private void ApplyFontPanelChange(Action<MindMapNode> mutate)
    {
        if (_suppressFontPanelChangeHandlers || _selectedNodes.Count == 0)
            return;

        PushUndoSnapshot();
        foreach (var node in _selectedNodes) mutate(node);
        RebuildMap(animate: false);
    }

    private void FontFamilyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var value = FontFamilyBox.SelectedValue as string;
        if (value == "__mixed") return;
        ApplyFontPanelChange(node => node.FontFamily = string.IsNullOrEmpty(value) ? null : value);
    }

    private void FontSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var value = FontSizeBox.SelectedValue as string;
        if (value == "__mixed") return;
        ApplyFontPanelChange(node => node.FontSize = string.IsNullOrEmpty(value)
            ? null
            : double.Parse(value, CultureInfo.InvariantCulture));
    }

    private void FontBoldToggleButton_Click(object sender, RoutedEventArgs e)
    {
        bool value = _selectedNodes.Select(node => node.IsBold).Distinct().Count() > 1 || FontBoldToggleButton.IsChecked == true;
        ApplyFontPanelChange(node => node.IsBold = value);
    }

    private void FontItalicToggleButton_Click(object sender, RoutedEventArgs e)
    {
        bool value = _selectedNodes.Select(node => node.IsItalic).Distinct().Count() > 1 || FontItalicToggleButton.IsChecked == true;
        ApplyFontPanelChange(node => node.IsItalic = value);
    }

    private void FontColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNodes.Count == 0) return;

        var dialog = new ColorPickerDialog { Owner = WorkspaceOwner };
        if (dialog.ShowDialog() != true || dialog.SelectedColor is not { } color)
            return;

        // No need to set FontColorButton.Background directly here - ApplyFontPanelChange's
        // RebuildMap(animate: false) runs RefreshFontPanelFromSelection synchronously, which
        // re-reads the swatch color from the node it just mutated.
        ApplyFontPanelChange(node => node.TextColor = color.ToString());
    }

    private void FontColorClearButton_Click(object sender, RoutedEventArgs e)
        => ApplyFontPanelChange(node => node.TextColor = null);

    private static void SetExpanded(MindMapNode node, bool isExpanded)
    {
        node.IsExpanded = isExpanded;
        foreach (var child in node.Children)
            SetExpanded(child, isExpanded);
    }

    private void ResetZoomButton_Click(object sender, RoutedEventArgs e)
    {
        ZoomSlider.Value = 1;
        Dispatcher.BeginInvoke(new Action(CenterViewOnRoot), DispatcherPriority.Loaded);
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MapScaleTransform is null || MapScrollViewer is null || MapCanvas is null)
            return;

        var oldZoom = e.OldValue > 0 ? e.OldValue : 1;
        var newZoom = Math.Max(e.NewValue, 0.1);
        var preserveViewportCenter = !_suppressZoomSliderViewportRecenter;
        var centerX = (MapScrollViewer.HorizontalOffset + MapScrollViewer.ViewportWidth / 2) / oldZoom;
        var centerY = (MapScrollViewer.VerticalOffset + MapScrollViewer.ViewportHeight / 2) / oldZoom;

        MapScaleTransform.ScaleX = newZoom;
        MapScaleTransform.ScaleY = newZoom;
        UpdateMapCanvasSize();
        MapCanvas.InvalidateMeasure();
        MapScrollViewer.InvalidateScrollInfo();

        BeginTileSession();

        if (!preserveViewportCenter)
            return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            MapCanvas.UpdateLayout();
            MapScrollViewer.UpdateLayout();

            var targetX = centerX * newZoom - MapScrollViewer.ViewportWidth / 2;
            var targetY = centerY * newZoom - MapScrollViewer.ViewportHeight / 2;
            MapScrollViewer.ScrollToHorizontalOffset(Math.Clamp(targetX, 0, Math.Max(0, MapScrollViewer.ScrollableWidth)));
            MapScrollViewer.ScrollToVerticalOffset(Math.Clamp(targetY, 0, Math.Max(0, MapScrollViewer.ScrollableHeight)));
        }), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Left-drag empty canvas (or middle-drag anywhere on the map) pans the viewport for navigation.
    /// </summary>
    private void MapScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingNode || _isManualDragPending || _isViewPanning)
            return;

        if (!IsMapBackgroundHit(e.OriginalSource as DependencyObject))
            return;

        BeginViewPan(e.GetPosition(MapScrollViewer));
        MapScrollViewer.CaptureMouse();
        e.Handled = true;
    }

    private void MapScrollViewer_PreviewMouseDownForPan(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
            return;

        if (_isDraggingNode || _isManualDragPending || _isViewPanning)
            return;

        BeginViewPan(e.GetPosition(MapScrollViewer));
        MapScrollViewer.CaptureMouse();
        e.Handled = true;
    }

    private void MapScrollViewer_PreviewMouseUpForPan(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || !_isViewPanning)
            return;

        EndViewPan();
        e.Handled = true;
    }

    private void MapScrollViewer_PreviewMouseMoveForPan(object sender, MouseEventArgs e)
    {
        if (!_isViewPanning || MapScrollViewer is null)
            return;

        var pos = e.GetPosition(MapScrollViewer);
        var dx = pos.X - _viewPanLastPos.X;
        var dy = pos.Y - _viewPanLastPos.Y;
        _viewPanLastPos = pos;

        // Drag the map with the pointer (grab-and-drag), same direction as content under the hand.
        MapScrollViewer.ScrollToHorizontalOffset(
            Math.Clamp(MapScrollViewer.HorizontalOffset - dx, 0, Math.Max(0, MapScrollViewer.ScrollableWidth)));
        MapScrollViewer.ScrollToVerticalOffset(
            Math.Clamp(MapScrollViewer.VerticalOffset - dy, 0, Math.Max(0, MapScrollViewer.ScrollableHeight)));
        e.Handled = true;
    }

    private void MapScrollViewer_PreviewMouseButtonUpForPan(object sender, MouseButtonEventArgs e)
    {
        if (!_isViewPanning)
            return;

        EndViewPan();
        e.Handled = true;
    }

    private void MapScrollViewer_LostMouseCaptureForPan(object sender, MouseEventArgs e)
        => EndViewPan();

    private void BeginViewPan(Point startPos)
    {
        _isViewPanning = true;
        _viewPanLastPos = startPos;
        MapScrollViewer.Cursor = Cursors.SizeAll;
    }

    private void EndViewPan()
    {
        if (!_isViewPanning)
            return;

        _isViewPanning = false;
        if (MapScrollViewer.IsMouseCaptured)
            MapScrollViewer.ReleaseMouseCapture();
        MapScrollViewer.Cursor = Cursors.Arrow;
    }

    private bool IsMapBackgroundHit(DependencyObject? source)
    {
        if (source is null)
            return true;

        for (var current = source; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Border { Tag: MindMapNode })
                return false;
            // Scrollbar template parts (thumb / track / repeat buttons) must keep their own
            // drag behavior - never treat them as map background, or grabbing a scrollbar
            // would start a map pan instead.
            if (current is System.Windows.Controls.Primitives.ScrollBar)
                return false;
            if (ReferenceEquals(current, MapCanvas) || ReferenceEquals(current, MapScrollViewer))
                return true;
        }

        return false;
    }

    private void MapScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var ctrlHeld = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            || Keyboard.IsKeyDown(Key.LeftCtrl)
            || Keyboard.IsKeyDown(Key.RightCtrl);
        if (!ctrlHeld || ZoomSlider is null)
            return;

        // Zoom toward the pointer (not only viewport center).
        var oldZoom = Math.Max(ZoomSlider.Value, 0.1);
        var step = e.Delta > 0 ? 0.1 : -0.1;
        var newZoom = Math.Clamp(oldZoom + step, ZoomSlider.Minimum, ZoomSlider.Maximum);
        if (Math.Abs(newZoom - oldZoom) < 0.0001)
        {
            e.Handled = true;
            return;
        }

        var pointer = e.GetPosition(MapScrollViewer);
        var contentX = (MapScrollViewer.HorizontalOffset + pointer.X) / oldZoom;
        var contentY = (MapScrollViewer.VerticalOffset + pointer.Y) / oldZoom;

        if (Scriblucent.Services.ScrollPerfDiag.Enabled)
        {
            var sv = MapScrollViewer;
            Scriblucent.Services.ScrollPerfDiag.Log(
                $"mmWheel\treq\t{oldZoom:F3}\t{newZoom:F3}\t{sv.HorizontalOffset:F2}\t{sv.VerticalOffset:F2}\t" +
                $"{contentX:F2}\t{contentY:F2}\t{pointer.X:F2}\t{pointer.Y:F2}\t{sv.ScrollableWidth:F2}\t{sv.ScrollableHeight:F2}");
        }

        _suppressZoomSliderViewportRecenter = true;
        try
        {
            ZoomSlider.Value = newZoom;
        }
        finally
        {
            _suppressZoomSliderViewportRecenter = false;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (MapScrollViewer is null || MapCanvas is null)
                return;

            MapCanvas.UpdateLayout();
            MapScrollViewer.UpdateLayout();
            var targetX = contentX * newZoom - pointer.X;
            var targetY = contentY * newZoom - pointer.Y;
            MapScrollViewer.ScrollToHorizontalOffset(Math.Clamp(targetX, 0, Math.Max(0, MapScrollViewer.ScrollableWidth)));
            MapScrollViewer.ScrollToVerticalOffset(Math.Clamp(targetY, 0, Math.Max(0, MapScrollViewer.ScrollableHeight)));

            if (Scriblucent.Services.ScrollPerfDiag.Enabled)
            {
                var sv = MapScrollViewer;
                Scriblucent.Services.ScrollPerfDiag.Log(
                    $"mmWheel\tdone\t{newZoom:F3}\t{sv.HorizontalOffset:F2}\t{sv.VerticalOffset:F2}\t" +
                    $"{Math.Clamp(targetX, 0, Math.Max(0, sv.ScrollableWidth)):F2}\t{Math.Clamp(targetY, 0, Math.Max(0, sv.ScrollableHeight)):F2}\t" +
                    $"{sv.ScrollableWidth:F2}\t{sv.ScrollableHeight:F2}");
            }
        }), DispatcherPriority.Loaded);

        e.Handled = true;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveAndClose();
    }

    private void ExportAllButton_Click(object sender, RoutedEventArgs e)
    {
        ExportMindMap(exportSelectedBranchOnly: false);
    }

    private void ExportSelectedBranchButton_Click(object sender, RoutedEventArgs e)
    {
        if (!HasSelectedNodes())
        {
            ShowExportDialog(
                LocalizationService.GetString("Export"),
                LocalizationService.GetString("MindMapExportSelectBranchFirst"));
            return;
        }

        ExportMindMap(exportSelectedBranchOnly: true);
    }

    private void ExportMindMap(bool exportSelectedBranchOnly)
    {
        var saveDialog = new SaveFileDialog
        {
            Title = LocalizationService.GetString("MindMapExportDialogTitle"),
            Filter = LocalizationService.GetString("MindMapExportDialogFilter"),
            FileName = GetExportFileName(exportSelectedBranchOnly)
        };

        if (saveDialog.ShowDialog(WorkspaceOwner) != true)
            return;

        try
        {
            var extension = System.IO.Path.GetExtension(saveDialog.FileName).ToLowerInvariant();
            var selectedRoots = exportSelectedBranchOnly ? GetSelectedExportRoots() : Array.Empty<MindMapNode>();
            var exportRoot = exportSelectedBranchOnly
                ? BuildExportRoot(selectedRoots)
                : _root;

            switch (extension)
            {
                case ".png":
                case ".jpg":
                case ".jpeg":
                    ExportToImage(saveDialog.FileName, extension, exportRoot, exportSelectedBranchOnly, selectedRoots);
                    break;
                case ".scribmap":
                    var document = ToDocument();
                    if (exportSelectedBranchOnly)
                        document.Root = exportRoot;
                    MindMapFileService.SaveScribmapFile(saveDialog.FileName, document);
                    break;
                default:
                    throw new InvalidOperationException(LocalizationService.GetString("MindMapExportUnsupportedFormat"));
            }

            ShowExportDialog(
                LocalizationService.GetString("Success"),
                LocalizationService.GetString("MindMapExportSuccess"));
        }
        catch (Exception ex)
        {
            ShowExportDialog(
                LocalizationService.GetString("ExportError"),
                string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("MindMapExportFailedFormat"), ex.Message));
        }
    }

    private void ShowExportDialog(string title, string message)
    {
        ModernDialog.ShowInfo(WorkspaceOwner, title, message);
    }

    private string GetExportFileName(bool exportSelectedBranchOnly)
    {
        var baseName = string.IsNullOrWhiteSpace(EditorTitle)
            ? LocalizationService.GetString("MindMapUntitled")
            : EditorTitle;
        var suffix = exportSelectedBranchOnly
            ? LocalizationService.GetString("MindMapExportBranchFileSuffix")
            : LocalizationService.GetString("MindMapExportAllFileSuffix");

        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(invalid, '_');

        return $"{baseName}-{suffix}";
    }

    // preserveId matters once a node's Id can be the target of a link (LinkedNoteId lives on the
    // node itself so it always survives cloning regardless; CrossLinkTargetIds is what depends on
    // Id staying stable). Undo/redo/restore need preserveId: true - it's the SAME logical node,
    // just being restored to an earlier tree shape, and any cross-link pointing at it must still
    // resolve after an undo. Cut/copy/paste, import-merge, and export-root synthesis need
    // preserveId: false (the default) - those create genuinely new/independent nodes, and two
    // nodes sharing an Id would make cross-link/hyperlink targeting ambiguous.
    private MindMapNode CloneNode(MindMapNode source, bool preserveId = false)
    {
        var copy = new MindMapNode
        {
            Id = preserveId ? source.Id : Guid.NewGuid(),
            Text = source.Text,
            IsExpanded = source.IsExpanded,
            BackgroundColor = source.BackgroundColor,
            BorderColor = source.BorderColor,
            BorderThickness = source.BorderThickness,
            NodeShape = source.NodeShape,
            Icons = new List<string>(source.Icons),
            IconBadgeColor = source.IconBadgeColor,
            IsBold = source.IsBold,
            IsItalic = source.IsItalic,
            TextColor = source.TextColor,
            FontFamily = source.FontFamily,
            FontSize = source.FontSize,
            Note = source.Note,
            LinkedNoteId = source.LinkedNoteId,
            CrossLinkTargetIds = new List<Guid>(source.CrossLinkTargetIds),
            ManualX = source.ManualX,
            ManualY = source.ManualY
        };

        foreach (var child in source.Children)
            copy.Children.Add(CloneNode(child, preserveId));

        return copy;
    }

    /// <summary>
    /// Call before a content-mutating edit (add/delete/edit text/restyle/reposition/import-merge)
    /// so it can be undone. Not for view-state changes (expand/collapse, zoom, selection) - those
    /// are deliberately excluded from the undo stack.
    /// </summary>
    private void PushUndoSnapshot()
    {
        _undoStack.Add(CloneNode(_root, preserveId: true));
        if (_undoStack.Count > MaxUndoDepth)
            _undoStack.RemoveAt(0);

        _redoStack.Clear();
        UpdateUndoRedoButtonStates();
    }

    private bool CanUndo => _undoStack.Count > 0;
    private bool CanRedo => _redoStack.Count > 0;

    private void UndoButton_Click(object sender, RoutedEventArgs e) => Undo();
    private void RedoButton_Click(object sender, RoutedEventArgs e) => Redo();

    private void UpdateUndoRedoButtonStates()
    {
        if (FindName("UndoButton") is Button undoButton)
            undoButton.IsEnabled = CanUndo;
        if (FindName("RedoButton") is Button redoButton)
            redoButton.IsEnabled = CanRedo;
    }

    private void Undo()
    {
        if (!CanUndo)
            return;

        var previous = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _redoStack.Add(CloneNode(_root, preserveId: true));

        RestoreRootFrom(previous);
        ClearSelectedNodes();
        RebuildMap();
        UpdateUndoRedoButtonStates();
    }

    private void Redo()
    {
        if (!CanRedo)
            return;

        var next = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _undoStack.Add(CloneNode(_root, preserveId: true));

        RestoreRootFrom(next);
        ClearSelectedNodes();
        RebuildMap();
        UpdateUndoRedoButtonStates();
    }

    /// <summary>
    /// _root is readonly (see field declaration), so restoring a snapshot copies its fields into
    /// the existing _root instance rather than reassigning the reference - mirrors how
    /// ImportButton_Click already replaces the tree in place.
    /// </summary>
    private void RestoreRootFrom(MindMapNode snapshot)
    {
        CopyNodeStyleFields(_root, snapshot);
        _root.Children.Clear();
        foreach (var child in snapshot.Children)
            _root.Children.Add(CloneNode(child, preserveId: true));
    }

    private static void CopyNodeStyleFields(MindMapNode target, MindMapNode source)
    {
        target.Text = source.Text;
        target.IsExpanded = source.IsExpanded;
        target.BackgroundColor = source.BackgroundColor;
        target.BorderColor = source.BorderColor;
        target.BorderThickness = source.BorderThickness;
        target.NodeShape = source.NodeShape;
        target.Icons = new List<string>(source.Icons);
        target.IconBadgeColor = source.IconBadgeColor;
        target.IsBold = source.IsBold;
        target.IsItalic = source.IsItalic;
        target.TextColor = source.TextColor;
        target.FontFamily = source.FontFamily;
        target.FontSize = source.FontSize;
        target.Note = source.Note;
        target.LinkedNoteId = source.LinkedNoteId;
        target.CrossLinkTargetIds = new List<Guid>(source.CrossLinkTargetIds);
        target.ManualX = source.ManualX;
        target.ManualY = source.ManualY;
    }

    private void ExportToImage(string path, string extension, MindMapNode exportRoot, bool isBranch, IReadOnlyList<MindMapNode> selectedRoots)
    {
        if (MapCanvas.ActualWidth <= 0 || MapCanvas.ActualHeight <= 0)
            RebuildMap();

        var previousSearchQuery = _searchQuery;
        var previousZoom = ZoomSlider.Value;
        var previousRenderFilter = _renderOnlyNodes;
        var previousSuppressManualPositionCapture = _suppressManualPositionCapture;
        _searchQuery = string.Empty;
        RefreshSearchMatches(resetIndex: true);

        var expansionState = SnapshotExpansionState(_root);
        try
        {
            _suppressManualPositionCapture = true;
            ZoomSlider.Value = 1;
            SetExpanded(exportRoot, true);
            SetExpanded(_root, true);

            var branchNodes = isBranch
                ? CollectSubtreeNodes(selectedRoots.Count > 0 ? selectedRoots : new List<MindMapNode> { exportRoot })
                : null;
            _renderOnlyNodes = branchNodes;

            RebuildMap();
            MapCanvas.UpdateLayout();

            var bounds = isBranch
                ? GetBranchBounds(branchNodes ?? CollectSubtreeNodes(exportRoot))
                : GetAllContentBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0)
                throw new InvalidOperationException(LocalizationService.GetString("MindMapExportEmptyCanvas"));

            var visual = new DrawingVisual();
            double exportScale = 1;
            const double minBranchPixelSize = 1400;
            if (isBranch)
            {
                var branchBaseWidth = Math.Max(1, (int)Math.Ceiling(bounds.Width * 192.0 / 96.0));
                var branchBaseHeight = Math.Max(1, (int)Math.Ceiling(bounds.Height * 192.0 / 96.0));
                exportScale = Math.Max(1,
                    Math.Min(3,
                        Math.Max(minBranchPixelSize / branchBaseWidth, minBranchPixelSize / branchBaseHeight)));
            }

            using (var ctx = visual.RenderOpen())
            {
                ctx.PushTransform(new ScaleTransform(exportScale, exportScale));
                var backgroundBrush = TryFindResource("WindowBackground") as Brush ?? Brushes.White;
                ctx.DrawRectangle(backgroundBrush, null, new Rect(0, 0, bounds.Width, bounds.Height));
                ctx.DrawRectangle(new VisualBrush(MapCanvas)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                    Viewbox = bounds,
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, bounds.Width, bounds.Height),
                    ViewportUnits = BrushMappingMode.Absolute
                }, null, new Rect(0, 0, bounds.Width, bounds.Height));
                ctx.Pop();
            }

            var dpi = 192.0;
            var pixelWidth = Math.Max(1, (int)Math.Ceiling(bounds.Width * exportScale * dpi / 96.0));
            var pixelHeight = Math.Max(1, (int)Math.Ceiling(bounds.Height * exportScale * dpi / 96.0));
            var renderTarget = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
            renderTarget.Render(visual);

            BitmapEncoder encoder;
            if (string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                // JPEG has no alpha channel - drop it before encoding rather than let the
                // encoder do something undefined with it (the canvas already painted an opaque
                // background into renderTarget, so nothing is actually lost by dropping it here).
                BitmapSource opaqueSource = renderTarget.Format == PixelFormats.Bgr24
                    ? renderTarget
                    : new FormatConvertedBitmap(renderTarget, PixelFormats.Bgr24, null, 0);
                if (opaqueSource.CanFreeze)
                    opaqueSource.Freeze();

                encoder = new JpegBitmapEncoder { QualityLevel = 86 };
                encoder.Frames.Add(BitmapFrame.Create(opaqueSource));
            }
            else
            {
                encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(renderTarget));
            }

            using var stream = System.IO.File.Create(path);
            encoder.Save(stream);
        }
        finally
        {
            RestoreExpansionState(expansionState);
            ZoomSlider.Value = previousZoom;
            _renderOnlyNodes = previousRenderFilter;
            _suppressManualPositionCapture = previousSuppressManualPositionCapture;
            _searchQuery = previousSearchQuery;
            RefreshSearchMatches(resetIndex: true);
            RebuildMap();
        }
    }

    private static HashSet<MindMapNode> CollectSubtreeNodes(MindMapNode root)
    {
        var nodes = new HashSet<MindMapNode>();
        foreach (var node in EnumerateAllNodes(root))
            nodes.Add(node);

        return nodes;
    }

    private static HashSet<MindMapNode> CollectSubtreeNodes(IReadOnlyCollection<MindMapNode> roots)
    {
        var nodes = new HashSet<MindMapNode>();
        foreach (var root in roots)
        {
            foreach (var node in EnumerateAllNodes(root))
                nodes.Add(node);
        }

        return nodes;
    }

    private Rect GetAllContentBounds()
    {
        var renderedBounds = GetRenderedContentBounds();
        if (!renderedBounds.IsEmpty)
            return renderedBounds;

        var nodes = _layouts.Keys.ToList();
        return GetNodesBounds(nodes);
    }

    private Rect GetBranchBounds(MindMapNode branchRoot)
    {
        return GetBranchBounds(new HashSet<MindMapNode>(EnumerateAllNodes(branchRoot)));
    }

    private Rect GetBranchBounds(HashSet<MindMapNode> branchNodes)
    {
        var renderedBounds = GetRenderedContentBounds();
        if (!renderedBounds.IsEmpty)
            return renderedBounds;

        var connectedNodes = _layouts.Keys
            .Where(node => branchNodes.Contains(node) || IsDirectlyConnectedToBranch(node, branchNodes))
            .ToList();

        return GetNodesBounds(connectedNodes);
    }

    private bool IsDirectlyConnectedToBranch(MindMapNode candidate, HashSet<MindMapNode> branchNodes)
    {
        if (branchNodes.Contains(candidate))
            return true;

        return branchNodes.Any(branchNode =>
            branchNode.Children.Contains(candidate)
            || candidate.Children.Contains(branchNode)
            || FindParentNode(_root, branchNode) == candidate
            || FindParentNode(_root, candidate) == branchNode);
    }

    private Rect GetRenderedContentBounds()
    {
        Rect? union = null;

        foreach (UIElement child in MapCanvas.Children)
        {
            var localBounds = VisualTreeHelper.GetDescendantBounds(child);
            if (localBounds.IsEmpty)
                continue;

            var transformedBounds = child.TransformToAncestor(MapCanvas).TransformBounds(localBounds);
            union = union is null ? transformedBounds : Rect.Union(union.Value, transformedBounds);
        }

        if (union is null)
            return Rect.Empty;

        const double margin = 36;
        var bounded = union.Value;
        bounded.Inflate(margin, margin);

        bounded.X = Math.Max(0, bounded.X);
        bounded.Y = Math.Max(0, bounded.Y);

        var maxWidth = Math.Max(MapCanvas.ActualWidth, MapCanvas.Width);
        var maxHeight = Math.Max(MapCanvas.ActualHeight, MapCanvas.Height);
        bounded.Width = Math.Min(bounded.Width, Math.Max(1, maxWidth - bounded.X));
        bounded.Height = Math.Min(bounded.Height, Math.Max(1, maxHeight - bounded.Y));

        return bounded;
    }

    private Rect GetNodesBounds(IReadOnlyList<MindMapNode> nodes)
    {
        if (nodes.Count == 0)
            return Rect.Empty;

        var minX = nodes.Min(node => _layouts[node].X);
        var minY = nodes.Min(node => _layouts[node].Y);
        var maxX = nodes.Max(node => _layouts[node].X + _layouts[node].Width);
        var maxY = nodes.Max(node => _layouts[node].Y + _layouts[node].Height);

        const double margin = 100;
        minX = Math.Max(0, minX - margin);
        minY = Math.Max(0, minY - margin);
        maxX = Math.Min(MapCanvas.ActualWidth, maxX + margin);
        maxY = Math.Min(MapCanvas.ActualHeight, maxY + margin);

        return new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
    }

    private Dictionary<MindMapNode, bool> SnapshotExpansionState(MindMapNode root)
    {
        var state = new Dictionary<MindMapNode, bool>();
        foreach (var node in EnumerateAllNodes(root))
            state[node] = node.IsExpanded;

        return state;
    }

    private void RestoreExpansionState(Dictionary<MindMapNode, bool> state)
    {
        foreach (var pair in state)
            pair.Key.IsExpanded = pair.Value;
    }

    private static IEnumerable<MindMapNode> EnumerateAllNodes(MindMapNode root)
    {
        yield return root;
        foreach (var child in root.Children)
        {
            foreach (var descendant in EnumerateAllNodes(child))
                yield return descendant;
        }
    }

    private static bool IsAncestorNode(MindMapNode ancestor, MindMapNode descendant)
    {
        if (ReferenceEquals(ancestor, descendant))
            return false;

        foreach (var node in EnumerateAllNodes(ancestor))
        {
            if (ReferenceEquals(node, descendant))
                return true;
        }

        return false;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspaceClose is not null) { _workspaceClose(); return; }
        switch (GetCloseDecision())
        {
            case UnsavedCloseDecision.Cancel:
                return;
            case UnsavedCloseDecision.SaveAndClose:
                if (_workspaceSave is not null) { if (_workspaceSave()) _workspaceClose?.Invoke(); return; }
                SaveAndClose();
                return;
            case UnsavedCloseDecision.LeaveWithoutSaving:
                _allowCloseWithoutPrompt = true;
                Close();
                return;
        }
    }

    private void MindMapPreviewWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_workspaceDisposed) return;
        if (_allowCloseWithoutPrompt)
            return;

        switch (GetCloseDecision())
        {
            case UnsavedCloseDecision.Cancel:
                e.Cancel = true;
                return;
            case UnsavedCloseDecision.SaveAndClose:
                _allowCloseWithoutPrompt = true;
                MarkCurrentStateSaved();
                e.Cancel = true;
                WindowContentEnterHelper.CloseWithDialogResult(this, true);
                return;
            case UnsavedCloseDecision.LeaveWithoutSaving:
                _allowCloseWithoutPrompt = true;
                return;
        }
    }

    private Brush GetThemeBrush(string resourceKey, Color fallbackColor)
    {
        if (TryFindResource(resourceKey) is Brush brush)
            return brush;

        return FrozenBrush(fallbackColor);
    }

    private Brush GetNodeForeground(Brush nodeBackground, bool isRoot, MindMapNode node)
    {
        // Custom fill: pick readable black/white from luminance. Default cards always use theme text.
        if (!string.IsNullOrWhiteSpace(node.BackgroundColor) && nodeBackground is SolidColorBrush solid)
            return IsLightColor(solid.Color)
                ? Brushes.Black
                : Brushes.White;

        return GetCardTitleBrush();
    }

    private static bool IsLightColor(Color color)
    {
        var luminance = (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B);
        return luminance >= 145;
    }

    private bool IsDarkTheme()
    {
        if (TryFindResource("WindowBackground") is SolidColorBrush background)
            return !IsLightColor(background.Color);

        return false;
    }

    private static IReadOnlyList<string> ParseTags(string? rawTags)
    {
        if (string.IsNullOrWhiteSpace(rawTags))
            return Array.Empty<string>();

        return rawTags
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries)
            .Select(tag => tag.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private enum MindMapLayoutMode
    {
        BalancedTree,
        RightTree,
        LeftTree,
        TopDown,
        Radial
    }

    private sealed record NodeLayout(double X, double Y, double Width, double Height, int Direction, int Depth);

    // ---------------------------------------------------------------------------------------------
    // Zoom-session tile layer: the live canvas collapses during zoom gestures and cached off-thread
    // snapshots (SkiaMindMapSnapshotCache) cover the viewport, so zooming never blocks on the
    // LayoutTransform re-raster of the whole map. The canvas returns after the settle delay.
    // ---------------------------------------------------------------------------------------------

    private void MapCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The wrapper (MapViewport) carries the zoom transform and must keep the canvas extent
        // even while the canvas is collapsed during a tile session, or the scrollable area
        // would collapse mid-zoom.
        if (MapViewport is null || MapCanvas is null || MapCanvas.ActualWidth <= 0 || MapCanvas.ActualHeight <= 0)
            return;

        MapViewport.MinWidth = MapCanvas.ActualWidth;
        MapViewport.MinHeight = MapCanvas.ActualHeight;
    }

    /// <summary>Pre-renders the whole-canvas snapshot at the current zoom so zoom sessions
    /// start with a snapshot ready instead of a blank flash.</summary>
    private void WarmSnapshot()
    {
        if (_mapClosed || MapScaleTransform is null || _tileSessionActive)
            return;

        // Always rebuild: the canvas extent changes with the zoom (viewport fit), and a stale
        // extent would size the snapshot image wrong and shift the scrollable area.
        var snapshot = BuildTileSnapshot();
        if (snapshot is null)
            return;

        var zoom = (float)Math.Max(0.1, MapScaleTransform.ScaleX);
        var dpi = (float)VisualTreeHelper.GetDpi(ContentRoot).PixelsPerDip;
        var request = ++_snapshotRequest;
        var theme = ThemeManager.CurrentTheme;
        Scriblucent.Rendering.SkiaMindMapSnapshotCache.Request(snapshot, zoom, dpi, result =>
        {
            if (_mapClosed || _tileSessionActive || request != _snapshotRequest
                || snapshot.Revision != _tileRevision || theme != ThemeManager.CurrentTheme)
                return;
            if (result is not null) ApplySnapshot(result, snapshot);
        });
    }

    private void BeginTileSession()
    {
        if (MapSnapshotLayer is null || MapCanvas is null || MapViewport is null)
            return;

        if (!_tileSessionActive)
        {
            if (Scriblucent.Services.ScrollPerfDiag.Enabled)
            {
                var sv = MapScrollViewer;
                Scriblucent.Services.ScrollPerfDiag.Log(
                    $"mmTile\tbegin\t{MapScaleTransform.ScaleX:F3}\t{sv.HorizontalOffset:F2}\t{sv.VerticalOffset:F2}\t" +
                    $"{sv.ScrollableWidth:F2}\t{sv.ScrollableHeight:F2}");
            }

            _tileSessionActive = true;
            if (MapSnapshotLayer.Source is not null && _snapshotSourceRevision == _tileRevision
                && _snapshotSourceTheme == ThemeManager.CurrentTheme)
            {
                MapCanvas.Visibility = Visibility.Collapsed;
                MapSnapshotLayer.Visibility = Visibility.Visible;
            }
            // Else: no snapshot yet (first session before the warm landed) - keep the live
            // canvas; the session becomes a no-op until the warm applies.
        }

        _tileSettleTimer?.Stop();
        _tileSettleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _tileSettleTimer.Tick += (_, _) => EndTileSession();
        _tileSettleTimer.Start();
    }

    private void EndTileSession()
    {
        _tileSettleTimer?.Stop();
        _tileSettleTimer = null;

        if (!_tileSessionActive)
            return;

        _tileSessionActive = false;

        if (Scriblucent.Services.ScrollPerfDiag.Enabled)
        {
            var sv = MapScrollViewer;
            Scriblucent.Services.ScrollPerfDiag.Log(
                $"mmTile\tend\t{MapScaleTransform.ScaleX:F3}\t{sv.HorizontalOffset:F2}\t{sv.VerticalOffset:F2}\t" +
                $"{sv.ScrollableWidth:F2}\t{sv.ScrollableHeight:F2}");
        }

        // Render the fresh snapshot at the settled zoom off-thread; when it lands, swap it in,
        // force the live canvas's re-render underneath, then lift the snapshot - both show the
        // same content at the same zoom, so the transition is seamless. The snapshot is rebuilt
        // because the canvas extent changed with the zoom.
        var snapshot = BuildTileSnapshot();
        if (snapshot is null || MapScaleTransform is null)
        {
            RestoreCanvas();
            return;
        }

        var zoom = (float)Math.Max(0.1, MapScaleTransform.ScaleX);
        var dpi = (float)VisualTreeHelper.GetDpi(ContentRoot).PixelsPerDip;
        var request = ++_snapshotRequest;
        var theme = ThemeManager.CurrentTheme;
        Scriblucent.Rendering.SkiaMindMapSnapshotCache.Request(snapshot, zoom, dpi, result =>
        {
            if (_mapClosed || _tileSessionActive || request != _snapshotRequest)
                return; // a new session started - the render still warms the cache
            if (result is not null && snapshot.Revision == _tileRevision && theme == ThemeManager.CurrentTheme)
                ApplySnapshot(result, snapshot);
            RestoreCanvas();
        });
    }

    private void ApplySnapshot(Scriblucent.Rendering.SkiaMindMapSnapshotCache.SnapshotResult result,
        Scriblucent.Rendering.MindMapTileSnapshot snapshot)
    {
        if (MapSnapshotLayer is null || _mapClosed)
            return;

        if (Scriblucent.Services.ScrollPerfDiag.Enabled)
        {
            var sv = MapScrollViewer;
            Scriblucent.Services.ScrollPerfDiag.Log(
                $"mmTile\tapply\t{MapScaleTransform.ScaleX:F3}\t{sv.HorizontalOffset:F2}\t{sv.VerticalOffset:F2}\t" +
                $"{sv.ScrollableWidth:F2}\t{sv.ScrollableHeight:F2}");
        }

        MapSnapshotLayer.Source = result.Image;
        _snapshotSourceRevision = snapshot.Revision;
        _snapshotSourceTheme = ThemeManager.CurrentTheme;
        // The image represents the full canvas extent; the wrapper's zoom transform scales it
        // continuously during gestures (no per-level swaps).
        MapSnapshotLayer.Width = snapshot.CanvasWidthDip;
        MapSnapshotLayer.Height = snapshot.CanvasHeightDip;
        Canvas.SetLeft(MapSnapshotLayer, 0);
        Canvas.SetTop(MapSnapshotLayer, 0);
    }

    private void RestoreCanvas()
    {
        if (MapCanvas is not null)
        {
            MapCanvas.Visibility = Visibility.Visible;
            // Force the re-render at the final zoom before lifting the snapshot - the canvas
            // paints underneath the (sharp, settled-zoom) snapshot, so the swap is seamless.
            MapCanvas.UpdateLayout();
        }

        if (MapSnapshotLayer is not null)
            MapSnapshotLayer.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Snapshots the already-built live visuals (node borders + connector paths) into the
    /// off-thread renderable model — extracts the fitted titles/colors/icons from the visual
    /// tree instead of re-measuring text, so a session enter adds no layout cost.
    /// </summary>
    private Scriblucent.Rendering.MindMapTileSnapshot? BuildTileSnapshot()
    {
        if (MapCanvas is null || _root is null)
            return null;

        var nodes = new List<Scriblucent.Rendering.MindMapTileNode>();
        var edges = new List<Scriblucent.Rendering.MindMapTileEdge>();

        foreach (var child in MapCanvas.Children)
        {
            if (child is Border { Tag: MindMapNode node } border)
            {
                nodes.Add(BuildNodeSnapshot(node, border));
            }
            else if (child is ShapePath { Data: PathGeometry geometry, Stroke: SolidColorBrush stroke })
            {
                foreach (var figure in geometry.Figures)
                {
                    if (figure.Segments.Count > 0 && figure.Segments[0] is BezierSegment bezier)
                    {
                        edges.Add(new Scriblucent.Rendering.MindMapTileEdge
                        {
                            SX = figure.StartPoint.X,
                            SY = figure.StartPoint.Y,
                            C1X = bezier.Point1.X,
                            C1Y = bezier.Point1.Y,
                            C2X = bezier.Point2.X,
                            C2Y = bezier.Point2.Y,
                            EX = bezier.Point3.X,
                            EY = bezier.Point3.Y,
                            Color = ToArgb(stroke.Color),
                        });
                    }
                }
            }
        }

        return new Scriblucent.Rendering.MindMapTileSnapshot
        {
            MapId = _tileMapId,
            Revision = _tileRevision,
            // Width/Height (not ActualWidth) stay current even while the canvas is collapsed
            // during a session - ActualWidth would lag behind the zoom-dependent canvas size.
            CanvasWidthDip = Math.Max(1, double.IsNaN(MapCanvas.Width) ? MapCanvas.ActualWidth : MapCanvas.Width),
            CanvasHeightDip = Math.Max(1, double.IsNaN(MapCanvas.Height) ? MapCanvas.ActualHeight : MapCanvas.Height),
            Nodes = nodes,
            Edges = edges,
        };
    }

    private Scriblucent.Rendering.MindMapTileNode BuildNodeSnapshot(MindMapNode node, Border border)
    {
        var frame = SurfaceContour.EffectiveFrame(border);
        var outer = SurfaceContour.Create(border.RenderSize, border.CornerRadius, frame);
        var borderContour = SurfaceContour.Ring(outer,
            SurfaceContour.Create(border.RenderSize, border.CornerRadius, frame, frame.Left));
        var (resource, thickness) = ResolveNodeRim(node);
        var rim = resource is null ? null : TryFindResource(resource) as Brush;
        var rimContour = rim is null ? null : SurfaceContour.Ring(outer,
            SurfaceContour.Create(border.RenderSize, border.CornerRadius, frame, thickness));
        var glyphs = new List<Scriblucent.Rendering.MindMapTileGlyph>();
        var metadata = new List<Scriblucent.Rendering.MindMapTileText>();
        Scriblucent.Rendering.MindMapTileText? title = null;
        foreach (var element in NodeVisuals(border))
        {
            if (element is AppIcon icon && icon.Visibility == Visibility.Visible && icon.ActualWidth > 0)
            {
                var path = new PackIconLucide { Kind = icon.LucideKind }.Data;
                if (string.IsNullOrEmpty(path)) continue;
                var position = NodeLayoutOffset(icon, border);
                glyphs.Add(new() { X = position.X, Y = position.Y, Width = icon.ActualWidth,
                    Height = icon.ActualHeight, Path = path, Color = BrushColor(icon.Foreground) });
            }
            else if (element is TextBlock text && text.Visibility == Visibility.Visible && text.ActualWidth > 0)
            {
                var position = NodeLayoutOffset(text, border);
                var snapshot = new Scriblucent.Rendering.MindMapTileText { X = position.X, Y = position.Y,
                    Width = text.ActualWidth, Height = text.ActualHeight, Text = text.Text,
                    FontFamily = text.FontFamily.Source, FontSizeDip = (float)text.FontSize,
                    FontWeight = text.FontWeight.ToOpenTypeWeight(), Italic = text.FontStyle == FontStyles.Italic,
                    Color = BrushColor(text.Foreground) };
                // Command templates may acquire helper text later; only document rows enter the tile.
                if (text.Parent is DockPanel) { snapshot.MaxLines = MaxTitleLines; title = snapshot; }
                else if (text.Parent is StackPanel) metadata.Add(snapshot);
            }
        }
        var (borderTop, borderBody, borderBottom) = BrushColors(border.BorderBrush);
        var (rimTop, rimBody, rimBottom) = BrushColors(rim);
        return new Scriblucent.Rendering.MindMapTileNode
        {
            X = Canvas.GetLeft(border), Y = Canvas.GetTop(border), Width = border.Width, Height = border.Height,
            Background = BrushColor(border.Background), Border = borderBody,
            BorderTop = borderTop, BorderBottom = borderBottom,
            OuterContour = SnapshotGeometry(outer),
            BorderContour = SnapshotGeometry(borderContour),
            RimContour = rimContour is null ? null : SnapshotGeometry(rimContour),
            RimTop = rimTop, RimBody = rimBody, RimBottom = rimBottom,
            Title = title, Glyphs = glyphs, Metadata = metadata
        };
    }

    private static string SnapshotGeometry(Geometry geometry)
        => PathGeometry.CreateFromGeometry(geometry).ToString(CultureInfo.InvariantCulture);

    private static IEnumerable<DependencyObject> NodeVisuals(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            // Popups render in their own window and are not part of a zoom tile.
            if (child is Popup) continue;
            yield return child;
            foreach (var descendant in NodeVisuals(child)) yield return descendant;
        }
    }
    private static Point NodeLayoutOffset(Visual element, Visual ancestor)
    {
        var position = new Point();
        for (Visual? current = element; current is not null && !ReferenceEquals(current, ancestor);
             current = VisualTreeHelper.GetParent(current) as Visual)
            position += VisualTreeHelper.GetOffset(current);
        return position; // Layout positions exclude transient button press/arrival transforms.
    }
    private static uint BrushColor(Brush? brush)
        => brush is SolidColorBrush solid ? ToArgb(solid.Color) : 0;
    private static (uint Top, uint Body, uint Bottom) BrushColors(Brush? brush)
    {
        if (brush is not LinearGradientBrush { GradientStops.Count: > 0 } gradient)
        { var color = BrushColor(brush); return (color, color, color); }
        return (ToArgb(gradient.GradientStops.First().Color),
            ToArgb(gradient.GradientStops.OrderBy(stop => Math.Abs(stop.Offset - .42)).First().Color),
            ToArgb(gradient.GradientStops.Last().Color));
    }

    private static uint ToArgb(Color color)
        => ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
}


