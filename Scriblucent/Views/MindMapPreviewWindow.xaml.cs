using Scriblucent.Animations;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent;
using Scriblucent.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using ShapePath = System.Windows.Shapes.Path;

namespace Scriblucent.Views;

public partial class MindMapPreviewWindow : Window, INotifyPropertyChanged
{
    private enum UnsavedCloseDecision
    {
        Cancel,
        LeaveWithoutSaving,
        SaveAndClose
    }

    private const double NodeWidth = 190;
    private const double HorizontalGap = 150;
    private const double VerticalGap = 18;
    private const double VerticalLayoutLevelGap = 118;
    private const double VerticalLayoutNodeGap = 34;
    private const double CanvasPadding = 52;
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
    private bool _connectionRedrawQueued;
    private bool _mapViewportRefreshQueued;
    private MindMapNode? _draggedNode;
    private MindMapNode? _pendingDragNode;
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
            node.Icon ?? string.Empty,
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

        if (_isManualPositioningMode)
            ApplyManualPositionsToLayouts();

        NormalizeLayoutOffset();

        if (_isManualPositioningMode && !_suppressManualPositionCapture)
            CaptureVisibleLayoutAsManualPositions();

        UpdateMapCanvasSize();

        DrawConnections(_root);
        DrawNodes(_root);

        if (CurrentSearchMatch is not null)
            CenterViewOnCurrentSearchMatch();

