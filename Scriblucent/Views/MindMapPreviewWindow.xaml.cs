using Scriblucent.Animations;
using Scriblucent.Icons;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent;
using Scriblucent.Services;
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

public partial class MindMapPreviewWindow : Window, INotifyPropertyChanged
{
    private enum UnsavedCloseDecision
    {
        Cancel,
        LeaveWithoutSaving,
        SaveAndClose
    }

    private const double BaseNodeWidth = 190;
    private const double MaxNodeWidth = 340;
    // Every layout algorithm (balanced/left/right tree, top-down, radial) treats this as a single
    // shared column width, not a per-node one - reworking all four to place variable-width boxes
    // without overlapping neighbors is a much bigger change than this needs. Instead the whole
    // map uses one width, recomputed once per RebuildMapCore pass (see ComputeEffectiveNodeWidth)
    // to fit whatever node currently needs the most room at its own font size - static, not const,
    // and static (not instance) because the layout-measurement helpers below are static and this
    // window is always modal/single-instance (see the clipboard fields for the same reasoning).
    private static double NodeWidth = BaseNodeWidth;
    private const double HorizontalGap = 150;
    private const double VerticalGap = 18;
    private const double VerticalLayoutLevelGap = 118;
    private const double VerticalLayoutNodeGap = 34;
    private const double CanvasPadding = 52;
    // Must mirror DrawNodes' node Border: Padding(12,8,12,8) and the icon badge's 28px right
    // margin on the text - MeasureNodeHeight needs the same constraints DrawNodes actually
    // renders with, or a node with a large font/long text will measure short and get clipped.
    private const double NodeTextHorizontalPadding = 24;
    private const double NodeTextVerticalPadding = 16;
    private const double NodeIconTextMargin = 28;
    private const double MinNodeHeight = 52;
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
    private int _arrangePanelAnimationVersion;
    private int _searchPanelAnimationVersion;
    private int _fontPanelAnimationVersion;
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
    // MindMapPreviewWindow is always opened via ShowDialog (confirmed - only one instance can
    // ever be open at a time), so there's no cross-window conflict risk - and a static field means
    // the clipboard survives closing one map and opening another, matching how copy/paste behaves
    // in every other app instead of resetting on close.
    private static MindMapNode? _clipboardNode;
    private static CopiedNodeStyle? _copiedStyle;

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

    private static readonly Brush[] LightNodeBackgrounds =
    [
        FrozenBrush(Color.FromRgb(63, 111, 232)),
        FrozenBrush(Color.FromRgb(236, 253, 245)),
        FrozenBrush(Color.FromRgb(239, 246, 255)),
        FrozenBrush(Color.FromRgb(245, 243, 255)),
        FrozenBrush(Color.FromRgb(255, 251, 235))
    ];