        QueueInitialCenterOnRoot();
        UpdateEditedIndicator();
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
        RefreshSearchMatches(resetIndex: true);
        RebuildMap();
    }

    private void SearchPreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (_searchMatches.Count == 0)
            return;

        _currentSearchMatchIndex = (_currentSearchMatchIndex - 1 + _searchMatches.Count) % _searchMatches.Count;
        UpdateSearchResultsText();
        CenterViewOnCurrentSearchMatch();
        RebuildMap();
    }

    private void SearchNextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_searchMatches.Count == 0)
            return;

        _currentSearchMatchIndex = (_currentSearchMatchIndex + 1) % _searchMatches.Count;
        UpdateSearchResultsText();
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
            foreach (var node in EnumerateVisibleNodes(_root))
            {
                if (IsSearchMatch(node, tokens))
                    _searchMatches.Add(node);
            }
        }

        if (_searchMatches.Count == 0)
        {
            _currentSearchMatchIndex = -1;
            UpdateSearchResultsText();
            return;
        }

        if (resetIndex)
        {
            _currentSearchMatchIndex = 0;
            UpdateSearchResultsText();
            return;
        }

        if (previousCurrent is not null)
        {
            var existingIndex = _searchMatches.IndexOf(previousCurrent);
            if (existingIndex >= 0)
            {
                _currentSearchMatchIndex = existingIndex;
                UpdateSearchResultsText();
                return;
            }
        }

        _currentSearchMatchIndex = Math.Clamp(_currentSearchMatchIndex, 0, _searchMatches.Count - 1);
        UpdateSearchResultsText();
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
        return tokens.All(token => text.Contains(token, StringComparison.CurrentCultureIgnoreCase));
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

    private static double MeasureNodeHeight(MindMapNode node)
    {
        var lineCount = Math.Max(1, (int)Math.Ceiling(node.Text.Length / 24.0));
        return Math.Clamp(42 + (lineCount - 1) * 17, 52, 118);
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

        DrawConnections(_root);
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

        SetManualPosition(_draggedNode, new Point(newX, newY));
        _layouts[_draggedNode] = layout with { X = newX, Y = newY };
        Canvas.SetLeft(_draggedBorder, newX);
        Canvas.SetTop(_draggedBorder, newY);
        QueueConnectionRedraw();
    }

    private static void ApplyNodeDragChrome(Border border, bool isDragging)
    {
        if (isDragging)
        {
            border.Cursor = Cursors.SizeAll;
            border.Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                Opacity = 0.28,
                BlurRadius = 18,
                ShadowDepth = 4
            };
            return;
        }

        border.Cursor = Cursors.Hand;
        border.Effect = null;
    }

    private void CancelManualDragPending()
    {
        _isManualDragPending = false;
        _pendingDragNode = null;
        _pendingDragBorder = null;
    }

    private void MapCanvas_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isManualPositioningMode || _isDraggingNode)
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
            FinalizeManualNodeDrag(_draggedNode, _draggedBorder);
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

        FinalizeManualNodeDrag(_draggedNode, _draggedBorder);
        CancelManualDragPending();
    }

    private void ApplyNodeBorderChrome(Border border, MindMapNode node)
    {
        var isRoot = ReferenceEquals(node, _root);
        var defaultThickness = node.BorderThickness > 0 ? node.BorderThickness : 1;
        border.BorderThickness = new Thickness(defaultThickness);
        border.BorderBrush = !string.IsNullOrWhiteSpace(node.BorderColor)
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(node.BorderColor))
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
    }

    private void FinalizeManualNodeDrag(MindMapNode node, Border border)
    {
        _isDraggingNode = false;
        _draggedNode = null;
        _draggedBorder = null;

        if (MapCanvas.IsMouseCaptured)
            MapCanvas.ReleaseMouseCapture();

        Panel.SetZIndex(border, NodeZIndex);
        ApplyNodeDragChrome(border, isDragging: false);
        SelectSingleNode(node);
        ApplyNodeBorderChrome(border, node);
        RedrawConnections();
        UpdateMapCanvasSize();
        UpdateEditedIndicator();
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
            Text = node.HasChildren ? $"{node.Text} {(node.IsExpanded ? "−" : "+")}" : node.Text,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontWeight = isRoot ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = GetNodeForeground(nodeBackground, isRoot, node),
            Margin = !string.IsNullOrWhiteSpace(node.Icon) ? new Thickness(0, 0, 28, 0) : new Thickness(0)
        };
        mainPanel.Children.Add(text);

        if (!string.IsNullOrWhiteSpace(node.Icon))
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

            var iconText = new TextBlock
            {
                Text = node.Icon,
                FontSize = TypeScale.Px(TypeScale.Body, 14),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            iconBadge.Child = iconText;
            mainPanel.Children.Add(iconBadge);
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

                if (_isManualPositioningMode)
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
                Header = LocalizationService.GetString(node.IsExpanded ? "MindMapCollapseNode" : "MindMapExpandNode"),
                Tag = node
            };
            toggleExpansionMenuItem.Click += (s, e) => ToggleNodeExpansion(node);
            border.ContextMenu.Items.Add(toggleExpansionMenuItem);
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

    private void SelectSingleNode(MindMapNode node)
    {
        _selectedNodes.Clear();
        _selectedNodes.Add(node);
        _selectedNode = node;
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
        dialog.LoadFromNode(node);

        if (dialog.ShowDialog() == true)
        {
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

        // Move all children of the deleted node to the parent
        foreach (var child in node.Children.ToList())
            parent.Children.Add(child);

        // Now remove the selected node
        parent.Children.Remove(node);

        RemoveSelectedNode(node);

        RebuildMap();
    }

    private void AddNodeButton_Click(object sender, RoutedEventArgs e)
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
        RebuildMap();
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

        RebuildMap();
        Dispatcher.BeginInvoke(new Action(CenterViewOnRoot), DispatcherPriority.Loaded);
    }
    private void ManualPositioningToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressManualPositioningToggleChanged)
            return;

        _isManualPositioningMode = true;
        CaptureVisibleLayoutAsManualPositions();
        UpdateManualPositioningVisualState();
        RebuildMap();
    }

    private void ManualPositioningToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressManualPositioningToggleChanged)
            return;

        _isManualPositioningMode = false;
        _manualPositions.Clear();
        ClearManualPositions(_root);
        UpdateManualPositioningVisualState();
        RebuildMap();
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
                    ExportToPng(saveDialog.FileName, exportRoot, exportSelectedBranchOnly, selectedRoots);
                    break;
                case ".mm":
                    ExportToMindMapFile(saveDialog.FileName, exportRoot, exportSelectedBranchOnly);
                    break;
                case ".mmap":
                    ExportToMindMapFile(saveDialog.FileName, exportRoot, exportSelectedBranchOnly);
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

    private void ExportToMindMapFile(string path, MindMapNode exportRoot, bool isBranch)
    {
        var sourceDocument = ToDocument();
        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("map",
                new XAttribute("version", "1.0.1"),
                CreateFreeMindNode(exportRoot, isRoot: true, sourceDocument.Title, isBranch)));

        document.Save(path);
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var openDialog = new OpenFileDialog
        {
            Title = LocalizationService.GetString("MindMapImportDialogTitle"),
            Filter = LocalizationService.GetString("MindMapImportDialogFilter")
        };

        if (openDialog.ShowDialog(this) != true)
            return;

        try
        {
            var imported = ImportMindMapFile(openDialog.FileName);
            if (imported is null)
                throw new InvalidOperationException(LocalizationService.GetString("MindMapImportFailed"));

            _root.Text = imported.Text ?? string.Empty;
            _root.Children.Clear();
            foreach (var child in imported.Children)
                _root.Children.Add(CloneNode(child));

            ClearSelectedNodes();
            RebuildMap();

            ShowExportDialog(
                LocalizationService.GetString("Success"),
                LocalizationService.GetString("MindMapImportSuccess"));
        }
        catch (Exception ex)
        {
            ShowExportDialog(
                LocalizationService.GetString("ImportError"),
                ex.Message);
        }
    }

    private MindMapNode? ImportMindMapFile(string path)
    {
        if (!System.IO.File.Exists(path))
            throw new FileNotFoundException(LocalizationService.GetString("MindMapImportFileNotFound"), path);

        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext != ".mm" && ext != ".mmap")
            throw new InvalidOperationException(LocalizationService.GetString("MindMapImportUnsupportedFormat"));

        XDocument doc;
        try
        {
            doc = XDocument.Load(path);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(LocalizationService.GetString("MindMapImportReadFailed"), ex);
        }

        var map = doc.Root;
        if (map == null || !string.Equals(map.Name.LocalName, "map", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(LocalizationService.GetString("MindMapImportInvalidRoot"));

        var firstNode = map.Elements("node").FirstOrDefault();
        if (firstNode == null)
            return null;

        return ParseFreeMindNode(firstNode);
    }

    private MindMapNode ParseFreeMindNode(XElement element)
    {
        var node = new MindMapNode();
        var textAttr = element.Attribute("TEXT")?.Value;
        node.Text = string.IsNullOrWhiteSpace(textAttr) ? string.Empty : textAttr.Trim();
        var bg = element.Attribute("BACKGROUND_COLOR")?.Value;
        if (!string.IsNullOrWhiteSpace(bg)) node.BackgroundColor = bg;
        var color = element.Attribute("COLOR")?.Value;
        if (!string.IsNullOrWhiteSpace(color)) node.BorderColor = color;

        var rich = element.Elements("richcontent").FirstOrDefault(rc => string.Equals(rc.Attribute("TYPE")?.Value, "NOTE", StringComparison.OrdinalIgnoreCase));
        if (rich != null)
        {
            var body = rich.Descendants("body").FirstOrDefault();
            if (body != null)
            {
                var content = body.Value;
                if (!string.IsNullOrWhiteSpace(content))
                    node.Icon = content.Trim();
            }
        }

        foreach (var childEl in element.Elements("node"))
        {
            var childNode = ParseFreeMindNode(childEl);
            node.Children.Add(childNode);
        }

        return node;
    }

    private static XElement CreateFreeMindNode(MindMapNode node, bool isRoot, string documentTitle, bool isBranch)
    {
        var text = node.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            text = isRoot
                ? (isBranch ? $"{documentTitle} (branch)" : documentTitle)
                : "Node";
        }

        var element = new XElement("node", new XAttribute("TEXT", text));

        if (!string.IsNullOrWhiteSpace(node.BackgroundColor))
            element.SetAttributeValue("BACKGROUND_COLOR", node.BackgroundColor);

        if (!string.IsNullOrWhiteSpace(node.BorderColor))
            element.SetAttributeValue("COLOR", node.BorderColor);

        if (!string.IsNullOrWhiteSpace(node.Icon))
            element.Add(new XElement("richcontent",
                new XAttribute("TYPE", "NOTE"),
                new XElement("html",
                    new XElement("body", node.Icon))));

        foreach (var child in node.Children)
            element.Add(CreateFreeMindNode(child, isRoot: false, documentTitle, isBranch));

        return element;
    }

    private MindMapNode CloneNode(MindMapNode source)
    {
        var copy = new MindMapNode
        {
            Text = source.Text,
            IsExpanded = source.IsExpanded,
            BackgroundColor = source.BackgroundColor,
            BorderColor = source.BorderColor,
            BorderThickness = source.BorderThickness,
            NodeShape = source.NodeShape,
            Icon = source.Icon,
            IconBadgeColor = source.IconBadgeColor,
            ManualX = source.ManualX,
            ManualY = source.ManualY
        };

        foreach (var child in source.Children)
            copy.Children.Add(CloneNode(child));

        return copy;
    }

    private void ExportToPng(string path, MindMapNode exportRoot, bool isBranch, IReadOnlyList<MindMapNode> selectedRoots)
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

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(renderTarget));
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

        return new SolidColorBrush(fallbackColor);
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