    private static readonly Brush[] DarkNodeBackgrounds =
    [
        FrozenBrush(Color.FromRgb(63, 111, 232)),
        FrozenBrush(Color.FromRgb(34, 56, 49)),
        FrozenBrush(Color.FromRgb(30, 48, 69)),
        FrozenBrush(Color.FromRgb(49, 39, 72)),
        FrozenBrush(Color.FromRgb(73, 56, 37))
    ];

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
        Action<Guid>? openNoteAction = null)
    {
        _root = root;
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
        Scriblucent.Services.WindowThemeService.Register(this);
        PopulateFontFamilyBox();

        TitleTextBox.Text = string.IsNullOrWhiteSpace(title)
            ? (string.IsNullOrWhiteSpace(root.Text) ? LocalizationService.GetString("MindMapUntitled") : root.Text.Trim())
            : title.Trim();
        TagsTextBox.Text = tags is null
            ? string.Empty
            : string.Join(", ", tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()));
        ConfigureAiGeneratedIndicator(modelDisplayName);
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

        Loaded += (_, _) =>
        {
            MapCanvas.PreviewMouseMove += MapCanvas_PreviewMouseMove;
            MapCanvas.MouseMove += MapCanvas_MouseMove;
            MapCanvas.PreviewMouseLeftButtonUp += MapCanvas_PreviewMouseLeftButtonUp;
            MapCanvas.LostMouseCapture += MapCanvas_LostMouseCapture;
            RebuildMap(animate: false);
            MarkCurrentStateSaved();
            _isInitializing = false;
            UpdateEditedIndicator();
        };
        WindowContentEnterHelper.AnimateOnLoaded(this);
        SizeChanged += (_, _) => QueueMapViewportRefresh();
        StateChanged += (_, _) => QueueMapViewportRefresh();
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
        UpdateEditedIndicator();
    }

    public bool HasPendingAutoSaveChanges() => HasUnsavedChanges();

    public void MarkCurrentStateAutoSaved() => MarkCurrentStateSaved();

    private bool HasUnsavedChanges()
    {
        if (_isInitializing)
            return false;

        return !string.Equals(GetEditorSnapshot(), _lastSavedSnapshot, StringComparison.Ordinal);
    }

    private void UpdateEditedIndicator()
    {
        if (_isInitializing || EditedIndicatorText is null)
            return;

        UiMotion.AnimateInlineReveal(EditedIndicatorText, HasUnsavedChanges());
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
            this,
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

    public MindMapDocument ToDocument(MindMapDocument? existingDocument = null)
    {
        if (_isManualPositioningMode)
            CaptureVisibleLayoutAsManualPositions();
        else
            ClearManualPositions(_root);

        return new MindMapDocument
        {
            Id = existingDocument?.Id ?? Guid.NewGuid(),
            Title = string.IsNullOrWhiteSpace(EditorTitle)
                ? LocalizationService.GetString("MindMapUntitled")
                : EditorTitle,
            Tags = Tags.ToList(),
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
            IsPinned = existingDocument?.IsPinned ?? false
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
            ManualPositioningStatusDot.Fill = Brushes.White;
            ManualPositioningStateBadge.Background = Brushes.White;
            ManualPositioningStateBadge.BorderBrush = Brushes.Transparent;
            ManualPositioningStateText.Foreground = GetThemeBrush("MindMapSelectedBorderBrush", Color.FromRgb(63, 111, 232));
            ManualPositioningToggle.ToolTip = LocalizationService.GetString("MindMapManualPositioningOnHint");
            return;
        }

        ManualPositioningStateText.Text = LocalizationService.GetString("MindMapManualPositioningOff");
        ManualPositioningStatusDot.Fill = GetThemeBrush("TextColorSecondary", Color.FromRgb(92, 92, 92));
        ManualPositioningStateBadge.Background = GetThemeBrush("IconButtonBackground", Color.FromRgb(245, 247, 251));
        ManualPositioningStateBadge.BorderBrush = Brushes.Transparent;
        ManualPositioningStateText.Foreground = GetThemeBrush("TextColorSecondary", Color.FromRgb(92, 92, 92));
        ManualPositioningToggle.ToolTip = LocalizationService.GetString("MindMapManualPositioningOffHint");
    }

    private void ConfigureAiGeneratedIndicator(string? modelDisplayName)
    {
        _modelDisplayName = modelDisplayName?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(_modelDisplayName))
        {
            AiGeneratedInfoBadge.ToolTip = null;
            if (_isInitializing)
            {
                AiGeneratedInfoBadge.Visibility = Visibility.Collapsed;
                return;
            }

            UiMotion.AnimateInlineReveal(AiGeneratedInfoBadge, false);
            return;
        }

        AiGeneratedInfoBadge.ToolTip = string.Format(
            LocalizationService.GetString("MindMapGeneratedWithModel"),
            _modelDisplayName);

        if (_isInitializing)
        {
            AiGeneratedInfoBadge.Visibility = Visibility.Visible;
            return;
        }

        UiMotion.AnimateInlineReveal(AiGeneratedInfoBadge, true);
    }

    private void RebuildMap(bool animate = true)
    {
        if (animate && !_isInitializing && IsLoaded && !UiMotion.PrefersReducedMotion)
        {
            UiMotion.Stop(MapCanvas);
            UiMotion.Fade(
                MapCanvas,
                0,
                UiMotion.NavFadeOutMs,
                UiMotion.InCubic,
                () =>
                {
                    RebuildMapCore();
                    MapCanvas.Opacity = 0;
                    UiMotion.Fade(MapCanvas, 1, UiMotion.QuickMs, UiMotion.OutCubic);
                });
            return;
        }

        RebuildMapCore();
    }

    private void RebuildMapCore()
    {
        RefreshSearchMatches(resetIndex: false);

        _layouts.Clear();
        MapCanvas.Children.Clear();
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
            return;

        if (string.IsNullOrWhiteSpace(_searchQuery))
        {
            SearchResultsTextBlock.Text = LocalizationService.GetString("MindMapSearchNoResults");
            return;
        }

        if (_searchMatches.Count == 0)
        {
            SearchResultsTextBlock.Text = string.Format(
                CultureInfo.CurrentCulture,
                LocalizationService.GetString("MindMapSearchResultsFormat"),
                0,
                0);
            return;
        }

        SearchResultsTextBlock.Text = string.Format(
            CultureInfo.CurrentCulture,
            LocalizationService.GetString("MindMapSearchResultsFormat"),
            _currentSearchMatchIndex + 1,
            _searchMatches.Count);
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
            if (!IsLoaded)
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

        var zoom = Math.Max(ZoomSlider?.Value ?? 1, 0.1);
        var maxRight = _layouts.Values.Max(layout => layout.X + layout.Width);
        var maxBottom = _layouts.Values.Max(layout => layout.Y + layout.Height);

        var viewportWidth = MapScrollViewer.ViewportWidth;
        var viewportHeight = MapScrollViewer.ViewportHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            viewportWidth = MapScrollViewer.ActualWidth;
            viewportHeight = MapScrollViewer.ActualHeight;
        }

        MapCanvas.Width = Math.Max(viewportWidth / zoom, maxRight + CanvasPadding);
        MapCanvas.Height = Math.Max(viewportHeight / zoom, maxBottom + CanvasPadding);
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

    private static double MeasureChildrenHeight(IReadOnlyList<MindMapNode> children)
    {
        if (children.Count == 0)
            return 0;

        return children.Sum(MeasureSubtree)
            + VerticalGap * Math.Max(0, children.Count - 1);
    }

    private static double MeasureSubtree(MindMapNode node)
    {
        var ownHeight = MeasureNodeHeight(node);
        if (!node.IsExpanded || node.Children.Count == 0)
            return ownHeight;

        var childrenHeight = node.Children.Sum(MeasureSubtree)
            + VerticalGap * Math.Max(0, node.Children.Count - 1);

        return Math.Max(ownHeight, childrenHeight);
    }

    private static double MeasureVerticalSubtree(MindMapNode node)
    {
        if (!node.IsExpanded || node.Children.Count == 0)
            return NodeWidth;

        var childrenWidth = node.Children.Sum(MeasureVerticalSubtree)
            + VerticalLayoutNodeGap * Math.Max(0, node.Children.Count - 1);

        return Math.Max(NodeWidth, childrenWidth);
    }

    private static Typeface CreateNodeTypeface(MindMapNode node, out double fontSize)
    {
        fontSize = node.FontSize ?? DefaultNodeFontSize;
        var fontFamily = !string.IsNullOrWhiteSpace(node.FontFamily)
            ? new FontFamily(node.FontFamily)
            : DefaultNodeFontFamily;

        return new Typeface(
            fontFamily,
            node.IsItalic ? FontStyles.Italic : FontStyles.Normal,
            node.IsBold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);
    }

    // Recomputes the whole map's shared node width (see NodeWidth's declaration comment) from
    // whatever's currently visible, so a node with a large font/long-ish title gets enough room
    // without needing to badly wrap a short phrase into a tall, narrow column. Only currently-
    // visible nodes count - a big font on a collapsed-away node shouldn't widen nodes the user
    // can actually see, and every expand/collapse already triggers a fresh RebuildMap anyway, so
    // revealing it later naturally re-measures.
    private static double ComputeEffectiveNodeWidth(MindMapNode root)
    {
        var maxWidth = BaseNodeWidth;

        foreach (var node in EnumerateVisibleNodes(root))
        {
            var typeface = CreateNodeTypeface(node, out var fontSize);
            var formatted = new FormattedText(
                string.IsNullOrEmpty(node.Text) ? " " : node.Text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                Brushes.Black,
                1.0);

            var iconMargin = node.Icons.Count > 0 ? NodeIconTextMargin : 0;
            var neededWidth = formatted.WidthIncludingTrailingWhitespace + NodeTextHorizontalPadding + iconMargin;
            maxWidth = Math.Max(maxWidth, Math.Min(neededWidth, MaxNodeWidth));
        }

        return maxWidth;
    }

    // Measures against the node's real font (size/family/bold/italic), not a fixed default - a
    // character-count heuristic tuned for the default ~14px font badly underestimates height
    // once a node has a larger custom font size, clipping wrapped text inside a too-short card.
    private static double MeasureNodeHeight(MindMapNode node)
    {
        var typeface = CreateNodeTypeface(node, out var fontSize);
        var iconMargin = node.Icons.Count > 0 ? NodeIconTextMargin : 0;
        var availableWidth = Math.Max(20, NodeWidth - NodeTextHorizontalPadding - iconMargin);

        var formatted = new FormattedText(
            string.IsNullOrEmpty(node.Text) ? " " : node.Text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.Black,
            1.0)
        {
            MaxTextWidth = availableWidth,
            TextAlignment = TextAlignment.Center
        };

        return Math.Clamp(formatted.Height + NodeTextVerticalPadding, MinNodeHeight, MaxNodeHeight);
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
        SelectSingleNode(node);
        ApplyNodeSelectionVisuals();
    }

    private void UpdateManualNodeDragPosition(Point currentCanvasPos)
    {
        if (!_isDraggingNode || _draggedNode is null || _draggedBorder is null)
            return;

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

    private void ApplyNodeBorderChrome(Border border, MindMapNode node)
    {
        var isRoot = ReferenceEquals(node, _root);
        var defaultThickness = node.BorderThickness > 0 ? node.BorderThickness : 1;
        border.BorderThickness = new Thickness(defaultThickness);
        border.BorderBrush = !string.IsNullOrWhiteSpace(node.BorderColor)
            ? FrozenBrush((Color)ColorConverter.ConvertFromString(node.BorderColor))
            : isRoot
                ? GetThemeBrush("MindMapNodeRootBorderBrush", Color.FromRgb(47, 92, 208))
                : GetThemeBrush("MindMapNodeBorderBrush", Color.FromRgb(194, 203, 220));
        border.Effect = null;

        if (_searchMatches.Contains(node))
        {
            border.BorderBrush = GetThemeBrush("MindMapSearchMatchBorderBrush", Color.FromRgb(245, 158, 11));
            border.BorderThickness = new Thickness(2);
        }

        if (ReferenceEquals(node, CurrentSearchMatch))
        {
            border.BorderBrush = GetThemeBrush("MindMapSearchActiveBorderBrush", Color.FromRgb(245, 114, 11));
            border.BorderThickness = new Thickness(3);
            // Border highlight only — avoid a permanent live DropShadowEffect on canvas nodes.
        }

        if (_selectedNodes.Contains(node))
        {
            border.BorderBrush = GetThemeBrush("MindMapSelectedBorderBrush", Color.FromRgb(63, 111, 232));
            border.BorderThickness = new Thickness(3);
        }
    }

    private void ApplyNodeSelectionVisuals()
    {
        foreach (var border in MapCanvas.Children.OfType<Border>())
        {
            if (border.Tag is MindMapNode node)
                ApplyNodeBorderChrome(border, node);
        }

        RefreshFontPanelFromSelection();
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

        var reparented = dropTarget is not null && ReparentNode(node, dropTarget);

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

        if (_dropTargetHoverNode is not null)
        {
            var previousBorder = FindNodeBorder(_dropTargetHoverNode);
            if (previousBorder is not null)
                ApplyNodeBorderChrome(previousBorder, _dropTargetHoverNode);
        }

        _dropTargetHoverNode = candidate;

        if (candidate is not null)
        {
            var border = FindNodeBorder(candidate);
            if (border is not null)
            {
                border.BorderBrush = GetThemeBrush("MindMapSearchActiveBorderBrush", Color.FromRgb(245, 114, 11));
                border.BorderThickness = new Thickness(3);
            }
        }
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
    private Popup BuildNotePopup(Border placementTarget, string noteText)
    {
        var shadowBorder = new Border
        {
            Background = GetThemeBrush("CardBackground", Color.FromRgb(255, 255, 255)),
            BorderBrush = GetThemeBrush("PopupBorder", Color.FromRgb(208, 208, 220)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Effect = TryFindResource("Elevation.Popup") as Effect,
            IsHitTestVisible = false
        };

        var contentBorder = new Border
        {
            Background = GetThemeBrush("CardBackground", Color.FromRgb(255, 255, 255)),
            BorderBrush = GetThemeBrush("PopupBorder", Color.FromRgb(208, 208, 220)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            MaxWidth = 260,
            Child = new TextBlock
            {
                Text = noteText,
                TextWrapping = TextWrapping.Wrap,
                Foreground = GetThemeBrush("TextColor", Color.FromRgb(31, 41, 55)),
                FontSize = TypeScale.Px(TypeScale.Caption, 12)
            }
        };

        // Padding-only outer border, matching Chrome.xaml's own popups - gives the shadow's blur
        // and offset room to render instead of getting clipped at the popup's own edge.
        var shell = new Grid { Margin = new Thickness(10) };
        shell.Children.Add(shadowBorder);
        shell.Children.Add(contentBorder);

        return new Popup
        {
            PlacementTarget = placementTarget,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 6,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = shell
        };
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
            Padding = new Thickness(12, 8, 12, 8),
            Cursor = Cursors.Hand
        };

        ApplyNodeBorderChrome(border, node);

        var mainPanel = new Grid();

        // Node text
        var text = new TextBlock
        {
            Text = node.Text,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontWeight = node.IsBold ? FontWeights.Bold : isRoot ? FontWeights.SemiBold : FontWeights.Normal,
            FontStyle = node.IsItalic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = !string.IsNullOrWhiteSpace(node.TextColor)
                ? FrozenBrush((Color)ColorConverter.ConvertFromString(node.TextColor))
                : GetNodeForeground(nodeBackground, isRoot, node),
            Margin = node.Icons.Count > 0 ? new Thickness(0, 0, 28, 0) : new Thickness(0)
        };

        // Left unset (rather than assigned a hardcoded fallback) when the node has no override,
        // so untouched nodes keep inheriting the ambient default exactly as before this field
        // existed - only nodes with an explicit choice deviate from it.
        if (!string.IsNullOrWhiteSpace(node.FontFamily))
            text.FontFamily = new FontFamily(node.FontFamily);
        if (node.FontSize is double fontSize)
            text.FontSize = fontSize;

        mainPanel.Children.Add(text);

        if (node.Icons.Count > 0)
        {
            var iconBadgeColor = !string.IsNullOrWhiteSpace(node.IconBadgeColor)
            ? (Color)ColorConverter.ConvertFromString(node.IconBadgeColor)
            : Color.FromRgb(245, 158, 11); // Default amber

            var iconBadge = new Border
            {
                Background = FrozenBrush(iconBadgeColor),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 4, 6, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -8, -8, 0) // Position outside the node
            };

            // FreeMind allows stacking any number of icons on a node - shown together in this one
            // badge (space-separated) rather than a separate badge per icon, so the layout doesn't
            // need to reason about positioning an unbounded number of corner badges.
            var iconText = new TextBlock
            {
                Text = string.Join(" ", node.Icons),
                FontSize = TypeScale.Px(TypeScale.Body, 14),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            iconBadge.Child = iconText;
            mainPanel.Children.Add(iconBadge);
        }

        if (!string.IsNullOrWhiteSpace(node.Note))
        {
            // Opposite corner from the icon badge (top-right) - a fixed indicator, not a
            // user-choosable one like Icon, so it always reads as "this node has a note" rather
            // than needing its own color/glyph picker. Click opens the full text in a popup
            // (a tooltip was awkward for anything longer than a line or two); StyleNodeDialog is
            // where it's actually written/edited.
            var noteBadge = new Border
            {
                Background = FrozenBrush(Color.FromRgb(96, 165, 250)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(5, 3, 5, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(-8, 0, 0, -8),
                Cursor = Cursors.Hand,
                ToolTip = LocalizationService.GetString("MindMapClickToViewNote")
            };

            var noteGlyph = new TextBlock
            {
                Text = "\U0001F5D2️",
                FontSize = TypeScale.Px(TypeScale.Body, 12),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            noteBadge.Child = noteGlyph;

            var notePopup = BuildNotePopup(noteBadge, node.Note!);
            noteBadge.MouseLeftButtonDown += (_, e) =>
            {
                notePopup.IsOpen = !notePopup.IsOpen;
                e.Handled = true;
            };

            mainPanel.Children.Add(notePopup);
            mainPanel.Children.Add(noteBadge);
        }

        if (node.HasChildren)
        {
            // A real click target replacing the old decorative "-"/"+" baked into the node text.
            // Top-left corner is the one badge slot the icon (top-right) and note (bottom-left)
            // badges leave free.
            var foldToggle = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = GetThemeBrush("IconButtonBackground", Color.FromRgb(245, 247, 251)),
                BorderBrush = GetThemeBrush("MindMapNodeBorderBrush", Color.FromRgb(194, 203, 220)),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(-10, -10, 0, 0),
                Cursor = Cursors.Hand,
                ToolTip = LocalizationService.GetString(node.IsExpanded ? "MindMapCollapseThisNode" : "MindMapExpandThisNode")
            };

            var foldIcon = new AppIcon
            {
                Kind = node.IsExpanded ? AppIconKind.Minus : AppIconKind.Plus,
                Width = 12,
                Height = 12,
                StrokeThickness = 2,
                Foreground = GetThemeBrush("TextColorSecondary", Color.FromRgb(92, 92, 92))
            };

            foldToggle.Child = foldIcon;
            foldToggle.MouseLeftButtonDown += (_, e) =>
            {
                ToggleNodeExpansion(node);
                e.Handled = true;
            };

            mainPanel.Children.Add(foldToggle);

            if (!node.IsExpanded && !node.LinkedNoteId.HasValue)
            {
                // Bottom-right is the one badge slot left free - purely informational (not a
                // click target itself, the fold toggle above already covers that) so the user
                // can tell a collapsed branch is hiding content without expanding it first.
                // Yields the slot to the link badge below when both would want it - a link is
                // something the user deliberately set, not just passive information.
                var hiddenCount = EnumerateAllNodes(node).Count() - 1;
                var foldHintBadge = new Border
                {
                    Background = GetThemeBrush("IconButtonBackground", Color.FromRgb(245, 247, 251)),
                    BorderBrush = GetThemeBrush("MindMapNodeBorderBrush", Color.FromRgb(194, 203, 220)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(6, 2, 6, 2),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, -8, -8),
                    IsHitTestVisible = false,
                    ToolTip = string.Format(LocalizationService.GetString("MindMapHiddenNodesCountFormat"), hiddenCount)
                };

                var foldHintText = new TextBlock
                {
                    Text = hiddenCount.ToString(CultureInfo.InvariantCulture),
                    FontSize = TypeScale.Px(TypeScale.Body, 11),
                    Foreground = GetThemeBrush("TextColorSecondary", Color.FromRgb(92, 92, 92)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                foldHintBadge.Child = foldHintText;
                mainPanel.Children.Add(foldHintBadge);
            }
        }

        if (node.LinkedNoteId.HasValue)
        {
            // Bottom-right corner, same slot the collapsed hidden-count hint uses (see above) -
            // a link wins that slot when both apply. Click reuses the exact same "given a note
            // Guid, open it" plumbing the document-level linked-note button already has.
            var linkBadge = new Border
            {
                Background = FrozenBrush(Color.FromRgb(52, 211, 153)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(5, 3, 5, 3),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -8, -8),
                Cursor = Cursors.Hand,
                ToolTip = LocalizationService.GetString("MindMapOpenLinkedNote")
            };

            var linkGlyph = new TextBlock
            {
                Text = "\U0001F517",
                FontSize = TypeScale.Px(TypeScale.Body, 11),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            linkBadge.Child = linkGlyph;
            linkBadge.MouseLeftButtonDown += (_, e) =>
            {
                OpenNodeLink(node);
                e.Handled = true;
            };

            mainPanel.Children.Add(linkBadge);
        }

        border.Child = mainPanel;

            border.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2)
                {
                    EditNodeText(node);
                    e.Handled = true;
                    return;
                }

                // Manual mode: any node (including root) can be dragged to reposition it, as
                // before. Auto-layout mode: dragging is new here and exists only to reparent by
                // dropping onto another node - root is excluded since it has no parent to move
                // out from under.
                if (_isManualPositioningMode || !ReferenceEquals(node, _root))
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

                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
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
        border.ContextMenuOpening += (_, _) =>
        {
            EnsureNodeSelected(node);
        };

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

        // _clipboardNode/_copiedStyle are static and can change between when this menu was built
        // (during the last RebuildMap) and when it's actually opened - refresh enabled state right
        // before showing rather than relying on stale values captured at construction time. The OS
        // clipboard can change from outside the app entirely, so it needs the same live check.
        border.ContextMenu.Opened += (_, _) =>
        {
            pasteMenuItem.IsEnabled = _clipboardNode is not null;
            pasteStyleMenuItem.IsEnabled = _copiedStyle is not null;
            pasteAsOutlineMenuItem.IsEnabled = TryGetClipboardText(out _);
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
        }

        border.ContextMenu.Items.Add(new Separator());

        var deleteMenuItem = new MenuItem
        {
            Header = LocalizationService.GetString("Delete"),
            Tag = node
        };
        deleteMenuItem.Click += (s, e) => DeleteNodeWithConfirmation(node);
        border.ContextMenu.Items.Add(deleteMenuItem);

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
        RebuildMap();
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
        RebuildMap();
    }

    private void SelectSingleNode(MindMapNode node)
    {
        _selectedNodes.Clear();
        _selectedNodes.Add(node);
        _selectedNode = node;

        // Keyboard shortcuts (OnKeyDown) only fire while focus is on the canvas itself - selecting
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
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled)
            return;

        var focused = Keyboard.FocusedElement;
        var isCanvasFocused = focused is null
            || ReferenceEquals(focused, this)
            || ReferenceEquals(focused, MapScrollViewer)
            || ReferenceEquals(focused, MapCanvas);

        if (!isCanvasFocused)
            return;

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

        if (_selectedNode is null)
            return;

        switch (e.Key)
        {
            case Key.Delete:
                DeleteNodeWithConfirmation(_selectedNode);
                e.Handled = true;
                break;
            case Key.F2:
                EditNodeText(_selectedNode);
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
        if (_selectedNodes.Add(node))
            _selectedNode = node;
        else
            _selectedNode = node;
    }

    private void RemoveSelectedNode(MindMapNode node)
    {
        if (!_selectedNodes.Remove(node))
            return;

        if (ReferenceEquals(_selectedNode, node))
            _selectedNode = _selectedNodes.FirstOrDefault();
    }

    private void ClearSelectedNodes()
    {
        _selectedNodes.Clear();
        _selectedNode = null;
    }

    private bool HasSelectedNodes() => _selectedNodes.Count > 0;

    private IReadOnlyList<MindMapNode> GetSelectedExportRoots()
    {
        if (_selectedNodes.Count == 0)
            return Array.Empty<MindMapNode>();

        if (_selectedNodes.Count == 1)
            return _selectedNodes.ToList();

        var selected = _selectedNodes.ToList();
        return selected
            .Where(node => !selected.Any(other => !ReferenceEquals(node, other) && IsAncestorNode(other, node)))
            .ToList();
    }

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

        foreach (var node in selectedRoots)
            exportRoot.Children.Add(CloneNode(node));

        return exportRoot;
    }

    private static CornerRadius GetCornerRadius(string? nodeShape, bool isRoot)
    {
        return nodeShape switch
        {
            "Circle" => new CornerRadius(95), // Approximate circle for 190x52 node
            "Ellipse" => new CornerRadius(26),
            "Rounded" => new CornerRadius(isRoot ? 14 : 10),
            _ => new CornerRadius(0) // Rectangle
        };
    }

    private Brush GetNodeBackground(MindMapNode node, int depth)
    {
        if (!string.IsNullOrWhiteSpace(node.BackgroundColor))
        {
            return FrozenBrush((Color)ColorConverter.ConvertFromString(node.BackgroundColor));
        }

        return GetDefaultNodeBackground(depth);
    }

    private Brush GetDefaultNodeBackground(int depth)
    {
        var palette = IsDarkTheme() ? DarkNodeBackgrounds : LightNodeBackgrounds;

        if (depth <= 0)
            return palette[0];

        return palette[((depth - 1) % (palette.Length - 1)) + 1];
    }

    private void StyleNode(MindMapNode node)
    {
        var dialog = new StyleNodeDialog { Owner = this };
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
        var dialog = new SimpleInputDialog(
            LocalizationService.GetString("MindMapAddChild"),
            LocalizationService.GetString("MindMapAddChildPrompt"),
            string.Empty)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
        {
            PushUndoSnapshot();
            var newNode = new MindMapNode
            {
                Text = NormalizeNodeText(dialog.InputText)
            };

            parentNode.Children.Add(newNode);
            parentNode.IsExpanded = true;
            SelectSingleNode(newNode);
            RebuildMap();
        }
    }

    private void AddSiblingNode(MindMapNode node)
    {
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
            Owner = this
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
        {
            PushUndoSnapshot();
            var newNode = new MindMapNode
            {
                Text = NormalizeNodeText(dialog.InputText)
            };

            parent.Children.Add(newNode);
            parent.IsExpanded = true;
            SelectSingleNode(newNode);
            RebuildMap();
        }
    }


    private void EditNodeText(MindMapNode node)
    {
        var dialog = new SimpleInputDialog(
            LocalizationService.GetString("Edit"),
            LocalizationService.GetString("MindMapEditNodePrompt"),
            node.Text)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
        {
            PushUndoSnapshot();
            node.Text = NormalizeNodeText(dialog.InputText);
            RebuildMap();
        }
    }

    private void DeleteNodeWithConfirmation(MindMapNode node)
    {
        if (ReferenceEquals(node, _root))
        {
            ModernMessageBox.Show(
                LocalizationService.GetString("MindMapCannotDeleteRoot"),
                LocalizationService.GetString("Delete"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var nodeName = string.IsNullOrWhiteSpace(node.Text)
            ? LocalizationService.GetString("MindMapNodeDefaultName")
            : node.Text.Trim();

        var confirmed = ModernDialog.ConfirmDanger(
            this,
            LocalizationService.GetString("MindMapDeleteNodeTitle"),
            string.Format(
                CultureInfo.CurrentCulture,
                LocalizationService.GetString("MindMapDeleteNodeConfirmationFormat"),
                nodeName),
            LocalizationService.GetString("Confirm"));

        if (!confirmed)
            return;

        // Find parent and reparent children before deleting
        var parent = FindParentNode(_root, node);
        if (parent is null)
            return;

        PushUndoSnapshot();

        // Move all children of the deleted node to the parent
        foreach (var child in node.Children.ToList())
            parent.Children.Add(child);

        // Now remove the selected node
        parent.Children.Remove(node);

        RemoveSelectedNode(node);

        RebuildMap();
    }

    private void CutNode(MindMapNode node)
    {
        if (ReferenceEquals(node, _root))
        {
            ModernMessageBox.Show(
                LocalizationService.GetString("MindMapCannotCutRoot"),
                LocalizationService.GetString("Cut"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var parent = FindParentNode(_root, node);
        if (parent is null)
            return;

        // No confirmation, unlike delete - cut is recoverable via paste or undo.
        _clipboardNode = CloneNode(node);

        PushUndoSnapshot();
        parent.Children.Remove(node);
        RemoveSelectedNode(node);
        RebuildMap();
    }

    private void CopyNode(MindMapNode node)
        => _clipboardNode = CloneNode(node);

    private void PasteNodeAsChild(MindMapNode targetNode)
    {
        if (_clipboardNode is null)
            return;

        PushUndoSnapshot();

        // Clone again rather than reusing _clipboardNode directly, so repeated pastes each get
        // their own independent node objects instead of sharing references.
        var pasted = CloneNode(_clipboardNode);
        targetNode.Children.Add(pasted);
        targetNode.IsExpanded = true;
        SelectSingleNode(pasted);
        RebuildMap();
    }

    // Reads plain text from the OS clipboard (not the app's own node clipboard _clipboardNode
    // above - a deliberately separate action/shortcut so the two don't get confused) and parses
    // it as an indented outline, adding the resulting nodes as children of targetNode. Lets you
    // paste a bulleted list or a plain tab-indented outline copied from anywhere and get a real
    // branch out of it instead of one node containing the whole blob of text.
    private void PasteAsOutline(MindMapNode targetNode)
    {
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
        node.BackgroundColor = _copiedStyle.BackgroundColor;
        node.BorderColor = _copiedStyle.BorderColor;
        node.BorderThickness = _copiedStyle.BorderThickness;
        node.NodeShape = _copiedStyle.NodeShape;
        node.Icons = new List<string>(_copiedStyle.Icons);
        node.IconBadgeColor = _copiedStyle.IconBadgeColor;
        node.IsBold = _copiedStyle.IsBold;
        node.IsItalic = _copiedStyle.IsItalic;
        node.TextColor = _copiedStyle.TextColor;
        node.FontFamily = _copiedStyle.FontFamily;
        node.FontSize = _copiedStyle.FontSize;
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

    private Brush GetNodeBackground(int depth)
    {
        var palette = IsDarkTheme() ? DarkNodeBackgrounds : LightNodeBackgrounds;

        if (depth <= 0)
            return palette[0];

        return palette[((depth - 1) % (palette.Length - 1)) + 1];
    }

    private void ExpandAllButton_Click(object sender, RoutedEventArgs e)
    {
        SetExpanded(_root, true);
        RebuildMap(animate: false);
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
        RebuildMap(animate: false);
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
        RebuildMap(animate: false);
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

        RebuildMap(animate: false);
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

        _arrangePanelAnimationVersion++;
        AnimateChromePanel(ArrangePanel, isOpen, _arrangePanelAnimationVersion, () => _isArrangePanelOpen);
    }

    private void SetSearchPanelOpen(bool isOpen)
    {
        _isSearchPanelOpen = isOpen;
        if (isOpen)
        {
            if (_isArrangePanelOpen) SetArrangePanelOpen(false);
            if (_isFontPanelOpen) SetFontPanelOpen(false);
        }

        _searchPanelAnimationVersion++;
        AnimateChromePanel(SearchPanel, isOpen, _searchPanelAnimationVersion, () => _isSearchPanelOpen);
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

        _fontPanelAnimationVersion++;
        AnimateChromePanel(FontPanel, isOpen, _fontPanelAnimationVersion, () => _isFontPanelOpen);
        if (isOpen)
            RefreshFontPanelFromSelection();
    }

    /// <summary>
    /// Fade + slide reveal/hide for a collapsible chrome panel - same mechanism as
    /// NoteEditorWindow's FontPanel (AnimateFontPanel), generalized for reuse by both
    /// ArrangePanel and SearchPanel. <paramref name="isStillOpen"/> re-checks the panel's current
    /// desired state when the hide animation finishes, so a rapid open-then-close-then-open
    /// doesn't leave the panel wrongly collapsed.
    /// </summary>
    private void AnimateChromePanel(Border panel, bool show, int animationVersion, Func<bool> isStillOpen)
    {
        if (UiMotion.PrefersReducedMotion)
        {
            UiMotion.Stop(panel);
            panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            panel.Opacity = show ? 1 : 0;
            EnsureChromePanelTransform(panel).Y = show ? 0 : -6;
            return;
        }

        var transform = EnsureChromePanelTransform(panel);
        var easing = show ? UiMotion.OutQuad : UiMotion.InQuad;
        var opacityFrom = panel.Opacity;
        var yFrom = transform.Y;

        if (show)
            panel.Visibility = Visibility.Visible;

        UiMotion.Stop(panel);
        UiMotion.RunComposited(
            panel,
            new[]
            {
                UiMotion.Track(UiMotion.FastMs, easing, opacityFrom, show ? 1 : 0, value => panel.Opacity = value),
                UiMotion.Track(UiMotion.FastMs, easing, yFrom, show ? 0 : -6, value => transform.Y = value)
            },
            () =>
            {
                if (!show && animationVersion == GetLatestAnimationVersion(panel) && !isStillOpen())
                    panel.Visibility = Visibility.Collapsed;
            });
    }

    private int GetLatestAnimationVersion(Border panel)
        => ReferenceEquals(panel, ArrangePanel) ? _arrangePanelAnimationVersion
            : ReferenceEquals(panel, SearchPanel) ? _searchPanelAnimationVersion
            : _fontPanelAnimationVersion;

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
    /// by hand. Font editing only makes sense for exactly one selected node at a time (unlike
    /// e.g. delete), so anything else disables the panel rather than guessing.
    /// </summary>
    private void RefreshFontPanelFromSelection()
    {
        if (!_isFontPanelOpen)
            return;

        var node = _selectedNodes.Count == 1 ? _selectedNode : null;

        FontFamilyBox.IsEnabled = node is not null;
        FontSizeBox.IsEnabled = node is not null;
        FontBoldToggleButton.IsEnabled = node is not null;
        FontItalicToggleButton.IsEnabled = node is not null;
        FontColorButton.IsEnabled = node is not null;
        FontPanelHintText.Visibility = node is null ? Visibility.Visible : Visibility.Collapsed;

        _suppressFontPanelChangeHandlers = true;
        try
        {
            FontFamilyBox.SelectedValue = node?.FontFamily ?? string.Empty;
            FontSizeBox.SelectedValue = node?.FontSize is double size
                ? ((int)size).ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            FontBoldToggleButton.IsChecked = node?.IsBold ?? false;
            FontItalicToggleButton.IsChecked = node?.IsItalic ?? false;
            FontColorButton.Background = !string.IsNullOrWhiteSpace(node?.TextColor)
                ? FrozenBrush((Color)ColorConverter.ConvertFromString(node!.TextColor))
                : Brushes.Transparent;
        }
        finally
        {
            _suppressFontPanelChangeHandlers = false;
        }
    }

    private void ApplyFontPanelChange(Action<MindMapNode> mutate)
    {
        if (_suppressFontPanelChangeHandlers || _selectedNode is null || _selectedNodes.Count != 1)
            return;

        PushUndoSnapshot();
        mutate(_selectedNode);
        RebuildMap(animate: false);
    }

    private void FontFamilyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var value = FontFamilyBox.SelectedValue as string;
        ApplyFontPanelChange(node => node.FontFamily = string.IsNullOrEmpty(value) ? null : value);
    }

    private void FontSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var value = FontSizeBox.SelectedValue as string;
        ApplyFontPanelChange(node => node.FontSize = string.IsNullOrEmpty(value)
            ? null
            : double.Parse(value, CultureInfo.InvariantCulture));
    }

    private void FontBoldToggleButton_Click(object sender, RoutedEventArgs e)
        => ApplyFontPanelChange(node => node.IsBold = FontBoldToggleButton.IsChecked == true);

    private void FontItalicToggleButton_Click(object sender, RoutedEventArgs e)
        => ApplyFontPanelChange(node => node.IsItalic = FontItalicToggleButton.IsChecked == true);

    private void FontColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is null || _selectedNodes.Count != 1)
            return;

        var dialog = new ColorPickerDialog { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedColor is not { } color)
            return;

        // No need to set FontColorButton.Background directly here - ApplyFontPanelChange's
        // RebuildMap(animate: false) runs RefreshFontPanelFromSelection synchronously, which
        // re-reads the swatch color from the node it just mutated.
        ApplyFontPanelChange(node => node.TextColor = color.ToString());
    }

    private void FontColorClearButton_Click(object sender, RoutedEventArgs e)
        => ApplyFontPanelChange(node => node.TextColor = null);

    private static TranslateTransform EnsureChromePanelTransform(Border panel)
    {
        if (panel.RenderTransform is TranslateTransform existing)
            return existing;

        var transform = new TranslateTransform(0, -6);
        panel.RenderTransform = transform;
        return transform;
    }

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

        if (saveDialog.ShowDialog(this) != true)
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
        ModernDialog.ShowInfo(this, title, message);
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
        switch (GetCloseDecision())
        {
            case UnsavedCloseDecision.Cancel:
                return;
            case UnsavedCloseDecision.SaveAndClose:
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
        if (isRoot && string.IsNullOrWhiteSpace(node.BackgroundColor))
            return Brushes.White;

        if (nodeBackground is SolidColorBrush solid)
            return IsLightColor(solid.Color) ? Brushes.Black : Brushes.White;

        return GetThemeBrush("TextColor", Color.FromRgb(31, 41, 55));
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
}
