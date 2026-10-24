using Microsoft.Win32;
using Scriblucent.Animations;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.Services.AI;
using Scriblucent.ViewModels;
using Scriblucent.Views;
using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using WeCantSpell.Hunspell;

namespace Scriblucent
{
    public partial class NoteEditorWindow : Window, Scriblucent.Services.IHostedDocumentEditor
    {
        private bool _isHostedInTab;
        // Last search/replace state for Find Next / Replace functionality
        private string? _lastSearchQuery = null;
        private string? _lastReplacementText = null;
        private bool _lastFindMatchCase;
        private bool _lastFindWholeWord;
        private bool _lastFindWrapAround = true;
        private bool _hasFindHighlights;
        private bool _findBarOpen;
        private DispatcherTimer? _findHighlightDebounceTimer;
        private int _findDocumentVersion;
        private int _findCachedVersion = -1;
        private string? _findCachedQuery;
        private bool _findCachedMatchCase;
        private bool _findCachedWholeWord;
        private DocumentTextIndex _findCachedIndex;
        private List<FindMatch>? _findCachedMatches;
        private MatchSpan[] _findCachedSpans = Array.Empty<MatchSpan>();
        private int _findMutationDepth;
        private bool _findRefreshPending;
        private MatchSpan[] _findOverlaySpans = Array.Empty<MatchSpan>();
        private int _findOverlayActiveIndex = -1;
        private DocumentTextIndex _findOverlayIndex;
        private bool _findOverlayIndexReady;
        private readonly TranslateTransform _findOverlayCommit = new();
        private readonly TransformGroup _findOverlayMotion = new();
        private readonly TranslateTransform _selectionCommit = new();
        private readonly TransformGroup _selectionMotion = new();
        private bool _selectionOverlayQueued;
        private DispatcherOperation? _selectionOverlayOperation;
        private DispatcherTimer? _selectionUiTimer;
        private bool _suppressSelectionChange;
        private bool _freezeSelectionScroll;
        private bool _selectionCollapsedForScroll;
        private TextPointer? _heldSelectionStart;
        private TextPointer? _heldSelectionEnd;
        private bool _heldCaretAtStart;
        private Brush? _cachedSelectionOverlayBrush;
        private bool _findOverlayQueued;
        private const double HighlightTileHeight = 256;
        private const double HighlightBuildBudgetMs = 2;
        private readonly Dictionary<int, HighlightTile> _selectionTiles = new();
        private readonly Dictionary<int, HighlightTile> _findTiles = new();
        private readonly Dictionary<int, HighlightTile> _findActiveTiles = new();
        private int _findComposedFirstTile = -1;
        private int _findComposedLastTile = -1;
        private MatchSpan[] _findActiveSpan = Array.Empty<MatchSpan>();
        private double _findActiveTop = double.NaN;
        private double _findActiveBottom = double.NaN;
        private DispatcherOperation? _findOverlayOperation;
        private TextPointer? _selectionTileStart;
        private TextPointer? _selectionTileEnd;
        private readonly SortedDictionary<TextPointer, BlueLineMeasure> _selectionLineRects =
            new(Comparer<TextPointer>.Create((left, right) => left.CompareTo(right)));
        private System.Windows.Shapes.Path? _selectionHighlightPath;
        private System.Windows.Shapes.Path? _findNormalHighlightPath;
        private System.Windows.Shapes.Path? _findActiveHighlightPath;

        private sealed class HighlightTile
        {
            public required int Index;
            public required double Left;
            public required double Width;
            public required GeometryGroup NormalGeometry;
            public readonly List<Rect> NormalRects = new();
            public readonly List<Geometry> SelectionGeometry = new();
            public TextPointer? NextLine;
            public FindLineRange? CurrentFindRange;
            public FindDrawCursor? PendingFindDraw;
            public bool Initialized;
            public bool Complete;
            public bool ValidatedInViewport;
            public double ValidatedTop;
            public double ValidatedBottom;
            public HighlightTile? PendingReplacement;
            public bool NeedsSelectionRebuild;
            public TextPointer? CoveredStart;
            public TextPointer? CoveredEnd;
        }

        private readonly record struct BlueLineMeasure(Geometry DocumentGeometry, bool ValidatedInViewport);

        private sealed class FindLineRange
        {
            public required int Start;
            public required int End;
            public required int NextMatch;
            public required int EndMatch;
        }

        private sealed class FindDrawCursor
        {
            public required int StartOffset;
            public required int EndOffset;
            public required int RunIndex;
            public TextPointer? Position;
            public TextPointer? RunEnd;
        }

        // Spell check - see SpellCheckDictionaryService/SpellCheckService for the Hunspell-backed
        // engine this drives. Squiggles are real in-document TextDecorations
        // (TextDecorationLocation.Baseline, unused by any other feature in this editor) applied
        // per misspelled-word TextRange and tracked here so they can be cleared precisely
        // (including before every save/export) without disturbing Underline/Strikethrough.
        // Find marks are drawn over the viewport and are not part of the document.
        private bool _isSpellCheckEnabled;
        private DispatcherTimer? _spellCheckDebounceTimer;
        private readonly List<TextRange> _spellCheckDecoratedRanges = new();
        private IReadOnlyList<SpellCheckService.WordSpan>? _lastSpellCheckAppliedSpans;
        private long _lastSpellCheckAppliedVersion = -1;
        private long _spellCheckRequestVersion;
        private bool _isSpellCheckScanning;
        private bool _isApplyingSpellCheckDecorations;
        private ContextMenu? _defaultContentTextBoxContextMenu;
        // "Ignore" (Word-style) is session-only, unlike "Add to Dictionary" which persists to
        // AppSettings.SpellCheckCustomWords - resets the next time this note is opened.
        private readonly HashSet<string> _sessionIgnoredWords = new(StringComparer.OrdinalIgnoreCase);

        // Auto-save fields
        public event Action<NoteDocument>? DocumentAutoSaved;
        public event Action<NoteEditorWindow>? CloseRequested;
        public static event EventHandler? AiGenerationStateChanged;
        private static int _activeAiGenerationCount;
        private System.Threading.Timer? _autoSaveTimer;
        private DispatcherTimer? _counterUpdateDebounceTimer;
        private bool _isAutoSaveEnabled = true;
        private int _autoSaveIntervalMs = 30000; // 30 seconds (default)
        private DateTime _lastAutoSaveTime = DateTime.MinValue;
        private string _lastSavedContent = string.Empty;
        private string _lastSavedSnapshot = string.Empty;
        private bool _restoreBaselineHasFieldEdits;
        private NoteDocument? _currentDocument;
        private bool _aiReviewNoticeDismissedWithoutDocument;
        private readonly FloatingImageCoordinator _floatingImages = new();
        private NoteWrapLayoutEngine? _wrapLayout;
        private bool _isInsertingPicture;
        private bool _isInsertingSketch;
        private const int MaxEditHistoryEntries = 100;

        /// <summary>Draws spell-check waves with a tiled zigzag brush. Baseline keeps the
        /// decoration independent of underline and strikethrough formatting.</summary>
        private static readonly TextDecoration SpellCheckSquiggleDecoration = CreateSpellCheckSquiggleDecoration();

        private static TextDecoration CreateSpellCheckSquiggleDecoration()
        {
            const double tileWidth = 4;
            const double tileHeight = 3;

            var zigzag = new StreamGeometry();
            using (var context = zigzag.Open())
            {
                context.BeginFigure(new Point(0, tileHeight), isFilled: false, isClosed: false);
                context.LineTo(new Point(tileWidth / 2, 0), isStroked: true, isSmoothJoin: false);
                context.LineTo(new Point(tileWidth, tileHeight), isStroked: true, isSmoothJoin: false);
            }
            zigzag.Freeze();

            var strokeBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E));
            strokeBrush.Freeze();

            var strokePen = new Pen(strokeBrush, 1.1);
            strokePen.Freeze();

            var zigzagDrawing = new GeometryDrawing { Geometry = zigzag, Pen = strokePen };
            zigzagDrawing.Freeze();

            var tileBrush = new DrawingBrush(zigzagDrawing)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, tileWidth, tileHeight),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, tileWidth, tileHeight),
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None
            };
            tileBrush.Freeze();

            var decorationPen = new Pen(tileBrush, tileHeight)
            {
                StartLineCap = PenLineCap.Flat,
                EndLineCap = PenLineCap.Flat
            };
            decorationPen.Freeze();

            return new TextDecoration
            {
                Location = TextDecorationLocation.Baseline,
                Pen = decorationPen,
                PenOffset = 1.5,
                PenOffsetUnit = TextDecorationUnit.Pixel,
                PenThicknessUnit = TextDecorationUnit.Pixel
            };
        }

        // Share one serialized worker adapter across editor tabs.
        private readonly ITextGenerationBackend _aiBackend = AiBackendHost.Shared;
        private FlashcardConversionService _flashcardConversionService = null!;
        private MindMapConversionService _mindMapConversionService = null!;
        private QuizConversionService _quizConversionService = null!;
        private StudyPlanCoordinator _studyPlanCoordinator = null!;
        private bool _isDisposed;
        private bool _isConvertingToFlashcards;
        private CancellationTokenSource? _flashcardConversionCancellationSource;
        private bool _isConvertingToMindMap;
        private CancellationTokenSource? _mindMapConversionCancellationSource;
        private bool _isConvertingToTest;
        private CancellationTokenSource? _testConversionCancellationSource;
        private bool _isSyncingFontSelectors;
        private bool _isApplyingTypography;
        private bool _refreshLineSpacingAfterNextTyping;
        private bool _isApplyingHyperlinkCtrlHints;
        private bool _hyperlinkCtrlHintsVisible;
        private bool _isLoadingDocument;
        /// <summary>Suppresses the edited-marking while the async photo restore + the wrap pass
        /// materialize after a document load - their flow mutations fire TextChanged, which
        /// would otherwise mark the freshly-opened note "edited". Dedicated flag (NOT the load's
        /// <see cref="_isLoadingDocument"/>): the restore's continuation runs after the load
        /// finishes, and reusing the load's flag would leave it stuck true, silencing every
        /// legitimate edit afterwards.</summary>
        private bool _suppressEditedMarking;
        /// <summary>One-shot settle timer: keeps <see cref="_suppressEditedMarking"/> active
        /// through the post-load mutation fallout, then captures the baseline once quiet.</summary>
        private DispatcherTimer? _restoreSettleTimer;
        private bool _isSerializingEditorContent;
        private bool _allowCloseWithoutPrompt;
        private bool _isFontPanelOpen;
        private bool _isTagsPanelOpen;
        private bool _lastSavedTagsPanelOpen;
        private bool _isSyncingLineSpacingSelector;
        private bool _isSyncingIndentSizeSelector;
        private readonly Dictionary<string, ComboBoxItem> _fontFamilyItemsByName =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ComboBoxItem> _fontSizeItemsByContent =
            new(StringComparer.Ordinal);
        private string? _lastFontButtonTooltip;
        private double _lineSpacingMultiplier = 1.0;
        private double _indentSize = DefaultIndentSize;
        private string _measurementUnitSystem = AppSettings.UnitSystemImperial;
        private const string DefaultEditorFontFamily = "Calibri";
        // Symbol (not Emoji): WPF renders these as classic monochrome glyphs, which stay
        // sharp and match the UI chrome better than color emoji bitmaps.
        private const string EditorSymbolFontFallback = "Segoe UI Symbol";

        private static FontFamily CreateEditorFontFamily(string? primary)
        {
            var name = string.IsNullOrWhiteSpace(primary) ? DefaultEditorFontFamily : primary.Trim();
            if (name.Contains("Segoe UI Symbol", StringComparison.OrdinalIgnoreCase))
                return new FontFamily(name);

            return new FontFamily($"{GetPrimaryFontFamily(name)}, {EditorSymbolFontFallback}");
        }

        private static string GetPrimaryFontFamily(string? fontFamilySource)
        {
            if (string.IsNullOrWhiteSpace(fontFamilySource))
                return DefaultEditorFontFamily;

            var primary = fontFamilySource.Split(',')[0].Trim();
            return string.IsNullOrWhiteSpace(primary) ? DefaultEditorFontFamily : primary;
        }
        private long _editorChangeVersion;
        private long _lastSavedEditorChangeVersion;
        private const double DefaultIndentSize = 48.0;
        private const double MinimumIndentSize = 24.0;
        private const double MaximumIndentSize = 144.0;
        private const double MaximumAppliedIndent = 640.0;
        private const double DipsPerInch = 96.0;
        private const double CentimetersPerInch = 2.54;
        private const string LiteralTabText = "\t";
        private const int PlainTextTabEquivalentSpaces = 4;
        private const int MaximumInsertedIndentSpaces = 96;
        private const double PdfPageWidth = 816;
        private const double PdfPageHeight = 1056;
        private const double PdfPagePadding = 60;
        private const double PdfContentTopOffset = 74;
        private const double StatusIndicatorExpandedHeight = 24;
        private const double UnwrappedDocumentPageWidth = 10000;
        private ScrollViewer? _contentScrollViewer;
        private DispatcherTimer? _editorScrollbarHideTimer;
        private bool _syncingEditorScrollbarMirror;
        private bool _editorScrollbarsShown;
        private const int EditorScrollbarHideDelayMs = 1200;
        private INotifyPropertyChanged? _editorSettingsSource;
        private static readonly double[] WordFontSizes = [8, 9, 10, 11, 12, 14, 16, 18, 20, 22, 24, 26, 28, 36, 48, 72];
        private static readonly double[] WordIndentSizes = [24, 48, 72, 96, 120, 144];
        private static readonly string[] PreferredFontFamilyOptions =
        [
            "Calibri",
            "Aptos",
            "Segoe UI",
            "Arial",
            "Times New Roman",
            "Verdana",
            "Tahoma",
            "Georgia",
            "Trebuchet MS",
            "Cambria",
            "Garamond",
            "Consolas",
            "Courier New"
        ];

        public static bool IsAiGenerationInProgress => _activeAiGenerationCount > 0;

        public NoteEditorWindow()
        {
            InitializeComponent();
            FontButton.ToolTipOpening += FontButton_ToolTipOpening;
            var groundedQuestionPlanner = new GroundedQuestionPlanner();
            _flashcardConversionService = new FlashcardConversionService(_aiBackend, groundedQuestionPlanner);
            _mindMapConversionService = new MindMapConversionService(_aiBackend);
            _quizConversionService = new QuizConversionService(_aiBackend, groundedQuestionPlanner);
            _studyPlanCoordinator = new StudyPlanCoordinator(_aiBackend);
            // Note editors are hosted inside EditorWorkspaceWindow (never shown as their own
            // window). Do not attach ShellWindowChrome here — that would nest a second caption.
            InitializeEditorFormattingOptions();
            InitializeEditorUiPreferences();
            InitializeAutoSave();
            UpdateCounter();
            UpdateOnlineSearchAvailability();
            DataContextChanged += NoteEditorWindow_DataContextChanged;
            AttachEditorSettingsSource(DataContext as INotifyPropertyChanged);
            AiGenerationStateChanged += NoteEditorWindow_AiGenerationStateChanged;
            ApplyEditorTextLayout();
            ApplyCurrentLineSpacingToDocument();
            UpdateEditorToolButtonStates();
            // Spell check picks its dictionary from the effective Language - the editor's Content
            // gets reparented into EditorWorkspaceWindow's tree (see that window's host.Content
            // swap), so setting it on this Window instance wouldn't survive; set it directly on
            // the RichTextBox instead.
            ContentTextBox.Language = XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag);
            // The flow has its own language default. WPF's first input-language change
            // can clear pending font formatting, so initialize it from the active keyboard.
            // Saved language values on runs and spans continue to take precedence.
            ContentTextBox.Document.Language = XmlLanguage.GetLanguage(
                InputLanguageManager.Current.CurrentInputLanguage.IetfLanguageTag);
            ContentTextBox.IsDocumentEnabled = true;
            // IsDocumentEnabled also makes Hyperlinks raise RequestNavigate on click - always handle
            // it ourselves so unknown schemes (scriblucent-note://) and default navigation cannot crash.
            ContentTextBox.AddHandler(
                Hyperlink.RequestNavigateEvent,
                new System.Windows.Navigation.RequestNavigateEventHandler(ContentTextBox_HyperlinkRequestNavigate),
                handledEventsToo: true);
            ContentTextBox.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, ContentTextBox_CopyExecuted, ContentTextBox_CopyCanExecute));
            ContentTextBox.CommandBindings.Add(new CommandBinding(ApplicationCommands.Cut, ContentTextBox_CutExecuted, ContentTextBox_CutCanExecute));
            ContentTextBox.AddHandler(
                FrameworkElement.RequestBringIntoViewEvent,
                new RequestBringIntoViewEventHandler(ContentTextBox_RequestBringIntoView),
                handledEventsToo: true);
            DataObject.AddPastingHandler(ContentTextBox, ContentTextBox_Pasting);
            ContentTextBox.PreviewMouseDown += ContentTextBox_PreviewMouseDown;
            ContentTextBox.PreviewKeyDown += ContentTextBox_HyperlinkHintPreviewKeyDown;
            ContentTextBox.PreviewKeyUp += ContentTextBox_HyperlinkHintPreviewKeyUp;
            ContentTextBox.AddHandler(CommandManager.PreviewExecutedEvent,
                new ExecutedRoutedEventHandler(ContentTextBox_PreviewSelectionCommand), handledEventsToo: true);
            // The static Cut/Copy/Paste menu is set via XAML; captured here so
            // ContentTextBox_ContextMenuOpening can restore it after showing a dynamic
            // spell-check suggestions menu for a right-click that landed on a misspelled word.
            _defaultContentTextBoxContextMenu = ContentTextBox.ContextMenu;
            InitializeTableEditing();
            InitializeListEditing();
            ContentTextBox.ContextMenuOpening += ContentTextBox_ContextMenuOpening;
            PreviewKeyDown += NoteEditorWindow_PreviewKeyDown;
            PreviewKeyUp += NoteEditorWindow_PreviewKeyUp;
            // RootGrid is reparented into EditorWorkspaceWindow; keep image shortcuts on the body
            // so Delete/Back/Escape still reach the coordinator when this Window is not in the route.
            RootGrid.PreviewKeyDown += RootGrid_PreviewKeyDown;
            RootGrid.PreviewMouseDown += RootGrid_PreviewMouseDown;
            RootGrid.PreviewMouseWheel += RootGrid_PreviewMouseWheel;
            Deactivated += (_, _) => SetHyperlinkCtrlHintsVisible(false);
            Loaded += NoteEditorWindow_Loaded;
            RootGrid.Loaded += NoteEditorWindow_Loaded;
            AiFeatureGate.Changed += NoteEditorWindow_AiFeatureGateChanged;
            ApplyAiConvertVisibility();

            _floatingImages.Attach(FloatingImageOverlay, ContentTextBox, EditorSurface);
            _floatingImages.ImagesChanged += (_, _) =>
            {
                ApplyEditorEdgeSpacing();
                MarkEditorContentChanged();
                UpdateEditedIndicator();
                _wrapLayout?.ScheduleApply();
            };
            // Live text reflow while an image is being dragged/resized (Word-style), throttled
            // inside the engine; the gesture end re-runs through ImagesChanged above.
            _floatingImages.ImagesGeometryChanging += (_, _) => _wrapLayout?.ScheduleApplyLive();

            // Photo context-menu actions (the window owns the clipboard and the file dialog).
            _floatingImages.CopyRequested += FloatingImages_CopyRequested;
            _floatingImages.CutRequested += FloatingImages_CutRequested;
            _floatingImages.ChangePictureRequested += FloatingImages_ChangePictureRequested;
            _floatingImages.CaptionRequested += FloatingImages_CaptionRequested;
            _floatingImages.SelectionChanged += (_, _) =>
            {
                UpdateInsertCaptionAvailability();
                if (_floatingImages.SelectedImageId is not null) UpdateFormattingToolbarState();
                ScheduleSelectionChromeRefresh();
            };
            UpdateInsertCaptionAvailability();

            // Square text wrap around floating images (paragraph lane margins, recomputed on
            // text/size changes and cleared before any content serialization).
            _wrapLayout = new NoteWrapLayoutEngine(ContentTextBox, EditorSurface, _floatingImages);

            // Subscribe to theme changes to update RichTextBox foreground
            ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
        }

        private void InitializeEditorUiPreferences()
        {
            var settings = AppSettingsService.Load();
            _measurementUnitSystem = AppSettings.NormalizeMeasurementUnitSystem(settings.MeasurementUnitSystem);
            _isWordWrapEnabled = settings.IsEditorWordWrapEnabled;
            ApplySpellCheckPreference(settings.EnableSpellCheck);
            _lineSpacingMultiplier = NormalizeLineSpacingMultiplier(settings.PreferredLineSpacing);
            _indentSize = NormalizeIndentSize(settings.PreferredIndentSize);
            PopulateIndentSizeOptions();
            SyncLineSpacingSelectorFromPreference();
            SyncIndentSizeSelectorFromPreference();
            SetFontPanelOpen(settings.IsEditorFontPanelOpen, animate: false, persist: false);
            SetTagsPanelOpen(false, animate: false, persist: false);
            _lastSavedTagsPanelOpen = _isTagsPanelOpen;
        }

        private void InitializeEditorFormattingOptions()
        {
            PopulateFontFamilyOptions();
            PopulateFontSizeOptions();
        }

        // Fonts.SystemFontFamilies enumerates every installed font via OS interop (commonly
        // 150-400+ on a real machine) - too expensive to redo on every NoteEditorWindow
        // construction (i.e. every note opened). The installed font set doesn't change during a
        // session, so compute the deduplicated, sorted name list once per process and reuse it.
        private static List<string>? _cachedFontNames;

        private static List<string> GetOrBuildFontNames()
        {
            if (_cachedFontNames is { } cached)
                return cached;

            var fontNames = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddFontName(string? fontName)
            {
                if (string.IsNullOrWhiteSpace(fontName))
                    return;

                if (!seen.Add(fontName))
                    return;

                fontNames.Add(fontName);
            }

            foreach (var fontName in PreferredFontFamilyOptions)
                AddFontName(fontName);

            foreach (var fontFamily in Fonts.SystemFontFamilies
                         .Select(fontFamily => fontFamily.Source)
                         .OrderBy(fontName => fontName, StringComparer.CurrentCultureIgnoreCase))
            {
                AddFontName(fontFamily);
            }

            _cachedFontNames = fontNames;
            return fontNames;
        }

        private void PopulateFontFamilyOptions()
        {
            FontFamilyBox.Items.Clear();
            _fontFamilyItemsByName.Clear();
            foreach (var fontName in GetOrBuildFontNames())
            {
                var item = CreateEditorComboBoxItem(fontName);
                FontFamilyBox.Items.Add(item);
                _fontFamilyItemsByName[fontName] = item;
            }
        }

        private void PopulateFontSizeOptions()
        {
            FontSizeBox.Items.Clear();
            _fontSizeItemsByContent.Clear();
            foreach (var fontSize in WordFontSizes)
            {
                var content = FormatFontSize(fontSize);
                var item = CreateEditorComboBoxItem(content, fontSize);
                FontSizeBox.Items.Add(item);
                _fontSizeItemsByContent[content] = item;
            }
        }

        private void PopulateIndentSizeOptions()
        {
            IndentSizeBox.Items.Clear();
            foreach (var indentSize in WordIndentSizes)
                IndentSizeBox.Items.Add(CreateEditorComboBoxItem(FormatIndentSizeLabel(indentSize, _measurementUnitSystem), indentSize));
        }

        private ComboBoxItem CreateEditorComboBoxItem(string content, object? tag = null)
        {
            var item = new ComboBoxItem
            {
                Content = content,
                Tag = tag
            };

            if (TryFindResource("EditorFontComboBoxItemStyle") is Style style)
                item.Style = style;

            return item;
        }

        private void NoteEditorWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            AttachEditorSettingsSource(e.NewValue as INotifyPropertyChanged);
            ApplyEditorTextLayout();
        }

        private void AttachEditorSettingsSource(INotifyPropertyChanged? source)
        {
            if (ReferenceEquals(_editorSettingsSource, source))
                return;

            if (_editorSettingsSource != null)
                _editorSettingsSource.PropertyChanged -= EditorSettingsSource_PropertyChanged;

            _editorSettingsSource = source;

            if (_editorSettingsSource != null)
                _editorSettingsSource.PropertyChanged += EditorSettingsSource_PropertyChanged;
        }

        private void EditorSettingsSource_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.PropertyName)
                && !string.Equals(e.PropertyName, nameof(MainViewModel.EnableScrollbar), StringComparison.Ordinal)
                && !string.Equals(e.PropertyName, nameof(MainViewModel.EnableVerticalScrollbar), StringComparison.Ordinal)
                && !string.Equals(e.PropertyName, nameof(MainViewModel.EnableHorizontalScrollbar), StringComparison.Ordinal))
            {
                return;
            }

            Dispatcher.Invoke(ApplyEditorTextLayout);
        }

        private (bool Vertical, bool Horizontal) ResolveEditorScrollbarSettings()
        {
            if (DataContext is MainViewModel mainViewModel)
            {
                return (
                    mainViewModel.EnableScrollbar && mainViewModel.EnableVerticalScrollbar,
                    mainViewModel.EnableScrollbar && mainViewModel.EnableHorizontalScrollbar);
            }

            var settings = AppSettingsService.Load();
            return (
                settings.EnableScrollbar && settings.EnableVerticalScrollbar,
                settings.EnableScrollbar && settings.EnableHorizontalScrollbar);
        }

        private readonly EditorEdgeSpacing _editorEdgeSpacing = new();
        private bool _isApplyingEditorEdgeSpacing;
        private bool _suspendEditorEdgeSpacingForLoad;

        private void ApplyEditorEdgeSpacing(bool forImageInsertion = false)
        {
            if (_isApplyingEditorEdgeSpacing || _suspendEditorEdgeSpacingForLoad
                || ContentTextBox.Document is null)
                return;

            _isApplyingEditorEdgeSpacing = true;
            try
            {
                var storedImagesPending = (_isLoadingDocument || _suppressEditedMarking)
                    && _currentDocument?.Images is { Count: > 0 };
                var useEmptyViewportPadding = !forImageInsertion
                    && !_floatingImages.HasImages && !storedImagesPending;
                if (_editorEdgeSpacing.UpdateEditor(ContentTextBox, useEmptyViewportPadding))
                    UpdateFindHighlightClip();
            }
            finally
            {
                _isApplyingEditorEdgeSpacing = false;
            }
        }

        private void InsertFloatingImage(NoteImageLoader.LoadResult load, double width, double height)
        {
            ApplyEditorEdgeSpacing(forImageInsertion: true);
            try
            {
                _floatingImages.Insert(load, width, height);
            }
            finally
            {
                ApplyEditorEdgeSpacing();
            }
        }

        private void SuspendEditorEdgeSpacing()
        {
            if (_isApplyingEditorEdgeSpacing)
                return;

            _isApplyingEditorEdgeSpacing = true;
            try
            {
                _editorEdgeSpacing.Suspend();
            }
            finally
            {
                _isApplyingEditorEdgeSpacing = false;
            }
        }

        private void RemoveLegacyEditorEndPads()
        {
            if (_isApplyingEditorEdgeSpacing)
                return;

            _isApplyingEditorEdgeSpacing = true;
            try
            {
                EditorEdgeSpacing.RemoveLegacyPads(ContentTextBox.Document);
            }
            finally
            {
                _isApplyingEditorEdgeSpacing = false;
            }

            ApplyEditorEdgeSpacing();
        }

        private void ApplyEditorTextLayout()
        {
            ScheduleTableLayout();
            var scrollbars = ResolveEditorScrollbarSettings();
            var horizontalScrollEnabled = scrollbars.Horizontal && !_isWordWrapEnabled;

            ContentTextBox.VerticalScrollBarVisibility = scrollbars.Vertical
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Hidden;

            // The unscaled mirror is the editor's only visible horizontal bar.
            // Hidden still allows horizontal scrolling; Auto shows a second bar
            // inside the RichTextBox when word wrap is off.
            var internalHorizontalVisibility = horizontalScrollEnabled
                ? ScrollBarVisibility.Hidden
                : ScrollBarVisibility.Disabled;
            ContentTextBox.HorizontalScrollBarVisibility = internalHorizontalVisibility;
            if (_contentScrollViewer != null)
                _contentScrollViewer.HorizontalScrollBarVisibility = internalHorizontalVisibility;

            ContentTextBox.Document.PageWidth = horizontalScrollEnabled
                ? UnwrappedDocumentPageWidth
                : double.NaN;

            // RichTextBox ignores PagePadding and the first block's top margin. Edge
            // block padding scrolls with the text without adding selectable blocks.
            ApplyEditorEdgeSpacing();

            ContentTextBox.InvalidateMeasure();
            _contentScrollViewer?.InvalidateScrollInfo();
            InvalidateSelectionTiles();
            InvalidateFindTiles();
            QueueSelectionOverlay();
            if (_findBarOpen)
                QueueFindOverlay(DispatcherPriority.Render);
            UpdateEditorToolButtonStates();
            UpdateEditorScrollbarMirror();
            UpdateEditorScrollbarCorner();
            Dispatcher.InvokeAsync(UpdateEditorScrollbarMirror);
            Dispatcher.InvokeAsync(UpdateEditorScrollbarCorner);

            // Zoom / word-wrap / scrollbar preference changes alter line extents and column
            // width, so wrap lanes must be recomputed.
            _wrapLayout?.ScheduleApply();

            // Word-wrap toggling reflows the whole document (lines get far wider/shorter), so
            // the images must be re-clamped against the new reachable area and the extent pad
            // recomputed - otherwise deep or far-right images can end up misplaced.
            _floatingImages.SyncAll();
        }

        private void ThemeManager_ThemeChanged(object? sender, EventArgs e)
        {
            _cachedSelectionOverlayBrush = null;
            Scriblucent.Services.WindowThemeService.ApplyTheme(this, ThemeManager.CurrentTheme, rebuildFrame: true);
            ApplyRichTextBoxTheme();
            UpdateEditorToolButtonStates();
            UpdateEditorScrollbarMirror();
            UpdateEditorScrollbarCorner();
        }

        private bool _windowMotionRegistered;

        private void NoteEditorWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UiMotion.ConfigurePopupsForWindow(this);
            if (!_isHostedInTab && ReferenceEquals(sender, this) && !_windowMotionRegistered)
            {
                WindowContentEnterHelper.AnimateOnLoaded(this, beforeFinalClose: PerformEditorCloseCleanup);
                _windowMotionRegistered = true;
            }
            AttachContentScrollViewer();
        }

        private void AttachContentScrollViewer()
        {
            if (_contentScrollViewer != null)
                return;

            ContentTextBox.ApplyTemplate();
            _contentScrollViewer = FindVisualChild<ScrollViewer>(ContentTextBox);
            if (_contentScrollViewer != null)
            {
                // The RichTextBox attached property does not reliably reach the internal
                // viewer across themes - hide its scrollbars directly (a local value is
                // authoritative) so only the unscaled mirrors are visible.
                _contentScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
                _contentScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;

                // The page color sits behind FindHighlightOverlay. An opaque viewer fill
                // would hide those marks; the glyphs stay above them.
                _contentScrollViewer.Background = Brushes.Transparent;
                SmoothScrollCompositor.SetDirectOffsetMode(_contentScrollViewer, true);
                _contentScrollViewer.ScrollChanged += ContentScrollViewer_ScrollChanged;
                ContentTextBox.SizeChanged += ContentTextBox_SizeChanged;

                // The internal RichTextBox scrollbar is hidden; the unscaled mirrors beside the
                // editor surface (outside the zoom transform) show/hide with it and auto-hide
                // like the app's other scrollbars.
                _editorScrollbarHideTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(EditorScrollbarHideDelayMs)
                };
                _editorScrollbarHideTimer.Tick += (_, _) => HideEditorScrollbars();

                _contentScrollViewer.PreviewMouseMove += (_, _) => ShowEditorScrollbars();
                _contentScrollViewer.PreviewMouseWheel += (_, _) => ShowEditorScrollbars();
                _contentScrollViewer.PreviewMouseDown += (_, _) => ShowEditorScrollbars();
                _contentScrollViewer.PreviewKeyDown += (_, _) => ShowEditorScrollbars();
                _contentScrollViewer.MouseEnter += (_, _) => ShowEditorScrollbars();

                // The mirror bars sit outside the zoom transform. Real offsets keep their
                // thumbs in sync; this hook routes wheel input over them to the editor.
                // Slack marks sit just outside the viewport and slide in. The editor surface
                // clips the result; clipping the canvas first would throw that slack away.
                FindHighlightOverlay.ClipToBounds = false;
                if (_findOverlayMotion.Children.Count == 0)
                    _findOverlayMotion.Children.Add(_findOverlayCommit);
                FindHighlightOverlay.RenderTransform = _findOverlayMotion;
                if (_selectionMotion.Children.Count == 0)
                    _selectionMotion.Children.Add(_selectionCommit);
                SelectionHighlightOverlay.ClipToBounds = false;
                SelectionHighlightOverlay.RenderTransform = _selectionMotion;
                SmoothScrollCompositor.RegisterSessionLifecycle(
                    _contentScrollViewer,
                    HoldSelectionForScroll,
                    FinishSelectionScroll);
                SmoothScrollBehavior.RegisterExternalBar(_contentScrollViewer, EditorMirrorVerticalScrollBar);
                SmoothScrollBehavior.RegisterExternalBar(_contentScrollViewer, EditorMirrorHorizontalScrollBar);

                UpdateEditorScrollbarMirror();
                UpdateEditorScrollbarCorner();
                ShowEditorScrollbars();
            }
        }

        private void DetachContentScrollViewer()
        {
            if (_contentScrollViewer != null)
            {
                _contentScrollViewer.ScrollChanged -= ContentScrollViewer_ScrollChanged;
                ContentTextBox.SizeChanged -= ContentTextBox_SizeChanged;
                SmoothScrollCompositor.UnregisterSessionLifecycle(
                    _contentScrollViewer, HoldSelectionForScroll, FinishSelectionScroll);
                SmoothScrollCompositor.SetDirectOffsetMode(_contentScrollViewer, false);
            }

            _editorScrollbarHideTimer?.Stop();
            _editorScrollbarHideTimer = null;
            _editorScrollbarsShown = false;
            _contentScrollViewer = null;
        }

        private void ContentScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            RefreshTableChrome();
            UpdateEditorScrollbarMirror();
            UpdateEditorScrollbarCorner();
            ShowEditorScrollbars();
            if (_selectionCollapsedForScroll || ContentTextBox.Selection is { IsEmpty: false })
            {
                ApplySelectionOverlayCommit();
                if ((TryGetPaintedSelection(out var start, out var end)
                        && EditorTables.TryGetRange(start, end, out _, out _))
                    || HighlightTilesNeedUpdate(_selectionTiles)
                    || SelectionTilesNeedVisibleValidation())
                    QueueSelectionOverlay();
            }
            if (!_findBarOpen || !_hasFindHighlights)
                return;

            ApplyFindOverlayCommit();
            if (FindCompositionNeedsUpdate()
                || HighlightTilesNeedUpdate(_findTiles)
                || (_findActiveSpan.Length > 0 && HighlightTilesNeedUpdate(_findActiveTiles)))
                QueueFindOverlay(DispatcherPriority.Render);
        }

        private void ContentTextBox_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            CancelTableResize();
            ScheduleTableLayout();
            UpdateFindHighlightClip();
            InvalidateSelectionTiles();
            InvalidateFindTiles();
            QueueSelectionOverlay();
            if (_findBarOpen && _hasFindHighlights)
                QueueFindOverlay(DispatcherPriority.Render);
        }

        private void UpdateFindHighlightClip()
        {
            if (FindHighlightClip == null)
                return;

            var width = FindHighlightClip.ActualWidth;
            var height = FindHighlightClip.ActualHeight;
            if (width <= 0 || height <= 0)
            {
                FindHighlightClip.Clip = null;
                return;
            }

            var pad = ContentTextBox.Padding;
            var rect = new Rect(
                pad.Left,
                pad.Top,
                Math.Max(0, width - pad.Left - pad.Right),
                Math.Max(0, height - pad.Top - pad.Bottom));

            if (FindHighlightClip.Clip is RectangleGeometry clip)
            {
                clip.Rect = rect;
                return;
            }

            FindHighlightClip.Clip = new RectangleGeometry(rect);
        }

        /// <summary>
        /// Mirrors the (hidden) internal editor ScrollViewer onto the unscaled scrollbars that
        /// sit outside the zoom transform. The thumb length still reflects the visible fraction
        /// of the document (like Word's); the bars themselves never scale with the zoom.
        /// </summary>
        private void UpdateEditorScrollbarMirror()
        {
            if (_contentScrollViewer == null)
                return;

            _syncingEditorScrollbarMirror = true;
            try
            {
                var viewer = _contentScrollViewer;

                var vertical = EditorMirrorVerticalScrollBar;
                if (vertical != null)
                {
                    var scrollable = Math.Max(0, viewer.ExtentHeight - viewer.ViewportHeight);
                    var visible = scrollable > 0.5;
                    var wasVisible = vertical.Visibility == Visibility.Visible;
                    vertical.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                    if (visible)
                    {
                        if (!wasVisible && _editorScrollbarsShown)
                            vertical.Opacity = 1;
                        vertical.Maximum = scrollable;
                        vertical.ViewportSize = viewer.ViewportHeight;
                        vertical.SmallChange = Math.Max(16, viewer.ViewportHeight * 0.1);
                        vertical.LargeChange = Math.Max(1, viewer.ViewportHeight);
                        vertical.Value = Math.Min(viewer.VerticalOffset, scrollable);
                    }
                }

                var horizontal = EditorMirrorHorizontalScrollBar;
                if (horizontal != null)
                {
                    var scrollable = Math.Max(0, viewer.ExtentWidth - viewer.ViewportWidth);
                    var visible = scrollable > 0.5;
                    var wasVisible = horizontal.Visibility == Visibility.Visible;
                    horizontal.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                    if (visible)
                    {
                        if (!wasVisible && _editorScrollbarsShown)
                            horizontal.Opacity = 1;
                        horizontal.Maximum = scrollable;
                        horizontal.ViewportSize = viewer.ViewportWidth;
                        horizontal.SmallChange = Math.Max(16, viewer.ViewportWidth * 0.1);
                        horizontal.LargeChange = Math.Max(1, viewer.ViewportWidth);
                        horizontal.Value = Math.Min(viewer.HorizontalOffset, scrollable);
                    }
                }
            }
            finally
            {
                _syncingEditorScrollbarMirror = false;
            }
        }

        private void EditorMirrorVerticalScrollBar_Scroll(object sender, ScrollEventArgs e)
        {
            if (_syncingEditorScrollbarMirror || _contentScrollViewer == null)
                return;

            HoldSelectionForScroll();
            _contentScrollViewer.ScrollToVerticalOffset(e.NewValue);
        }

        private void EditorMirrorHorizontalScrollBar_Scroll(object sender, ScrollEventArgs e)
        {
            if (_syncingEditorScrollbarMirror || _contentScrollViewer == null)
                return;

            HoldSelectionForScroll();
            _contentScrollViewer.ScrollToHorizontalOffset(e.NewValue);
        }

        private void ShowEditorScrollbars()
        {
            if (!_editorScrollbarsShown)
            {
                _editorScrollbarsShown = true;
                AnimateEditorScrollbars(1.0);
            }
            _editorScrollbarHideTimer?.Stop();
            _editorScrollbarHideTimer?.Start();
        }

        private void HideEditorScrollbars()
        {
            if (EditorSurface.IsMouseOver || EditorSurface.IsKeyboardFocusWithin)
            {
                ShowEditorScrollbars();
                return;
            }

            if (EditorMirrorVerticalScrollBar is { IsMouseOver: true }
                || EditorMirrorVerticalScrollBar.IsMouseCaptureWithin
                || EditorMirrorHorizontalScrollBar is { IsMouseOver: true }
                || EditorMirrorHorizontalScrollBar.IsMouseCaptureWithin)
            {
                ShowEditorScrollbars();
                return;
            }

            if (_editorScrollbarsShown)
            {
                _editorScrollbarsShown = false;
                AnimateEditorScrollbars(0);
            }
        }

        private void AnimateEditorScrollbars(double targetOpacity)
        {
            if (EditorMirrorVerticalScrollBar is { Visibility: Visibility.Visible })
                UiMotion.Fade(EditorMirrorVerticalScrollBar, targetOpacity, 120, UiMotion.OutCubic);

            if (EditorMirrorHorizontalScrollBar is { Visibility: Visibility.Visible })
                UiMotion.Fade(EditorMirrorHorizontalScrollBar, targetOpacity, 120, UiMotion.OutCubic);
        }

        private void UpdateEditorScrollbarCorner()
        {
            var verticalVisible = EditorMirrorVerticalScrollBar?.Visibility == Visibility.Visible;
            var horizontalVisible = EditorMirrorHorizontalScrollBar?.Visibility == Visibility.Visible;

            if (EditorScrollbarCorner != null)
            {
                EditorScrollbarCorner.Visibility = verticalVisible && horizontalVisible
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private void UpdateEditorToolButtonStates()
        {
            var horizontalScrollbarAvailable = ResolveEditorScrollbarSettings().Horizontal;
            SetEditorToolButtonActive(WordWrapButton, _isWordWrapEnabled || !horizontalScrollbarAvailable);
            SetEditorToolButtonActive(FontButton, _isFontPanelOpen);
            SetEditorToolButtonActive(SearchButton, _findBarOpen);
            SetEditorToolButtonActive(TagsPanelToggleButton, _isTagsPanelOpen);
            SetEditorToolButtonActive(ZoomMenuButton, Math.Abs(_zoomLevel - 1.0) > 0.0001);
        }

        private static void SetEditorToolButtonActive(Button button, bool isActive)
        {
            if (isActive)
            {
                button.SetResourceReference(Control.BackgroundProperty, "EditorToolActiveBackground");
                button.SetResourceReference(Control.BorderBrushProperty, "EditorToolActiveBorder");
                button.SetResourceReference(Control.ForegroundProperty, "EditorToolActiveForeground");
                button.BorderThickness = new Thickness(2);
                return;
            }

            button.SetResourceReference(Control.BackgroundProperty, "IconButtonBackground");
            button.BorderBrush = Brushes.Transparent;
            button.SetResourceReference(Control.ForegroundProperty, "IconButtonForeground");
            button.BorderThickness = new Thickness(0);
        }

        public void EnableTabMode()
        {
            _isHostedInTab = true;

            // In standalone mode the window-level Loaded trigger animates RootGrid from Opacity=0.
            // When hosted inside tabs, the window is never shown, so force visible state here.
            if (RootGrid != null)
            {
                UiMotion.Stop(RootGrid);
                UiMotion.Clear(RootGrid, UIElement.OpacityProperty);
                RootGrid.Opacity = 1;
                UiMotion.EnsureTranslate(RootGrid).Y = 0;
            }
        }

        public void ApplyMeasurementUnitSystemPreference(string unitSystem)
        {
            var normalized = AppSettings.NormalizeMeasurementUnitSystem(unitSystem);
            if (string.Equals(_measurementUnitSystem, normalized, StringComparison.Ordinal))
                return;

            _measurementUnitSystem = normalized;
            PopulateIndentSizeOptions();
            SyncIndentSizeSelectorFromPreference();
        }

        public void ApplySpellCheckPreference(bool enabled)
        {
            _isSpellCheckEnabled = enabled;
            if (enabled)
                ScheduleSpellCheck();
            else
                ClearSpellCheckDecorations();
            UpdateSpellCheckLanguageChooserText();
        }

        /// <summary>Called by the Settings panel whenever the enabled-language set or custom-word
        /// list changes, so an already-open editor re-scans immediately instead of waiting for the
        /// next keystroke.</summary>
        public void RefreshSpellCheckLanguages()
        {
            if (_isSpellCheckEnabled)
                ScheduleSpellCheck();
            UpdateSpellCheckLanguageChooserText();
        }

        /// <summary>Status-bar chip mirroring Word/LibreOffice's bottom-bar language indicator -
        /// shows the single active proofing language using the full "{native name} ({name in the
        /// app's current UI language})" label, and doubles as the entry point for the quick
        /// language-picker menu below.</summary>
        private void UpdateSpellCheckLanguageChooserText()
        {
            if (SpellCheckLanguageChooserText is null)
                return;

            if (!_isSpellCheckEnabled)
            {
                SpellCheckLanguageChooserText.Text = LocalizationService.GetString("SpellCheckLanguageChooserOff");
                return;
            }

            var settings = AppSettingsService.Load();
            SpellCheckLanguageChooserText.Text = !string.IsNullOrEmpty(settings.SpellCheckLanguageKey)
                ? SpellCheckDictionaryService.GetFullLanguageLabel(settings.SpellCheckLanguageKey)
                : LocalizationService.GetString("SpellCheckLanguageChooserNone");
        }

        /// <summary>Quick language picker, like clicking the language indicator in Word/LibreOffice's
        /// status bar - single-select (only one language is ever active at a time, matching that
        /// model rather than checking against several dictionaries at once), and only offers
        /// already-downloaded dictionaries; adding new languages stays in Settings, matching how
        /// Word's own status-bar picker only lists installed proofing languages rather than
        /// duplicating the full language-install flow inline. Also offers a one-click way to turn
        /// spell check off entirely, without a trip to Settings.</summary>
        private void SpellCheckLanguageChooserButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
                return;

            var settings = AppSettingsService.Load();
            var activeKey = settings.SpellCheckLanguageKey;

            var downloadedLanguages = SpellCheckDictionaryService.GetSupportedLanguages()
                .Where(language => SpellCheckDictionaryService.IsDictionaryDownloaded(language.Key))
                .ToList();

            var menu = new ContextMenu
            {
                Placement = PlacementMode.Top,
                PlacementTarget = button
            };

            if (downloadedLanguages.Count == 0)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = LocalizationService.GetString("SpellCheckLanguageChooserNoneDownloaded"),
                    IsEnabled = false
                });
            }
            else
            {
                foreach (var language in downloadedLanguages)
                {
                    var item = new MenuItem
                    {
                        Header = SpellCheckDictionaryService.GetFullLanguageLabel(language.Key),
                        IsCheckable = true,
                        IsChecked = string.Equals(language.Key, activeKey, StringComparison.OrdinalIgnoreCase),
                        Tag = language.Key
                    };
                    item.Click += SpellCheckLanguageMenuItem_Click;
                    menu.Items.Add(item);
                }
            }

            menu.Items.Add(new Separator());
            var disableItem = new MenuItem
            {
                Header = LocalizationService.GetString("SpellCheckDisableEntirely"),
                IsEnabled = _isSpellCheckEnabled
            };
            disableItem.Click += DisableSpellCheckMenuItem_Click;
            menu.Items.Add(disableItem);

            button.ContextMenu = menu;
            menu.IsOpen = true;
        }

        private void DisableSpellCheckMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var settings = AppSettingsService.Load();
            if (!settings.EnableSpellCheck)
                return;

            settings.EnableSpellCheck = false;
            AppSettingsService.Save(settings);
            BroadcastSpellCheckPreference(false);
        }

        /// <summary>Single-select, like a radio group - picking a language always makes it the sole
        /// active one (matching Word/LibreOffice's one-proofing-language model). Deliberately
        /// ignores the menu item's own checked state after the click (WPF auto-toggles it before
        /// this handler runs) and just always activates the clicked language - there's no
        /// "uncheck to select none" here, since "Disable spell check" already covers that.</summary>
        private void SpellCheckLanguageMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string key })
                return;

            var settings = AppSettingsService.Load();
            settings.SpellCheckLanguageKey = key;

            // Picking a language from this chooser is a clear signal the user wants spell check
            // active - mirrors Word, where selecting a proofing language just works rather than
            // silently no-opping because a separate master switch is still off.
            if (!settings.EnableSpellCheck)
                settings.EnableSpellCheck = true;

            AppSettingsService.Save(settings);
            BroadcastSpellCheckPreference(settings.EnableSpellCheck);
        }

        private static void BroadcastSpellCheckPreference(bool enabled)
        {
            foreach (var window in Application.Current.Windows.OfType<Window>())
            {
                switch (window)
                {
                    case NoteEditorWindow editorWindow:
                        editorWindow.ApplySpellCheckPreference(enabled);
                        break;
                    case EditorWorkspaceWindow tabsWindow:
                        tabsWindow.ApplySpellCheckPreference(enabled);
                        break;
                }
            }
        }

        public UIElement? DetachEditorContentForHosting()
        {
            // Peel accidental chrome first, then unwrap FeatureWindowShell so the tabs window
            // (which already has chrome + FeatureWindowShell) does not get a nested caption/frame.
            var content = Services.ShellWindowChrome.DetachContent(this);
            if (content is Controls.FeatureWindowShell shell)
            {
                var body = shell.Body as UIElement;
                shell.Body = null!;
                return body;
            }

            return content;
        }

        /// <param name="disposeAiBackendSynchronously">
        /// Kept for compat with EditorWorkspaceWindow.Closed sweep; previously per-tab
        /// LLamaSharpWorkerAdapter disposal could block ~3s. With app-scoped AiBackendHost
        /// the backend is singleton and app-lifetime only (App.OnExit calls Shutdown), so
        /// per-tab close no longer tears it down - parameter is now ignored.
        /// </param>
        public void DisposeHostedEditor(bool disposeAiBackendSynchronously = false)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            DropParkedSelection();

            // No per-tab backend teardown: AiBackendHost.Shared is singleton, disposed only on App.OnExit.
            _ = disposeAiBackendSynchronously;

            StopAutoSaveTimer();
            _counterUpdateDebounceTimer?.Stop();
            _selectionUiTimer?.Stop();
            _findHighlightDebounceTimer?.Stop();
            DetachContentScrollViewer();
            _floatingImages.Detach();
            _wrapLayout?.Dispose();
            _wrapLayout = null;
            Loaded -= NoteEditorWindow_Loaded;
            RootGrid.Loaded -= NoteEditorWindow_Loaded;
            RootGrid.PreviewKeyDown -= RootGrid_PreviewKeyDown;
            RootGrid.PreviewMouseDown -= RootGrid_PreviewMouseDown;
            RootGrid.PreviewMouseWheel -= RootGrid_PreviewMouseWheel;
            FontButton.ToolTipOpening -= FontButton_ToolTipOpening;
            DataContextChanged -= NoteEditorWindow_DataContextChanged;
            AttachEditorSettingsSource(null);
            _flashcardConversionCancellationSource?.Cancel();
            _flashcardConversionCancellationSource?.Dispose();
            _flashcardConversionCancellationSource = null;
            _mindMapConversionCancellationSource?.Cancel();
            _mindMapConversionCancellationSource?.Dispose();
            _mindMapConversionCancellationSource = null;
            _testConversionCancellationSource?.Cancel();
            _testConversionCancellationSource?.Dispose();
            _testConversionCancellationSource = null;
            ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;
            AiGenerationStateChanged -= NoteEditorWindow_AiGenerationStateChanged;
            AiFeatureGate.Changed -= NoteEditorWindow_AiFeatureGateChanged;
        }

        private Window GetDialogOwnerWindow()
        {
            // Hosted editors live inside EditorWorkspaceWindow; this window itself is never
            // shown, so it must never be used as a dialog Owner (that throws). Fall back to
            // the main window when the hosted content is already detached.
            return _workspaceOwner ?? Window.GetWindow(ContentTextBox)
                ?? Application.Current?.MainWindow
                ?? this;
        }

        private void NoteEditorWindow_Closing(object sender, CancelEventArgs e)
        {
            if (WindowContentEnterHelper.ShouldBypassCloseHandling(this)) return;
            if (!_allowCloseWithoutPrompt && !ConfirmCloseIfNeeded())
            {
                e.Cancel = true;
                return;
            }

            _allowCloseWithoutPrompt = true;
            if (!_windowMotionRegistered || UiMotion.IsMotionDisabled) PerformEditorCloseCleanup();
        }

        private void PerformEditorCloseCleanup()
        {
            DropParkedSelection();
            StopAutoSaveTimer();
            _counterUpdateDebounceTimer?.Stop();
            _selectionUiTimer?.Stop();
            _findHighlightDebounceTimer?.Stop();
            DetachContentScrollViewer();
            _floatingImages.Detach();
            _wrapLayout?.Dispose();
            _wrapLayout = null;
            Loaded -= NoteEditorWindow_Loaded;
            RootGrid.Loaded -= NoteEditorWindow_Loaded;
            RootGrid.PreviewKeyDown -= RootGrid_PreviewKeyDown;
            RootGrid.PreviewMouseDown -= RootGrid_PreviewMouseDown;
            RootGrid.PreviewMouseWheel -= RootGrid_PreviewMouseWheel;
            FontButton.ToolTipOpening -= FontButton_ToolTipOpening;
            DataContextChanged -= NoteEditorWindow_DataContextChanged;
            AttachEditorSettingsSource(null);
            _flashcardConversionCancellationSource?.Cancel();
            _mindMapConversionCancellationSource?.Cancel();
            _testConversionCancellationSource?.Cancel();
            ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;
        }

        public bool ConfirmCloseIfNeeded()
        {
            if (!HasUnsavedChanges())
                return true;

            var documentTitle = ResolveEditorTitleForPrompt();
            var result = ModernDialog.Show(
                GetDialogOwnerWindow(),
                LocalizationService.GetString("UnsavedChanges"),
                string.Format(LocalizationService.GetString("UnsavedChangesConfirmationFormat"), documentTitle),
                ModernDialogTone.Warning,
                LocalizationService.GetString("LeaveWithoutSaving"),
                LocalizationService.GetString("Cancel"),
                LocalizationService.GetString("SaveAndExit"),
                primaryStyle: ModernDialogButtonStyle.Danger,
                secondaryStyle: ModernDialogButtonStyle.Primary);

            switch (result)
            {
                case ModernDialogResult.Primary:
                    return true;
                case ModernDialogResult.Secondary:
                    // Closing after this save: mark it BEFORE the save so the save uses the fast
                    // exact wrap restore (no full convergence pass - the wrap engine is disposed
                    // moments later anyway). Reset if the save fails and the close is cancelled.
                    _allowCloseWithoutPrompt = true;
                    var saved = SaveCurrentDocument();
                    if (!saved)
                        _allowCloseWithoutPrompt = false;
                    return saved;
                default:
                    return false;
            }
        }

        private string ResolveEditorTitleForPrompt()
        {
            var title = TitleTextBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(title))
                return title;

            title = _currentDocument?.Title?.Trim() ?? string.Empty;
            return string.IsNullOrWhiteSpace(title)
                ? LocalizationService.GetString("NewNoteTitle")
                : title;
        }

        private void ClearAllHighlights()
        {
            if (!_hasFindHighlights && !_findOverlayIndexReady && _findOverlaySpans.Length == 0)
                return;

            // Find marks are viewport drawings, never document formatting. In particular,
            // clearing a search must not overwrite user-applied text backgrounds.
            _hasFindHighlights = false;
            _findOverlaySpans = Array.Empty<MatchSpan>();
            _findActiveSpan = Array.Empty<MatchSpan>();
            _findActiveTop = double.NaN;
            _findActiveBottom = double.NaN;
            _findOverlayIndex = default;
            _findOverlayIndexReady = false;
            InvalidateFindTiles();
        }

        private void ApplyRichTextBoxTheme()
        {
            try
            {
                // Default text follows the theme. A color the user chose stays as saved.
                if (TryGetRichTextBoxForeground(out var foregroundBrush))
                {
                    var doc = ContentTextBox.Document;
                    doc.Foreground = foregroundBrush;
                    foreach (Block block in doc.Blocks)
                        RecolorThemeDefaultForeground(block, foregroundBrush);
                }
            }
            catch (Exception)
            {
                // Non-critical - theme application shouldn't crash the app.
            }

            _cachedSelectionOverlayBrush = null;
            InvalidateSelectionTiles();
            InvalidateFindTiles();
            if (_findBarOpen && _hasFindHighlights)
                QueueFindOverlay(DispatcherPriority.Render);
            QueueSelectionOverlay();
        }

        private static readonly Color LightThemeForeground = Color.FromRgb(0x1C, 0x1C, 0x1C);
        private static readonly Color DarkThemeForeground = Color.FromRgb(0xFF, 0xFF, 0xFF);

        private static bool IsThemeDefaultForeground(Color color)
            => color.A == 0xFF
                && (color == LightThemeForeground || color == DarkThemeForeground);

        /// <summary>Recolors only text that is still using a theme default. Chosen colors,
        /// highlights, and list marker offsets stay untouched.</summary>
        private void RecolorThemeDefaultForeground(TextElement element, Brush themeBrush)
        {
            if (element.ReadLocalValue(TextElement.ForegroundProperty) is SolidColorBrush brush
                && IsThemeDefaultForeground(brush.Color))
            {
                element.Foreground = themeBrush;
            }

            switch (element)
            {
                case Paragraph paragraph:
                    foreach (Inline inline in paragraph.Inlines)
                        RecolorThemeDefaultInline(inline, themeBrush);
                    break;
                case Section section:
                    foreach (Block block in section.Blocks)
                        RecolorThemeDefaultForeground(block, themeBrush);
                    break;
                case System.Windows.Documents.List list:
                    foreach (ListItem item in list.ListItems)
                    {
                        RecolorThemeDefaultForeground(item, themeBrush);
                        foreach (Block block in item.Blocks)
                            RecolorThemeDefaultForeground(block, themeBrush);
                    }
                    break;
                case Table table:
                    foreach (TableRowGroup rowGroup in table.RowGroups)
                    {
                        RecolorThemeDefaultForeground(rowGroup, themeBrush);
                        foreach (TableRow row in rowGroup.Rows)
                        {
                            RecolorThemeDefaultForeground(row, themeBrush);
                            foreach (TableCell cell in row.Cells)
                            {
                                RecolorThemeDefaultForeground(cell, themeBrush);
                                foreach (Block block in cell.Blocks)
                                    RecolorThemeDefaultForeground(block, themeBrush);
                            }
                        }
                    }
                    break;
            }
        }

        private void RecolorThemeDefaultInline(Inline inline, Brush themeBrush)
        {
            if (inline is not TextElement element)
                return;

            if (element.ReadLocalValue(TextElement.ForegroundProperty) is SolidColorBrush brush
                && IsThemeDefaultForeground(brush.Color))
            {
                element.Foreground = themeBrush;
            }

            switch (inline)
            {
                case Span span:
                    foreach (Inline child in span.Inlines)
                        RecolorThemeDefaultInline(child, themeBrush);
                    break;
                case AnchoredBlock anchored:
                    foreach (Block block in anchored.Blocks)
                        RecolorThemeDefaultForeground(block, themeBrush);
                    break;
            }
        }

        private static bool TryGetRichTextBoxForeground(out Brush foregroundBrush)
        {
            if (Application.Current?.Resources.Contains("RichTextBoxForeground") == true
                && Application.Current.Resources["RichTextBoxForeground"] is Brush brush)
            {
                foregroundBrush = brush;
                return true;
            }

            foregroundBrush = Brushes.Black;
            return false;
        }

        private void UpdateCounter()
        {
            TextRange textRange = new TextRange(
                ContentTextBox.Document.ContentStart,
                ContentTextBox.Document.ContentEnd);

            var text = textRange.Text;

            text = TrimSingleTrailingLineBreak(text);

            var normalizedText = text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            var countableText = RemoveIndentationForCounter(normalizedText);

            int characters = countableText.Length;

            int words = string.IsNullOrWhiteSpace(countableText)
                ? 0
                : countableText.Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;

            int lines = string.IsNullOrEmpty(normalizedText)
                ? 1
                : normalizedText.Split('\n').Length;

            CounterText.Text = string.Format(
                LocalizationService.GetString("EditorCounterFormat"),
                words,
                characters,
                lines);
        }

        private void ContentTextBox_SelectionChanged(object sender, RoutedEventArgs e)
        {
            UpdateTableToolsState();
            RefreshTableChrome();
            if (_suppressSelectionChange)
                return;

            if (EditorEdgeSpacing.CollapseEmptySelection(ContentTextBox))
            {
                // WPF selects a synthetic paragraph break in an otherwise empty document.
                DropParkedSelection();
                InvalidateSelectionTiles();
                return;
            }

            // Internal layout/save helpers can move the caret while a selection is parked.
            // Their collapsed caret does not replace the logical selection. A deliberate
            // nonempty programmatic selection does.
            if (_selectionCollapsedForScroll)
            {
                if (ContentTextBox.Selection.IsEmpty)
                    return;
                DropParkedSelection();
            }

            UpdateOnlineSearchAvailability();
            ScheduleSelectionChromeRefresh();
            // Several SelectionChanged events can precede one render, including a transient
            // collapsed range. Keep the last painted blue geometry until that final range is known.
            QueueSelectionOverlay();
        }

        private void ScheduleSelectionChromeRefresh()
        {
            if (_selectionCollapsedForScroll)
                return;

            _selectionUiTimer ??= new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(180)
            };
            _selectionUiTimer.Stop();
            _selectionUiTimer.Tick -= SelectionUiTimer_Tick;
            _selectionUiTimer.Tick += SelectionUiTimer_Tick;
            _selectionUiTimer.Start();
        }

        private void SelectionUiTimer_Tick(object? sender, EventArgs e)
        {
            _selectionUiTimer?.Stop();
            if (_selectionCollapsedForScroll || _isDisposed)
                return;

            // Both operations can inspect a whole selection. They belong after a gesture,
            // never in SelectionChanged or a scroll callback.
            if (!SelectionExtendsOutsideViewport())
                UpdateFontButtonText();
            UpdateFormattingToolbarState();
        }

        private void FontButton_ToolTipOpening(object sender, ToolTipEventArgs e)
            => UpdateFontButtonText();

        private void ContentTextBox_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            if (_freezeSelectionScroll)
                e.Handled = true;
        }

        private void HoldSelectionForScroll()
        {
            if (_selectionCollapsedForScroll)
                return;

            var selection = ContentTextBox.Selection;
            if (selection == null || selection.IsEmpty || !SelectionExtendsOutsideViewport())
                return;

            _heldSelectionStart = selection.Start;
            _heldSelectionEnd = selection.End;
            _heldCaretAtStart = ContentTextBox.CaretPosition?.CompareTo(selection.Start) == 0;
            SmoothCaretBehavior.SetIsSuspended(ContentTextBox, true);
            _selectionCollapsedForScroll = true;
            _selectionUiTimer?.Stop();
            var visible = ContentTextBox.GetPositionFromPoint(new Point(8, 4), snapToText: true)
                ?? selection.Start;
            ContentTextBox.CaretBrush = Brushes.Transparent;
            ReplaceEditorSelection(visible, visible);
            QueueSelectionOverlay();
        }

        private void FinishSelectionScroll()
        {
            // Keep the large selection parked across consecutive wheel bursts. A non-scroll
            // action restores it once, immediately before that action reaches RichTextBox.
            QueueSelectionOverlay();
        }

        private void MaterializeSelectionForAction()
        {
            if (!_selectionCollapsedForScroll)
                return;

            var start = _heldSelectionStart;
            var end = _heldSelectionEnd;
            var caretAtStart = _heldCaretAtStart;
            DropParkedSelection();
            if (start is null || end is null)
                return;

            ReplaceEditorSelection(caretAtStart ? end : start, caretAtStart ? start : end);
            ScheduleSelectionChromeRefresh();
            QueueSelectionOverlay();
        }

        private void DropParkedSelection()
        {
            if (!_selectionCollapsedForScroll)
                return;

            _selectionCollapsedForScroll = false;
            _heldSelectionStart = null;
            _heldSelectionEnd = null;
            ContentTextBox.ClearValue(TextBoxBase.CaretBrushProperty);
            SmoothCaretBehavior.SetIsSuspended(ContentTextBox, false);
        }

        private void ReplaceEditorSelection(TextPointer start, TextPointer end)
        {
            var viewer = _contentScrollViewer;
            var x = viewer?.HorizontalOffset ?? 0;
            var y = viewer?.VerticalOffset ?? 0;
            _freezeSelectionScroll = true;
            _suppressSelectionChange = true;
            try
            {
                ContentTextBox.Selection.Select(start, end);
            }
            finally
            {
                _suppressSelectionChange = false;
                _freezeSelectionScroll = false;
            }

            if (viewer == null)
                return;

            viewer.ScrollToHorizontalOffset(x);
            viewer.ScrollToVerticalOffset(y);
        }

        private void ApplySelectionOverlayCommit()
        {
            if (_contentScrollViewer == null)
                return;

            _selectionCommit.X = -_contentScrollViewer.HorizontalOffset;
            _selectionCommit.Y = -_contentScrollViewer.VerticalOffset;
        }

        private void QueueSelectionOverlay(DispatcherPriority priority = DispatcherPriority.Render)
        {
            if (_selectionOverlayQueued)
            {
                if (_selectionOverlayOperation is { Status: DispatcherOperationStatus.Pending } queued
                    && queued.Priority < priority)
                    queued.Priority = priority;
                return;
            }

            _selectionOverlayQueued = true;
            _selectionOverlayOperation = Dispatcher.BeginInvoke(PaintSelectionOverlayQueued, priority);
        }

        private void PaintSelectionOverlayQueued()
        {
            _selectionOverlayOperation = null;
            _selectionOverlayQueued = false;
            PaintSelectionOverlay();
        }

        private void PaintSelectionOverlay()
        {
            PaintSelectionTiles();
        }

        /// <summary>
        /// One visual line of the selection. The last selected glyph determines the right
        /// edge even when the selection continues onto another line.
        /// </summary>
        internal static Rect SelectionLineRect(TextPointer start, TextPointer end)
        {
            var startRect = SafeCharacterRect(start, LogicalDirection.Forward);
            if (startRect.IsEmpty)
                startRect = SafeCharacterRect(start, LogicalDirection.Backward);

            var endRect = EndRectOnLine(start, end, startRect);
            var rect = UnionOnSameLine(startRect, endRect);
            if (rect.IsEmpty || rect.Height <= 0)
                return Rect.Empty;

            // A selected paragraph break has no glyph width. Give it a caret-sized mark,
            // not the remaining width of the editor column.
            if (rect.Width <= 0)
                rect.Width = 2;
            return rect;
        }

        /// <summary>
        /// The selection's horizontal edges follow its characters, but its vertical edges
        /// belong to the visual line. A character rect can be much shorter or taller than
        /// the line box when the selection starts or ends inside a differently sized run.
        /// </summary>
        internal static Rect BlueSelectionLineRect(
            TextPointer lineStart,
            TextPointer? nextLine,
            TextPointer start,
            TextPointer end)
            => BlueSelectionLineRect(lineStart, nextLine, start, end, null, 0);

        internal readonly record struct LineBandOrigin(double TextCenter, double BoxTop);

        internal static Rect BlueSelectionLineRect(
            TextPointer lineStart,
            TextPointer? nextLine,
            TextPointer start,
            TextPointer end,
            Dictionary<Paragraph, LineBandOrigin>? lineOrigins,
            double verticalOffset)
        {
            return WpfEditorGeometry.SelectionBounds(start, end);
        }

        private static Rect EndRectOnLine(TextPointer start, TextPointer end, Rect startRect)
        {
            var directed = end.GetPositionAtOffset(0, LogicalDirection.Backward);
            if (directed is not null)
            {
                var directedRect = SafeCharacterRect(directed, LogicalDirection.Backward);
                if (IsMeasuredEnd(start, end, startRect, directedRect))
                    return directedRect;
            }

            var direct = SafeCharacterRect(end, LogicalDirection.Backward);
            if (IsMeasuredEnd(start, end, startRect, direct))
                return direct;

            var probe = end;
            for (var i = 0; i < 8 && probe.CompareTo(start) > 0; i++)
            {
                var previous = probe.GetNextInsertionPosition(LogicalDirection.Backward);
                if (previous == null || previous.CompareTo(probe) >= 0)
                    break;

                probe = previous;
                var forward = SafeCharacterRect(probe, LogicalDirection.Forward);
                if (IsMeasuredEnd(start, end, startRect, forward))
                    return forward;
            }

            return Rect.Empty;
        }

        private static bool IsMeasuredEnd(TextPointer rangeStart, TextPointer rangeEnd, Rect startRect, Rect endRect)
        {
            if (!IsGlyphOnLine(startRect, endRect))
                return false;

            if (startRect.IsEmpty || endRect.Right > startRect.Right + 0.5)
                return true;

            // A one-character run is the start rect itself. A longer run whose end sits
            // on the left edge is the paragraph break, not the last glyph.
            return !SpansMoreThanOneCharacter(rangeStart, rangeEnd);
        }

        private static bool IsGlyphOnLine(Rect line, Rect glyph)
        {
            // TextPointer.GetCharacterRect reports caret positions with zero width. Their
            // X coordinates are exactly the edges needed for a selected-character span.
            if (glyph.IsEmpty || glyph.Height <= 0)
                return false;

            if (line.IsEmpty)
                return true;

            var overlap = Math.Min(line.Bottom, glyph.Bottom) - Math.Max(line.Top, glyph.Top);
            return overlap > Math.Min(line.Height, glyph.Height) * 0.5;
        }

        private static Rect UnionOnSameLine(Rect start, Rect end)
        {
            if (start.IsEmpty)
                return end;

            if (end.IsEmpty || !IsGlyphOnLine(start, end))
                return start;

            return Rect.Union(start, end);
        }

        private static bool SpansMoreThanOneCharacter(TextPointer start, TextPointer end)
        {
            try
            {
                return start.GetOffsetToPosition(end) > 1;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static Rect SafeCharacterRect(TextPointer pointer, LogicalDirection direction)
        {
            try
            {
                return pointer.GetCharacterRect(direction);
            }
            catch (Exception)
            {
                return Rect.Empty;
            }
        }

        private bool TryGetPaintedSelection(out TextPointer start, out TextPointer end)
        {
            if (EditorEdgeSpacing.IsEmpty(ContentTextBox.Document))
            {
                start = null!;
                end = null!;
                return false;
            }

            if (_selectionCollapsedForScroll && _heldSelectionStart is not null && _heldSelectionEnd is not null)
            {
                start = _heldSelectionStart;
                end = _heldSelectionEnd;
            }
            else if (ContentTextBox.Selection is { IsEmpty: false } selection)
            {
                start = selection.Start;
                end = selection.End;
            }
            else
            {
                start = null!;
                end = null!;
                return false;
            }

            if (start.CompareTo(end) > 0)
                (start, end) = (end, start);

            return start.CompareTo(end) != 0;
        }

        private Brush CreateSelectionOverlayBrush()
        {
            if (_cachedSelectionOverlayBrush is not null)
                return _cachedSelectionOverlayBrush;

            var source = TryFindResource("EditorSelectionBrush") as Brush
                ?? new SolidColorBrush(Color.FromRgb(67, 92, 236));
            if (source.IsFrozen)
                source = source.Clone();

            source.Opacity = 0.4;
            if (source.CanFreeze)
                source.Freeze();

            return _cachedSelectionOverlayBrush = source;
        }

        private void ContentTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Wrap lanes/spacers alter the live FlowDocument, but are stripped from saved
            // content. Refresh geometry/cache state without recording an authored edit or
            // scheduling another wrap pass in response to the engine's own changes.
            if (_wrapLayout?.IsApplying == true)
            {
                InvalidateSelectionTiles();
                InvalidateFindTiles();
                _findDocumentVersion++;
                ReleaseFindCache();
                if (_selectionCollapsedForScroll || ContentTextBox.Selection is { IsEmpty: false })
                    QueueSelectionOverlay();
                if (_findBarOpen) QueueFindOverlay(DispatcherPriority.Render);
                return;
            }

            TableEditor_TextChanged(e);
            if (!_isLoadingDocument && !_isApplyingEditorEdgeSpacing && !_isSerializingEditorContent
                && !_isApplyingHyperlinkCtrlHints && !_isApplyingSpellCheckDecorations && !_floatingImages.IsAdjustingExtentPad)
                _lists?.TextChanged(e);
            if (_isApplyingEditorEdgeSpacing)
            {
                InvalidateSelectionTiles();
                InvalidateFindTiles();
                if (_selectionCollapsedForScroll || ContentTextBox.Selection is { IsEmpty: false })
                    QueueSelectionOverlay();
                if (_findBarOpen)
                    QueueFindOverlay(DispatcherPriority.Render);
                return;
            }

            if (_refreshLineSpacingAfterNextTyping && !_isLoadingDocument
                && e.Changes.Any(change => change.AddedLength > 0))
            {
                _refreshLineSpacingAfterNextTyping = false;
                if (ContentTextBox.CaretPosition.Paragraph is { } typedParagraph)
                    ApplyLineSpacingToParagraph(typedParagraph, _lineSpacingMultiplier);
            }

            InvalidateSelectionTiles();
            if (_selectionCollapsedForScroll || ContentTextBox.Selection is { IsEmpty: false })
                QueueSelectionOverlay();

            _findDocumentVersion++;
            ReleaseFindCache();
            if (_findBarOpen)
            {
                ClearAllHighlights();
                if (_findMutationDepth > 0 || _isApplyingSpellCheckDecorations || _isSerializingEditorContent)
                    _findRefreshPending = true;
                else
                    ScheduleFindHighlights();
            }

            if (_isSerializingEditorContent
                || _isApplyingHyperlinkCtrlHints
                || _isApplyingSpellCheckDecorations
                || _floatingImages.IsAdjustingExtentPad)
                return;

            if (_findMutationDepth > 0)
            {
                MarkEditorContentChanged();
                return;
            }

            if (!_isLoadingDocument)
                ApplyEditorEdgeSpacing();

            MarkEditorContentChanged();

            // Dropdown typography ApplyPropertyValue fires TextChanged; skip chrome that the
            // Changed handlers already refresh explicitly to avoid a double flash/jump.
            if (_isApplyingTypography)
                return;

            // Only update counter, not theme (theme is applied during load and on theme change)
            ScheduleCounterUpdate();
            UpdateEditedIndicator();
            ScheduleSelectionChromeRefresh();

            // Applying/clearing spell-check squiggles is itself a document-content change (real
            // TextDecorations, not an overlay) - guarded by _isApplyingSpellCheckDecorations so
            // that doesn't schedule another pass against itself forever.
            if (_isSpellCheckEnabled && !_isApplyingSpellCheckDecorations)
                ScheduleSpellCheck();

            // Squiggle edits are not text-flow changes. Spell-check already restores the caret;
            // scheduling wrap from those events forces a second layout.
            if (!_isApplyingSpellCheckDecorations)
                _wrapLayout?.ScheduleApply();

            // Text edits also shift in-line image anchors - keep the caption sequence order fresh.
            _floatingImages.RenumberCaptions();
        }

        // UpdateCounter() walks the entire document text (TextRange extraction, multiple full-string
        // Replace/Split passes) - too expensive to run synchronously on every keystroke in a long note.
        // Debounce it the same way every other per-keystroke hot path in this app is debounced.
        private void ScheduleCounterUpdate()
        {
            _counterUpdateDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _counterUpdateDebounceTimer.Stop();
            _counterUpdateDebounceTimer.Tick -= CounterUpdateDebounceTimer_Tick;
            _counterUpdateDebounceTimer.Tick += CounterUpdateDebounceTimer_Tick;
            _counterUpdateDebounceTimer.Start();
        }

        private void CounterUpdateDebounceTimer_Tick(object? sender, EventArgs e)
        {
            _counterUpdateDebounceTimer?.Stop();
            UpdateCounter();
        }

        // Same 150ms lazy-create/stop/resubscribe/start debounce idiom as ScheduleCounterUpdate -
        // Hunspell's Check() is cheap (~5.4ms/7000 words per the library's own benchmarks) but
        // still not worth running synchronously on every keystroke.
        private void ScheduleSpellCheck()
        {
            _spellCheckRequestVersion++;
            _spellCheckDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _spellCheckDebounceTimer.Stop();
            _spellCheckDebounceTimer.Tick -= SpellCheckDebounceTimer_Tick;
            _spellCheckDebounceTimer.Tick += SpellCheckDebounceTimer_Tick;
            _spellCheckDebounceTimer.Start();
        }

        private async void SpellCheckDebounceTimer_Tick(object? sender, EventArgs e)
        {
            _spellCheckDebounceTimer?.Stop();
            await RunSpellCheckPassAsync();
        }

        private async Task RunSpellCheckPassAsync()
        {
            if (!_isSpellCheckEnabled || _isSpellCheckScanning)
                return;

            _isSpellCheckScanning = true;
            try
            {
                var requestVersion = _spellCheckRequestVersion;
                var settings = AppSettingsService.Load();
                var dictionary = string.IsNullOrEmpty(settings.SpellCheckLanguageKey)
                    ? null
                    : await SpellCheckDictionaryService.GetOrLoadWordListAsync(settings.SpellCheckLanguageKey);

                if (dictionary is null)
                {
                    ClearSpellCheckDecorations();
                    return;
                }

                var text = GetDocumentSearchText();
                var textVersion = _editorChangeVersion;
                var customWords = new HashSet<string>(settings.SpellCheckCustomWords, StringComparer.OrdinalIgnoreCase);
                customWords.UnionWith(_sessionIgnoredWords);

                // The scan is pure CPU over the extracted text (and the dictionary is
                // read-only once loaded) - run it off the UI thread so a long note's save
                // does not hitch the editor. The decorations are applied back on the UI
                // thread by the continuation.
                var spans = await Task.Run(
                    () => SpellCheckService.FindMisspelledWordSpans(text, dictionary, customWords));

                if (!_isSpellCheckEnabled)
                    return;

                // A later edit may have happened while Hunspell scanned the snapshot. Its
                // offsets must never be applied to the newer FlowDocument.
                if (textVersion != _editorChangeVersion
                    || requestVersion != _spellCheckRequestVersion)
                {
                    ScheduleSpellCheck();
                    return;
                }

                if (_lastSpellCheckAppliedVersion == textVersion
                    && _lastSpellCheckAppliedSpans is not null
                    && _lastSpellCheckAppliedSpans.SequenceEqual(spans))
                    return;

                ApplySpellCheckDecorations(spans);
                _lastSpellCheckAppliedSpans = spans.ToArray();
                _lastSpellCheckAppliedVersion = textVersion;
            }
            finally
            {
                _isSpellCheckScanning = false;
            }
        }

        private void ApplySpellCheckDecorations(IReadOnlyList<SpellCheckService.WordSpan> spans)
        {
            // The decoration mutations fire TextChanged, which can trigger the RichTextBox's
            // caret-following - the debounced re-decoration pass that runs after an auto-save
            // would otherwise yank the view to the caret, away from a photo deep in the pad.
            var viewState = _floatingImages.CaptureViewState();
            try
            {
                ApplySpellCheckDecorationsCore(spans);
            }
            finally
            {
                _floatingImages.RestoreViewState(viewState);
            }
        }

        private void ApplySpellCheckDecorationsCore(IReadOnlyList<SpellCheckService.WordSpan> spans)
        {
            _isApplyingSpellCheckDecorations = true;
            try
            {
                ClearSpellCheckDecorations();

                var runs = BuildTextRunIndex();
                foreach (var span in spans)
                {
                    var start = PointerAtTextOffset(runs, span.Start);
                    var end = PointerAtTextOffset(runs, span.Start + span.Length);
                    if (start is null || end is null)
                        continue;

                    var range = new TextRange(start, end);
                    SetRangeTextDecoration(range, SpellCheckSquiggleDecoration, enabled: true);
                    _spellCheckDecoratedRanges.Add(range);
                }
            }
            finally
            {
                _isApplyingSpellCheckDecorations = false;
                SchedulePendingFindRefresh();
            }
        }

        /// <summary>Clears squiggles range-by-range using the exact TextRange objects they were
        /// applied to (TextPointers auto-track document edits elsewhere), rather than one call
        /// across the whole document - SetRangeTextDecoration's read-current-then-diff logic
        /// assumes a uniform range, which a mixed clear-everything range is not.</summary>
        private void ClearSpellCheckDecorations()
        {
            _lastSpellCheckAppliedSpans = null;
            _lastSpellCheckAppliedVersion = -1;
            if (_spellCheckDecoratedRanges.Count == 0)
                return;

            var wasApplying = _isApplyingSpellCheckDecorations;
            _isApplyingSpellCheckDecorations = true;
            try
            {
                foreach (var range in _spellCheckDecoratedRanges)
                    SetRangeTextDecoration(range, SpellCheckSquiggleDecoration, enabled: false);

                _spellCheckDecoratedRanges.Clear();
            }
            finally
            {
                _isApplyingSpellCheckDecorations = wasApplying;
                SchedulePendingFindRefresh();
            }
        }

        /// <summary>Right-clicking a squiggle-underlined word swaps in a dynamic suggestions/
        /// Ignore/Add-to-Dictionary menu, mirroring how MindMapPreviewWindow builds per-node
        /// context menus in code-behind; right-clicking anywhere else restores the static
        /// Cut/Copy/Paste menu captured at startup.</summary>
        private void ContentTextBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var point = Mouse.GetPosition(ContentTextBox);
            var spellRange = FindSpellCheckRangeAtPoint(point);
            if (spellRange is not null)
            {
                ContentTextBox.ContextMenu = BuildSpellCheckContextMenu(spellRange);
                return;
            }

            var pointer = ContentTextBox.GetPositionFromPoint(point, snapToText: true)
                ?? ContentTextBox.CaretPosition;
            var cell = FindAncestorTableCell(pointer);
            if (cell is null)
                SyncPasteOffers(_defaultContentTextBoxContextMenu);
            ContentTextBox.ContextMenu = cell is not null
                ? BuildTableContextMenu(cell)
                : _defaultContentTextBoxContextMenu;
        }

        private TextRange? FindSpellCheckRangeAtPoint(Point point)
        {
            var pointer = ContentTextBox.GetPositionFromPoint(point, snapToText: true);
            if (pointer is null)
                return null;

            foreach (var range in _spellCheckDecoratedRanges)
            {
                if (pointer.CompareTo(range.Start) >= 0 && pointer.CompareTo(range.End) <= 0)
                    return range;
            }

            return null;
        }

        private ContextMenu BuildSpellCheckContextMenu(TextRange range)
        {
            var word = range.Text;
            var settings = AppSettingsService.Load();

            // Resolving this synchronously is safe here specifically: a word can only be
            // squiggle-underlined at all if RunSpellCheckPassAsync already loaded the active
            // dictionary, so GetOrLoadWordListAsync is guaranteed to hit its in-memory cache and
            // complete synchronously - not a general sync-over-async pattern.
            var dictionary = string.IsNullOrEmpty(settings.SpellCheckLanguageKey)
                ? null
                : SpellCheckDictionaryService.GetOrLoadWordListAsync(settings.SpellCheckLanguageKey).GetAwaiter().GetResult();

            var suggestions = SpellCheckService.GetSuggestions(word, dictionary);
            var menu = new ContextMenu();

            if (suggestions.Count == 0)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = LocalizationService.GetString("SpellCheckNoSuggestions"),
                    IsEnabled = false
                });
            }
            else
            {
                foreach (var suggestion in suggestions)
                {
                    var suggestionItem = new MenuItem { Header = suggestion };
                    suggestionItem.Click += (_, _) => ReplaceMisspelledWord(range, suggestion);
                    menu.Items.Add(suggestionItem);
                }
            }

            menu.Items.Add(new Separator());

            var ignoreItem = new MenuItem { Header = LocalizationService.GetString("SpellCheckIgnore") };
            ignoreItem.Click += (_, _) => IgnoreMisspelledWordForSession(word);
            menu.Items.Add(ignoreItem);

            var addToDictionaryItem = new MenuItem { Header = LocalizationService.GetString("SpellCheckAddToDictionary") };
            addToDictionaryItem.Click += (_, _) => AddWordToCustomDictionary(word);
            menu.Items.Add(addToDictionaryItem);

            return menu;
        }

        private void ReplaceMisspelledWord(TextRange range, string replacement)
        {
            range.Text = replacement;
            ContentTextBox.Focus();
        }

        private void IgnoreMisspelledWordForSession(string word)
        {
            _sessionIgnoredWords.Add(word);
            ScheduleSpellCheck();
        }

        private void AddWordToCustomDictionary(string word)
        {
            var settings = AppSettingsService.Load();
            settings.SpellCheckCustomWords ??= new List<string>();
            if (!settings.SpellCheckCustomWords.Contains(word, StringComparer.OrdinalIgnoreCase))
            {
                settings.SpellCheckCustomWords.Add(word);
                AppSettingsService.Save(settings);
            }

            ScheduleSpellCheck();
        }

        private void EditorField_TextChanged(object sender, TextChangedEventArgs e)
        {
            // A late photo-load settle must not acknowledge title/tag edits made after load.
            if (!_isLoadingDocument && _currentDocument is not null) _restoreBaselineHasFieldEdits = true;
            UpdateEditedIndicator();
        }

        private void ContentTextBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Prefer IsKeyDown: Keyboard.Modifiers can lag on the Ctrl key itself.
            if (e.ChangedButton == MouseButton.Left
                && IsCtrlHeldForHyperlinkHints()
                && FindHyperlinkAtPoint(e.GetPosition(ContentTextBox)) is { } hyperlink)
            {
                ActivateHyperlink(hyperlink);
                e.Handled = true;
                return;
            }

            if (e.ChangedButton == MouseButton.Left
                && TryToggleChecklistGlyphAtPoint(e.GetPosition(ContentTextBox)))
            {
                e.Handled = true;
            }
        }

        private void ContentTextBox_HyperlinkRequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            // Always swallow - default navigation of custom schemes / IsDocumentEnabled clicks crashes
            // or opens the OS handler. Ctrl+Click activates via PreviewMouseDown instead.
            e.Handled = true;
        }

        private void MarkEditorContentChanged()
        {
            // Spell-check squiggle application is a real TextDecoration mutation but cosmetic
            // (cleared before every save) - it must not mark the note as edited.
            if (_isLoadingDocument || _isSerializingEditorContent || _suppressEditedMarking
                || _isApplyingSpellCheckDecorations || _wrapLayout?.IsApplying == true)
                return;

            _editorChangeVersion++;
            ScheduleSelectionChromeRefresh();
        }

        private void OnlineSearchButton_Click(object sender, RoutedEventArgs e)
        {
            OpenOnlineSearch();
        }

        private void UpdateOnlineSearchAvailability()
        {
            // Online search was removed from note-editor chrome (v2).
        }

        private string ResolveOnlineSearchQuery()
        {
            var selectedText = ContentTextBox.Selection?.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(selectedText))
                return selectedText;

            return string.Empty;
        }

        private void OpenOnlineSearch()
        {
            var query = ResolveOnlineSearchQuery();
            if (string.IsNullOrWhiteSpace(query))
                return;

            var url = $"https://www.google.com/search?q={Uri.EscapeDataString(query)}";

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("SearchOnlineFailed")}\n\n{ex.Message}",
                    LocalizationService.GetString("Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            if (_findBarOpen)
                CloseFindBar();
            else
                OpenFindBar(focusReplace: false);
        }

        private void OpenFindBar(bool focusReplace)
        {
            var wasOpen = _findBarOpen;
            if (!wasOpen)
            {
                FindMatchCaseToggle.IsChecked = _lastFindMatchCase;
                FindWholeWordToggle.IsChecked = _lastFindWholeWord;
                FindWrapAroundToggle.IsChecked = _lastFindWrapAround;
                FindReplacementBox.Text = _lastReplacementText ?? string.Empty;
                FindQueryBox.Text = ResolveInitialFindText() ?? string.Empty;
                SetFindReplaceRowVisible(focusReplace);
            }
            else if (focusReplace)
            {
                SetFindReplaceRowVisible(true);
            }

            _findBarOpen = true;
            FindReplaceBar.Visibility = Visibility.Visible;
            UpdateFindBarButtons();
            UpdateEditorToolButtonStates();
            RefreshFindHighlights();
            AnimateCollapsingRow(FindReplaceRow, FindReplaceBar, expand: true);

            if (focusReplace)
            {
                FindReplacementBox.SelectAll();
                FindReplacementBox.Focus();
            }
            else
            {
                FindQueryBox.SelectAll();
                FindQueryBox.Focus();
            }
        }

        private void CloseFindBar()
        {
            if (!_findBarOpen && FindReplaceBar.Visibility != Visibility.Visible)
                return;

            RememberFindState();
            _findBarOpen = false;
            _findHighlightDebounceTimer?.Stop();
            UpdateEditorToolButtonStates();
            ClearFindHighlights();
            ReleaseFindCache();
            FindStatusText.Text = LocalizationService.GetString("FindReplaceEnterSearch");
            AnimateCollapsingRow(
                FindReplaceRow,
                FindReplaceBar,
                expand: false,
                onCollapsed: () =>
                {
                    if (!_findBarOpen)
                        FindReplaceBar.Visibility = Visibility.Collapsed;
                });
            ContentTextBox.Focus();
        }

        private void SetFindReplaceRowVisible(bool visible)
        {
            FindReplaceToggle.IsChecked = visible;
            FindReplaceReplaceRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AnimateCollapsingRow(RowDefinition row, FrameworkElement content, bool expand,
            Action? onCollapsed = null, Action? onExpanded = null)
            => UiMotion.AnimateLayoutReveal(content, expand,
                () => { if (expand) onExpanded?.Invoke(); else onCollapsed?.Invoke(); }, row: row);

        private void RememberFindState()
        {
            _lastSearchQuery = FindQueryBox.Text;
            _lastReplacementText = FindReplacementBox.Text;
            _lastFindMatchCase = FindMatchCaseToggle.IsChecked == true;
            _lastFindWholeWord = FindWholeWordToggle.IsChecked == true;
            _lastFindWrapAround = FindWrapAroundToggle.IsChecked == true;
        }

        private void UpdateFindBarButtons()
        {
            var hasSearch = !string.IsNullOrEmpty(FindQueryBox.Text);
            FindPreviousButton.IsEnabled = hasSearch;
            FindNextButton.IsEnabled = hasSearch;
            ReplaceButton.IsEnabled = hasSearch;
            ReplaceAllButton.IsEnabled = hasSearch;
        }

        private void ScheduleFindHighlights()
        {
            _findHighlightDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _findHighlightDebounceTimer.Stop();
            _findHighlightDebounceTimer.Tick -= FindHighlightDebounceTimer_Tick;
            _findHighlightDebounceTimer.Tick += FindHighlightDebounceTimer_Tick;
            _findHighlightDebounceTimer.Start();
        }

        private void SchedulePendingFindRefresh()
        {
            if (!_findRefreshPending || !_findBarOpen || _findMutationDepth > 0
                || _isApplyingSpellCheckDecorations || _isSerializingEditorContent)
                return;

            _findRefreshPending = false;
            ScheduleFindHighlights();
        }

        private void FindHighlightDebounceTimer_Tick(object? sender, EventArgs e)
        {
            _findHighlightDebounceTimer?.Stop();
            if (_findBarOpen)
                RefreshFindHighlights();
        }

        private void RefreshFindHighlights()
        {
            _findHighlightDebounceTimer?.Stop();
            if (_findMutationDepth > 0 || _isSerializingEditorContent)
            {
                _findRefreshPending = true;
                return;
            }

            var query = FindQueryBox.Text ?? string.Empty;
            if (string.IsNullOrEmpty(query))
            {
                ClearFindHighlights();
                FindStatusText.Text = LocalizationService.GetString("FindReplaceEnterSearch");
                return;
            }

            var (index, matches) = GetOrBuildFindMatches(
                query,
                FindMatchCaseToggle.IsChecked == true,
                FindWholeWordToggle.IsChecked == true);
            HighlightMatches(index, matches, activeIndex: -1);
            FindStatusText.Text = matches.Count == 0
                ? LocalizationService.GetString("FindReplaceNoMatches")
                : string.Format(LocalizationService.GetString("FindReplaceMatchesFormat"), matches.Count);
        }

        private void ApplyFindStatus(FindReplaceResult result)
        {
            if (result.ReplacedCount > 0 && result.MatchCount == 0)
            {
                FindStatusText.Text = string.Format(
                    LocalizationService.GetString("FindReplaceReplacedCountFormat"),
                    result.ReplacedCount);
                return;
            }

            var prefix = result.ReplacedCount > 0
                ? string.Format(LocalizationService.GetString("FindReplaceReplacedCountFormat"), result.ReplacedCount) + " - "
                : string.Empty;

            if (string.IsNullOrEmpty(FindQueryBox.Text))
            {
                FindStatusText.Text = LocalizationService.GetString("FindReplaceEnterSearch");
                return;
            }

            if (result.MatchCount == 0)
            {
                FindStatusText.Text = prefix + LocalizationService.GetString("FindReplaceNoMatches");
                return;
            }

            if (!result.Found)
            {
                FindStatusText.Text = prefix + string.Format(
                    LocalizationService.GetString("FindReplaceMatchesFormat"),
                    result.MatchCount);
                return;
            }

            var key = result.Wrapped
                ? "FindReplaceWrappedResultFormat"
                : "FindReplaceResultFormat";
            FindStatusText.Text = prefix + string.Format(
                LocalizationService.GetString(key),
                result.ActiveIndex + 1,
                result.MatchCount);
        }

        private void FindQueryBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_findBarOpen)
                return;

            RememberFindState();
            UpdateFindBarButtons();
            ClearAllHighlights();
            if (string.IsNullOrEmpty(FindQueryBox.Text))
                RefreshFindHighlights();
            else
                ScheduleFindHighlights();
        }

        private void FindReplacementBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_findBarOpen)
                RememberFindState();
        }

        private void FindOptions_Changed(object sender, RoutedEventArgs e)
        {
            if (!_findBarOpen || FindQueryBox == null)
                return;

            RememberFindState();
            RefreshFindHighlights();
        }

        private void FindReplaceToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!_findBarOpen || FindReplaceReplaceRow == null)
                return;

            FindReplaceReplaceRow.Visibility = FindReplaceToggle.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
            AnimateCollapsingRow(FindReplaceRow, FindReplaceBar, expand: true);
        }

        private void FindQueryBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Shift)
            {
                FindPreviousButton_Click(sender, e);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                FindNextButton_Click(sender, e);
                e.Handled = true;
            }
        }

        private void FindReplacementBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                ReplaceButton_Click(sender, e);
                e.Handled = true;
            }
        }

        private void FindPreviousButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(FindQueryBox.Text))
                return;

            RememberFindState();
            ApplyFindStatus(PerformFindPrevious(
                FindQueryBox.Text,
                FindMatchCaseToggle.IsChecked == true,
                FindWholeWordToggle.IsChecked == true,
                FindWrapAroundToggle.IsChecked == true));
        }

        private void FindNextButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(FindQueryBox.Text))
                return;

            RememberFindState();
            ApplyFindStatus(PerformFindNext(
                FindQueryBox.Text,
                FindMatchCaseToggle.IsChecked == true,
                FindWholeWordToggle.IsChecked == true,
                FindWrapAroundToggle.IsChecked == true));
        }

        private void ReplaceButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(FindQueryBox.Text))
                return;

            RememberFindState();
            var result = PerformReplace(
                FindQueryBox.Text,
                FindReplacementBox.Text,
                FindMatchCaseToggle.IsChecked == true,
                FindWholeWordToggle.IsChecked == true);
            if (result is not null)
                ApplyFindStatus(result);
            UpdateFindBarButtons();
        }

        private void ReplaceAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(FindQueryBox.Text))
                return;

            RememberFindState();
            ApplyFindStatus(PerformReplaceAll(
                FindQueryBox.Text,
                FindReplacementBox.Text,
                FindMatchCaseToggle.IsChecked == true,
                FindWholeWordToggle.IsChecked == true));
            UpdateFindBarButtons();
        }

        private void CloseFindBarButton_Click(object sender, RoutedEventArgs e)
            => CloseFindBar();

        private bool TryHandleFindReplaceKey(KeyEventArgs e)
        {
            if (e.Handled)
                return false;

            if (_findBarOpen && e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
            {
                CloseFindBar();
                e.Handled = true;
                return true;
            }

            var isCtrlOnly = IsCtrlHeldForHyperlinkHints();
            if (isCtrlOnly && e.Key == Key.F)
            {
                OpenFindBar(focusReplace: false);
                e.Handled = true;
                return true;
            }

            if (isCtrlOnly && e.Key == Key.H)
            {
                OpenFindBar(focusReplace: true);
                e.Handled = true;
                return true;
            }

            if (e.Key == Key.F3 && !string.IsNullOrWhiteSpace(_lastSearchQuery))
            {
                var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                var result = shift
                    ? PerformFindPrevious(_lastSearchQuery, _lastFindMatchCase, _lastFindWholeWord, _lastFindWrapAround)
                    : PerformFindNext(_lastSearchQuery, _lastFindMatchCase, _lastFindWholeWord, _lastFindWrapAround);
                if (_findBarOpen)
                    ApplyFindStatus(result);
                e.Handled = true;
                return true;
            }

            return false;
        }

        private string? ResolveInitialFindText()
        {
            var start = _selectionCollapsedForScroll
                ? _heldSelectionStart : ContentTextBox.Selection?.Start;
            var end = _selectionCollapsedForScroll
                ? _heldSelectionEnd : ContentTextBox.Selection?.End;
            if (start is null || end is null || start.CompareTo(end) >= 0)
                return _lastSearchQuery;

            // WPF materializes Selection.Text before Length can be checked. Bound the
            // TextRange itself so Ctrl+F on a page-spanning selection stays cheap.
            var limit = start.GetPositionAtOffset(161, LogicalDirection.Forward);
            if (limit is not null && limit.CompareTo(end) < 0)
                return _lastSearchQuery;

            var selectedText = new TextRange(start, end).Text.Trim();
            if (!string.IsNullOrWhiteSpace(selectedText)
                && !selectedText.Contains('\r')
                && !selectedText.Contains('\n')
                && selectedText.Length <= 160)
            {
                return selectedText;
            }

            return _lastSearchQuery;
        }

        private void NoteEditorWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (TryHandleTableKey(e)) return;
            UpdateHyperlinkCtrlHintsFromKeyEvent(e, isDown: true);

            if (TryHandleFloatingImageKey(e))
                return;

            if (TryHandleChecklistKey(e))
                return;

            if (TryHandleEditorTabKey(e))
                return;
        }

        private void NoteEditorWindow_PreviewKeyUp(object sender, KeyEventArgs e)
            => UpdateHyperlinkCtrlHintsFromKeyEvent(e, isDown: false);

        private void RootGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (TryHandleTableKey(e)) return;
            if (TryHandleChecklistKey(e)) return;
            if (_selectionCollapsedForScroll
                && Keyboard.FocusedElement is DependencyObject focused
                && (ReferenceEquals(focused, ContentTextBox) || IsEditorDescendant(focused))
                && Keyboard.Modifiers == ModifierKeys.None
                && e.Key is Key.Left or Key.Right)
            {
                var endpoint = e.Key == Key.Left ? _heldSelectionStart : _heldSelectionEnd;
                DropParkedSelection();
                if (endpoint is not null)
                    ReplaceEditorSelection(endpoint, endpoint);
                InvalidateSelectionTiles();
                QueueSelectionOverlay();
                e.Handled = true;
                return;
            }

            var handlesFind = (Keyboard.Modifiers == ModifierKeys.Control
                && e.Key is Key.F or Key.H)
                || (_findBarOpen && e.Key == Key.Escape)
                || (e.Key == Key.F3 && !string.IsNullOrWhiteSpace(_lastSearchQuery));
            var nonSelectionChrome = e.OriginalSource is DependencyObject keySource
                && IsNonSelectionEditorChrome(keySource);
            if (!handlesFind && !nonSelectionChrome
                && e.Key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt))
                MaterializeSelectionForAction();

            if (TryHandleFindReplaceKey(e))
                return;

            TryHandleFloatingImageKey(e);
        }

        private void RootGrid_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle)
                return;

            if (e.OriginalSource is DependencyObject source && IsEditorScrollbarSource(source))
                return;

            if (e.ChangedButton == MouseButton.Left
                && e.OriginalSource is DependencyObject editorSource
                && IsEditorDescendant(editorSource))
            {
                ClearParkedSelection();
                return;
            }

            if (e.ChangedButton == MouseButton.Left
                && e.OriginalSource is DependencyObject textFieldSource
                && (IsDescendantOf(textFieldSource, TitleTextBox)
                    || IsDescendantOf(textFieldSource, TagsTextBox)))
            {
                ClearParkedSelection();
                return;
            }

            if (e.OriginalSource is DependencyObject otherSource
                && IsNonSelectionEditorChrome(otherSource))
                return;

            MaterializeSelectionForAction();
        }

        private void ClearParkedSelection()
        {
            if (!_selectionCollapsedForScroll)
                return;

            var clearStarted = ScrollPerfDiag.Enabled ? Stopwatch.GetTimestamp() : 0;
            DropParkedSelection();
            InvalidateSelectionTiles();
            QueueSelectionOverlay();
            if (ScrollPerfDiag.Enabled)
                ScrollPerfDiag.LogEditorSelectionClear(Stopwatch.GetElapsedTime(clearStarted).TotalMilliseconds);
        }

        private bool IsEditorDescendant(DependencyObject source)
            => IsDescendantOf(source, ContentTextBox);

        private bool IsNonSelectionEditorChrome(DependencyObject source)
            => IsDescendantOf(source, SearchButton)
                || IsDescendantOf(source, FindReplaceBar)
                || IsDescendantOf(source, TitleTextBox)
                || IsDescendantOf(source, TagsTextBox);

        private static bool IsDescendantOf(DependencyObject source, DependencyObject ancestor)
        {
            for (DependencyObject? current = source; current is not null;)
            {
                if (ReferenceEquals(current, ancestor))
                    return true;

                current = current is Visual
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }

            return false;
        }

        private void RootGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                return;

            if (EditorSurface.IsMouseOver || EditorMirrorVerticalScrollBar.IsMouseOver
                || EditorMirrorHorizontalScrollBar.IsMouseOver)
                HoldSelectionForScroll();
        }

        private bool IsEditorScrollbarSource(DependencyObject source)
            => ReferenceEquals(source, EditorMirrorVerticalScrollBar)
                || ReferenceEquals(source, EditorMirrorHorizontalScrollBar)
                || (source is Visual visual
                    && (EditorMirrorVerticalScrollBar.IsAncestorOf(visual)
                        || EditorMirrorHorizontalScrollBar.IsAncestorOf(visual)));

        private void ContentTextBox_PreviewSelectionCommand(object sender, ExecutedRoutedEventArgs e)
            => MaterializeSelectionForAction();

        /// <summary>
        /// Handles Escape/Delete/Back for a selected floating image.
        /// Selection chrome is the gate — RTB often keeps keyboard focus after image mouse-down.
        /// Public so <see cref="EditorWorkspaceWindow"/> can forward keys when content is hosted.
        /// </summary>
        public bool TryHandleFloatingImageKey(KeyEventArgs e)
        {
            if (e.Handled
                || !_floatingImages.HasSelection
                || e.Key is not (Key.Escape or Key.Delete or Key.Back))
            {
                return false;
            }

            return _floatingImages.TryHandleKeyDown(e);
        }

        private bool IsEditorKeyboardFocusWithin(object? eventSource = null)
        {
            if (eventSource != null)
            {
                if (ReferenceEquals(eventSource, ContentTextBox))
                    return true;
                if (eventSource is DependencyObject src && ContentTextBox.IsAncestorOf(src))
                    return true;
            }

            if (ContentTextBox.IsKeyboardFocusWithin)
                return true;

            return ReferenceEquals(Keyboard.FocusedElement, ContentTextBox);
        }

        public bool TryHandleEditorTabKey(KeyEventArgs e)
        {
            if (e.Handled
                || !IsTabKey(e)
                || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0
                || !IsEditorKeyboardFocusWithin(e.OriginalSource))
            {
                return false;
            }

            if (!HandleEditorTabKey((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift))
                return false;

            e.Handled = true;
            return true;
        }

        private bool IsAnyAiConversionInProgress()
        {
            return _isConvertingToFlashcards || _isConvertingToMindMap || _isConvertingToTest;
        }

        private CancellationTokenSource? GetActiveCancellationSource()
        {
            if (_isConvertingToFlashcards) return _flashcardConversionCancellationSource;
            if (_isConvertingToMindMap) return _mindMapConversionCancellationSource;
            if (_isConvertingToTest) return _testConversionCancellationSource;
            return null;
        }

        private void AiCancelButton_Click(object sender, RoutedEventArgs e)
        {
            var cts = GetActiveCancellationSource();
            if (cts is null)
                return;

            cts.Cancel();

            // Cancelling doesn't stop the worker mid-generation (it just discards the eventual
            // response - see LLamaSharpWorkerAdapter's RequestId filtering), so there can be a
            // real gap between this click and the status indicator actually clearing. Disable
            // (the style dims disabled buttons, see EditorDangerToolButtonStyle's IsEnabled
            // trigger) and swap the tooltip immediately so the click visibly registered instead
            // of looking inert or inviting a repeat click. The button is icon-only, so feedback
            // goes through ToolTip/opacity rather than resizing button text to fit new content.
            AiCancelButton.IsEnabled = false;
            AiCancelButton.ToolTip = "Cancelling…";
        }

        /// Shown at the start of a conversion, hidden unconditionally in that conversion's
        /// `finally` block - deliberately not tied to the generic status-indicator show/hide
        /// helpers below, since those are also used for non-cancelable messages (e.g. "Version
        /// restored") that have no business offering a Cancel button.
        private void ShowAiCancelButton()
        {
            AiCancelButton.ToolTip = "Cancel";
            AiCancelButton.IsEnabled = true;
            AiCancelButton.Visibility = Visibility.Visible;
            AnimateStatusIndicatorRow(expand: true);
        }

        private void HideAiCancelButton()
        {
            AiCancelButton.Visibility = Visibility.Collapsed;
            if (StatusIndicatorText.Visibility != Visibility.Visible) AnimateStatusIndicatorRow(expand: false);
        }

        private void SetAiConversionButtonsEnabled(bool isEnabled)
        {
            if (!AiFeatureGate.IsEnabled)
            {
                ConvertMenuButton.IsEnabled = false;
                return;
            }

            ConvertMenuButton.IsEnabled = isEnabled;
        }

        private void ApplyAiConvertVisibility()
        {
            if (ConvertMenuButton is null)
                return;

            var enabled = AiFeatureGate.IsEnabled;
            ConvertMenuButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            if (!enabled)
            {
                ConvertMenuButton.IsEnabled = false;
                CancelInFlightAiConversion();
                return;
            }

            if (!IsAnyAiConversionInProgress())
                ConvertMenuButton.IsEnabled = !IsAiGenerationInProgress;
        }

        private void CancelInFlightAiConversion()
        {
            if (!IsAnyAiConversionInProgress())
                return;

            GetActiveCancellationSource()?.Cancel();
        }

        private void NoteEditorWindow_AiFeatureGateChanged(object? sender, EventArgs e)
        {
            if (_isDisposed)
                return;

            Dispatcher.Invoke(ApplyAiConvertVisibility);
        }

        /// With app-scoped AiBackendHost, only one worker process exists and its SemaphoreSlim
        /// serializes generations; this still mirrors the global IsAiGenerationInProgress flag
        /// onto every OTHER tab's Convert button so a second generation can't be queued while
        /// one is running. The actively-converting tab manages its own button directly in its
        /// click handler/finally block, so it's skipped here to avoid fighting that.
        private void NoteEditorWindow_AiGenerationStateChanged(object? sender, EventArgs e)
        {
            if (_isDisposed || IsAnyAiConversionInProgress())
                return;

            SetAiConversionButtonsEnabled(!IsAiGenerationInProgress);
        }

        private void ConvertMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (!AiFeatureGate.IsEnabled)
                return;

            UpdateStudyCoverageMenuChecks();
            if (sender is Button { ContextMenu: { } menu } button)
                UiMotion.OpenContextMenu(menu, button);
        }

        private void StudyCoverageMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string tag }
                || !Enum.TryParse<StudyCoverageMode>(tag, ignoreCase: true, out var coverage))
                return;

            var settings = AppSettingsService.Load();
            settings.StudyCoverage = StudyPlanCoordinator.NormalizeCoverage(coverage);
            AppSettingsService.Save(settings);
            UpdateStudyCoverageMenuChecks();
        }

        private void UpdateStudyCoverageMenuChecks()
        {
            var coverage = StudyPlanCoordinator.NormalizeCoverage(AppSettingsService.Load().StudyCoverage);
            StudyCoverageAutoMenuItem.IsChecked = coverage == StudyCoverageMode.Auto;
            StudyCoverageBalancedMenuItem.IsChecked = coverage == StudyCoverageMode.Balanced;
            StudyCoverageThoroughMenuItem.IsChecked = coverage == StudyCoverageMode.Thorough;
        }

        private void FileMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { ContextMenu: { } menu } button)
                UiMotion.OpenContextMenu(menu, button);
        }

        private void ZoomMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { ContextMenu: { } menu } button)
                UiMotion.OpenContextMenu(menu, button);
        }

        private async void ConvertToFlashcardsButton_Click(object sender, RoutedEventArgs e)
        {
            if (!AiFeatureGate.IsEnabled)
                return;

            if (IsAnyAiConversionInProgress() || IsAiGenerationInProgress)
                return;

            var plainText = GetEditorAiText();
            if (string.IsNullOrWhiteSpace(plainText))
            {
                ModernMessageBox.Show(
                    LocalizationService.GetString("ConvertToFlashcardsEmpty"),
                    LocalizationService.GetString("Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (!TryConfirmSelectedAiModelDownload(out var selectedModel))
                return;

            _isConvertingToFlashcards = true;
            BeginAiGeneration();
            SetAiConversionButtonsEnabled(false);
            ShowAiCancelButton();
            ShowPersistentStatusIndicator(LocalizationService.GetString("ConvertToFlashcardsInProgress"));

            var cts = new CancellationTokenSource();
            _flashcardConversionCancellationSource = cts;

            int? flashcardsSuccessfulPacketCount = null;
            int? flashcardsTotalPacketCount = null;
            var acceptProgress = true;
            var progress = new Progress<AiGenerationProgress>(status =>
            {
                if (_isDisposed || !acceptProgress) return;
                if (status.SuccessfulPacketCount.HasValue)
                {
                    flashcardsSuccessfulPacketCount = status.SuccessfulPacketCount;
                    flashcardsTotalPacketCount = status.TotalPacketCount;
                }

                var text = BuildAiProgressText(status, null, null, out var useAnimation);

                if (useAnimation)
                    ShowPersistentStatusIndicator(text, pulse: true);
                else
                    ShowPersistentStatusIndicatorWithoutAnimation(text);
            });

            void CloseAiStatus()
            {
                acceptProgress = false;
                DismissStatusIndicator();
            }

            var restoreAutoSave = false;
            try
            {
                restoreAutoSave = TemporarilyDisableAutoSaveForAi();
                await EnsureAiModelAvailableAsync(selectedModel, progress, cts.Token);
                var documentTitle = ResolveEditorTitleForPrompt();
                var studyContext = await _studyPlanCoordinator.GetOrCreateAsync(
                    plainText,
                    selectedModel.Key,
                    AppSettingsService.Load().StudyCoverage,
                    progress,
                    cts.Token);
                var flashcards = await _flashcardConversionService.ConvertToFlashcardsAsync(
                    studyContext,
                    progress,
                    cts.Token);
                RestoreAutoSaveAfterAi(ref restoreAutoSave);

                if (_isDisposed) return;

                if (flashcards.Count == 0)
                {
                    CloseAiStatus();
                    ModernMessageBox.Show(
                        LocalizationService.GetString("ConvertToFlashcardsParseFailed"),
                        LocalizationService.GetString("Error"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                var modelDisplayName = BundledModelHostService.Instance.GetSelectedModelDisplayName();
                var flashcardsMainWindow = Application.Current.MainWindow as MainWindow;

                flashcardsMainWindow?.EditorWorkspace.OpenFlashcards(new FlashcardSetDocument
                {
                    Cards = flashcards.ToList(), Title = documentTitle, Tags = ParseTags(TagsTextBox.Text).ToList(),
                    AiModelDisplayName = modelDisplayName, SourceNoteId = _currentDocument?.Id
                }, draft: true);

                ShowStatusIndicator(BuildAiSuccessText(
                    "ConvertToFlashcardsSuccess",
                    "ConvertToFlashcardsSuccessPartialFormat",
                    flashcardsSuccessfulPacketCount,
                    flashcardsTotalPacketCount,
                    flashcards.Count,
                    studyContext.TargetCount));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                CloseAiStatus();
            }
            catch (OperationCanceledException ex) when (!_isDisposed)
            {
                CloseAiStatus();
                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("ConvertToFlashcardsFailed")}\n\n{ex.Message}",
                    LocalizationService.GetString("Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (AiInputRejectedException ex) when (!_isDisposed)
            {
                CloseAiStatus();
                ModernMessageBox.Show(
                    ex.Message,
                    LocalizationService.GetString("AiInputRejectedTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (TimeoutException ex) when (!_isDisposed)
            {
                CloseAiStatus();
                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("AiGenerationTimedOut")}\n\n{ex.Message}",
                    LocalizationService.GetString("Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (AiModelDownloadException ex) when (!_isDisposed)
            {
                CloseAiStatus();
                ModernDialog.ShowError(GetDialogOwnerWindow(), LocalizationService.GetString("Error"), ex.Message);
            }
            catch (Exception ex) when (!_isDisposed)
            {
                CloseAiStatus();
                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("ConvertToFlashcardsFailed")}\n\n{ex.Message}",
                    LocalizationService.GetString("Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                RestoreAutoSaveAfterAi(ref restoreAutoSave);
                _isConvertingToFlashcards = false;
                EndAiGeneration();
                SetAiConversionButtonsEnabled(true);
                HideAiCancelButton();
                cts.Dispose();
                if (_flashcardConversionCancellationSource == cts)
                    _flashcardConversionCancellationSource = null;
            }
        }

        private async void ConvertToMindMapButton_Click(object sender, RoutedEventArgs e)
        {
            if (!AiFeatureGate.IsEnabled)
                return;

            if (IsAnyAiConversionInProgress() || IsAiGenerationInProgress)
                return;

            var plainText = GetEditorAiText();
            if (string.IsNullOrWhiteSpace(plainText))
            {
                ModernMessageBox.Show(
                    LocalizationService.GetString("ConvertToMindMapEmpty"),
                    LocalizationService.GetString("Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (!TryConfirmSelectedAiModelDownload(out var selectedModel))
                return;

            _isConvertingToMindMap = true;
            BeginAiGeneration();
            SetAiConversionButtonsEnabled(false);
            ShowAiCancelButton();
            ShowPersistentStatusIndicator(LocalizationService.GetString("ConvertToMindMapInProgress"));

            var cts = new CancellationTokenSource();
            _mindMapConversionCancellationSource = cts;

            int? mindMapSuccessfulPacketCount = null;
            int? mindMapTotalPacketCount = null;
            var acceptMindMapProgress = true;
            var progress = new Progress<AiGenerationProgress>(status =>
            {
                if (_isDisposed || !acceptMindMapProgress) return;
                if (status.SuccessfulPacketCount.HasValue)
                {
                    mindMapSuccessfulPacketCount = status.SuccessfulPacketCount;
                    mindMapTotalPacketCount = status.TotalPacketCount;
                }

                var text = BuildAiProgressText(
                    status,
                    processingStatusKey: "ConvertToMindMapStatusProcessing",
                    finalizingStatusKey: "ConvertToMindMapStatusFinalizing",
                    out var useAnimation);

                if (useAnimation)
                    ShowPersistentStatusIndicator(text, pulse: true);
                else
                    ShowPersistentStatusIndicatorWithoutAnimation(text);
            });

            void CloseMindMapStatus()
            {
                acceptMindMapProgress = false;
                DismissStatusIndicator();
            }

            var restoreAutoSave = false;
            try
            {
                restoreAutoSave = TemporarilyDisableAutoSaveForAi();
                await EnsureAiModelAvailableAsync(selectedModel, progress, cts.Token);
                var documentTitle = ResolveEditorTitleForPrompt();
                var studyContext = await _studyPlanCoordinator.GetOrCreateAsync(
                    plainText,
                    selectedModel.Key,
                    AppSettingsService.Load().StudyCoverage,
                    progress,
                    cts.Token);
                var mindMap = await _mindMapConversionService.ConvertToMindMapAsync(
                    documentTitle,
                    studyContext,
                    progress,
                    cts.Token);
                RestoreAutoSaveAfterAi(ref restoreAutoSave);

                if (_isDisposed) return;

                if (mindMap is null || mindMap.Children.Count == 0)
                {
                    CloseMindMapStatus();
                    ModernMessageBox.Show(
                        LocalizationService.GetString("ConvertToMindMapParseFailed"),
                        LocalizationService.GetString("Error"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                var modelDisplayName = BundledModelHostService.Instance.GetSelectedModelDisplayName();

                // The workspace supplies the shared note-link options and opens the result as a draft.
                var mainWindow = Application.Current.MainWindow as MainWindow;

                mainWindow?.EditorWorkspace.OpenMindMap(new MindMapDocument
                {
                    Root = mindMap, Title = documentTitle, Tags = ParseTags(TagsTextBox.Text).ToList(),
                    AiModelDisplayName = modelDisplayName, SourceNoteId = _currentDocument?.Id
                }, draft: true);

                ShowStatusIndicator(BuildAiSuccessText(
                    "ConvertToMindMapSuccess",
                    "ConvertToMindMapSuccessPartialFormat",
                    mindMapSuccessfulPacketCount,
                    mindMapTotalPacketCount,
                    studyContext.TargetCount,
                    studyContext.TargetCount));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                CloseMindMapStatus();
            }
            catch (OperationCanceledException ex) when (!_isDisposed)
            {
                CloseMindMapStatus();
                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("ConvertToMindMapFailed")}\n\n{ex.Message}",
                    LocalizationService.GetString("Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (AiInputRejectedException ex) when (!_isDisposed)
            {
                CloseMindMapStatus();
                ModernMessageBox.Show(
                    ex.Message,
                    LocalizationService.GetString("AiInputRejectedTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (TimeoutException ex) when (!_isDisposed)
            {
                CloseMindMapStatus();
                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("AiGenerationTimedOut")}\n\n{ex.Message}",
                    LocalizationService.GetString("Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (AiModelDownloadException ex) when (!_isDisposed)
            {
                CloseMindMapStatus();
                ModernDialog.ShowError(GetDialogOwnerWindow(), LocalizationService.GetString("Error"), ex.Message);
            }
            catch (Exception ex) when (!_isDisposed)
            {
                CloseMindMapStatus();
                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("ConvertToMindMapFailed")}\n\n{ex.Message}",
                    LocalizationService.GetString("Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                RestoreAutoSaveAfterAi(ref restoreAutoSave);
                _isConvertingToMindMap = false;
                EndAiGeneration();
                SetAiConversionButtonsEnabled(true);
                HideAiCancelButton();
                cts.Dispose();
                if (_mindMapConversionCancellationSource == cts)
                    _mindMapConversionCancellationSource = null;
            }
        }

        private async void ConvertToTestButton_Click(object sender, RoutedEventArgs e)
        {
            if (!AiFeatureGate.IsEnabled)
                return;

            if (IsAnyAiConversionInProgress() || IsAiGenerationInProgress)
                return;

            var plainText = GetEditorAiText();
            if (string.IsNullOrWhiteSpace(plainText))
            {
                ModernMessageBox.Show(
                    LocalizationService.GetString("ConvertToTestEmpty"),
                    LocalizationService.GetString("Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (!TryConfirmSelectedAiModelDownload(out var selectedModel))
                return;

            _isConvertingToTest = true;
            BeginAiGeneration();
            SetAiConversionButtonsEnabled(false);
            ShowAiCancelButton();
            ShowPersistentStatusIndicator(LocalizationService.GetString("ConvertToTestInProgress"));

            var cts = new CancellationTokenSource();
            _testConversionCancellationSource = cts;

            int? quizSuccessfulPacketCount = null;
            int? quizTotalPacketCount = null;
            var acceptQuizProgress = true;
            var progress = new Progress<AiGenerationProgress>(status =>
            {
                if (_isDisposed || !acceptQuizProgress) return;
                if (status.SuccessfulPacketCount.HasValue)
                {
                    quizSuccessfulPacketCount = status.SuccessfulPacketCount;
                    quizTotalPacketCount = status.TotalPacketCount;
                }

                var text = BuildAiProgressText(
                    status,
                    processingStatusKey: "ConvertToTestStatusProcessing",
                    finalizingStatusKey: "ConvertToTestStatusFinalizing",
                    out var useAnimation);

                if (useAnimation)
                    ShowPersistentStatusIndicator(text, pulse: true);
                else
                    ShowPersistentStatusIndicatorWithoutAnimation(text);
            });

            void CloseQuizStatus()
            {
                acceptQuizProgress = false;
                DismissStatusIndicator();
            }

            var restoreAutoSave = false;
            try
            {
                restoreAutoSave = TemporarilyDisableAutoSaveForAi();
                await EnsureAiModelAvailableAsync(selectedModel, progress, cts.Token);
                var documentTitle = ResolveEditorTitleForPrompt();
                var studyContext = await _studyPlanCoordinator.GetOrCreateAsync(
                    plainText,
                    selectedModel.Key,
                    AppSettingsService.Load().StudyCoverage,
                    progress,
                    cts.Token);
                var quiz = await _quizConversionService.ConvertToQuizAsync(
                    documentTitle,
                    studyContext,
                    _currentDocument?.Id,
                    progress,
                    cts.Token);
                RestoreAutoSaveAfterAi(ref restoreAutoSave);

                if (_isDisposed) return;

                if (quiz is null || quiz.Questions.Count == 0)
                {
                    CloseQuizStatus();
                    ModernMessageBox.Show(
                        LocalizationService.GetString("ConvertToTestParseFailed"),
                        LocalizationService.GetString("Error"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                var modelDisplayName = BundledModelHostService.Instance.GetSelectedModelDisplayName();
                quiz.AiModelDisplayName = modelDisplayName;
                quiz.SourceNoteId = _currentDocument?.Id;
                quiz.Tags = ParseTags(TagsTextBox.Text).ToList();

                var quizMainWindow = Application.Current.MainWindow as MainWindow;

                quiz.Title = documentTitle;
                quizMainWindow?.EditorWorkspace.OpenQuiz(quiz, draft: true);

                ShowStatusIndicator(BuildAiSuccessText(
                    "ConvertToTestSuccess",
                    "ConvertToTestSuccessPartialFormat",
                    quizSuccessfulPacketCount,
                    quizTotalPacketCount,
                    quiz.Questions.Count,
                    studyContext.TargetCount));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                CloseQuizStatus();
            }
            catch (OperationCanceledException ex) when (!_isDisposed)
            {
                CloseQuizStatus();
                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("ConvertToTestFailed")}\n\n{ex.Message}",
                    LocalizationService.GetString("Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (AiInputRejectedException ex) when (!_isDisposed)
            {
                CloseQuizStatus();
                ModernMessageBox.Show(
                    ex.Message,
                    LocalizationService.GetString("AiInputRejectedTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (TimeoutException ex) when (!_isDisposed)
            {
                CloseQuizStatus();
                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("AiGenerationTimedOut")}\n\n{ex.Message}",
                    LocalizationService.GetString("Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (AiModelDownloadException ex) when (!_isDisposed)
            {
                CloseQuizStatus();
                ModernDialog.ShowError(GetDialogOwnerWindow(), LocalizationService.GetString("Error"), ex.Message);
            }
            catch (Exception ex) when (!_isDisposed)
            {
                CloseQuizStatus();
                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("ConvertToTestFailed")}\n\n{ex.Message}",
                    LocalizationService.GetString("Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                RestoreAutoSaveAfterAi(ref restoreAutoSave);
                _isConvertingToTest = false;
                EndAiGeneration();
                SetAiConversionButtonsEnabled(true);
                HideAiCancelButton();
                cts.Dispose();
                if (_testConversionCancellationSource == cts)
                    _testConversionCancellationSource = null;
            }
        }

        private static string BuildAiProgressText(
            AiGenerationProgress status,
            string? processingStatusKey,
            string? finalizingStatusKey,
            out bool useAnimation)
        {
            var baseText = status.Status switch
            {
                AiGenerationStatus.Processing => LocalizationService.GetString(
                    processingStatusKey ?? "ConvertToFlashcardsStatusProcessing"),
                AiGenerationStatus.Finalizing => LocalizationService.GetString(
                    finalizingStatusKey ?? "ConvertToFlashcardsStatusFinalizing"),
                AiGenerationStatus.PreparingAssets => LocalizationService.GetString("ConvertToFlashcardsStatusPreparingAssets"),
                AiGenerationStatus.VerifyingModel => LocalizationService.GetString(BundledModelHostService.ModelVerificationStatusKey),
                AiGenerationStatus.DownloadingModel => LocalizationService.GetString("ConvertToFlashcardsStatusDownloadingModel"),
                AiGenerationStatus.AssetsReady => LocalizationService.GetString("ConvertToFlashcardsStatusAssetsReady"),
                AiGenerationStatus.AnalyzingSource => LocalizationService.GetString("AiStatusAnalyzingStudyFacts"),
                AiGenerationStatus.ValidatingQuestionAnswer => LocalizationService.GetString("AiStatusCheckingQuestionAnswer"),
                AiGenerationStatus.SelectingStudyPoints => LocalizationService.GetString("AiStatusSelectingStudyPoints"),
                AiGenerationStatus.WritingQuestions => LocalizationService.GetString("AiStatusWritingQuestions"),
                AiGenerationStatus.GroundingAnswers => LocalizationService.GetString("AiStatusGroundingAnswers"),
                AiGenerationStatus.FallingBackToCpu => LocalizationService.GetString("AiStatusFallingBackToCpu"),
                _ => ""
            };
            useAnimation = true;

            if (status.PacketIndex.HasValue
                && status.PacketCount.HasValue
                && status.PacketCount.Value > 0)
            {
                var packetCount = Math.Max(1, status.PacketCount.Value);
                var packetIndex = Math.Clamp(status.PacketIndex.Value, 1, packetCount);
                baseText = $"({packetIndex}/{packetCount}) {baseText}";
            }

            if (status.GeneratedChars.HasValue
                && status.Status is AiGenerationStatus.Processing
                    or AiGenerationStatus.ValidatingQuestionAnswer
                    or AiGenerationStatus.SelectingStudyPoints
                    or AiGenerationStatus.WritingQuestions
                    or AiGenerationStatus.GroundingAnswers)
            {
                return string.Format(
                    LocalizationService.GetString("ConvertToFlashcardsStatusGeneratedCharsFormat"),
                    baseText,
                    status.GeneratedChars.Value);
            }

            if (status.Percent.HasValue)
            {
                useAnimation = false;
                return string.Format(LocalizationService.GetString("ConvertToFlashcardsStatusPercentFormat"), baseText, status.Percent.Value);
            }

            return baseText;
        }

        private bool TryConfirmSelectedAiModelDownload(out AiModelInfo selectedModel)
        {
            selectedModel = AiModelManifest.Instance.GetSelectedModel(AppSettingsService.Load());
            if (BundledModelHostService.IsModelDownloaded(selectedModel.Key))
                return true;

            var minimumSizeMb = Math.Max(1, (selectedModel.MinimumBytes + (1024L * 1024L) - 1) / (1024L * 1024L));
            var message = string.Format(
                LocalizationService.GetString("AiModelDownloadRequiredMessageFormat"),
                selectedModel.DisplayName,
                minimumSizeMb);

            return ModernDialog.Show(
                GetDialogOwnerWindow(),
                LocalizationService.GetString("AiModelDownloadRequiredTitle"),
                message,
                ModernDialogTone.Question,
                LocalizationService.GetString("AiModelDownloadAndContinue"),
                LocalizationService.GetString("Cancel"),
                defaultChoice: ModernDialogResult.Cancel) == ModernDialogResult.Primary;
        }

        private static async Task EnsureAiModelAvailableAsync(
            AiModelInfo selectedModel,
            IProgress<AiGenerationProgress> progress,
            CancellationToken cancellationToken)
        {
            progress.Report(new AiGenerationProgress(AiGenerationStatus.PreparingAssets));
            var downloadProgress = new Progress<BundledModelHostService.FlashcardProgress>(status =>
                progress.Report(new AiGenerationProgress(
                    status.StatusKey == BundledModelHostService.ModelVerificationStatusKey
                        ? AiGenerationStatus.VerifyingModel
                        : AiGenerationStatus.DownloadingModel,
                    status.Percent)));

            try
            {
                await BundledModelHostService.Instance.EnsureModelAvailableAsync(
                    selectedModel.Key,
                    downloadProgress,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new AiModelDownloadException(
                    string.Format(
                        LocalizationService.GetString("AiModelDownloadFailedForTaskFormat"),
                        selectedModel.DisplayName,
                        ex.Message),
                    ex);
            }

            progress.Report(new AiGenerationProgress(AiGenerationStatus.AssetsReady));
        }

        private sealed class AiModelDownloadException : Exception
        {
            public AiModelDownloadException(string message, Exception innerException)
                : base(message, innerException)
            {
            }
        }

        /// Plain success text unless the planner covered only some contextual packets; then use
        /// the localized partial-coverage variant so the result is not presented as complete.
        private static string BuildAiSuccessText(
            string successKey,
            string partialFormatKey,
            int? successfulPacketCount,
            int? totalPacketCount,
            int? generatedItemCount = null,
            int? targetItemCount = null)
        {
            string message;
            if (successfulPacketCount.HasValue
                && totalPacketCount.HasValue
                && successfulPacketCount.Value < totalPacketCount.Value)
            {
                message = string.Format(
                    LocalizationService.GetString(partialFormatKey),
                    successfulPacketCount.Value,
                    totalPacketCount.Value);
            }
            else
            {
                message = LocalizationService.GetString(successKey);
            }

            return generatedItemCount.HasValue && targetItemCount.HasValue && targetItemCount.Value > 0
                ? string.Format(
                    LocalizationService.GetString("AiGenerationItemCoverageFormat"),
                    message,
                    generatedItemCount.Value,
                    targetItemCount.Value)
                : message;
        }

        private void ShowPersistentStatusIndicator(string message, bool pulse = false)
        {
            AnimateStatusIndicatorRow(expand: true);
            UiMotion.Clear(StatusIndicatorText, UIElement.OpacityProperty);
            StatusIndicatorText.Text = message;
            StatusIndicatorText.Visibility = Visibility.Visible;
            StatusIndicatorText.Opacity = 0;
            UiMotion.Fade(StatusIndicatorText, 1, 120, UiMotion.OutQuad);

        }

    private void ShowPersistentStatusIndicatorWithoutAnimation(string message)
    {
        AnimateStatusIndicatorRow(expand: true);
        UiMotion.Clear(StatusIndicatorText, UIElement.OpacityProperty);
        StatusIndicatorText.Opacity = 1;
        StatusIndicatorText.Text = message;
        StatusIndicatorText.Visibility = Visibility.Visible;
    }

        private void DismissStatusIndicator()
        {
            UiMotion.Stop(StatusIndicatorText);
            if (StatusIndicatorRow != null)
                UiMotion.Stop(StatusIndicatorRow);
            UiMotion.Clear(StatusIndicatorText, UIElement.OpacityProperty);
            StatusIndicatorText.Opacity = 1;
            StatusIndicatorText.Text = string.Empty;
            StatusIndicatorText.Visibility = Visibility.Collapsed;
            if (StatusIndicatorRow != null)
                StatusIndicatorRow.Height = new GridLength(StatusIndicatorExpandedHeight);
        }

        private void HideStatusIndicator()
        {
            if (StatusIndicatorText.Visibility != Visibility.Visible)
            {
                AnimateStatusIndicatorRow(expand: false);
                return;
            }

            UiMotion.Clear(StatusIndicatorText, UIElement.OpacityProperty);
            var opacityStart = StatusIndicatorText.Opacity;

            UiMotion.Fade(
                StatusIndicatorText,
                0,
                120,
                UiMotion.InQuad,
                () =>
                {
                    StatusIndicatorText.Visibility = Visibility.Collapsed;
                    StatusIndicatorText.Text = string.Empty;
                    AnimateStatusIndicatorRow(expand: false);
                });
        }

        private void AnimateStatusIndicatorRow(bool expand)
        {
            if (StatusIndicatorRow is null) return;
            var target = expand || AiCancelButton.Visibility == Visibility.Visible ? StatusIndicatorExpandedHeight : 0;
            UiMotion.AnimateRowHeight(StatusIndicatorRow, StatusIndicatorRow, StatusIndicatorRow.ActualHeight,
                target, 160);
        }

        internal FindReplaceResult PerformFindNext(
            string query,
            bool matchCase = false,
            bool wholeWord = false,
            bool wrapAround = true)
        {
            return PerformFind(query, FindDirection.Next, matchCase, wholeWord, wrapAround);
        }

        internal FindReplaceResult PerformFindPrevious(
            string query,
            bool matchCase = false,
            bool wholeWord = false,
            bool wrapAround = true)
        {
            return PerformFind(query, FindDirection.Previous, matchCase, wholeWord, wrapAround);
        }

        internal FindReplaceResult? PerformReplace(
            string query,
            string replacement,
            bool matchCase = false,
            bool wholeWord = false)
        {
            if (string.IsNullOrEmpty(query))
                return FindReplaceResult.Empty;

            var (index, matches) = GetOrBuildFindMatches(query, matchCase, wholeWord);
            if (matches.Count == 0)
            {
                ClearAllHighlights();
                return new FindReplaceResult(false, 0, -1, false, 0);
            }

            var currentMatch = GetCurrentSelectionMatch(index.Runs, matches);
            if (currentMatch is null)
                return null;

            var start = PointerAtTextOffset(index.Runs, currentMatch.Start);
            var end = PointerAtTextOffset(index.Runs, currentMatch.End);
            if (start is null || end is null)
                return null;

            DropParkedSelection();
            ReplaceEditorSelection(end, end);
            InvalidateSelectionTiles();
            var range = new TextRange(start, end);
            range.Text = replacement ?? string.Empty;
            ReplaceEditorSelection(range.End, range.End);
            MarkEditorContentChanged();

            return new FindReplaceResult(false, 0, -1, false, 1);
        }

        internal FindReplaceResult PerformReplaceAll(
            string query,
            string replacement,
            bool matchCase = false,
            bool wholeWord = false)
        {
            if (string.IsNullOrEmpty(query))
                return FindReplaceResult.Empty;

            DropParkedSelection();
            InvalidateSelectionTiles();

            var (index, matches) = GetOrBuildFindMatches(query, matchCase, wholeWord);
            if (matches.Count == 0)
            {
                ClearAllHighlights();
                return new FindReplaceResult(false, 0, -1, false, 0);
            }

            var ranges = new List<(TextPointer Start, TextPointer End)>(matches.Count);
            foreach (var match in matches)
            {
                var start = PointerAtTextOffset(index.Runs, match.Start);
                var end = PointerAtTextOffset(index.Runs, match.End);
                if (start is not null && end is not null)
                    ranges.Add((start, end));
            }

            ClearAllHighlights();
            _findMutationDepth++;
            try
            {
                for (var i = ranges.Count - 1; i >= 0; i--)
                    new TextRange(ranges[i].Start, ranges[i].End).Text = replacement ?? string.Empty;
            }
            finally
            {
                _findMutationDepth--;
                SchedulePendingFindRefresh();
            }

            MarkEditorContentChanged();
            if (!_isLoadingDocument)
                ApplyEditorEdgeSpacing();
            ScheduleCounterUpdate();
            UpdateEditedIndicator();
            ScheduleSelectionChromeRefresh();
            if (_isSpellCheckEnabled)
                ScheduleSpellCheck();
            _wrapLayout?.ScheduleApply();
            _floatingImages.RenumberCaptions();
            ContentTextBox.Focus();
            return new FindReplaceResult(false, 0, -1, false, ranges.Count);
        }

        internal int CountFindMatches(string query, bool matchCase = false, bool wholeWord = false)
        {
            if (string.IsNullOrEmpty(query))
                return 0;

            return FindTextMatches(query, matchCase, wholeWord).Count;
        }

        internal void ClearFindHighlights()
        {
            ClearAllHighlights();
            _findOverlayActiveIndex = -1;
            InvalidateFindTiles();
        }

        private FindReplaceResult PerformFind(
            string query,
            FindDirection direction,
            bool matchCase,
            bool wholeWord,
            bool wrapAround)
        {
            if (string.IsNullOrEmpty(query))
                return FindReplaceResult.Empty;

            var (index, matches) = GetOrBuildFindMatches(query, matchCase, wholeWord);
            if (matches.Count == 0)
            {
                ClearAllHighlights();
                return new FindReplaceResult(false, 0, -1, false, 0);
            }

            var selection = ContentTextBox.Selection;
            var start = _selectionCollapsedForScroll
                ? _heldSelectionStart : selection?.Start;
            var end = _selectionCollapsedForScroll
                ? _heldSelectionEnd : selection?.End;
            var selectionStart = GetTextOffsetForPointer(index.Runs, start ?? ContentTextBox.Document.ContentStart);
            var selectionEnd = GetTextOffsetForPointer(index.Runs, end ?? ContentTextBox.Document.ContentStart);

            var activeIndex = direction == FindDirection.Next
                ? FindFirstMatchIndexAtOrAfter(matches, selectionEnd)
                : FindLastMatchIndexBefore(matches,
                    _selectionCollapsedForScroll || selection?.IsEmpty == false
                        ? selectionStart : selectionEnd);

            var wrapped = false;
            if (activeIndex < 0 && wrapAround)
            {
                activeIndex = direction == FindDirection.Next ? 0 : matches.Count - 1;
                wrapped = true;
            }

            if (activeIndex < 0)
            {
                HighlightMatches(index, matches, activeIndex: -1);
                return new FindReplaceResult(false, matches.Count, -1, false, 0);
            }

            HighlightMatches(index, matches, activeIndex);
            SelectMatch(index.Runs, matches[activeIndex]);
            return new FindReplaceResult(true, matches.Count, activeIndex, wrapped, 0);
        }

        private static int FindLastMatchIndexBefore(IReadOnlyList<FindMatch> matches, int offset)
        {
            var lo = 0;
            var hi = matches.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (matches[mid].Start < offset)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            return lo - 1;
        }

        private static int FindFirstMatchIndexAtOrAfter(IReadOnlyList<FindMatch> matches, int offset)
        {
            var lo = 0;
            var hi = matches.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (matches[mid].Start < offset)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            return lo < matches.Count ? lo : -1;
        }

        private FindMatch? GetCurrentSelectionMatch(IReadOnlyList<TextRunSpan> runs, IReadOnlyList<FindMatch> matches)
        {
            var selection = ContentTextBox.Selection;
            var start = _selectionCollapsedForScroll ? _heldSelectionStart : selection?.Start;
            var end = _selectionCollapsedForScroll ? _heldSelectionEnd : selection?.End;
            if (start is null || end is null || start.CompareTo(end) >= 0)
                return null;

            var startOffset = GetTextOffsetForPointer(runs, start);
            var endOffset = GetTextOffsetForPointer(runs, end);
            var index = FindFirstMatchIndexAtOrAfter(matches, startOffset);
            return index >= 0 && matches[index].Start == startOffset && matches[index].End == endOffset
                ? matches[index]
                : null;
        }

        private List<FindMatch> FindTextMatches(string query, bool matchCase, bool wholeWord)
            => GetOrBuildFindMatches(query, matchCase, wholeWord).Matches;

        private (DocumentTextIndex Index, List<FindMatch> Matches) GetOrBuildFindMatches(
            string query,
            bool matchCase,
            bool wholeWord)
        {
            if (_findCachedVersion == _findDocumentVersion
                && _findCachedMatches is not null
                && string.Equals(_findCachedQuery, query, StringComparison.Ordinal)
                && _findCachedMatchCase == matchCase
                && _findCachedWholeWord == wholeWord)
                return (_findCachedIndex, _findCachedMatches);

            var index = BuildDocumentTextIndex();
            var matches = FindTextMatches(index, query, matchCase, wholeWord);
            var spans = new MatchSpan[matches.Count];
            for (var i = 0; i < matches.Count; i++)
                spans[i] = new MatchSpan(matches[i].Start, matches[i].Length);

            _findCachedVersion = _findDocumentVersion;
            _findCachedQuery = query;
            _findCachedMatchCase = matchCase;
            _findCachedWholeWord = wholeWord;
            _findCachedIndex = index;
            _findCachedMatches = matches;
            _findCachedSpans = spans;
            return (index, matches);
        }

        private void ReleaseFindCache()
        {
            _findCachedVersion = -1;
            _findCachedQuery = null;
            _findCachedIndex = default;
            _findCachedMatches = null;
            _findCachedSpans = Array.Empty<MatchSpan>();
        }

        private List<FindMatch> FindTextMatches(DocumentTextIndex index, string query, bool matchCase, bool wholeWord)
        {
            var documentText = index.Text;
            var matches = new List<FindMatch>();
            if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(documentText))
                return matches;

            var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var searchStart = 0;
            while (searchStart <= documentText.Length - query.Length)
            {
                var found = documentText.IndexOf(query, searchStart, comparison);
                if (found < 0)
                    break;

                if (!wholeWord || IsWholeWordMatch(documentText, found, query.Length))
                    matches.Add(new FindMatch(found, query.Length));

                searchStart = found + Math.Max(1, query.Length);
            }

            return matches;
        }

        private string GetDocumentSearchText()
            => BuildDocumentTextIndex().Text;

        private readonly struct DocumentTextIndex
        {
            public DocumentTextIndex(string text, List<TextRunSpan> runs)
            {
                Text = text;
                Runs = runs;
            }

            public string Text { get; }
            public List<TextRunSpan> Runs { get; }
        }

        private readonly struct TextRunSpan
        {
            public TextRunSpan(int start, int length, TextPointer pointer)
            {
                Start = start;
                Length = length;
                Pointer = pointer;
            }

            public int Start { get; }
            public int Length { get; }
            public TextPointer Pointer { get; }
        }

        /// <summary>
        /// One forward walk. Paragraphs, line breaks, tables, and images insert a newline so a
        /// match cannot form across unrelated words. Formatting inside a paragraph does not.
        /// </summary>
        private DocumentTextIndex BuildDocumentTextIndex()
        {
            var text = new StringBuilder();
            var runs = new List<TextRunSpan>();
            var traversed = 0;
            var seenText = false;
            var pendingBreak = false;
            var navigator = ContentTextBox.Document.ContentStart;
            var end = ContentTextBox.Document.ContentEnd;

            while (navigator is not null && navigator.CompareTo(end) < 0)
            {
                var context = navigator.GetPointerContext(LogicalDirection.Forward);
                if (context == TextPointerContext.Text)
                {
                    var runText = navigator.GetTextInRun(LogicalDirection.Forward);
                    if (runText.Length > 0)
                    {
                        if (seenText && pendingBreak)
                        {
                            text.Append('\n');
                            traversed++;
                        }

                        pendingBreak = false;
                        runs.Add(new TextRunSpan(traversed, runText.Length, navigator));
                        text.Append(runText);
                        traversed += runText.Length;
                        seenText = true;
                    }
                }
                else if (IsSearchBreak(navigator, context))
                {
                    pendingBreak = true;
                }

                navigator = navigator.GetNextContextPosition(LogicalDirection.Forward);
            }

            return new DocumentTextIndex(text.ToString(), runs);
        }

        private static bool IsSearchBreak(TextPointer pointer, TextPointerContext context)
        {
            if (context == TextPointerContext.EmbeddedElement)
                return true;

            if (context == TextPointerContext.ElementStart || context == TextPointerContext.ElementEnd)
                return pointer.GetAdjacentElement(LogicalDirection.Forward) is Block or LineBreak or InlineUIContainer;

            return false;
        }

        /// <summary>One forward walk of the document. Spell-check and find resolve every hit
        /// from this list instead of restarting at <see cref="TextElement.ContentStart"/>.</summary>
        private List<TextRunSpan> BuildTextRunIndex()
            => BuildDocumentTextIndex().Runs;

        private TextPointer? PointerAtTextOffset(IReadOnlyList<TextRunSpan> runs, int targetOffset)
        {
            targetOffset = Math.Max(0, targetOffset);
            var lo = 0;
            var hi = runs.Count - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >>> 1;
                var run = runs[mid];
                if (targetOffset < run.Start)
                {
                    hi = mid - 1;
                    continue;
                }

                if (targetOffset > run.Start + run.Length)
                {
                    lo = mid + 1;
                    continue;
                }

                return FindHighlightSlices.Advance(run.Pointer, targetOffset - run.Start);
            }

            return null;
        }

        private TextPointer? GetTextPointerAtTextOffset(int targetOffset)
            => PointerAtTextOffset(BuildTextRunIndex(), targetOffset);

        private int GetTextOffsetForPointer(TextPointer pointer)
            => GetTextOffsetForPointer(BuildTextRunIndex(), pointer);

        private int GetTextOffsetForPointer(IReadOnlyList<TextRunSpan> runs, TextPointer pointer)
        {
            if (runs.Count == 0)
                return 0;

            // TextPointers and run starts are in document order. Find the containing run
            // without measuring from the document start on every viewport repaint.
            var lo = 0;
            var hi = runs.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (runs[mid].Pointer.CompareTo(pointer) <= 0)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            if (lo == 0)
                return runs[0].Start;

            var run = runs[lo - 1];
            var runEnd = run.Pointer.GetPositionAtOffset(run.Length, LogicalDirection.Forward);
            if (runEnd is not null && pointer.CompareTo(runEnd) <= 0)
                return run.Start + Math.Clamp(run.Pointer.GetOffsetToPosition(pointer), 0, run.Length);

            return lo < runs.Count ? runs[lo].Start : run.Start + run.Length;
        }

        private void HighlightMatches(DocumentTextIndex index, IReadOnlyList<FindMatch> matches, int activeIndex)
        {
            var spans = ReferenceEquals(matches, _findCachedMatches)
                ? _findCachedSpans
                : matches.Select(match => new MatchSpan(match.Start, match.Length)).ToArray();
            var sameResults = _findOverlayIndexReady
                && ReferenceEquals(_findOverlaySpans, spans)
                && ReferenceEquals(_findOverlayIndex.Runs, index.Runs);
            var activeChanged = _findOverlayActiveIndex != activeIndex;
            _findOverlayIndex = index;
            _findOverlayIndexReady = true;
            _findOverlayActiveIndex = activeIndex;
            _findOverlaySpans = spans;
            _findActiveSpan = activeIndex >= 0 && activeIndex < spans.Length
                ? [spans[activeIndex]] : Array.Empty<MatchSpan>();
            UpdateActiveFindBounds(index.Runs);

            _hasFindHighlights = matches.Count > 0;
            if (!sameResults)
                InvalidateFindTiles();
            else if (activeChanged)
                InvalidateFindActiveTiles();
            QueueFindOverlay(DispatcherPriority.Render);
        }

        private void UpdateActiveFindBounds(IReadOnlyList<TextRunSpan> runs)
        {
            _findActiveTop = double.NaN;
            _findActiveBottom = double.NaN;
            if (_findActiveSpan.Length == 0)
                return;

            var match = _findActiveSpan[0];
            var start = PointerAtTextOffset(runs, match.Start);
            var end = PointerAtTextOffset(runs, match.End);
            if (start is null || end is null)
                return;

            var first = SafeCharacterRect(start, LogicalDirection.Forward);
            var last = SafeCharacterRect(end, LogicalDirection.Backward);
            if (first.IsEmpty || last.IsEmpty)
                return;

            var reach = Math.Max(HighlightTileHeight, Math.Max(first.Height, last.Height));
            var offset = _contentScrollViewer?.VerticalOffset ?? 0;
            _findActiveTop = Math.Min(first.Top, last.Top) + offset - reach;
            _findActiveBottom = Math.Max(first.Bottom, last.Bottom) + offset + reach;
        }

        private void ApplyFindOverlayCommit()
        {
            if (_contentScrollViewer == null)
                return;

            _findOverlayCommit.X = -_contentScrollViewer.HorizontalOffset;
            _findOverlayCommit.Y = -_contentScrollViewer.VerticalOffset;
        }

        private void QueueFindOverlay(DispatcherPriority priority)
        {
            if (!_findBarOpen)
                return;

            if (_findOverlayQueued)
            {
                if (_findOverlayOperation is { Status: DispatcherOperationStatus.Pending } queued
                    && queued.Priority < priority)
                    queued.Priority = priority;
                return;
            }

            _findOverlayQueued = true;
            _findOverlayOperation = Dispatcher.BeginInvoke(new Action(PaintFindOverlayQueued), priority);
        }

        private void PaintFindOverlayQueued()
        {
            _findOverlayOperation = null;
            _findOverlayQueued = false;
            if (_findBarOpen)
                PaintFindOverlay();
        }

        private bool PaintFindOverlay()
        {
            return PaintFindTiles();
        }

        private static int FirstRunEndingAfter(IReadOnlyList<TextRunSpan> runs, int offset)
        {
            var lo = 0;
            var hi = runs.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (runs[mid].Start + runs[mid].Length <= offset)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            return lo;
        }

        private static void AddFindMarkRect(StreamGeometryContext geometry, Rect rect)
        {
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
                return;

            var right = rect.X + Math.Max(1, rect.Width);
            var bottom = rect.Y + Math.Max(1, rect.Height);
            geometry.BeginFigure(rect.Location, isFilled: true, isClosed: true);
            geometry.LineTo(new Point(right, rect.Y), isStroked: false, isSmoothJoin: false);
            geometry.LineTo(new Point(right, bottom), isStroked: false, isSmoothJoin: false);
            geometry.LineTo(new Point(rect.X, bottom), isStroked: false, isSmoothJoin: false);
        }

        private void SelectMatch(IReadOnlyList<TextRunSpan> runs, FindMatch match)
        {
            var start = PointerAtTextOffset(runs, match.Start);
            var end = PointerAtTextOffset(runs, match.End);
            if (start is null || end is null)
                return;

            ContentTextBox.Selection.Select(start, end);
            if (start.Paragraph is FrameworkContentElement paragraph)
                paragraph.BringIntoView();
        }

        private static bool IsWholeWordMatch(string text, int index, int length)
        {
            var beforeIsBoundary = index == 0 || !IsWordCharacter(text[index - 1]);
            var afterIndex = index + length;
            var afterIsBoundary = afterIndex >= text.Length || !IsWordCharacter(text[afterIndex]);
            return beforeIsBoundary && afterIsBoundary;
        }

        private static bool IsWordCharacter(char value)
            => char.IsLetterOrDigit(value) || value == '_';

        // Load data FROM a NoteDocument
        public void LoadFromDocument(NoteDocument document)
        {
            _refreshLineSpacingAfterNextTyping = false;
            DropParkedSelection();
            _findDocumentVersion++;
            ReleaseFindCache();
            ClearFindHighlights();
            try
            {
                if (document != null)
                {
                    _isLoadingDocument = true;
                    _currentDocument = document; // Set current document
                    _indentSize = NormalizeIndentSize(document.IndentSize ?? AppSettingsService.Load().PreferredIndentSize);
                    SuspendEditorEdgeSpacing();
                    TitleTextBox.Text = document.Title;
                    TagsTextBox.Text = string.Join(", ", document.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()));

                    // The loaded flow legitimately carries in-line photo markers from the content
                    // until RestoreInlineSessions re-anchors them below. Formatting changes
                    // (font, theme, line spacing) fire TextChanged, so the undo/redo sweep must
                    // stay away across all of it - reset in the outer finally.
                    _floatingImages.SuppressMarkerSweep = true;

                    if (!string.IsNullOrEmpty(document.Content))
                    {
                        TextRange tr = new TextRange(ContentTextBox.Document.ContentStart, ContentTextBox.Document.ContentEnd);

                        try
                        {
                            // Try load as Base64 RTF, but verify decoded bytes look like RTF to avoid
                            // misinterpreting plain text that happens to be valid Base64.
                            byte[] bytes = Convert.FromBase64String(document.Content);
                            // Check for RTF header at start of decoded bytes ("{\rtf")
                            if (bytes.Length >= 5)
                            {
                                var hdr = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(5, bytes.Length));
                                if (hdr.StartsWith("{\\rtf"))
                                {
                                    using (MemoryStream ms = new MemoryStream(bytes))
                                    {
                                        ListEditor.LoadContent(ms, DataFormats.Rtf);
                                    }
                                }
                                else if (bytes[0] == 0x50 && bytes[1] == 0x4B) // ZIP / XamlPackage
                                {
                                    using (MemoryStream ms = new MemoryStream(bytes))
                                    {
                                        ListEditor.LoadContent(ms, DataFormats.XamlPackage);
                                    }
                                }
                                else
                                {
                                    throw new FormatException();
                                }
                            }
                        }
                        catch (FormatException)
                        {
                            // If not Base64, just load as plain text
                            tr.Text = document.Content;
                        }
                    }

                    RemoveLegacyEditorEndPads();

                    var settings = AppSettingsService.Load();
                    _isWordWrapEnabled = document.IsWordWrapEnabled ?? settings.IsEditorWordWrapEnabled;
                    if (document.IndentSize is double noteIndent && noteIndent > 0)
                        _indentSize = NormalizeIndentSize(noteIndent);
                    ListEditor.NormalizeLegacy(resetHistory: false);
                    SyncIndentSizeSelectorFromPreference();
                    SetFontPanelOpen(
                        document.IsEditorFontPanelOpen ?? settings.IsEditorFontPanelOpen,
                        animate: false,
                        persist: false);
                    SetTagsPanelOpen(document.IsEditorTagsPanelOpen ?? false, animate: false, persist: false);
                    _lastSavedTagsPanelOpen = _isTagsPanelOpen;
                    ApplyAiReviewNoticeVisibility();

                    var preferredFontFamily = string.IsNullOrWhiteSpace(settings.PreferredFontFamily)
                        ? DefaultEditorFontFamily
                        : settings.PreferredFontFamily;
                    var preferredFontSize = settings.PreferredFontSize > 0
                        ? settings.PreferredFontSize
                        : 14;

                    var targetFontFamily = string.IsNullOrWhiteSpace(document.FontFamily)
                        ? preferredFontFamily
                        : document.FontFamily;
                    var targetFontSize = document.FontSize > 0
                        ? document.FontSize
                        : preferredFontSize;

                    ContentTextBox.FontFamily = CreateEditorFontFamily(targetFontFamily);
                    ContentTextBox.FontSize = targetFontSize;

                    document.FontFamily = GetPrimaryFontFamily(targetFontFamily);
                    document.FontSize = ContentTextBox.FontSize;

                    SyncFontSelectorsFromSelection();
                    UpdateFontButtonText();

                    // Initialize last saved content
                    MarkCurrentStateSaved();

                    // Apply theme colors to the loaded content. Custom text colors stay as saved.
                    // A menu spacing step is brought onto the font's own line.
                    ApplyRichTextBoxTheme();
                    ApplyEditorTextLayout();
                    // The constructor gives the empty placeholder paragraph a line height
                    // using the XAML font size. A new note can use a different preferred font
                    // size, so that placeholder is not saved note formatting to sync from.
                    var isNewBlankNote = string.IsNullOrEmpty(document.Content)
                        && (document.Images is null || document.Images.Count == 0);
                    if (isNewBlankNote || !TrySyncLineSpacingFromDocument())
                        ApplyCurrentLineSpacingToDocument();

                    _floatingImages.MediaDirectory = MainViewModel.NotesMediaDirectory;
                    // Decode the photos off the UI thread (the STA decode thread) so the note's
                    // text and window appear immediately; the wrap applies once the photos land.
                    _ = RestorePhotosAsync(document.Images);

                    // Wrap lanes depend on image geometry + paragraph extents; the async restore
                    // re-schedules the apply after the photos materialize.

                    // Clear any selection and move caret to start
                    ContentTextBox.CaretPosition = ContentTextBox.Document.ContentStart;
                    SyncFontSelectorsFromSelection();
                    MarkCurrentStateSaved();
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(
                    $"Error loading document: {ex.Message}",
                    "Load Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                ApplyEditorEdgeSpacing();
                ContentTextBox.IsUndoEnabled = false;
                ContentTextBox.IsUndoEnabled = true;
                ListEditor.ResetHistory();
                _isLoadingDocument = false;
                _floatingImages.SuppressMarkerSweep = false;
                UpdateEditedIndicator();
            }
        }

        /// <summary>Restores the note's photos off the UI thread (the coordinator's dedicated
        /// STA decode thread) so opening a photo-heavy note shows the text immediately. The
        /// marker sweep stays suppressed and the edited-marking stays disabled across the
        /// materialization, the wrap pass, and the short settle window afterwards (the apply's
        /// tail events and the debounced re-apply fire TextChanged a few hundred ms after the
        /// load - the "edited" flash); the baseline is re-captured once the flow is quiet so
        /// the note opens clean.</summary>
        private async Task RestorePhotosAsync(IReadOnlyList<NoteImageAttachment>? attachments)
        {
            _restoreBaselineHasFieldEdits = false;
            _suppressEditedMarking = true;
            _floatingImages.SuppressMarkerSweep = true;
            try
            {
                await _floatingImages.RestoreAsync(attachments);
                ApplyEditorEdgeSpacing();

                // The open animation shares this thread. A full wrap settle waits on the
                // dispatcher at render priority, so it runs after that motion, or immediately
                // when motion is reduced.
                if (!UiMotion.IsMotionDisabled)
                    await Task.Delay(UiMotion.OpenMs);
                _wrapLayout?.Apply();
            }
            finally
            {
                _floatingImages.SuppressMarkerSweep = false;
            }

            // The apply's mutations settle over the next few hundred ms (tail TextChanged
            // events, the debounced re-apply). Keep the edited-marking suppressed through the
            // settle, then capture the baseline once the flow is quiet - the post-settle bumps
            // would otherwise flash the freshly-opened note as "edited".
            _restoreSettleTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _restoreSettleTimer.Tick -= OnRestoreSettleTick;
            _restoreSettleTimer.Tick += OnRestoreSettleTick;
            _restoreSettleTimer.Start();
        }

        private void OnRestoreSettleTick(object? sender, EventArgs e)
        {
            _restoreSettleTimer?.Stop();
            _suppressEditedMarking = false;
            ApplyEditorEdgeSpacing();
            if (!_restoreBaselineHasFieldEdits) MarkCurrentStateSaved();
            UpdateEditedIndicator();
        }

        // Print functionality
        private void PrintButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Create a PrintDialog
                PrintDialog pd = new PrintDialog();
                if (pd.ShowDialog() == true)
                {
                    FlowDocument documentToPrint;
                    using (_tableLayout.UsePreferredWidths(ContentTextBox.Document))
                        documentToPrint = CloneFlowDocument(ContentTextBox.Document)
                            ?? throw new InvalidOperationException("Unable to create the print document.");
                    documentToPrint.PagePadding = new Thickness(50);
                    documentToPrint.PageWidth = pd.PrintableAreaWidth;
                    documentToPrint.PageHeight = pd.PrintableAreaHeight;
                    documentToPrint.ColumnWidth = Math.Max(1, pd.PrintableAreaWidth - 100);
                    new EditorTableLayout().Apply(documentToPrint, Math.Max(1, pd.PrintableAreaWidth - 100));

                    // Print the document with fonts and formatting
                    pd.PrintDocument(((IDocumentPaginatorSource)documentToPrint).DocumentPaginator, TitleTextBox.Text);
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("FailedToPrintNote")}\n\n{ex.Message}",
                    LocalizationService.GetString("PrintError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // Save data TO a NoteDocument
        public void SaveToDocument(NoteDocument document)
        {
            if (document == null)
                return;

            // The save path mutates the live flow: the spell-check/highlight decoration clears,
            // the wrap engine's Clear+Reapply (its character-rect measurements force layouts
            // while the flow is shrunk - the scroll offset gets clamped), and the serialization's
            // pad/marker stripping. Preserve the view across all of it so the auto-save never
            // teleports the editor.
            var viewState = _floatingImages.CaptureViewState();
            try
            {
                SaveToDocumentCore(document);
            }
            finally
            {
                _floatingImages.RestoreViewState(viewState);
            }
        }

        private void SaveToDocumentCore(NoteDocument document)
        {
            var previousContent = document.Content ?? string.Empty;
            var previousTitle = document.Title ?? string.Empty;
            var previousTags = document.Tags ?? new List<string>();
            var previousFontFamily = document.FontFamily ?? string.Empty;
            var previousFontSize = document.FontSize;

            var newTitle = TitleTextBox.Text;
            var newTags = ParseTags(TagsTextBox.Text);
            var newFontFamily = GetPrimaryFontFamily(ContentTextBox.FontFamily.Source);
            var newFontSize = ContentTextBox.FontSize;
            // Read source geometry before the serialization cycle removes wrap spacers and
            // inline elements. Capturing a preview never changes the live flow or selection.
            var cardPreview = CaptureCardPreview();
            ClearSpellCheckDecorations();
            // Wrap insets are transient layout margins - never persist them into saved content.
            // Capture and restore the live spacers so autosave does not run a second settle.
            var wrapState = _wrapLayout?.CaptureWrapState();
            _wrapLayout?.Clear();
            var newContent = SerializeEditorContent();
            var newImages = _floatingImages.BuildAttachments().ToList();
            // Squiggles are real TextDecorations - must never end up saved/exported (stale on
            // reload, and nobody wants spell-check marks baked into a shared PDF/RTF). Content
            // is already captured into newContent above, so reapplying now doesn't affect what
            // gets persisted; fire-and-forget since dictionaries are normally already cached
            // from the previous scan, making this effectively instant.
            _ = RunSpellCheckPassAsync();

            // Put the same spacers back. One layout, no convergence pass, so the note
            // never sits unwrapped and the exit animation is not asked to rebuild the wrap.
            _wrapLayout?.RestoreWrapState(wrapState);

            var contentChanged = !string.Equals(previousContent, newContent, StringComparison.Ordinal);
            var imagesChanged = !AreImageAttachmentsEqual(document.Images, newImages);
            if (contentChanged)
            {
                AppendEditHistoryVersion(document, previousContent);
            }

            var titleChanged = !string.Equals(previousTitle, newTitle, StringComparison.Ordinal);
            var tagsChanged = !AreTagListsEqual(previousTags, newTags);
            var fontChanged = !string.Equals(previousFontFamily, newFontFamily, StringComparison.Ordinal)
                || !previousFontSize.Equals(newFontSize);
            var previousIndent = document.IndentSize;
            var indentChanged = previousIndent is not double storedIndent
                || Math.Abs(storedIndent - _indentSize) >= 0.1;

            if (contentChanged || imagesChanged || titleChanged || tagsChanged || fontChanged || indentChanged)
            {
                document.LastModified = DateTime.Now;
            }

            document.Title = newTitle;
            document.Tags = newTags;
            document.Content = newContent;
            document.Images = newImages;
            document.FontFamily = newFontFamily;
            document.FontSize = newFontSize;
            document.IndentSize = _indentSize;
            document.IsEditorFontPanelOpen = _isFontPanelOpen;
            document.IsEditorTagsPanelOpen = _isTagsPanelOpen;
            document.IsWordWrapEnabled = _isWordWrapEnabled;
            document.PreviewLayoutWidth = cardPreview.LayoutWidth;
            document.CardPreview = NotePreviewService.Complete(cardPreview, NotePreviewService.SourceHash(document));
            document.SearchablePreview = null;
        }

        private static bool AreImageAttachmentsEqual(
            IReadOnlyList<NoteImageAttachment>? left,
            IReadOnlyList<NoteImageAttachment>? right)
        {
            left ??= Array.Empty<NoteImageAttachment>();
            right ??= Array.Empty<NoteImageAttachment>();
            if (left.Count != right.Count)
                return false;

            for (var i = 0; i < left.Count; i++)
            {
                var a = left[i];
                var b = right[i];
                if (a.Id != b.Id
                    || !string.Equals(a.DataFilePath, b.DataFilePath, StringComparison.Ordinal)
                    || !string.Equals(a.Data, b.Data, StringComparison.Ordinal)
                    || !a.Width.Equals(b.Width)
                    || !a.Height.Equals(b.Height)
                    || !a.Left.Equals(b.Left)
                    || !a.Top.Equals(b.Top)
                    || a.LockAspectRatio != b.LockAspectRatio
                    || a.WrapMode != b.WrapMode
                    || a.InlineAnchorOffset != b.InlineAnchorOffset
                    || a.ZIndex != b.ZIndex)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool AreTagListsEqual(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
        {
            left ??= Array.Empty<string>();
            right ??= Array.Empty<string>();

            if (left.Count != right.Count)
                return false;

            for (var i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private static void AppendEditHistoryVersion(
            NoteDocument document,
            string previousContent)
        {
            document.EditHistory ??= new List<NoteEditHistoryEntry>();
            document.EditHistory.Add(new NoteEditHistoryEntry
            {
                Timestamp = DateTime.UtcNow,
                Content = previousContent
            });

            if (document.EditHistory.Count > MaxEditHistoryEntries)
            {
                var overflow = document.EditHistory.Count - MaxEditHistoryEntries;
                document.EditHistory.RemoveRange(0, overflow);
            }
        }

        // Initialize auto-save timer
        private void InitializeAutoSave()
        {
            // Load auto-save setting from app settings
            var settings = AppSettingsService.Load();
            _isAutoSaveEnabled = settings.EnableAutoSave;
            _autoSaveIntervalMs = settings.AutoSaveIntervalSeconds * 1000;

            // Start the auto-save timer if enabled
            if (_isAutoSaveEnabled)
            {
                StartAutoSaveTimer();
            }

            // TextChanged is already wired in XAML.
            ContentTextBox.PreviewTextInput += ContentTextBox_PreviewTextInput;
            ContentTextBox.AddHandler(
                Keyboard.PreviewKeyDownEvent,
                new KeyEventHandler(ContentTextBox_PreviewKeyDown),
                true);
        }

        private void ContentTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (!_isLoadingDocument && Keyboard.Modifiers == ModifierKeys.None
                && AppSettingsService.Load().AutoFormatLists && ListEditor.AutoStart(e.Text))
            {
                e.Handled = true;
                return;
            }
            if (ContentTextBox.Selection.IsEmpty && ContentTextBox.CaretPosition.Paragraph is { } checkboxParagraph
                && EditorLists.Checkbox(checkboxParagraph, out var checkboxTextStart, out _)
                && ContentTextBox.CaretPosition.CompareTo(checkboxTextStart) < 0)
                ContentTextBox.CaretPosition = checkboxTextStart;
            if (!ContentTextBox.Selection.IsEmpty && ListEditor.HasList && !string.IsNullOrEmpty(e.Text))
            {
                ListEditor.ReplaceSelection(e.Text);
                e.Handled = true;
            }
            if (!string.IsNullOrEmpty(e.Text))
            {
                Scriblucent.Services.ActivityTracker.RecordTyping(e.Text.Length, 0);
            }
        }

        private void ContentTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (TryHandleTableKey(e)) return;
            if (TryHandleChecklistKey(e)) return;
            if (TryHandleEditorTabKey(e))
                return;

            if (e.Key == Key.Space || e.Key == Key.Enter)
            {
                Scriblucent.Services.ActivityTracker.RecordTyping(0, 1);
            }
        }

        private void ContentTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            // A photo becomes one of this note's photos, including when the clipboard wraps
            // the picture in HTML. Formatted text keeps the source font and styles, then
            // takes this note's line spacing.
            if (e.DataObject is not { } data)
                return;

            var mode = _pasteModeOverride ?? AppSettingsService.Load().DefaultPasteMode;
            if (ContainsInlineMarkers(data))
            {
                data = SanitizePasteDataObject(data);
                e.DataObject = data;
            }

            var action = EditorPaste.Resolve(mode, data);
            var pasteStartOffset = ContentTextBox.Document.ContentStart.GetOffsetToPosition(ContentTextBox.Selection.Start);
            void ApplyPasteSpacing()
            {
                var pasteStart = ContentTextBox.Document.ContentStart.GetPositionAtOffset(pasteStartOffset);
                if (pasteStart is not null && pasteStart.CompareTo(ContentTextBox.Selection.End) < 0)
                    ApplyLineSpacingToRange(pasteStart, ContentTextBox.Selection.End, _lineSpacingMultiplier);
            }

            switch (action)
            {
                case EditorPaste.PasteAction.Image:
                    PasteImageFromDataObject(data);
                    e.CancelCommand();
                    e.Handled = true;
                    return;
                case EditorPaste.PasteAction.RichFragment:
                    bool pasted = false;
                    ListEditor.Mutate(() =>
                    {
                        pasted = EditorPaste.TryPasteRich(ContentTextBox, data);
                        if (pasted)
                        {
                            ApplyPasteSpacing();
                            ContentTextBox.CaretPosition = ContentTextBox.Selection.End;
                        }
                    });
                    if (pasted)
                    {
                        e.CancelCommand(); e.Handled = true;
                        MarkEditorContentChanged(); UpdateEditedIndicator();
                        return;
                    }
                    break;
            }

            if (EditorPaste.PlainText(data) is { } plainText)
            {
                ListEditor.PasteText(plainText, ApplyPasteSpacing);
                e.CancelCommand(); e.Handled = true;
            }
        }

        internal static DispatcherOperation QueuePastedTextSpacing(
            RichTextBox editor,
            Action<TextPointer, TextPointer> applySpacing)
        {
            var document = editor.Document;
            var startOffset = document.ContentStart.GetOffsetToPosition(editor.Selection.Start);
            return editor.Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(editor.Document, document))
                    return;

                var start = document.ContentStart.GetPositionAtOffset(startOffset, LogicalDirection.Forward);
                var end = editor.Selection.End;
                if (start == null || start.CompareTo(end) >= 0)
                    return;

                // BeginChange suspends dispatcher processing, so it must start here,
                // after the queued callback begins, rather than in the paste event.
                editor.BeginChange();
                try
                {
                    applySpacing(start, end);
                }
                finally
                {
                    editor.EndChange();
                }
            }, DispatcherPriority.Normal);
        }

        private static bool ContainsInlineMarkers(IDataObject data)
        {
            if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string unicode
                && unicode.Contains('\uFFFC'))
                return true;

            if (data.GetDataPresent(DataFormats.Text) && data.GetData(DataFormats.Text) is string text
                && text.Contains('\uFFFC'))
                return true;

            return data.GetDataPresent(DataFormats.Rtf) && data.GetData(DataFormats.Rtf) is string rtf
                && rtf.Contains("\\u-4", StringComparison.Ordinal);
        }

        private static DataObject SanitizePasteDataObject(IDataObject data)
        {
            var sanitized = new DataObject();
            if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string unicode)
                sanitized.SetData(DataFormats.UnicodeText, unicode.Replace('\uFFFC', ' '));
            if (data.GetDataPresent(DataFormats.Text) && data.GetData(DataFormats.Text) is string text)
                sanitized.SetData(DataFormats.Text, text.Replace('\uFFFC', ' '));
            if (data.GetDataPresent(DataFormats.Rtf) && data.GetData(DataFormats.Rtf) is string rtf)
                sanitized.SetData(DataFormats.Rtf, System.Text.RegularExpressions.Regex.Replace(rtf, @"\\u-4\?", " "));
            if (data.GetDataPresent(DataFormats.Html) && data.GetData(DataFormats.Html) is string html)
                sanitized.SetData(DataFormats.Html, html.Replace('\uFFFC', ' '));

            return sanitized;
        }

        /// <summary>True when the pasted data carries an image in any common clipboard format.
        /// The legacy Bitmap check alone misses PNG/DIB-only clipboards (screenshots, many apps).</summary>
        private static bool IsImageData(IDataObject data)
        {
            return data.GetDataPresent(DataFormats.Bitmap)
                || data.GetDataPresent(DataFormats.Dib)
                || data.GetDataPresent(DataFormats.Tiff)
                || data.GetDataPresent("PNG");
        }

        /// <summary>Inserts an image from the pasted data as a new floating photo at the caret.
        /// Returns false when no usable image could be extracted.</summary>
        private bool PasteImageFromDataObject(IDataObject data)
        {
            try
            {
                var pngBytes = TryExtractPngBytes(data);
                if (pngBytes is null)
                    return false;

                var dpi = ResolveEditorDpiScale();
                if (!NoteImageLoader.TryLoadFromBytes(
                        pngBytes,
                        NoteImageLoader.DefaultInsertMaxWidthDip,
                        dpi,
                        out var load,
                        out _)
                    || load is null)
                {
                    return false;
                }

                var (width, height) = NoteImageLoader.FitWithinBox(
                    load.NaturalPixelWidth,
                    load.NaturalPixelHeight,
                    NoteImageLoader.DefaultInsertMaxWidthDip,
                    NoteImageLoader.DefaultInsertMaxHeightDip);

                InsertFloatingImage(load, width, height);
                ContentTextBox.Focus();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static byte[]? TryExtractPngBytes(IDataObject data)
        {
            // 1. Raw PNG bytes first: browsers and screenshot tools put the PNG on the
            // clipboard, and decoding those bytes preserves the pixels and alpha exactly
            // (the legacy Bitmap/HBitmap conversion can render 32bpp alpha as transparent).
            if (data.GetDataPresent("PNG"))
            {
                var png = data.GetData("PNG") switch
                {
                    byte[] bytes => bytes,
                    MemoryStream memory => memory.ToArray(),
                    Stream stream => CopyToArray(stream),
                    _ => null
                };
                if (png is { Length: > 0 })
                    return png;
            }

            // 2. Raw DIB bytes as a last-resort alpha-safe decode (synthesize a BMP header and
            // let the BmpBitmapDecoder handle the pixel format instead of the HBitmap path).
            if (data.GetDataPresent(DataFormats.Dib))
            {
                var dib = data.GetData(DataFormats.Dib) switch
                {
                    byte[] bytes => bytes,
                    MemoryStream memory => memory.ToArray(),
                    Stream stream => CopyToArray(stream),
                    _ => null
                };
                if (dib is { Length: > 14 } && TryDecodeDibAsBmp(dib) is { } dibImage)
                    return EncodePng(dibImage);
            }

            // 3. The WPF BitmapSource path - the app's own copy and most apps.
            if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource wpfBitmap)
                return EncodePng(wpfBitmap);

            // 4. The system clipboard's canonical image machinery (handles the HBitmap path).
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } fallback)
                return EncodePng(fallback);

            return null;
        }

        /// <summary>
        /// Decodes a raw DIB (device-independent bitmap, no file header) by wrapping it in a
        /// BITMAPFILEHEADER and running it through the WPF BmpBitmapDecoder - the DIB's own
        /// pixel format and alpha survive, unlike the clipboard HBitmap conversion.
        /// </summary>
        private static BitmapSource? TryDecodeDibAsBmp(byte[] dib)
        {
            try
            {
                if (dib.Length < 14)
                    return null;

                const int fileHeaderSize = 14;
                var bmp = new byte[dib.Length + fileHeaderSize];
                bmp[0] = (byte)'B';
                bmp[1] = (byte)'M';
                BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
                BitConverter.GetBytes(fileHeaderSize).CopyTo(bmp, 10);
                Array.Copy(dib, 0, bmp, fileHeaderSize, dib.Length);

                using var stream = new MemoryStream(bmp);
                var decoder = new BmpBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                return decoder.Frames.Count > 0 ? decoder.Frames[0] : null;
            }
            catch
            {
                return null;
            }
        }

        private static byte[]? EncodePng(BitmapSource source)
        {
            // Convert to straight alpha (Pbgra32) before the PNG encode: a premultiplied source
            // (Bgra32) can render as fully transparent in the resulting image.
            BitmapSource encodeSource = source;
            if (source.Format != PixelFormats.Pbgra32)
            {
                try
                {
                    encodeSource = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
                }
                catch
                {
                    encodeSource = source;
                }
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(encodeSource));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        private static byte[] CopyToArray(Stream stream)
        {
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }

        private void ContentTextBox_CopyCanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = _selectionCollapsedForScroll
                || ContentTextBox.Selection is { IsEmpty: false }
                || _floatingImages.SelectedImageId is not null;
            e.Handled = true;
        }

        private void ContentTextBox_CutCanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            if (!_selectionCollapsedForScroll)
                return;

            e.CanExecute = _heldSelectionStart is not null && _heldSelectionEnd is not null;
            e.Handled = true;
        }

        private void ContentTextBox_CutExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (_floatingImages.SelectedImageId is not null
                || !_selectionCollapsedForScroll && ContentTextBox.Selection.IsEmpty)
                return;

            e.Handled = true;
            MaterializeSelectionForAction();
            if (!CopyEditorSelectionToClipboard())
                return;

            ListEditor.Mutate(() => ContentTextBox.Selection.Text = string.Empty);
            QueueSelectionOverlay();
        }

        private void ContentTextBox_CopyExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            // A selected photo copies its image data; otherwise copy the text selection.
            if (_floatingImages.SelectedImageId is { } imageId)
            {
                if (CopyPhotoToClipboard(imageId))
                    e.Handled = true;
                return;
            }

            if (CopyEditorSelectionToClipboard())
                e.Handled = true;
        }

        private bool CopyEditorSelectionToClipboard()
        {
            MaterializeSelectionForAction();
            return CopyCurrentEditorSelectionToClipboard();
        }

        private bool CopyCurrentEditorSelectionToClipboard()
        {
            var selection = ContentTextBox.Selection;
            if (selection == null || selection.IsEmpty)
                return false;

            try
            {
                var dataObject = new DataObject();
                if (EditorTables.TryGetRange(selection.Start, selection.End, out var table, out var cells) && table is not null)
                {
                    var gridText = EditorTables.CopyGrid(table, cells);
                    dataObject.SetText(gridText, TextDataFormat.UnicodeText);
                    dataObject.SetText(gridText, TextDataFormat.Text);
                    dataObject.SetData(EditorTables.ClipboardGridFormat, gridText);
                    TryAddSelectionRtfToClipboardData(dataObject, selection);
                    Clipboard.SetDataObject(dataObject, true);
                    return true;
                }
                var plainText = GetClipboardPlainText(selection, out var convertedIndentationForPlainText);
                dataObject.SetText(plainText, TextDataFormat.UnicodeText);
                dataObject.SetText(plainText, TextDataFormat.Text);
                TryAddSelectionRtfToClipboardData(dataObject, selection);
                Clipboard.SetDataObject(dataObject, true);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private string GetClipboardPlainText(TextSelection selection, out bool convertedIndentationForPlainText)
        {
            string text;
            if (TryGetWholeParagraphSelectionParagraphs(selection, out var selectedParagraphs))
                text = BuildWholeParagraphClipboardText(selectedParagraphs, out convertedIndentationForPlainText);
            else
                text = NormalizeIndentationForPlainTextExport(selection.Text, out convertedIndentationForPlainText);

            // In-line photo markers (object replacement chars) would read as "obj" - the photo
            // is a picture, not text.
            return text.Replace('\uFFFC', ' ');
        }

        private string BuildWholeParagraphClipboardText(
            IReadOnlyList<Paragraph> paragraphs,
            out bool convertedIndentationForPlainText)
        {
            convertedIndentationForPlainText = false;
            var lines = new List<string>();
            foreach (var paragraph in paragraphs)
            {
                var prefix = ResolveParagraphMarginIndentClipboardPrefix(paragraph);
                if (prefix.Length > 0)
                    convertedIndentationForPlainText = true;

                var paragraphText = BuildListParagraphClipboardText(paragraph);
                paragraphText = paragraphText
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n');

                var paragraphLines = paragraphText.Split('\n');
                foreach (var line in paragraphLines)
                {
                    var normalizedLine = NormalizeLeadingIndentationForPlainTextExport(line, out var convertedLineIndentation);
                    convertedIndentationForPlainText |= convertedLineIndentation;
                    lines.Add(prefix + normalizedLine);
                }
            }

            return string.Join("\r\n", lines);
        }

        private string ResolveParagraphMarginIndentClipboardPrefix(Paragraph paragraph)
        {
            if (EditorLists.Ancestor<ListItem>(paragraph) is { } item)
            {
                int depth = item.Parent is System.Windows.Documents.List list ? ListEditor.NestingDepth(list) : 0;
                return new string('\t', depth);
            }
            var indentLevel = (int)Math.Round(
                NormalizeAppliedIndent(paragraph.Margin.Left) / NormalizeIndentSize(_indentSize),
                MidpointRounding.AwayFromZero);

            return indentLevel <= 0
                ? string.Empty
                : new string('\t', Math.Min(indentLevel * ResolveEditorIndentTabCount(), 64));
        }

        private void TryAddSelectionRtfToClipboardData(DataObject dataObject, TextSelection selection)
        {
            try
            {
                SaveWithInlineMarkers(() =>
                {
                    var range = new TextRange(selection.Start, selection.End);
                    using var stream = new MemoryStream();
                    range.Save(stream, DataFormats.Rtf);
                    dataObject.SetData(DataFormats.Rtf, Encoding.ASCII.GetString(stream.ToArray()));
                });
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Runs a serialization action with in-line images temporarily replaced by text
        /// markers (RTF/XAML cannot carry UI elements), restoring them afterwards.</summary>
        private void SaveWithInlineMarkers(Action save)
        {
            _isSerializingEditorContent = true;
            try
            {
                SuspendEditorEdgeSpacing();
                _floatingImages.PrepareInlineImagesForSerialization();
                try
                {
                    save();
                }
                finally
                {
                    _floatingImages.RestoreInlineImagesAfterSerialization();
                }
            }
            finally
            {
                try
                {
                    ApplyEditorEdgeSpacing();
                    _floatingImages.EnsureImageExtentPad();
                }
                finally
                {
                    _isSerializingEditorContent = false;
                }
            }
        }

        private static bool IsTabKey(KeyEventArgs e)
            => e.Key == Key.Tab
                || e.SystemKey == Key.Tab
                || e.ImeProcessedKey == Key.Tab;

        private bool HandleEditorTabKey(bool decreaseIndent)
        {
            if (TryNavigateTableCell(decreaseIndent))
                return true;

            var selection = ContentTextBox.Selection;
            if (selection == null)
                return false;

            if (selection.IsEmpty)
            {
                if (!decreaseIndent)
                    return TryInsertIndentAtCaret();

                if (EditorLists.Ancestor<ListItem>(selection.Start.Parent) is not null)
                {
                    TryRemoveIndentBeforeCaret();
                    return true;
                }

                return TryRemoveIndentBeforeCaret() || AdjustEditorIndent(decreaseIndent);
            }

            if (TryGetWholeParagraphSelectionParagraphs(selection, out var selectedParagraphs))
                return TryAdjustWholeParagraphSelectionIndent(selection, selectedParagraphs, decreaseIndent)
                    || AdjustEditorIndent(decreaseIndent);

            if (!decreaseIndent)
            {
                return TryReplaceSelectionWithIndent();
            }

            return AdjustEditorIndent(decreaseIndent);
        }

        private bool TryInsertIndentAtCaret()
        {
            var selection = ContentTextBox.Selection;
            if (selection == null || !selection.IsEmpty)
                return false;

            var insertionPosition = TryGetInsertionPosition(selection.Start, LogicalDirection.Forward)
                ?? TryGetInsertionPosition(ContentTextBox.CaretPosition, LogicalDirection.Forward)
                ?? selection.Start;
            var caretOffset = GetTextOffsetForPointer(insertionPosition);
            var indentText = ResolveEditorIndentText();
            var changeStarted = false;

            try
            {
                ContentTextBox.BeginChange();
                changeStarted = true;
                new TextRange(insertionPosition, insertionPosition).Text = indentText;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (changeStarted)
                    ContentTextBox.EndChange();
            }

            MoveEditorCaretToTextOffset(caretOffset + indentText.Length);
            return true;
        }

        private bool TryReplaceSelectionWithIndent()
        {
            var selection = ContentTextBox.Selection;
            if (selection == null || selection.IsEmpty)
                return false;

            var startOffset = GetTextOffsetForPointer(selection.Start);
            var indentText = ResolveEditorIndentText();
            var changeStarted = false;

            try
            {
                ContentTextBox.BeginChange();
                changeStarted = true;
                new TextRange(selection.Start, selection.End).Text = indentText;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (changeStarted)
                    ContentTextBox.EndChange();
            }

            MoveEditorCaretToTextOffset(startOffset + indentText.Length);
            return true;
        }

        private bool TryAdjustWholeParagraphSelectionIndent(
            TextSelection selection,
            IReadOnlyList<Paragraph> targetParagraphs,
            bool decreaseIndent)
        {
            if (targetParagraphs.Count == 0)
                return false;

            var selectionStart = selection.Start;
            var selectionEnd = selection.End;
            var changed = false;
            foreach (var paragraph in targetParagraphs)
                changed |= TryAdjustBlockIndent(paragraph, decreaseIndent);

            if (changed)
            {
                MarkEditorContentChanged();
                UpdateEditedIndicator();
                ContentTextBox.InvalidateVisual();
                ContentTextBox.UpdateLayout();
            }

            try
            {
                ContentTextBox.Selection.Select(selectionStart, selectionEnd);
            }
            catch
            {
                // Formatting-only changes should keep text pointers valid, but the indent itself succeeded.
            }

            return true;
        }

        private bool TryGetWholeParagraphSelectionParagraphs(
            TextSelection selection,
            out List<Paragraph> selectedParagraphs)
        {
            selectedParagraphs = new List<Paragraph>();
            if (selection.IsEmpty)
                return false;

            var paragraphs = new List<Paragraph>();
            CollectParagraphs(ContentTextBox.Document.Blocks, selection.Start, selection.End, paragraphs);
            var distinctParagraphs = paragraphs.Distinct().ToList();
            if (distinctParagraphs.Count == 0)
                return false;

            foreach (var paragraph in distinctParagraphs)
            {
                if (!IsParagraphFullySelected(paragraph, selection.Start, selection.End))
                    return false;
            }

            selectedParagraphs = distinctParagraphs;
            return true;
        }

        private static bool IsParagraphFullySelected(
            Paragraph paragraph,
            TextPointer selectionStart,
            TextPointer selectionEnd)
        {
            try
            {
                var paragraphStart = FindFirstParagraphTextPosition(paragraph) ?? paragraph.ContentStart;
                var paragraphEnd = FindLastParagraphTextPosition(paragraph) ?? paragraph.ContentEnd;

                return selectionStart.CompareTo(paragraphStart) <= 0
                    && selectionEnd.CompareTo(paragraphEnd) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static TextPointer? FindFirstParagraphTextPosition(Paragraph paragraph)
        {
            var navigator = paragraph.ContentStart;
            while (navigator != null && navigator.CompareTo(paragraph.ContentEnd) < 0)
            {
                if (navigator.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text
                    && navigator.GetTextInRun(LogicalDirection.Forward).Length > 0)
                {
                    return navigator;
                }

                navigator = navigator.GetNextContextPosition(LogicalDirection.Forward);
            }

            return null;
        }

        private static TextPointer? FindLastParagraphTextPosition(Paragraph paragraph)
        {
            TextPointer? lastTextEnd = null;
            var navigator = paragraph.ContentStart;
            while (navigator != null && navigator.CompareTo(paragraph.ContentEnd) < 0)
            {
                if (navigator.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                {
                    var text = navigator.GetTextInRun(LogicalDirection.Forward);
                    if (text.Length > 0)
                    {
                        var runEnd = navigator.GetPositionAtOffset(text.Length, LogicalDirection.Forward);
                        if (runEnd != null)
                            lastTextEnd = runEnd;
                    }
                }

                navigator = navigator.GetNextContextPosition(LogicalDirection.Forward);
            }

            return lastTextEnd;
        }

        private bool TryRemoveIndentBeforeCaret()
        {
            var selection = ContentTextBox.Selection;
            if (selection == null || !selection.IsEmpty)
                return false;

            var caret = TryGetInsertionPosition(selection.Start, LogicalDirection.Backward)
                ?? TryGetInsertionPosition(ContentTextBox.CaretPosition, LogicalDirection.Backward)
                ?? selection.Start;
            var textBeforeCaret = caret.GetTextInRun(LogicalDirection.Backward);
            if (string.IsNullOrEmpty(textBeforeCaret))
                return false;

            var removeLength = ResolveIndentTextRemovalLength(textBeforeCaret);
            if (removeLength <= 0)
                return false;

            var removalStart = caret.GetPositionAtOffset(-removeLength, LogicalDirection.Backward);
            if (removalStart == null)
                return false;
            var changeStarted = false;

            try
            {
                ContentTextBox.BeginChange();
                changeStarted = true;
                new TextRange(removalStart, caret).Text = string.Empty;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (changeStarted)
                    ContentTextBox.EndChange();
            }

            ContentTextBox.CaretPosition = removalStart;
            ContentTextBox.Selection.Select(removalStart, removalStart);
            return true;
        }

        private string ResolveEditorIndentText()
            => new('\t', ResolveEditorIndentTabCount());

        private int ResolveEditorIndentTabCount()
            => Math.Clamp((int)Math.Round(NormalizeIndentSize(_indentSize) / MinimumIndentSize), 1, 8);

        private int ResolveEditorIndentSpaceCount()
        {
            var typography = ResolveCurrentSelectionTypography();
            var fontSize = typography.FontSize > 0 ? typography.FontSize : ContentTextBox.FontSize;
            var fontFamily = string.IsNullOrWhiteSpace(typography.FontFamily)
                ? ContentTextBox.FontFamily
                : CreateEditorFontFamily(typography.FontFamily);
            var estimatedSpaceWidth = fontSize * 0.33;

            try
            {
                var dpi = VisualTreeHelper.GetDpi(ContentTextBox);
                var formattedSpace = new FormattedText(
                    " ",
                    CultureInfo.CurrentUICulture,
                    ContentTextBox.FlowDirection,
                    new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    fontSize,
                    Brushes.Transparent,
                    dpi.PixelsPerDip);

                if (!double.IsNaN(formattedSpace.WidthIncludingTrailingWhitespace)
                    && formattedSpace.WidthIncludingTrailingWhitespace > 0)
                {
                    estimatedSpaceWidth = formattedSpace.WidthIncludingTrailingWhitespace;
                }
            }
            catch
            {
                // Fall back to the common proportional-font space width estimate above.
            }

            var spaceCount = (int)Math.Round(NormalizeIndentSize(_indentSize) / Math.Max(1, estimatedSpaceWidth));
            return Math.Clamp(spaceCount, 1, MaximumInsertedIndentSpaces);
        }

        private int ResolveIndentTextRemovalLength(string textBeforeCaret)
        {
            var tabs = 0;
            var indentTabCount = ResolveEditorIndentTabCount();
            for (var i = textBeforeCaret.Length - 1; i >= 0 && textBeforeCaret[i] == '\t'; i--)
            {
                tabs++;
                if (tabs == indentTabCount)
                    break;
            }

            if (tabs > 0)
                return tabs;

            var spaces = 0;
            var indentSpaceCount = ResolveEditorIndentSpaceCount();
            for (var i = textBeforeCaret.Length - 1; i >= 0 && textBeforeCaret[i] == ' '; i--)
            {
                spaces++;
                if (spaces == indentSpaceCount)
                    break;
            }

            return spaces;
        }

        private string RemoveIndentationForCounter(string text)
        {
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
                lines[i] = RemoveLeadingIndentationForCounter(lines[i]);

            return string.Join('\n', lines);
        }

        private string RemoveLeadingIndentationForCounter(string line)
        {
            if (string.IsNullOrEmpty(line))
                return line;

            var index = 0;
            while (index < line.Length && line[index] == '\t')
                index++;

            var indentSpaceCount = ResolveEditorIndentSpaceCount();
            while (index + indentSpaceCount <= line.Length
                   && IsAllSpaces(line, index, indentSpaceCount))
            {
                index += indentSpaceCount;
            }

            return index == 0 ? line : line[index..];
        }

        private string NormalizeIndentationForPlainTextExport(
            string text,
            out bool convertedIndentationForPlainText)
        {
            convertedIndentationForPlainText = false;
            var normalized = text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            var lines = normalized.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                lines[i] = NormalizeLeadingIndentationForPlainTextExport(lines[i], out var convertedLineIndentation);
                convertedIndentationForPlainText |= convertedLineIndentation;
            }

            return string.Join("\r\n", lines);
        }

        private string NormalizeLeadingIndentationForPlainTextExport(
            string line,
            out bool convertedIndentationForPlainText)
        {
            convertedIndentationForPlainText = false;
            if (string.IsNullOrEmpty(line))
                return line;

            var index = 0;
            var tabs = 0;
            while (index < line.Length && line[index] == '\t')
            {
                tabs++;
                index++;
            }

            var indentSpaceCount = ResolveEditorIndentSpaceCount();
            while (index + indentSpaceCount <= line.Length
                   && IsAllSpaces(line, index, indentSpaceCount))
            {
                tabs += ResolveEditorIndentTabCount();
                index += indentSpaceCount;
                convertedIndentationForPlainText = true;
            }

            var leadingSpaces = 0;
            while (index < line.Length && line[index] == ' ')
            {
                leadingSpaces++;
                index++;
            }

            if (leadingSpaces >= PlainTextTabEquivalentSpaces)
            {
                tabs += leadingSpaces / PlainTextTabEquivalentSpaces;
                leadingSpaces %= PlainTextTabEquivalentSpaces;
                convertedIndentationForPlainText = true;
            }

            if (leadingSpaces > 0 && tabs == 0 && !convertedIndentationForPlainText)
                index -= leadingSpaces;

            return tabs == 0 ? line : new string('\t', tabs) + line[index..];
        }

        private static bool IsAllSpaces(string text, int start, int length)
        {
            for (var i = 0; i < length; i++)
            {
                if (text[start + i] != ' ')
                    return false;
            }

            return true;
        }

        private static string TrimSingleTrailingLineBreak(string text)
        {
            // RichTextBox/FlowDocument text ranges often include one synthetic paragraph break.
            if (text.EndsWith("\r\n", StringComparison.Ordinal))
                return text[..^2];
            if (text.EndsWith("\n", StringComparison.Ordinal) || text.EndsWith("\r", StringComparison.Ordinal))
                return text[..^1];

            return text;
        }

        private void MoveEditorCaretToTextOffset(int offset)
        {
            var caret = GetTextPointerAtTextOffset(offset)
                ?? ContentTextBox.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward)
                ?? ContentTextBox.Document.ContentEnd;

            ContentTextBox.CaretPosition = caret;
            ContentTextBox.Selection.Select(caret, caret);
        }

        private bool AdjustEditorIndent(bool decreaseIndent)
        {
            var targetBlocks = GetTargetBlocksForIndentFormatting()
                .Distinct()
                .ToList();

            if (targetBlocks.Count == 0)
                return false;

            var changed = false;
            foreach (var block in targetBlocks)
            {
                changed |= TryAdjustBlockIndent(block, decreaseIndent);
            }

            if (changed)
            {
                MarkEditorContentChanged();
                UpdateEditedIndicator();
                ContentTextBox.InvalidateVisual();
                ContentTextBox.UpdateLayout();
            }

            return true;
        }

        private bool TryAdjustBlockIndent(Block block, bool decreaseIndent)
        {
            var margin = block.Margin;
            var currentIndent = NormalizeAppliedIndent(margin.Left);
            var nextIndent = ResolveNextIndent(currentIndent, _indentSize, decreaseIndent);
            var changed = Math.Abs(nextIndent - currentIndent) >= 0.1
                || double.IsNaN(margin.Left)
                || double.IsInfinity(margin.Left)
                || margin.Left < 0;

            if (changed)
                block.Margin = new Thickness(nextIndent, margin.Top, margin.Right, margin.Bottom);

            if (block is Paragraph paragraph && Math.Abs(paragraph.TextIndent) >= 0.1)
            {
                paragraph.TextIndent = 0;
                changed = true;
            }

            return changed;
        }

        private List<Block> GetTargetBlocksForIndentFormatting()
        {
            var selection = ContentTextBox.Selection;
            if (selection == null)
                return new List<Block>();

            if (selection.IsEmpty)
            {
                var activeBlock = ResolveCurrentIndentTargetBlock();
                return activeBlock == null ? new List<Block>() : new List<Block> { activeBlock };
            }

            var blocks = new List<Block>();
            CollectIndentBlocks(ContentTextBox.Document.Blocks, selection.Start, selection.End, blocks);
            return blocks;
        }

        private Block? ResolveCurrentIndentTargetBlock()
        {
            foreach (var pointer in EnumerateCaretIndentPointerCandidates(
                         ContentTextBox.Selection?.Start,
                         ContentTextBox.CaretPosition))
            {
                var block = FindIndentTargetBlock(pointer);
                if (block != null)
                    return block;
            }

            return null;
        }

        private static void CollectIndentBlocks(
            BlockCollection blocks,
            TextPointer start,
            TextPointer end,
            ICollection<Block> targetBlocks)
        {
            foreach (Block block in blocks)
            {
                switch (block)
                {
                    case Paragraph paragraph when TextElementIntersectsRange(paragraph, start, end):
                        targetBlocks.Add(paragraph);
                        break;
                    case BlockUIContainer blockContainer when TextElementIntersectsRange(blockContainer, start, end):
                        targetBlocks.Add(blockContainer);
                        break;
                    case System.Windows.Documents.List list when TextElementIntersectsRange(list, start, end):
                        targetBlocks.Add(list);
                        break;
                    case Section section:
                        CollectIndentBlocks(section.Blocks, start, end, targetBlocks);
                        break;
                    case Table table:
                        foreach (TableRowGroup rowGroup in table.RowGroups)
                        {
                            foreach (TableRow row in rowGroup.Rows)
                            {
                                foreach (TableCell cell in row.Cells)
                                    CollectIndentBlocks(cell.Blocks, start, end, targetBlocks);
                            }
                        }
                        break;
                }
            }
        }

        private static Block? FindIndentTargetBlock(TextPointer? pointer)
        {
            if (pointer == null)
                return null;

            if (pointer.Paragraph != null)
                return pointer.Paragraph;

            DependencyObject? current = pointer?.Parent as DependencyObject;
            while (current != null)
            {
                if (current is Block block)
                    return block;

                current = current switch
                {
                    FrameworkContentElement contentElement => contentElement.Parent,
                    FrameworkElement frameworkElement => frameworkElement.Parent,
                    _ => LogicalTreeHelper.GetParent(current)
                };
            }

            return null;
        }

        private static IEnumerable<TextPointer> EnumerateCaretIndentPointerCandidates(
            params TextPointer?[] pointers)
        {
            foreach (var pointer in pointers)
            {
                if (pointer == null)
                    continue;

                yield return pointer;

                var forwardInsertion = TryGetInsertionPosition(pointer, LogicalDirection.Forward);
                if (forwardInsertion != null)
                    yield return forwardInsertion;

                var backwardInsertion = TryGetInsertionPosition(pointer, LogicalDirection.Backward);
                if (backwardInsertion != null)
                    yield return backwardInsertion;
            }
        }

        private static TextPointer? TryGetInsertionPosition(TextPointer? pointer, LogicalDirection direction)
        {
            if (pointer == null)
                return null;

            try
            {
                return pointer.GetInsertionPosition(direction);
            }
            catch
            {
                return null;
            }
        }

        private static double ResolveNextIndent(double currentIndent, double indentSize, bool decreaseIndent)
        {
            var step = NormalizeIndentSize(indentSize);
            var currentLevel = currentIndent / step;
            var nextLevel = decreaseIndent
                ? Math.Ceiling(currentLevel) - 1
                : Math.Floor(currentLevel) + 1;

            return Math.Clamp(nextLevel * step, 0, MaximumAppliedIndent);
        }

        private static double NormalizeAppliedIndent(double indent)
        {
            if (double.IsNaN(indent) || double.IsInfinity(indent) || indent < 0)
                return 0;

            return Math.Min(indent, MaximumAppliedIndent);
        }

        // Start the auto-save timer
        private void StartAutoSaveTimer()
        {
            if (_workspaceSave is not null) return;
            if (_isAutoSaveEnabled)
            {
                _autoSaveTimer?.Dispose();

                _autoSaveTimer = new System.Threading.Timer(
                    AutoSaveCallback,
                    null,
                    _autoSaveIntervalMs,
                    _autoSaveIntervalMs);
            }
        }

        // Stop the auto-save timer
        private void StopAutoSaveTimer()
        {
            _autoSaveTimer?.Dispose();
            _autoSaveTimer = null;
        }

        public void SetAutoSaveInterval(int intervalSeconds)
        {
            if (intervalSeconds <= 0)
                return;

            // Update the interval
            _autoSaveIntervalMs = intervalSeconds * 1000;

            // Restart timer with new interval if auto-save is enabled
            if (_isAutoSaveEnabled)
            {
                StartAutoSaveTimer();
            }
        }

        private static void BeginAiGeneration()
        {
            if (Interlocked.Increment(ref _activeAiGenerationCount) == 1)
                AiGenerationStateChanged?.Invoke(null, EventArgs.Empty);
        }

        private static void EndAiGeneration()
        {
            if (Interlocked.Decrement(ref _activeAiGenerationCount) <= 0)
            {
                _activeAiGenerationCount = 0;
                AiGenerationStateChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        private bool TemporarilyDisableAutoSaveForAi()
        {
            if (!_isAutoSaveEnabled)
                return false;

            _isAutoSaveEnabled = false;
            StopAutoSaveTimer();
            return true;
        }

        private void RestoreAutoSaveAfterAi(ref bool shouldRestore)
        {
            if (!shouldRestore)
                return;

            shouldRestore = false;
            if (!AppSettingsService.Load().EnableAutoSave)
                return;

            _isAutoSaveEnabled = true;
            StopAutoSaveTimer();
            StartAutoSaveTimer();
        }

        private void AutoSaveCallback(object? state)
        {
            // Switch to the UI thread asynchronously. This runs on a System.Threading.Timer
            // thread; a synchronous Invoke from here while a modal dialog is open throws
            // "Dispatcher processing has been suspended, but messages are still being processed".
            try
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    if (_isAutoSaveEnabled && _currentDocument != null && HasContentChanged())
                        PerformAutoSave();
                });
            }
            catch
            {
                // Application may be shutting down (dispatcher closing) - nothing to save.
            }
        }

        // Check if content has changed since last save. Tags-panel open/closed is excluded from
        // HasUnsavedChanges (toggling it shouldn't trigger the close-confirmation prompt) but
        // still needs to reach disk eventually - included here instead so it rides the next
        // natural autosave tick rather than forcing an immediate synchronous full-store rewrite
        // (and its visible "saved" flash) on every single toggle click.
        private bool HasContentChanged()
        {
            return HasUnsavedChanges() || _isTagsPanelOpen != _lastSavedTagsPanelOpen;
        }

        private bool HasUnsavedChanges()
        {
            if (_isLoadingDocument)
                return false;

            var currentSnapshot = GetEditorSnapshot();
            return !string.Equals(currentSnapshot, _lastSavedSnapshot, StringComparison.Ordinal);
        }

        private void MarkCurrentStateSaved()
        {
            _restoreBaselineHasFieldEdits = false;
            _lastSavedContent = GetContentAsText();
            _lastSavedEditorChangeVersion = _editorChangeVersion;
            _lastSavedSnapshot = GetEditorSnapshot();
            _lastSavedTagsPanelOpen = _isTagsPanelOpen;
            UpdateEditedIndicator();
        }

        private void UpdateEditedIndicator()
        {
            WorkspaceStateChanged?.Invoke();
            if (_workspaceOwner is not null && !_workspaceActive) return;
            if (_isLoadingDocument || EditedIndicatorText is null)
                return;

            EditedIndicatorText.SetEdited(HasUnsavedChanges());
        }

        private string GetEditorSnapshot()
        {
            return string.Join(
                '\u001F',
                TitleTextBox.Text,
                string.Join('\u001E', ParseTags(TagsTextBox.Text)),
                GetPrimaryFontFamily(ContentTextBox.FontFamily.Source),
                ContentTextBox.FontSize.ToString(CultureInfo.InvariantCulture),
                _editorChangeVersion.ToString(CultureInfo.InvariantCulture));
        }

        private string GetContentAsRtfBase64()
        {
            var textRange = new TextRange(
                ContentTextBox.Document.ContentStart,
                ContentTextBox.Document.ContentEnd);

            using var stream = new MemoryStream();
            textRange.Save(stream, DataFormats.XamlPackage);
            return Convert.ToBase64String(stream.ToArray());
        }

        // Get current content as plain text for comparison
        private string GetContentAsText()
        {
            var textRange = new TextRange(
                ContentTextBox.Document.ContentStart,
                ContentTextBox.Document.ContentEnd);
            return textRange.Text;
        }

        private string GetEditorPlainText()
        {
            // In-line photo markers (object replacement chars) read as "obj" - never export them.
            var text = GetContentAsText().Replace('\uFFFC', ' ');

            if (text.EndsWith("\r\n", StringComparison.Ordinal))
                text = text[..^2];
            else if (text.EndsWith("\n", StringComparison.Ordinal) || text.EndsWith("\r", StringComparison.Ordinal))
                text = text[..^1];

            return text.Trim();
        }

        private string GetEditorAiText()
        {
            var sb = new StringBuilder();
            foreach (Block block in ContentTextBox.Document.Blocks)
                AppendBlockTextForAi(block, sb, 0);

            var text = sb.ToString().Replace('\uFFFC', ' ');

            // Collapse multiple spaces (from uFFFC replacement or formatting)
            text = Regex.Replace(text, @"(?<=\S)[^\S\r\n]+", " ");

            // Collapse multiple blank lines into one
            text = Regex.Replace(text, @"\n{3,}", "\n\n");

            // Remove trailing whitespace on each line
            text = Regex.Replace(text, @"[ \t]+\n", "\n");

            if (text.EndsWith("\r\n", StringComparison.Ordinal))
                text = text[..^2];
            else if (text.EndsWith("\n", StringComparison.Ordinal) || text.EndsWith("\r", StringComparison.Ordinal))
                text = text[..^1];

            return text.Trim();
        }

        private static void AppendBlockTextForAi(Block block, StringBuilder target, int depth)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    AppendInlineTextForAi(paragraph.Inlines, target);
                    target.AppendLine();
                    break;
                case Section section:
                    foreach (Block child in section.Blocks)
                        AppendBlockTextForAi(child, target, depth);
                    target.AppendLine();
                    break;
                case System.Windows.Documents.List list:
                    var itemIndex = list.StartIndex - 1;
                    foreach (ListItem item in list.ListItems)
                    {
                        itemIndex++;
                        var marker = GetListMarker(list.MarkerStyle, itemIndex, depth);
                        target.Append(marker);
                        foreach (Block child in item.Blocks)
                            AppendBlockTextForAi(child, target, depth + 1);
                    }
                    target.AppendLine();
                    break;
                case Table table:
                    AppendTableTextForAi(table, target);
                    break;
                case BlockUIContainer:
                    break;
            }
        }

        private static string GetListMarker(TextMarkerStyle style, int index, int depth)
        {
            var indent = new string(' ', depth * 2);
            return indent + EditorLists.Marker(style, index);
       }

        private static void AppendInlineTextForAi(InlineCollection inlines, StringBuilder target)
        {
            foreach (Inline inline in inlines)
            {
                switch (inline)
                {
                    case Hyperlink hyperlink:
                        // Include display text and URL for AI context
                        var displayText = new StringBuilder();
                        foreach (var child in hyperlink.Inlines)
                        {
                            if (child is Run run)
                                displayText.Append(run.Text);
                            else if (child is Span span)
                                AppendInlineTextForAi(span.Inlines, displayText);
                        }
                        var linkText = displayText.ToString().Trim();
                        if (!string.IsNullOrWhiteSpace(linkText) && hyperlink.NavigateUri is not null)
                            target.Append($"{linkText} ({hyperlink.NavigateUri})");
                        else if (!string.IsNullOrWhiteSpace(linkText))
                            target.Append(linkText);
                        break;
                    case Run run:
                        target.Append(run.Text);
                        break;
                    case LineBreak:
                        target.AppendLine();
                        break;
                    case Span span:
                        AppendInlineTextForAi(span.Inlines, target);
                        break;
                    case AnchoredBlock anchoredBlock:
                        foreach (Block block in anchoredBlock.Blocks)
                            AppendBlockTextForAi(block, target, 0);
                        break;
                    case InlineUIContainer:
                        break;
                }
            }
        }

        private static void AppendTableTextForAi(Table table, StringBuilder target)
        {
            var isFirstRow = true;
            foreach (TableRowGroup rowGroup in table.RowGroups)
            {
                foreach (TableRow row in rowGroup.Rows)
                {
                    var cells = new List<string>();
                    foreach (TableCell cell in row.Cells)
                        cells.Add(GetTableCellPlainText(cell));

                    target.AppendLine(string.Join('\t', cells));

                    if (isFirstRow)
                    {
                        // Add separator after header row
                        target.AppendLine(string.Join('\t', Enumerable.Repeat("---", cells.Count)));
                        isFirstRow = false;
                    }
                }
            }
        }

        private static string GetTableCellPlainText(TableCell cell)
        {
            var sb = new StringBuilder();
            foreach (Block child in cell.Blocks)
            {
                if (child is Paragraph paragraph)
                    AppendInlineTextForAi(paragraph.Inlines, sb);
                else
                    AppendBlockTextForAi(child, sb, 0);
            }

            return sb.ToString()
                .Replace('\t', ' ')
                .Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace('\n', ' ')
                .Replace('\r', ' ')
                .Trim();
        }

        // Perform auto-save
        private void PerformAutoSave()
        {
            try
            {
                if (_currentDocument != null)
                {
                    // The save path strips the extent pad and the inline photo containers from
                    // the live flow - never run it while an image is being dragged or resized
                    // (the layout churn would fight the gesture). The timer re-checks next tick.
                    if (_floatingImages.HasActiveGestures)
                        return;

                    // Save to document
                    SaveToDocument(_currentDocument);

                    // A status reports the confirmed write, rather than a queued background save.
                    if (Application.Current.MainWindow?.DataContext is MainViewModel mainVm)
                    {
                        mainVm.RefreshTagFiltersAfterNoteEdit();
                        if (!mainVm.TrySaveNotes()) { ReportWorkspaceSaveFailure(); return; }
                    }

                    // Cosmetic wrap/spell-check work does not advance the authored revision.
                    // Acknowledge this write now, before a subsequent user edit can arrive.
                    MarkCurrentStateSaved();
                    _lastAutoSaveTime = DateTime.Now;
                    DocumentAutoSaved?.Invoke(_currentDocument);
                    if (Application.Current.MainWindow?.DataContext is MainViewModel) ShowAutoSaveIndicator();
                }
            }
            catch (Exception ex)
            {
                // Don't disturb the user during auto-save; log for later diagnosis instead.
                CrashLog.Append("AutoSave", ex);
                ReportWorkspaceSaveFailure();
            }
        }

        // Show visual auto-save indicator beside the title (same slot as Edited).
        private void ShowAutoSaveIndicator()
        {
            EditedIndicatorText?.ShowSaved(UpdateEditedIndicator);
        }

        public void ReportWorkspaceSaveFailure() => EditedIndicatorText?.ShowFailure();

        // Enable/disable auto-save
        public void SetAutoSaveEnabled(bool enabled)
        {
            _isAutoSaveEnabled = enabled;

            if (enabled)
            {
                StartAutoSaveTimer();
            }
            else
            {
                StopAutoSaveTimer();
            }

            // Save preference to app settings
            var settings = AppSettingsService.Load();
            settings.EnableAutoSave = enabled;
            AppSettingsService.Save(settings);
        }

        // Check if auto-save is enabled
        public bool IsAutoSaveEnabled() => _isAutoSaveEnabled;

        // Set the current document being edited
        public void SetCurrentDocument(NoteDocument document)
        {
            _currentDocument = document;
            ApplyAiReviewNoticeVisibility();
            MarkCurrentStateSaved();
        }

        private void ExportPdfButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var exported = ExportToPdf(TitleTextBox.Text);
                if (exported)
                {
                    ModernMessageBox.Show(
                        LocalizationService.GetString("PdfExportComplete"),
                        LocalizationService.GetString("Success"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("FailedToExportPdf")}\n\n{ex.Message}",
                    LocalizationService.GetString("ExportError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            FileMenuButton_Click(FileMenuButton, e);
        }

        private void ExportTextMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ExportFile(
                "ExportTextDialogFilter",
                ".txt",
                path => NoteFileService.SavePlainText(path, GetContentTextForFileExport()));
        }

        private void ExportRtfMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ExportFile(
                "ExportRtfDialogFilter",
                ".rtf",
                path =>
                {
                    using var tableWidths = _tableLayout.UsePreferredWidths(ContentTextBox.Document);
                    ClearSpellCheckDecorations();
                    var wrapState = _wrapLayout?.CaptureWrapState();
                    _wrapLayout?.Clear();
                    try
                    {
                        SaveWithInlineMarkers(() =>
                            NoteFileService.SaveTextRange(path, GetCurrentEditorTextRange(), DataFormats.Rtf));
                    }
                    finally
                    {
                        _wrapLayout?.RestoreWrapState(wrapState);
                    }
                    _ = RunSpellCheckPassAsync();
                });
        }

        private void ExportNotePackageMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ExportFile(
                "ExportNotePackageDialogFilter",
                ".scribnote",
                path =>
                {
                    var wrapState = _wrapLayout?.CaptureWrapState();
                    _wrapLayout?.Clear();
                    try
                    {
                        NoteFileService.SaveNotePackage(path, CreateExportNoteDocument());
                    }
                    finally
                    {
                        _wrapLayout?.RestoreWrapState(wrapState);
                    }
                });
        }

        private void ExportFile(string filterResourceKey, string defaultExtension, Action<string> saveAction)
        {
            try
            {
                if (!TryChooseExportPath(filterResourceKey, defaultExtension, out var path))
                    return;

                saveAction(path);

                ModernMessageBox.Show(
                    LocalizationService.GetString("ExportComplete"),
                    LocalizationService.GetString("Success"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("FailedToExport")}\n\n{ex.Message}",
                    LocalizationService.GetString("ExportError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private bool TryChooseExportPath(string filterResourceKey, string defaultExtension, out string path)
        {
            var fallbackTitle = LocalizationService.GetString("NewNoteTitle");
            var dialog = new SaveFileDialog
            {
                AddExtension = true,
                DefaultExt = defaultExtension,
                FileName = NoteFileService.CreateSafeFileName(TitleTextBox.Text, fallbackTitle),
                Filter = LocalizationService.GetString(filterResourceKey),
                Title = LocalizationService.GetString("Export")
            };

            if (dialog.ShowDialog(GetDialogOwnerWindow()) == true)
            {
                path = dialog.FileName;
                return true;
            }

            path = string.Empty;
            return false;
        }

        private TextRange GetCurrentEditorTextRange()
            => new(ContentTextBox.Document.ContentStart, ContentTextBox.Document.ContentEnd);

        private string GetContentTextForFileExport()
        {
            var text = BuildDocumentPlainTextForFileExport();

            if (text.EndsWith("\r\n", StringComparison.Ordinal))
                return text[..^2];

            if (text.EndsWith("\n", StringComparison.Ordinal) || text.EndsWith("\r", StringComparison.Ordinal))
                return text[..^1];

            return text;
        }

        private string BuildDocumentPlainTextForFileExport()
        {
            var lines = new List<string>();
            AppendBlocksPlainTextForFileExport(ContentTextBox.Document.Blocks, lines);
            return string.Join("\r\n", lines);
        }

        private void AppendBlocksPlainTextForFileExport(BlockCollection blocks, ICollection<string> lines)
        {
            foreach (Block block in blocks)
            {
                switch (block)
                {
                    case Paragraph paragraph:
                        AppendParagraphPlainTextForFileExport(paragraph, lines);
                        break;
                    case Section section:
                        AppendBlocksPlainTextForFileExport(section.Blocks, lines);
                        break;
                    case System.Windows.Documents.List list:
                        foreach (ListItem item in list.ListItems)
                            AppendBlocksPlainTextForFileExport(item.Blocks, lines);
                        break;
                    case Table table:
                        AppendTablePlainTextForFileExport(table, lines);
                        break;
                    default:
                        AppendPlainTextLinesForFileExport(
                            new TextRange(block.ContentStart, block.ContentEnd).Text,
                            string.Empty,
                            lines);
                        break;
                }
            }
        }

        private void AppendTablePlainTextForFileExport(Table table, ICollection<string> lines)
        {
            foreach (TableRowGroup rowGroup in table.RowGroups)
            {
                foreach (TableRow row in rowGroup.Rows)
                {
                    var cells = new List<string>();
                    foreach (TableCell cell in row.Cells)
                        cells.Add(GetTableCellPlainText(cell));

                    lines.Add(string.Join('\t', cells));
                }
            }
        }

        private void AppendParagraphPlainTextForFileExport(Paragraph paragraph, ICollection<string> lines)
        {
            var prefix = ResolveParagraphMarginIndentClipboardPrefix(paragraph);
            var paragraphText = BuildListParagraphClipboardText(paragraph);
            AppendPlainTextLinesForFileExport(paragraphText, prefix, lines);
        }

        private void AppendPlainTextLinesForFileExport(
            string text,
            string prefix,
            ICollection<string> lines)
        {
            text = TrimSingleTrailingLineBreak(text)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');

            foreach (var line in text.Split('\n'))
            {
                var normalizedLine = NormalizeLeadingIndentationForPlainTextExport(line, out _);
                lines.Add(prefix + normalizedLine);
            }
        }

        // Backs NotePackage and PDF export - both must never carry spell-check squiggles into the
        // exported file, so clear before serializing and reapply right after (fire-and-forget;
        // dictionaries are normally already cached, so this is effectively instant).
        private NoteDocument CreateExportNoteDocument()
        {
            var cardPreview = CaptureCardPreview();
            ClearSpellCheckDecorations();
            var document = new NoteDocument
            {
                Title = TitleTextBox.Text,
                Content = SerializeEditorContent(),
                // Exports/packages must be self-contained - embed the image bytes as Base64
                // instead of the file references the regular save uses.
                Images = _floatingImages.BuildSelfContainedAttachments().ToList(),
                Tags = ParseTags(TagsTextBox.Text),
                FontFamily = GetPrimaryFontFamily(ContentTextBox.FontFamily.Source),
                FontSize = ContentTextBox.FontSize,
                IsEditorFontPanelOpen = _isFontPanelOpen,
                IsEditorTagsPanelOpen = _isTagsPanelOpen,
                IsWordWrapEnabled = _isWordWrapEnabled,
                PreviewLayoutWidth = cardPreview.LayoutWidth,
                AiReviewNoticeDismissed = _currentDocument?.AiReviewNoticeDismissed == true,
                CreatedAt = _currentDocument?.CreatedAt ?? DateTime.UtcNow,
                LastModified = DateTime.Now
            };
            document.CardPreview = NotePreviewService.Complete(cardPreview, NotePreviewService.SourceHash(document));
            document.SearchablePreview = null;
            _ = RunSpellCheckPassAsync();
            return document;
        }

        private NotePreviewSnapshot CaptureCardPreview()
        {
            var width = NotePreviewService.ResolveWidth(ContentTextBox.ActualWidth - ContentTextBox.Padding.Left - ContentTextBox.Padding.Right);
            return NotePreviewService.Capture(ContentTextBox.Document, _floatingImages.PreviewImages(),
                ContentTextBox, width, _floatingImages.CaptureViewState().ScrollOffset);
        }

        private bool ExportToPdf(string title)
        {
            var exportDoc = new FlowDocument();
            exportDoc.PageWidth = PdfPageWidth;  // A4 at 96 DPI
            exportDoc.PageHeight = PdfPageHeight;
            exportDoc.ColumnWidth = PdfPageWidth - (PdfPagePadding * 2);
            exportDoc.PagePadding = new Thickness(PdfPagePadding);

            // Add title as FIRST paragraph (from TitleTextBox only)
            var titleParagraph = new Paragraph(new Run(title))
            {
                FontSize = TypeScale.Px(TypeScale.Display, 24),
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                Margin = new Thickness(0, 0, 0, 30)
            };
            exportDoc.Blocks.Add(titleParagraph);

            // Add separator line
            var separator = new Paragraph(new Run("────────────────────────────────────────────────"))
            {
                FontSize = TypeScale.Px(TypeScale.Chip, 10),
                Foreground = Brushes.LightGray,
                Margin = new Thickness(0, 0, 0, 20)
            };
            exportDoc.Blocks.Add(separator);

            var exportNote = CreateExportNoteDocument();
            var contentClone = CreatePdfContentDocument(exportNote);
            if (contentClone != null && contentClone.Blocks.FirstBlock != null)
            {
                while (contentClone.Blocks.FirstBlock != null)
                {
                    var block = contentClone.Blocks.FirstBlock;
                    contentClone.Blocks.Remove(block);
                    exportDoc.Blocks.Add(block);
                }
            }
            else
            {
                exportDoc.Blocks.Add(new Paragraph(new Run(LocalizationService.GetString("NoContent")))
                {
                    FontSize = TypeScale.Px(TypeScale.Caption, 12),
                    Foreground = Brushes.Gray,
                    FontStyle = FontStyles.Italic
                });
            }

            var printableDocument = CreatePdfFixedDocument(exportDoc);
            var printDialog = new PrintDialog();

            using var printServer = new System.Printing.PrintServer();
            using var printQueue = new System.Printing.PrintQueue(printServer, "Microsoft Print to PDF");

            printDialog.PrintQueue = printQueue;
            var queuedJobsBefore = GetQueuedJobCount(printQueue);

            printDialog.PrintDocument(
                printableDocument.DocumentPaginator,
                title);

            System.Threading.Thread.Sleep(150);
            var queuedJobsAfter = GetQueuedJobCount(printQueue);

            if (queuedJobsBefore < 0 || queuedJobsAfter < 0)
                return true;

            return queuedJobsAfter > queuedJobsBefore;
        }

        private FlowDocument? CreatePdfContentDocument(NoteDocument exportNote)
        {
            return LoadFlowDocumentFromStoredContent(exportNote.Content);
        }

        private FixedDocument CreatePdfFixedDocument(FlowDocument sourceDocument)
        {
            sourceDocument.PageWidth = PdfPageWidth;
            sourceDocument.PageHeight = PdfPageHeight;
            sourceDocument.PagePadding = new Thickness(PdfPagePadding);
            sourceDocument.ColumnWidth = PdfPageWidth - (PdfPagePadding * 2);
            new EditorTableLayout().Apply(sourceDocument, PdfPageWidth - (PdfPagePadding * 2));

            var paginator = ((IDocumentPaginatorSource)sourceDocument).DocumentPaginator;
            paginator.PageSize = new Size(PdfPageWidth, PdfPageHeight);
            paginator.ComputePageCount();

            var fixedDocument = new FixedDocument();
            fixedDocument.DocumentPaginator.PageSize = new Size(PdfPageWidth, PdfPageHeight);

            var pageCount = Math.Max(1, paginator.PageCount);
            for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
            {
                var documentPage = paginator.GetPage(pageIndex);
                var fixedPage = new FixedPage
                {
                    Width = PdfPageWidth,
                    Height = PdfPageHeight,
                    Background = Brushes.White
                };

                fixedPage.Children.Add(new Rectangle
                {
                    Width = PdfPageWidth,
                    Height = PdfPageHeight,
                    Fill = new VisualBrush(documentPage.Visual)
                    {
                        Stretch = Stretch.None,
                        AlignmentX = AlignmentX.Left,
                        AlignmentY = AlignmentY.Top,
                        ViewboxUnits = BrushMappingMode.Absolute,
                        Viewbox = new Rect(0, 0, PdfPageWidth, PdfPageHeight),
                        ViewportUnits = BrushMappingMode.Absolute,
                        Viewport = new Rect(0, 0, PdfPageWidth, PdfPageHeight)
                    }
                });

                fixedPage.Measure(new Size(PdfPageWidth, PdfPageHeight));
                fixedPage.Arrange(new Rect(0, 0, PdfPageWidth, PdfPageHeight));
                fixedPage.UpdateLayout();

                var pageContent = new PageContent();
                ((IAddChild)pageContent).AddChild(fixedPage);
                fixedDocument.Pages.Add(pageContent);
            }

            return fixedDocument;
        }

        private static FlowDocument? LoadFlowDocumentFromStoredContent(string? content)
        {
            if (string.IsNullOrEmpty(content))
                return null;

            var document = new FlowDocument();
            var range = new TextRange(document.ContentStart, document.ContentEnd);

            try
            {
                var bytes = Convert.FromBase64String(content);
                if (bytes.Length >= 5)
                {
                    var header = Encoding.ASCII.GetString(bytes, 0, Math.Min(5, bytes.Length));
                    using var stream = new MemoryStream(bytes);

                    if (header.StartsWith("{\\rtf", StringComparison.Ordinal))
                    {
                        range.Load(stream, DataFormats.Rtf);
                        return document;
                    }

                    if (bytes[0] == 0x50 && bytes[1] == 0x4B)
                    {
                        range.Load(stream, DataFormats.XamlPackage);
                        return document;
                    }
                }
            }
            catch (FormatException)
            {
                range.Text = content;
                return document;
            }
            catch (Exception ex)
            {
                CrashLog.Append("NoteEditorWindow.LoadFlowDocumentFromStoredContent", ex);
            }

            range.Text = content;
            return document;
        }

        private static TextRange? FindTextRange(FlowDocument document, string text)
        {
            var navigator = document.ContentStart;
            while (navigator != null && navigator.CompareTo(document.ContentEnd) < 0)
            {
                var runText = navigator.GetTextInRun(LogicalDirection.Forward);
                if (!string.IsNullOrEmpty(runText))
                {
                    var index = runText.IndexOf(text, StringComparison.Ordinal);
                    if (index >= 0)
                    {
                        var start = navigator.GetPositionAtOffset(index, LogicalDirection.Forward);
                        var end = start?.GetPositionAtOffset(text.Length, LogicalDirection.Forward);
                        if (start != null && end != null)
                            return new TextRange(start, end);
                    }
                }

                navigator = navigator.GetNextContextPosition(LogicalDirection.Forward);
            }

            return null;
        }

        private static int GetQueuedJobCount(System.Printing.PrintQueue printQueue)
        {
            try
            {
                printQueue.Refresh();
                return printQueue.GetPrintJobInfoCollection().Count();
            }
            catch
            {
                return -1;
            }
        }

        private static List<string> ParseTags(string? rawTags)
        {
            if (string.IsNullOrWhiteSpace(rawTags))
                return new List<string>();

            return rawTags
                .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries)
                .Select(tag => tag.Trim())
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static FlowDocument? CloneFlowDocument(FlowDocument source)
        {
            var xaml = XamlWriter.Save(source);
            return XamlReader.Parse(xaml) as FlowDocument;
        }

        private void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            TableAwareUndo(false);
        }

        private void RedoButton_Click(object sender, RoutedEventArgs e)
        {
            TableAwareUndo(true);
        }

        private void EditHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDocument == null)
                return;

            if (_currentDocument.EditHistory == null || _currentDocument.EditHistory.Count == 0)
            {
                ModernMessageBox.Show(
                    LocalizationService.GetString("NoEditHistory"),
                    LocalizationService.GetString("EditHistoryTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var dialog = new EditHistoryWindow(_currentDocument.EditHistory)
            {
                Owner = GetDialogOwnerWindow()
            };

            if (dialog.ShowDialog() != true || dialog.SelectedVersion == null)
                return;

            ApplyContentToEditor(dialog.SelectedVersion.Content);
            if (_workspaceSave is not null)
            {
                if (_workspaceSave()) ShowStatusIndicator(LocalizationService.GetString("VersionRestored"));
                return;
            }
            SaveToDocument(_currentDocument);
            MarkCurrentStateSaved();

            if (Application.Current.MainWindow?.DataContext is MainViewModel mainVm)
            {
                mainVm.SaveNotes();
            }

            DocumentAutoSaved?.Invoke(_currentDocument);
            ShowStatusIndicator(LocalizationService.GetString("VersionRestored"));
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_workspaceSave is not null) { _workspaceSave(); return; }
            // Mark the close FIRST: the save below skips the wrap engine's transient re-apply
            // (it would be disposed moments later anyway) so the exit animation isn't preceded
            // by a synchronous full convergence pass (tens of ms with photos).
            _allowCloseWithoutPrompt = true;
            if (!SaveCurrentDocument()) { _allowCloseWithoutPrompt = false; return; }

            // Close the window after saving
            if (_isHostedInTab)
                CloseRequested?.Invoke(this);
            else
                Close();
        }

        private bool SaveCurrentDocument()
        {
            if (_workspaceSave is not null) return _workspaceSave();
            if (_currentDocument == null)
                return true;

            try
            {
                SaveToDocument(_currentDocument);
                if (Application.Current.MainWindow?.DataContext is MainViewModel mainVm)
                {
                    mainVm.RefreshTagFiltersAfterNoteEdit();
                    if (!mainVm.TrySaveNotes()) { ReportWorkspaceSaveFailure(); return false; }
                }
                MarkCurrentStateSaved();
                ShowAutoSaveIndicator();
                DocumentAutoSaved?.Invoke(_currentDocument);
                return true;
            }
            catch (Exception ex)
            {
                CrashLog.Append("SaveNote", ex);
                ReportWorkspaceSaveFailure();
                return false;
            }
        }

        private void ApplyContentToEditor(string? content)
        {
            _suspendEditorEdgeSpacingForLoad = true;
            try
            {
                SuspendEditorEdgeSpacing();
                TextRange tr = new TextRange(ContentTextBox.Document.ContentStart, ContentTextBox.Document.ContentEnd);

                if (string.IsNullOrEmpty(content))
                {
                    ListEditor.ReplaceContent(string.Empty);
                    SyncFontSelectorsFromSelection();
                    return;
                }

                try
                {
                    byte[] bytes = Convert.FromBase64String(content);
                    if (bytes.Length >= 5)
                    {
                        var hdr = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(5, bytes.Length));
                        if (hdr.StartsWith("{\\rtf"))
                        {
                            using (MemoryStream ms = new MemoryStream(bytes))
                            {
                                ListEditor.LoadContent(ms, DataFormats.Rtf);
                            }
                        }
                        else if (bytes[0] == 0x50 && bytes[1] == 0x4B) // ZIP / XamlPackage
                        {
                            using (MemoryStream ms = new MemoryStream(bytes))
                            {
                                ListEditor.LoadContent(ms, DataFormats.XamlPackage);
                            }
                        }
                        else
                        {
                            throw new FormatException();
                        }
                    }
                }
                catch (FormatException)
                {
                    ListEditor.ReplaceContent(content ?? string.Empty);
                }

                SyncFontSelectorsFromSelection();
            }
            finally
            {
                _suspendEditorEdgeSpacingForLoad = false;
                RemoveLegacyEditorEndPads();
                ListEditor.NormalizeLegacy(resetHistory: false);
            }
        }

        private void ShowStatusIndicator(string message)
        {
            ShowPersistentStatusIndicator(message);

            UiMotion.Run(
                StatusIndicatorText,
                new[]
                {
                    UiMotion.Track(
                        800,
                        UiMotion.OutQuad,
                        1,
                        0,
                        value => StatusIndicatorText.Opacity = value,
                        () =>
                        {
                            StatusIndicatorText.Visibility = Visibility.Collapsed;
                            StatusIndicatorText.Text = string.Empty;
                            AnimateStatusIndicatorRow(expand: false);
                        },
                        startDelayMs: 1150)
                });
        }

        private void FontFamilyBox_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingFontSelectors)
                return;

            if (!TryGetSelectedComboBoxContent(FontFamilyBox, out var fontName))
                return;

            ApplyEditorFontFamily(fontName);
        }

        private void ApplyEditorFontFamily(string fontName)
        {
            var selectionChangesDocument = ContentTextBox.Selection is { IsEmpty: false };
            var fontFamily = CreateEditorFontFamily(fontName);
            ContentTextBox.Focus();
            _isApplyingTypography = true;
            if (selectionChangesDocument) ContentTextBox.BeginChange();
            try
            {
                // WPF normalizes the first insertion into an empty paragraph and can
                // discard its pending format. There is no existing text to restyle here.
                if (!selectionChangesDocument && EditorEdgeSpacing.IsEmpty(ContentTextBox.Document))
                    ContentTextBox.FontFamily = fontFamily;
                ContentTextBox.Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, fontFamily);
                if (selectionChangesDocument) ListEditor.RefreshLayout();
            }
            finally
            {
                if (selectionChangesDocument) ContentTextBox.EndChange();
                _isApplyingTypography = false;
            }

            UpdateFontButtonText();

            if (selectionChangesDocument)
                MarkEditorContentChanged();
            if (selectionChangesDocument || EditorEdgeSpacing.IsEmpty(ContentTextBox.Document))
                UpdateEditedIndicator();
        }

        private void FontSizeBox_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingFontSelectors)
                return;

            if (!TryGetSelectedComboBoxContent(FontSizeBox, out var sizeText)
                || !double.TryParse(sizeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var size))
            {
                return;
            }

            ApplyEditorFontSize(NormalizeEditorFontSize(size));
        }

        private void IncreaseFontSizeButton_Click(object sender, RoutedEventArgs e)
            => BumpEditorFontSize(+1);

        private void DecreaseFontSizeButton_Click(object sender, RoutedEventArgs e)
            => BumpEditorFontSize(-1);

        private void BumpEditorFontSize(int direction)
        {
            var currentSize = NormalizeEditorFontSize(ResolveCurrentSelectionTypography().FontSize);
            var nextSize = ResolveAdjacentFontSize(currentSize, direction);
            if (Math.Abs(nextSize - currentSize) < 0.01)
                return;

            ApplyEditorFontSize(nextSize);
            _isSyncingFontSelectors = true;
            try
            {
                SelectOrAddFontSizeItem(nextSize);
            }
            finally
            {
                _isSyncingFontSelectors = false;
            }
        }

        private static double ResolveAdjacentFontSize(double currentSize, int direction)
        {
            if (direction > 0)
            {
                foreach (var size in WordFontSizes)
                {
                    if (size > currentSize + 0.01)
                        return size;
                }

                return WordFontSizes[^1];
            }

            for (var i = WordFontSizes.Length - 1; i >= 0; i--)
            {
                if (WordFontSizes[i] < currentSize - 0.01)
                    return WordFontSizes[i];
            }

            return WordFontSizes[0];
        }

        private void ApplyEditorFontSize(double size)
        {
            size = NormalizeEditorFontSize(size);
            var selection = ContentTextBox.Selection;
            var selectionStart = selection?.Start;
            var selectionEnd = selection?.End;
            var selectionChangesDocument = selection is { IsEmpty: false };
            var isEmptyNote = !selectionChangesDocument && EditorEdgeSpacing.IsEmpty(ContentTextBox.Document);

            ContentTextBox.Focus();
            _isApplyingTypography = true;
            if (selectionChangesDocument) ContentTextBox.BeginChange();
            try
            {
                if (isEmptyNote)
                {
                    ContentTextBox.FontSize = size;
                    if (ContentTextBox.CaretPosition.Paragraph is { } emptyParagraph)
                        ApplyLineSpacingToParagraph(emptyParagraph, _lineSpacingMultiplier);
                }
                ContentTextBox.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, size);
                if (selectionChangesDocument)
                {
                    ApplyLineSpacingToInlineFormattingTargetIfNeeded(size);
                    _floatingImages.SyncInlineParagraphLineStrategies();
                    ListEditor.RefreshLayout();
                }
                else
                {
                    // WPF springloads the new font size at an empty caret. Recalculate
                    // its paragraph only once the first character has actually arrived.
                    _refreshLineSpacingAfterNextTyping = !isEmptyNote;
                }
            }
            finally
            {
                if (selectionChangesDocument) ContentTextBox.EndChange();
                _isApplyingTypography = false;
        }

            // Reselecting an empty range discards WPF's pending typing format. Keep the
            // caret's font size for the next characters, Word-style.
            if (selectionChangesDocument && selectionStart is not null && selectionEnd is not null)
                RefreshEditorSelectionLayout(selectionStart, selectionEnd);

            UpdateFontButtonText();

            if (selectionChangesDocument)
                MarkEditorContentChanged();
            if (selectionChangesDocument || isEmptyNote)
                UpdateEditedIndicator();
        }

        private void ClearFormattingButton_Click(object sender, RoutedEventArgs e)
        {
            MaterializeSelectionForAction();
            var selection = ContentTextBox.Selection;
            ListEditor.Mutate(() =>
            {
                selection.ClearAllProperties();
                // Normalize brushes left unset by WPF and retain list marker alignment.
                ApplySelectionColor(TextElement.ForegroundProperty, null);
                ApplySelectionColor(TextElement.BackgroundProperty, null);
                selection.ApplyPropertyValue(Inline.TextDecorationsProperty, new TextDecorationCollection());
                ListEditor.RefreshLayout();
            });

            SyncFontSelectorsFromSelection();
            UpdateFontButtonText();
            MarkEditorContentChanged();
            UpdateEditedIndicator();
            ContentTextBox.Focus();
        }

        private void LineSpacingBox_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingLineSpacingSelector)
                return;

            if (LineSpacingBox.SelectedItem is not ComboBoxItem item || item.Content == null)
                return;

            var spacingText = item.Content.ToString();
            if (string.IsNullOrWhiteSpace(spacingText)
                || !double.TryParse(spacingText, NumberStyles.Float, CultureInfo.InvariantCulture, out var spacing))
            {
                return;
            }

            _lineSpacingMultiplier = NormalizeLineSpacingMultiplier(spacing);
            _isApplyingTypography = true;
            try
            {
                ApplyLineSpacingToTargetParagraphs(_lineSpacingMultiplier);
            }
            finally
            {
                _isApplyingTypography = false;
            }

            MarkEditorContentChanged();
            UpdateEditedIndicator();
        }

        private void IndentSizeBox_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingIndentSizeSelector)
                return;

            if (IndentSizeBox.SelectedItem is not ComboBoxItem item)
                return;

            var indentText = item.Tag?.ToString() ?? item.Content?.ToString();
            if (string.IsNullOrWhiteSpace(indentText)
                || !double.TryParse(
                    indentText.Replace("px", string.Empty, StringComparison.OrdinalIgnoreCase).Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var indentSize))
            {
                return;
            }

            var previousIndentSize = _indentSize;
            _indentSize = NormalizeIndentSize(indentSize);
            RunListEdit(() => ListEditor.ChangeIndentStep(previousIndentSize));
            MarkEditorContentChanged();
            UpdateEditedIndicator();
        }

        private void FontSettings_Click(object sender, RoutedEventArgs e)
        {
            if (!_isFontPanelOpen) MaterializeSelectionForAction();
            SetFontPanelOpen(!_isFontPanelOpen, animate: true);
            UpdateFontButtonText();
        }

        private void SetFontPanelOpen(bool isOpen, bool animate, bool persist = true)
        {
            _isFontPanelOpen = isOpen;
            if (persist)
            {
                SaveEditorFontPanelPreference(isOpen);
                PersistCurrentDocumentEditorUiState();
            }

            UpdateEditorToolButtonStates();

            if (isOpen)
                SyncFontSelectorsFromSelection();

            if (!animate)
            {
                UiMotion.AnimateLayoutReveal(FontPanel, isOpen, animate: false, row: FontPanelRow);
                FontPanelRow.Height = isOpen ? GridLength.Auto : new GridLength(0);
                return;
            }

            if (isOpen)
                FontPanel.Visibility = Visibility.Visible;

            AnimateCollapsingRow(
                FontPanelRow,
                FontPanel,
                isOpen,
                () =>
                {
                    if (!_isFontPanelOpen)
                        FontPanel.Visibility = Visibility.Collapsed;
                },
                onExpanded: () =>
                {
                    if (_isFontPanelOpen)
                    {
                        UiMotion.Clear(FontPanelRow, RowDefinition.HeightProperty);
                        FontPanelRow.Height = GridLength.Auto;
                    }
                });
        }

        private void TagsPanelToggleButton_Click(object sender, RoutedEventArgs e)
            // persist: false - the click-driven immediate SaveNotes() rewrite was the expensive/
            // visible "auto-saving" trigger; HasContentChanged() now picks this up on the next
            // natural autosave tick instead (see its comment).
            => SetTagsPanelOpen(!_isTagsPanelOpen, animate: true, persist: false);

        private void SetTagsPanelOpen(bool isOpen, bool animate, bool persist = true)
        {
            _isTagsPanelOpen = isOpen;
            if (persist) PersistCurrentDocumentEditorUiState();
            UpdateEditorToolButtonStates();
            UiMotion.AnimateLayoutReveal(TagsPanel, isOpen, animate: animate);
        }

        private void ApplyLineSpacingToTargetParagraphs(double spacingMultiplier)
        {
            var selection = ContentTextBox.Selection;
            var start = selection == null || selection.IsEmpty
                ? ContentTextBox.Document.ContentStart
                : selection.Start;
            var end = selection == null || selection.IsEmpty
                ? ContentTextBox.Document.ContentEnd
                : selection.End;

            ApplyLineSpacingToRange(start, end, spacingMultiplier);
        }

        private void ApplyLineSpacingToInlineFormattingTarget(double? minimumFontSize = null)
        {
            foreach (var paragraph in GetTargetParagraphsForInlineFormatting())
                ApplyLineSpacingToParagraph(paragraph, _lineSpacingMultiplier, minimumFontSize);
        }

        private void ApplyLineSpacingToInlineFormattingTargetIfNeeded(double minimumFontSize)
        {
            foreach (var paragraph in GetTargetParagraphsForInlineFormatting())
            {
                var targetLineHeight = ResolveParagraphLineHeight(paragraph, _lineSpacingMultiplier, minimumFontSize);
                if (paragraph.LineStackingStrategy == LineStackingStrategy.MaxHeight
                    && Math.Abs(paragraph.LineHeight - targetLineHeight) < 0.01)
                {
                    continue;
                }

                ApplyLineSpacingToParagraph(paragraph, _lineSpacingMultiplier, minimumFontSize);
            }
        }

        private void ApplyCurrentLineSpacingToDocument()
        {
            if (ContentTextBox.Document == null)
                return;

            ApplyLineSpacingToRange(
                ContentTextBox.Document.ContentStart,
                ContentTextBox.Document.ContentEnd,
                _lineSpacingMultiplier);

            // Paragraphs hosting in-line images need MaxHeight so the line grows to fit them.
            _floatingImages.SyncInlineParagraphLineStrategies();
        }

        private void ApplyLineSpacingToRange(TextPointer start, TextPointer end, double spacingMultiplier)
        {
            NormalizeVerticalBlockMargins(ContentTextBox.Document.Blocks, start, end);

            var paragraphs = new List<Paragraph>();
            CollectParagraphs(ContentTextBox.Document.Blocks, start, end, paragraphs);
            // A paste ending at an empty paragraph has no content span to intersect,
            // but that paragraph still needs the preferred spacing for the next keystroke.
            if (start.Paragraph is { } first && first.ContentStart.CompareTo(first.ContentEnd) == 0
                && !paragraphs.Contains(first))
                paragraphs.Add(first);
            if (end.Paragraph is { } last && last.ContentStart.CompareTo(last.ContentEnd) == 0
                && !paragraphs.Contains(last))
                paragraphs.Add(last);
            foreach (var paragraph in paragraphs)
                ApplyLineSpacingToParagraph(paragraph, spacingMultiplier);
        }

        private void ApplyLineSpacingToParagraph(Paragraph paragraph, double spacingMultiplier, double? minimumFontSize = null)
        {
            // LineHeight is the font's own single line times the spacing step, and a minimum.
            // WPF may still grow the line around a taller glyph.
            if (paragraph.LineStackingStrategy != LineStackingStrategy.MaxHeight)
                paragraph.LineStackingStrategy = LineStackingStrategy.MaxHeight;
            var lineHeight = ResolveParagraphLineHeight(paragraph, spacingMultiplier, minimumFontSize);
            if (double.IsNaN(paragraph.LineHeight) || Math.Abs(paragraph.LineHeight - lineHeight) >= 0.01)
                paragraph.LineHeight = lineHeight;
            NormalizeVerticalBlockMarginIfNeeded(paragraph);
        }

        internal static void NormalizeVerticalBlockMargins(
            BlockCollection blocks,
            TextPointer start,
            TextPointer end)
        {
            foreach (Block block in blocks)
            {
                switch (block)
                {
                    case Paragraph paragraph when TextElementIntersectsRange(paragraph, start, end):
                        NormalizeVerticalBlockMarginIfNeeded(paragraph);
                        break;
                    case BlockUIContainer blockContainer when TextElementIntersectsRange(blockContainer, start, end):
                        NormalizeVerticalBlockMarginIfNeeded(blockContainer);
                        break;
                    case System.Windows.Documents.List list when TextElementIntersectsRange(list, start, end):
                        NormalizeVerticalBlockMarginIfNeeded(list);
                        foreach (ListItem item in list.ListItems)
                        {
                            if (!TextElementIntersectsRange(item, start, end))
                                continue;
                            NormalizeVerticalListItemMarginIfNeeded(item);
                            NormalizeVerticalBlockMargins(item.Blocks, start, end);
                        }
                        break;
                    case Section section when TextElementIntersectsRange(section, start, end):
                        NormalizeVerticalBlockMarginIfNeeded(section);
                        NormalizeVerticalBlockMargins(section.Blocks, start, end);
                        break;
                    case Table table when TextElementIntersectsRange(table, start, end):
                        NormalizeVerticalBlockMarginIfNeeded(table);
                        foreach (TableRowGroup rowGroup in table.RowGroups)
                        {
                            foreach (TableRow row in rowGroup.Rows)
                            {
                                foreach (TableCell cell in row.Cells)
                                    NormalizeVerticalBlockMargins(cell.Blocks, start, end);
                            }
                        }
                        break;
                }
            }
        }

        private static void NormalizeVerticalBlockMarginIfNeeded(Block block)
        {
            if (block.Margin.Top != 0 || block.Margin.Bottom != 0)
                block.Margin = NormalizeVerticalBlockMargin(block.Margin);
        }

        private static void NormalizeVerticalListItemMarginIfNeeded(ListItem item)
        {
            if (item.Margin.Top != 0 || item.Margin.Bottom != 0)
                item.Margin = NormalizeVerticalBlockMargin(item.Margin);
        }

        private static Thickness NormalizeVerticalBlockMargin(Thickness margin)
            => new(margin.Left, 0, margin.Right, 0);

        private List<Paragraph> GetTargetParagraphsForInlineFormatting()
        {
            var selection = ContentTextBox.Selection;
            if (selection == null)
                return new List<Paragraph>();

            if (selection.IsEmpty)
            {
                var paragraph = selection.Start.Paragraph ?? ContentTextBox.CaretPosition?.Paragraph;
                return paragraph == null ? new List<Paragraph>() : new List<Paragraph> { paragraph };
            }

            var paragraphs = new List<Paragraph>();
            CollectParagraphs(ContentTextBox.Document.Blocks, selection.Start, selection.End, paragraphs);
            return paragraphs;
        }

        private static void CollectParagraphs(
            BlockCollection blocks,
            TextPointer start,
            TextPointer end,
            ICollection<Paragraph> paragraphs)
        {
            foreach (Block block in blocks)
            {
                switch (block)
                {
                    case Paragraph paragraph when TextElementIntersectsRange(paragraph, start, end):
                        paragraphs.Add(paragraph);
                        CollectParagraphsFromInlines(paragraph.Inlines, start, end, paragraphs);
                        break;
                    case Section section:
                        CollectParagraphs(section.Blocks, start, end, paragraphs);
                        break;
                    case System.Windows.Documents.List list:
                        foreach (ListItem item in list.ListItems)
                            CollectParagraphs(item.Blocks, start, end, paragraphs);
                        break;
                    case Table table:
                        foreach (TableRowGroup rowGroup in table.RowGroups)
                        {
                            foreach (TableRow row in rowGroup.Rows)
                            {
                                foreach (TableCell cell in row.Cells)
                                    CollectParagraphs(cell.Blocks, start, end, paragraphs);
                            }
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Floater/Figure (AnchoredBlock) host their own Blocks inside a paragraph's inlines;
        /// their paragraphs need the same spacing/stacking treatment as regular ones.
        /// </summary>
        private static void CollectParagraphsFromInlines(
            InlineCollection inlines,
            TextPointer start,
            TextPointer end,
            ICollection<Paragraph> paragraphs)
        {
            foreach (Inline inline in inlines)
            {
                if (inline is AnchoredBlock anchoredBlock)
                    CollectParagraphs(anchoredBlock.Blocks, start, end, paragraphs);
            }
        }

        private static bool TextElementIntersectsRange(TextElement element, TextPointer start, TextPointer end)
        {
            try
            {
                return element.ContentEnd.CompareTo(start) > 0
                    && element.ContentStart.CompareTo(end) < 0;
            }
            catch
            {
                return false;
            }
        }

        private readonly record struct InlineFontMeasure(double FontSize, double SingleSpacing);

        private readonly record struct ParagraphLineBasis(double FontSize, double SingleLine);

        private double ResolveParagraphLineHeight(Paragraph paragraph, double spacingMultiplier, double? minimumFontSize = null)
        {
            var basis = ResolveParagraphLineBasis(paragraph);
            if (minimumFontSize is double floor && floor > basis.FontSize && basis.FontSize > 0)
            {
                var ratio = basis.SingleLine / basis.FontSize;
                basis = new ParagraphLineBasis(floor, floor * ratio);
            }

            return EditorLineSpacing.LineHeight(basis.SingleLine, spacingMultiplier);
        }

        private ParagraphLineBasis ResolveParagraphLineBasis(Paragraph paragraph)
        {
            var fontSize = 0d;
            var singleLine = 0d;
            foreach (var measure in EnumerateInlineFontSizes(paragraph.Inlines))
            {
                if (measure.FontSize > fontSize)
                    fontSize = measure.FontSize;
                var line = measure.FontSize * measure.SingleSpacing;
                if (line > singleLine)
                    singleLine = line;
            }

            // A paragraph can retain an old font size after every Run was restyled.
            // Only use it when the paragraph has no text to measure yet.
            if (fontSize <= 0)
                fontSize = NormalizeFontSizeValue(paragraph.FontSize);
            if (fontSize <= 0)
                fontSize = ContentTextBox.FontSize > 0 ? ContentTextBox.FontSize : 14;
            if (singleLine <= 0)
                singleLine = fontSize * EditorLineSpacing.SingleSpacing(paragraph.FontFamily);

            return new ParagraphLineBasis(fontSize, singleLine);
        }

        private static IEnumerable<InlineFontMeasure> EnumerateInlineFontSizes(InlineCollection inlines)
        {
            foreach (Inline inline in inlines)
            {
                switch (inline)
                {
                    case Run run when run.Text.Length > 0:
                        var fontSize = NormalizeFontSizeValue(run.FontSize);
                        if (fontSize > 0)
                            yield return new InlineFontMeasure(fontSize, EditorLineSpacing.SingleSpacing(run.FontFamily));
                        break;
                    case Span span:
                        foreach (var nestedFontSize in EnumerateInlineFontSizes(span.Inlines))
                            yield return nestedFontSize;
                        break;
                    case AnchoredBlock anchoredBlock:
                        foreach (var nestedFontSize in EnumerateBlockFontSizes(anchoredBlock.Blocks))
                            yield return nestedFontSize;
                        break;
                }
            }
        }

        private static IEnumerable<InlineFontMeasure> EnumerateBlockFontSizes(BlockCollection blocks)
        {
            foreach (Block block in blocks)
            {
                switch (block)
                {
                    case Paragraph paragraph:
                    {
                        var foundText = false;
                        foreach (var nestedFontSize in EnumerateInlineFontSizes(paragraph.Inlines))
                        {
                            foundText = true;
                            yield return nestedFontSize;
                        }
                        if (!foundText)
                        {
                            yield return new InlineFontMeasure(
                                NormalizeFontSizeValue(paragraph.FontSize),
                                EditorLineSpacing.SingleSpacing(paragraph.FontFamily));
                        }
                        break;
                    }
                    case Section section:
                        foreach (var nestedFontSize in EnumerateBlockFontSizes(section.Blocks))
                            yield return nestedFontSize;
                        break;
                    case System.Windows.Documents.List list:
                        foreach (ListItem item in list.ListItems)
                        {
                            foreach (var nestedFontSize in EnumerateBlockFontSizes(item.Blocks))
                                yield return nestedFontSize;
                        }

                        break;
                    case Table table:
                        foreach (TableRowGroup rowGroup in table.RowGroups)
                        {
                            foreach (TableRow row in rowGroup.Rows)
                            {
                                foreach (TableCell cell in row.Cells)
                                {
                                    foreach (var nestedFontSize in EnumerateBlockFontSizes(cell.Blocks))
                                        yield return nestedFontSize;
                                }
                            }
                        }

                        break;
                }
            }
        }

        private static double NormalizeFontSizeValue(double fontSize)
            => double.IsNaN(fontSize) || double.IsInfinity(fontSize) || fontSize <= 0
                ? 0
                : fontSize;

        private static double NormalizeEditorFontSize(double fontSize)
        {
            if (double.IsNaN(fontSize) || double.IsInfinity(fontSize) || fontSize <= 0)
                return 14;

            return Math.Clamp(fontSize, 1, 300);
        }

        private void SyncLineSpacingSelectorFromPreference()
        {
            _isSyncingLineSpacingSelector = true;

            try
            {
                var spacingText = _lineSpacingMultiplier.ToString("0.##", CultureInfo.InvariantCulture);
                SelectComboBoxItemByContent(LineSpacingBox, spacingText, "1.0", StringComparison.Ordinal);
            }
            finally
            {
                _isSyncingLineSpacingSelector = false;
            }
        }

        /// <summary>Points the spacing control at the spacing already stored on the note.
        /// Returns false when the note has no saved line height, so the caller can apply the
        /// app preference to a note that never had one.</summary>
        private bool TrySyncLineSpacingFromDocument()
        {
            var paragraphs = new List<Paragraph>();
            var document = ContentTextBox.Document;
            CollectParagraphs(document.Blocks, document.ContentStart, document.ContentEnd, paragraphs);

            Paragraph? spaced = null;
            foreach (var paragraph in paragraphs)
            {
                if ((paragraph.LineStackingStrategy == LineStackingStrategy.BlockLineHeight
                        || paragraph.LineStackingStrategy == LineStackingStrategy.MaxHeight)
                    && !double.IsNaN(paragraph.LineHeight)
                    && paragraph.LineHeight > 0)
                {
                    spaced = paragraph;
                    break;
                }
            }

            if (spaced is null)
                return false;

            var basis = ResolveParagraphLineBasis(spaced);
            _lineSpacingMultiplier = NormalizeLineSpacingMultiplier(
                EditorLineSpacing.ReadMultiplier(spaced.LineHeight, basis.FontSize, basis.SingleLine));
            SyncLineSpacingSelectorFromPreference();
            UpgradeLegacyLineHeights();
            return true;
        }

        private void UpgradeLegacyLineHeights()
        {
            var paragraphs = new List<Paragraph>();
            var document = ContentTextBox.Document;
            CollectParagraphs(document.Blocks, document.ContentStart, document.ContentEnd, paragraphs);
            foreach (var paragraph in paragraphs)
            {
                if (double.IsNaN(paragraph.LineHeight) || paragraph.LineHeight <= 0)
                    continue;

                var basis = ResolveParagraphLineBasis(paragraph);
                if (!EditorLineSpacing.IsLegacyPointSizeStep(paragraph.LineHeight, basis.FontSize, basis.SingleLine, out var step))
                    continue;

                var lineHeight = EditorLineSpacing.LineHeight(basis.SingleLine, step);
                if (Math.Abs(paragraph.LineHeight - lineHeight) < 0.01
                    && paragraph.LineStackingStrategy == LineStackingStrategy.MaxHeight)
                    continue;

                if (paragraph.LineStackingStrategy != LineStackingStrategy.MaxHeight)
                    paragraph.LineStackingStrategy = LineStackingStrategy.MaxHeight;
                paragraph.LineHeight = lineHeight;
            }
        }

        private void SyncIndentSizeSelectorFromPreference()
        {
            _isSyncingIndentSizeSelector = true;

            try
            {
                _indentSize = NormalizeIndentSize(_indentSize);
                if (!TrySelectComboBoxItemByTag(IndentSizeBox, _indentSize))
                {
                    _indentSize = DefaultIndentSize;
                    TrySelectComboBoxItemByTag(IndentSizeBox, DefaultIndentSize);
                }
            }
            finally
            {
                _isSyncingIndentSizeSelector = false;
            }
        }

        private static double NormalizeLineSpacingMultiplier(double spacing)
        {
            if (double.IsNaN(spacing) || double.IsInfinity(spacing))
                return 1.0;

            return Math.Clamp(spacing, 0.8, 3.0);
        }

        private static double NormalizeIndentSize(double indentSize)
        {
            if (double.IsNaN(indentSize) || double.IsInfinity(indentSize))
                return DefaultIndentSize;

            var clamped = Math.Clamp(indentSize, MinimumIndentSize, MaximumIndentSize);
            foreach (var option in WordIndentSizes)
            {
                if (Math.Abs(option - clamped) < 0.1)
                    return option;
            }

            return DefaultIndentSize;
        }

        private void ToggleBoldButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleSelectionTextStyle(TextElement.FontWeightProperty, FontWeights.Bold, FontWeights.Normal);
        }

        private void ToggleItalicButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleSelectionTextStyle(TextElement.FontStyleProperty, FontStyles.Italic, FontStyles.Normal);
        }

        private void ToggleUnderlineButton_Click(object sender, RoutedEventArgs e)
            => ToggleSelectionTextDecoration(TextDecorations.Underline[0]);

        private void ToggleStrikethroughButton_Click(object sender, RoutedEventArgs e)
            => ToggleSelectionTextDecoration(TextDecorations.Strikethrough[0]);

        private void ToggleSelectionTextDecoration(TextDecoration decoration)
        {
            var selection = ContentTextBox.Selection;
            SetRangeTextDecoration(selection, decoration, !RangeHasTextDecoration(selection, decoration.Location));

            MarkEditorContentChanged();
            UpdateEditedIndicator();
            ContentTextBox.Focus();
        }

        private static bool RangeHasTextDecoration(TextRange range, TextDecorationLocation location)
        {
            var currentValue = range.GetPropertyValue(Inline.TextDecorationsProperty);
            return currentValue is TextDecorationCollection decorations && decorations.Any(d => d.Location == location);
        }

        /// <summary>Adds or removes a single decoration (by Location) within whatever decorations
        /// a range already has, so e.g. Underline and Strikethrough can coexist - a naive whole-
        /// collection replace would wipe out one when toggling/setting the other. Shared by the
        /// Underline/Strikethrough toolbar toggles and checklist item completion styling.</summary>
        private static void SetRangeTextDecoration(TextRange range, TextDecoration decoration, bool enabled)
        {
            // GetPropertyValue returns UnsetValue for a range with mixed formatting. Walking
            // text runs lets the off path clear each run, even when only some of the item's
            // words were previously struck through.
            var pointer = range.Start;
            while (pointer.CompareTo(range.End) < 0)
            {
                if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                {
                    var runText = pointer.GetTextInRun(LogicalDirection.Forward);
                    if (runText.Length > 0)
                    {
                        var next = pointer.GetPositionAtOffset(runText.Length, LogicalDirection.Forward);
                        if (next is null || next.CompareTo(pointer) <= 0)
                            break;
                        if (next.CompareTo(range.End) > 0)
                            next = range.End;

                        var run = new TextRange(pointer, next);
                        var currentValue = run.GetPropertyValue(Inline.TextDecorationsProperty);
                        var currentDecorations = currentValue as TextDecorationCollection ?? new TextDecorationCollection();
                        var hasDecoration = currentDecorations.Any(d => d.Location == decoration.Location);
                        if (hasDecoration != enabled)
                        {
                            var newDecorations = new TextDecorationCollection(
                                currentDecorations.Where(d => d.Location != decoration.Location));
                            if (enabled)
                                newDecorations.Add(decoration);

                            // An empty, typed collection means "no decorations" and is accepted
                            // by ApplyPropertyValue, unlike DependencyProperty.UnsetValue.
                            run.ApplyPropertyValue(Inline.TextDecorationsProperty, newDecorations);
                        }

                        pointer = next;
                        continue;
                    }
                }

                var stepped = pointer.GetNextContextPosition(LogicalDirection.Forward);
                if (stepped is null || stepped.CompareTo(pointer) <= 0)
                    break;
                pointer = stepped;
            }
        }

        private void TextColorButton_Click(object sender, RoutedEventArgs e)
            => OpenButtonContextMenu(TextColorButton);

        private void HighlightButton_Click(object sender, RoutedEventArgs e)
            => OpenButtonContextMenu(HighlightButton);

        private void TextColorChooseMenuItem_Click(object sender, RoutedEventArgs e)
            => ChooseAndApplySelectionColor(TextElement.ForegroundProperty);

        private void TextColorClearMenuItem_Click(object sender, RoutedEventArgs e)
            => ApplySelectionColor(TextElement.ForegroundProperty, null);

        private void HighlightChooseMenuItem_Click(object sender, RoutedEventArgs e)
            => ChooseAndApplySelectionColor(TextElement.BackgroundProperty);

        private void HighlightClearMenuItem_Click(object sender, RoutedEventArgs e)
            => ApplySelectionColor(TextElement.BackgroundProperty, null);

        private const string InternalNoteLinkScheme = "scriblucent-note";

        /// <summary>Opens the same dialog for both a plain web link and a link to another note in
        /// this app - the two share all their insertion/click mechanics, only the resulting Uri's
        /// scheme differs.</summary>
        private void InsertLinkButton_Click(object sender, RoutedEventArgs e)
        {
            MaterializeSelectionForAction();
            var selection = ContentTextBox.Selection;
            var initialText = selection is { IsEmpty: false } ? selection.Text : string.Empty;

            var dialog = new InsertHyperlinkDialog(initialText, ResolveNoteLinkOptions())
            {
                Owner = GetDialogOwnerWindow()
            };

            if (dialog.ShowDialog() != true || dialog.ResultUri is null)
                return;

            InsertOrReplaceHyperlink(dialog.LinkText, dialog.ResultUri);
        }

        private static IReadOnlyList<NoteCardViewModel> ResolveNoteLinkOptions()
        {
            if (Application.Current.MainWindow is not MainWindow mainWindow || mainWindow.DataContext is not MainViewModel mainViewModel)
                return Array.Empty<NoteCardViewModel>();

            return mainViewModel.Notes;
        }

        /// <summary>Keep selected runs when linking their existing words. A new or renamed
        /// label takes the selection's formatting before its text is replaced.</summary>
        private void InsertOrReplaceHyperlink(string displayText, Uri uri)
        {
            MaterializeSelectionForAction();
            var selection = ContentTextBox.Selection;
            var insertionPoint = selection.IsEmpty ? ContentTextBox.CaretPosition : selection.Start;
            var firstPosition = selection.Start.GetInsertionPosition(LogicalDirection.Forward) ?? selection.Start;
            var lastPosition = selection.End.GetInsertionPosition(LogicalDirection.Backward) ?? selection.End;
            bool keepRuns = !selection.IsEmpty && displayText == selection.Text
                && firstPosition.Paragraph is { } paragraph && ReferenceEquals(paragraph, lastPosition.Paragraph)
                && !SelectionContainsHyperlink(selection);
            var run = new Run(displayText);
            if (!keepRuns)
            {
                foreach (var property in new[]
                {
                    TextElement.FontFamilyProperty, TextElement.FontSizeProperty, TextElement.FontWeightProperty,
                    TextElement.FontStyleProperty, TextElement.FontStretchProperty, TextElement.BackgroundProperty,
                    Inline.TextDecorationsProperty, Inline.BaselineAlignmentProperty, FrameworkContentElement.LanguageProperty
                })
                {
                    var value = selection.GetPropertyValue(property);
                    if (value == DependencyProperty.UnsetValue)
                        value = new TextRange(firstPosition,
                            firstPosition.GetNextInsertionPosition(LogicalDirection.Forward) ?? firstPosition).GetPropertyValue(property);
                    if (value != DependencyProperty.UnsetValue) run.SetValue(property, value);
                }
            }

            ListEditor.Mutate(() =>
            {
                Hyperlink hyperlink;
                if (keepRuns)
                    hyperlink = new Hyperlink(firstPosition, lastPosition);
                else
                {
                    if (!selection.IsEmpty) selection.Text = string.Empty;
                    hyperlink = new Hyperlink(run, insertionPoint);
                }
                hyperlink.NavigateUri = uri;
                // Keep links accent-colored and undecorated; Ctrl-hint underlines are drawn on
                // HyperlinkHintOverlay so we never mutate TextDecorations via TextRange (that
                // corrupted older links and could crash on click).
                hyperlink.TextDecorations = null;
                hyperlink.Foreground = ResolveHyperlinkForeground();
                ContentTextBox.CaretPosition = hyperlink.ElementEnd;
            });
            MarkEditorContentChanged();
            UpdateEditedIndicator();
            ContentTextBox.Focus();
            if (_hyperlinkCtrlHintsVisible)
                ApplyHyperlinkCtrlHintDecorations(true);
        }

        private static bool SelectionContainsHyperlink(TextSelection selection)
        {
            if (EditorLists.Ancestor<Hyperlink>(selection.Start.Parent) is not null
                || EditorLists.Ancestor<Hyperlink>(selection.End.Parent) is not null) return true;
            for (var pointer = selection.Start; pointer is not null && pointer.CompareTo(selection.End) < 0;
                pointer = pointer.GetNextContextPosition(LogicalDirection.Forward))
                if (pointer.GetAdjacentElement(LogicalDirection.Forward) is Hyperlink) return true;
            return false;
        }

        private void ContentTextBox_HyperlinkHintPreviewKeyDown(object sender, KeyEventArgs e)
            => UpdateHyperlinkCtrlHintsFromKeyEvent(e, isDown: true);

        private void ContentTextBox_HyperlinkHintPreviewKeyUp(object sender, KeyEventArgs e)
            => UpdateHyperlinkCtrlHintsFromKeyEvent(e, isDown: false);

        private void UpdateHyperlinkCtrlHintsFromKeyEvent(KeyEventArgs e, bool isDown)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl)
            {
                // Don't trust IsKeyDown/Modifiers on the Ctrl event itself - they often lag.
                SetHyperlinkCtrlHintsVisible(isDown && !IsAltHeld());
                return;
            }

            UpdateHyperlinkCtrlHints();
        }

        private void UpdateHyperlinkCtrlHints()
            => SetHyperlinkCtrlHintsVisible(IsCtrlHeldForHyperlinkHints());

        private static bool IsAltHeld()
            => Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt);

        private static bool IsCtrlHeldForHyperlinkHints()
        {
            var ctrlHeld = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
            return ctrlHeld && !IsAltHeld();
        }

        private void SetHyperlinkCtrlHintsVisible(bool visible)
        {
            if (_hyperlinkCtrlHintsVisible == visible)
                return;

            _hyperlinkCtrlHintsVisible = visible;
            ApplyHyperlinkCtrlHintDecorations(visible);
        }

        private void ApplyHyperlinkCtrlHintDecorations(bool visible)
        {
            if (ContentTextBox?.Document is null)
                return;

            _isApplyingHyperlinkCtrlHints = true;
            try
            {
                // Set decorations on the Hyperlink and its Runs. TextDecorations does not inherit,
                // so Runs need the value directly. On hide, ClearValue (not empty collection) so
                // we don't leave sticky local formatting in the document.
                //
                // Materialize first: applying TextDecorations can make WPF reflow/split inlines
                // (InlineCollection mutation) while the lazy enumerator is still walking it -
                // "Collection was modified; enumeration operation may not execute".
                var hyperlinks = EnumerateHyperlinks(ContentTextBox.Document.Blocks).ToList();
                foreach (var hyperlink in hyperlinks)
                {
                    if (visible)
                    {
                        hyperlink.TextDecorations = TextDecorations.Underline;
                        SetInlineTextDecorations(hyperlink.Inlines, TextDecorations.Underline);
                    }
                    else
                    {
                        hyperlink.TextDecorations = null;
                        ClearInlineTextDecorations(hyperlink.Inlines);
                    }
                }
            }
            finally
            {
                _isApplyingHyperlinkCtrlHints = false;
            }
        }

        private static void SetInlineTextDecorations(InlineCollection inlines, TextDecorationCollection decorations)
        {
            // ToList: property sets below can reflow/split the collection mid-enumeration.
            foreach (var inline in inlines.ToList())
            {
                inline.TextDecorations = decorations;
                if (inline is Span span)
                    SetInlineTextDecorations(span.Inlines, decorations);
            }
        }

        private static void ClearInlineTextDecorations(InlineCollection inlines)
        {
            // ToList: ClearValue below can reflow/split the collection mid-enumeration.
            foreach (var inline in inlines.ToList())
            {
                inline.ClearValue(Inline.TextDecorationsProperty);
                if (inline is Span span)
                    ClearInlineTextDecorations(span.Inlines);
            }
        }

        /// <summary>
        /// Repair link chrome after load / after older TextRange-based hint code may have
        /// pushed decorations/foreground onto child Runs (black text + sticky blue underline).
        /// </summary>
        private void NormalizeHyperlinkAppearance()
        {
            if (ContentTextBox?.Document is null)
                return;

            _isApplyingHyperlinkCtrlHints = true;
            try
            {
                var brush = ResolveHyperlinkForeground();
                foreach (var hyperlink in EnumerateHyperlinks(ContentTextBox.Document.Blocks).ToList())
                {
                    hyperlink.TextDecorations = null;
                    hyperlink.Foreground = brush;
                    ClearInheritedInlineChrome(hyperlink.Inlines);
                }
            }
            finally
            {
                _isApplyingHyperlinkCtrlHints = false;
            }

            if (_hyperlinkCtrlHintsVisible)
                ApplyHyperlinkCtrlHintDecorations(true);
        }

        private static void ClearInheritedInlineChrome(InlineCollection inlines)
        {
            // ToList: ClearValue below can reflow/split the collection mid-enumeration.
            foreach (var inline in inlines.ToList())
            {
                inline.ClearValue(Inline.TextDecorationsProperty);
                inline.ClearValue(TextElement.ForegroundProperty);

                if (inline is Span span)
                    ClearInheritedInlineChrome(span.Inlines);
            }
        }

        private static IEnumerable<Hyperlink> EnumerateHyperlinks(BlockCollection blocks)
        {
            foreach (Block block in blocks)
            {
                foreach (var hyperlink in EnumerateHyperlinksInBlock(block))
                    yield return hyperlink;
            }
        }

        private static IEnumerable<Hyperlink> EnumerateHyperlinksInBlock(Block block)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    foreach (var hyperlink in EnumerateHyperlinksInInlines(paragraph.Inlines))
                        yield return hyperlink;
                    break;
                case Section section:
                    foreach (var hyperlink in EnumerateHyperlinks(section.Blocks))
                        yield return hyperlink;
                    break;
                case List list:
                    foreach (ListItem item in list.ListItems)
                    {
                        foreach (var hyperlink in EnumerateHyperlinks(item.Blocks))
                            yield return hyperlink;
                    }
                    break;
                case Table table:
                    foreach (TableRowGroup group in table.RowGroups)
                    {
                        foreach (TableRow row in group.Rows)
                        {
                            foreach (TableCell cell in row.Cells)
                            {
                                foreach (var hyperlink in EnumerateHyperlinks(cell.Blocks))
                                    yield return hyperlink;
                            }
                        }
                    }
                    break;
            }
        }

        private static IEnumerable<Hyperlink> EnumerateHyperlinksInInlines(InlineCollection inlines)
        {
            foreach (Inline inline in inlines)
            {
                switch (inline)
                {
                    case Hyperlink hyperlink:
                        yield return hyperlink;
                        break;
                    case Span span:
                        foreach (var nested in EnumerateHyperlinksInInlines(span.Inlines))
                            yield return nested;
                        break;
                    case InlineUIContainer:
                        break;
                }
            }
        }

        private static Brush ResolveHyperlinkForeground()
        {
            if (Application.Current.Resources.Contains("AccentPrimary")
                && Application.Current.Resources["AccentPrimary"] is Brush brush)
            {
                return brush;
            }

            return Brushes.RoyalBlue;
        }

        /// <summary>Editable RichTextBoxes don't auto-navigate a Hyperlink on click (that's a
        /// read-only-viewer default) - Ctrl+Click is handled explicitly here instead, so a plain
        /// click still just places the caret and link text stays editable.</summary>
        private Hyperlink? FindHyperlinkAtPoint(Point point)
        {
            var pointer = ContentTextBox.GetPositionFromPoint(point, snapToText: true);
            var parent = pointer?.Parent;
            while (parent is TextElement element)
            {
                if (element is Hyperlink hyperlink)
                    return hyperlink;

                parent = element.Parent;
            }

            return null;
        }

        private void ActivateHyperlink(Hyperlink hyperlink)
        {
            try
            {
                var uri = hyperlink.NavigateUri;
                if (uri is null)
                    return;

                if (string.Equals(uri.Scheme, InternalNoteLinkScheme, StringComparison.OrdinalIgnoreCase))
                {
                    NavigateToInternalNote(uri);
                    return;
                }

                Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                CrashLog.Append("ActivateHyperlink", ex);
            }
        }

        /// <summary>A link to a trashed or deleted note resolves to null via FindNoteById (which
        /// only searches the active Notes collection) and no-ops here by design - but a dead click
        /// otherwise looks like a bug, so this surfaces a brief status message instead of staying
        /// fully silent.</summary>
        private void NavigateToInternalNote(Uri uri)
        {
            if (!Guid.TryParse(uri.Host, out var targetId))
                return;

            if (Application.Current.MainWindow is not MainWindow mainWindow || mainWindow.DataContext is not MainViewModel mainViewModel)
                return;

            var targetNote = mainViewModel.FindNoteById(targetId);
            if (targetNote is null)
            {
                ShowStatusIndicator(LocalizationService.GetString("InsertLinkTargetNoteMovedToTrash"));
                return;
            }

            mainWindow.OpenNoteById(mainViewModel, targetId);
        }

        private const int DefaultInsertedTableRows = 3;
        private const int DefaultInsertedTableColumns = 3;
        private const int MaxTableSizePickerDimension = TableSizePicker.MaximumDimension;
        private void InsertTableButton_Click(object sender, RoutedEventArgs e)
        {
            if (ActiveTable() is not null) OpenTableTools();
            else OpenTableSizePicker();
        }

        private void OpenTableSizePicker()
        {
            TableSizePickerGrid.Select(DefaultInsertedTableRows, DefaultInsertedTableColumns, animate: false);
            TableSizePickerPopup.IsOpen = true;
        }

        private void TableSizePickerPopup_Opened(object? sender, EventArgs e) => TableSizePickerGrid.Focus();

        private void TableSizePicker_SelectionChanged(object? sender, EventArgs e)
            => TableSizePickerLabel.Text = TableSizePickerGrid.SelectionLabel;

        private void TableSizePicker_InsertRequested(object? sender, EventArgs e)
        {
            var rows = TableSizePickerGrid.Rows;
            var columns = TableSizePickerGrid.Columns;
            TableSizePickerPopup.IsOpen = false;
            InsertTableOfSize(rows, columns);
        }

        private void TableSizePicker_DismissRequested(object? sender, EventArgs e)
        {
            TableSizePickerPopup.IsOpen = false;
            InsertTableButton.Focus();
        }

        private void InsertTableOfSize(int rows, int columns)
        {
            var table = BuildTable(rows, columns);
            ContentTextBox.BeginChange();
            try { InsertTableAtCaret(table); }
            finally { ContentTextBox.EndChange(); }
            _tableLayout.Apply(ContentTextBox.Document, TableViewportWidth);
            FocusTableCell(table, 0, 0);
            MarkEditorContentChanged();
            UpdateEditedIndicator();
            ContentTextBox.Focus();
        }

        private static Table BuildTable(int rows, int columns)
        {
            rows = Math.Clamp(rows, 1, MaxTableSizePickerDimension);
            columns = Math.Clamp(columns, 1, MaxTableSizePickerDimension);
            return EditorTables.Create(rows, columns, ResolveTableCellBorderBrush());
        }

        private static TableCell CreateEmptyTableCell()
        {
            return EditorTables.CreateCell(ResolveTableCellBorderBrush());
        }

        private static Brush ResolveTableCellBorderBrush()
        {
            if (Application.Current?.Resources.Contains("BorderColor") == true
                && Application.Current.Resources["BorderColor"] is Brush brush)
            {
                return brush;
            }

            return Brushes.Gray;
        }

        private void InsertTableAtCaret(Table table)
        {
            var caret = ContentTextBox.CaretPosition;
            var enclosingCell = FindAncestorTableCell(caret);
            if (enclosingCell is not null)
            {
                var outerTable = FindAncestorTable(enclosingCell);
                if (outerTable is not null)
                {
                    var outerParentBlocks = GetParentBlockCollection(outerTable);
                    if (outerParentBlocks is not null)
                    {
                        outerParentBlocks.InsertAfter(outerTable, table);
                        outerParentBlocks.InsertAfter(table, new Paragraph());
                        return;
                    }
                }
            }

            var paragraph = caret?.Paragraph;
            var parentBlocks = paragraph is not null ? GetParentBlockCollection(paragraph) : null;

            if (paragraph is not null && parentBlocks is not null)
            {
                parentBlocks.InsertAfter(paragraph, table);
                parentBlocks.InsertAfter(table, new Paragraph());
            }
            else
            {
                ContentTextBox.Document.Blocks.Add(table);
                ContentTextBox.Document.Blocks.Add(new Paragraph());
            }
        }

        private bool TryNavigateTableCell(bool reverse)
        {
            var pointer = ContentTextBox.Selection?.Start ?? ContentTextBox.CaretPosition;
            var cell = FindAncestorTableCell(pointer);
            if (cell is null)
                return false;

            var table = FindAncestorTable(cell);
            if (table is null || !TryGetTableCellIndices(table, cell, out var rowIndex, out var columnIndex))
                return false;

            var rowCount = GetTableRowCount(table);
            var columnCount = Math.Max(1, table.Columns.Count);
            if (rowCount <= 0)
                return false;

            if (reverse)
            {
                if (rowIndex == 0 && columnIndex == 0)
                    return true;

                if (columnIndex > 0)
                    columnIndex--;
                else
                {
                    rowIndex--;
                    columnIndex = columnCount - 1;
                }
            }
            else
            {
                if (rowIndex == rowCount - 1 && columnIndex == columnCount - 1)
                {
                    RunTableEdit(table, () =>
                    {
                        InsertTableRow(table, rowCount);
                        FocusTableCell(table, rowCount, 0);
                    });
                    rowIndex = rowCount;
                    columnIndex = 0;
                    MarkEditorContentChanged();
                    UpdateEditedIndicator();
                }
                else if (columnIndex < columnCount - 1)
                {
                    columnIndex++;
                }
                else
                {
                    rowIndex++;
                    columnIndex = 0;
                }
            }

            FocusTableCell(table, rowIndex, columnIndex);
            return true;
        }

        private ContextMenu BuildTableContextMenu(TableCell cell)
        {
            var menu = new ContextMenu();
            var table = FindAncestorTable(cell);
            if (table is null || !TryGetTableCellIndices(table, cell, out var rowIndex, out var columnIndex))
                return _defaultContentTextBoxContextMenu ?? menu;

            MenuItem Edit(string key, Action action, bool preserveView = false)
                => CreateTableMenuItem(key, action, table, preserveView);
            menu.Items.Add(CreateTableMenuItem("InsertAnotherTable", OpenTableSizePicker));
            menu.Items.Add(new Separator());
            AddTableLayoutMenu(menu, table, cell, rowIndex, columnIndex);
            var selectedCells = EditorTables.SelectedCells(ContentTextBox.Selection.Start, ContentTextBox.Selection.End, table, cell);
            var rowCount = GetTableRowCount(table);
            var columnCount = Math.Max(1, table.Columns.Count);

            var insertMenu = CreateTableSubMenu("TableInsert");
            insertMenu.Items.Add(Edit(
                "TableInsertRowAbove",
                () =>
                {
                    InsertTableRow(table, rowIndex);
                    FocusTableCell(table, rowIndex, columnIndex);
                }));
            insertMenu.Items.Add(Edit(
                "TableInsertRowBelow",
                () =>
                {
                    InsertTableRow(table, rowIndex + 1);
                    FocusTableCell(table, rowIndex + 1, columnIndex);
                }));
            insertMenu.Items.Add(new Separator());
            insertMenu.Items.Add(Edit(
                "TableInsertColumnLeft",
                () =>
                {
                    InsertTableColumn(table, columnIndex);
                    FocusTableCell(table, rowIndex, columnIndex);
                }));
            insertMenu.Items.Add(Edit(
                "TableInsertColumnRight",
                () =>
                {
                    InsertTableColumn(table, columnIndex + 1);
                    FocusTableCell(table, rowIndex, columnIndex + 1);
                }));
            insertMenu.IsEnabled = EditorTables.IsRectangular(table);
            menu.Items.Add(insertMenu);

            var duplicateMenu = CreateTableSubMenu("TableDuplicate");
            duplicateMenu.Items.Add(Edit(
                "TableDuplicateRow",
                () =>
                {
                    DuplicateTableRow(table, rowIndex);
                    FocusTableCell(table, rowIndex + 1, columnIndex);
                }));
            duplicateMenu.Items.Add(Edit(
                "TableDuplicateColumn",
                () =>
                {
                    DuplicateTableColumn(table, columnIndex);
                    FocusTableCell(table, rowIndex, columnIndex + 1);
                }));
            duplicateMenu.IsEnabled = EditorTables.IsRectangular(table);
            menu.Items.Add(duplicateMenu);

            var moveMenu = CreateTableSubMenu("TableMove");
            var moveUpItem = Edit(
                "TableMoveRowUp",
                () =>
                {
                    if (MoveTableRow(table, rowIndex, -1))
                        FocusTableCell(table, rowIndex - 1, columnIndex);
                });
            moveUpItem.IsEnabled = rowIndex > 0;
            moveUpItem.IsEnabled &= EditorTables.IsRectangular(table);
            moveMenu.Items.Add(moveUpItem);

            var moveDownItem = Edit(
                "TableMoveRowDown",
                () =>
                {
                    if (MoveTableRow(table, rowIndex, 1))
                        FocusTableCell(table, rowIndex + 1, columnIndex);
                });
            moveDownItem.IsEnabled = rowIndex < rowCount - 1;
            moveDownItem.IsEnabled &= EditorTables.IsRectangular(table);
            moveMenu.Items.Add(moveDownItem);
            moveMenu.Items.Add(new Separator());
            var moveLeft = Edit("TableMoveColumnLeft", () =>
            {
                EditorTables.MoveColumn(table, columnIndex, -1);
                FocusTableCell(table, rowIndex, columnIndex - 1);
            });
            moveLeft.IsEnabled = columnIndex > 0 && EditorTables.IsRectangular(table);
            moveMenu.Items.Add(moveLeft);
            var moveRight = Edit("TableMoveColumnRight", () =>
            {
                EditorTables.MoveColumn(table, columnIndex, 1);
                FocusTableCell(table, rowIndex, columnIndex + 1);
            });
            moveRight.IsEnabled = columnIndex < columnCount - 1 && EditorTables.IsRectangular(table);
            moveMenu.Items.Add(moveRight);
            menu.Items.Add(moveMenu);

            var clearMenu = CreateTableSubMenu("TableClear");
            clearMenu.Items.Add(Edit(
                "TableClearCell",
                () =>
                {
                    ClearTableCell(cell);
                    FocusTableCell(table, rowIndex, columnIndex);
                }));
            clearMenu.Items.Add(Edit(
                "TableClearRow",
                () =>
                {
                    ClearTableRow(table, rowIndex);
                    FocusTableCell(table, rowIndex, columnIndex);
                }));
            clearMenu.Items.Add(Edit("TableClearColumn", () =>
            {
                foreach (var row in EditorTables.Rows(table))
                    if (columnIndex < row.Cells.Count) ClearTableCell(row.Cells[columnIndex]);
                FocusTableCell(table, rowIndex, columnIndex);
            }));
            menu.Items.Add(clearMenu);

            var alignMenu = CreateTableSubMenu("TableAlign");
            alignMenu.Items.Add(Edit(
                "TableAlignLeft",
                () => EditorTables.Align(selectedCells, TextAlignment.Left), preserveView: true));
            alignMenu.Items.Add(Edit(
                "TableAlignCenter",
                () => EditorTables.Align(selectedCells, TextAlignment.Center), preserveView: true));
            alignMenu.Items.Add(Edit(
                "TableAlignRight",
                () => EditorTables.Align(selectedCells, TextAlignment.Right), preserveView: true));
            menu.Items.Add(alignMenu);

            var selectMenu = CreateTableSubMenu("TableSelect");
            selectMenu.Items.Add(CreateTableMenuItem(
                "TableSelectRow",
                () => SelectTableRow(table, rowIndex)));
            selectMenu.Items.Add(CreateTableMenuItem(
                "TableSelectColumn",
                () => SelectTableColumn(table, columnIndex)));
            selectMenu.Items.Add(CreateTableMenuItem(
                "TableSelectTable",
                () => SelectTable(table)));
            menu.Items.Add(selectMenu);

            var deleteMenu = CreateTableSubMenu("TableDelete");
            deleteMenu.Items.Add(Edit(
                "TableDeleteRow",
                () =>
                {
                    if (rowCount <= 1)
                    {
                        DeleteTable(table);
                        return;
                    }

                    DeleteTableRow(table, rowIndex);
                    FocusTableCell(table, Math.Min(rowIndex, rowCount - 2), columnIndex);
                }));
            deleteMenu.Items.Add(Edit(
                "TableDeleteColumn",
                () =>
                {
                    if (columnCount <= 1)
                    {
                        DeleteTable(table);
                        return;
                    }

                    DeleteTableColumn(table, columnIndex);
                    FocusTableCell(table, rowIndex, Math.Min(columnIndex, columnCount - 2));
                }));
            deleteMenu.Items.Add(new Separator());
            deleteMenu.Items.Add(Edit("TableDeleteTable", () => DeleteTable(table)));
            if (!EditorTables.IsRectangular(table))
            {
                ((MenuItem)deleteMenu.Items[0]).IsEnabled = false;
                ((MenuItem)deleteMenu.Items[1]).IsEnabled = false;
            }
            menu.Items.Add(deleteMenu);

            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = LocalizationService.GetString("Cut"), Command = ApplicationCommands.Cut });
            menu.Items.Add(new MenuItem { Header = LocalizationService.GetString("Copy"), Command = ApplicationCommands.Copy });
            menu.Items.Add(new MenuItem { Header = LocalizationService.GetString("Paste"), Command = ApplicationCommands.Paste });
            if (ClipboardOffersTextPasteChoices())
            {
                menu.Items.Add(CreatePasteModeMenuItem("PasteKeepFormatting", EditorPasteMode.KeepFormatting));
                menu.Items.Add(CreatePasteModeMenuItem("PasteAsText", EditorPasteMode.TextOnly));
            }
            if (ClipboardIsPhoto())
                menu.Items.Add(CreatePasteAsPhotoMenuItem());
            if (ClipboardHasTableGrid())
                menu.Items.Add(CreatePasteAsTableMenuItem());
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = LocalizationService.GetString("SelectAll"), Command = ApplicationCommands.SelectAll });
            return menu;
        }

        private static MenuItem CreateTableSubMenu(string localizationKey)
            => new() { Header = LocalizationService.GetString(localizationKey) };

        private MenuItem CreateTableMenuItem(string localizationKey, Action action, Table? table = null, bool preserveView = false)
        {
            var item = new MenuItem { Header = LocalizationService.GetString(localizationKey) };
            item.Click += (_, _) =>
            {
                // Run after the context menu closes so FlowDocument edits are not discarded.
                Dispatcher.BeginInvoke(() =>
                {
                    if (table is not null) RunTableEdit(table, action, preserveView: preserveView);
                    else action();
                    ContentTextBox.Focus();
                }, DispatcherPriority.Input);
            };
            return item;
        }

        private static void InsertTableRow(Table table, int rowIndex)
        {
            var rows = EditorTables.Rows(table);
            rowIndex = Math.Clamp(rowIndex, 0, rows.Length);
            var rowGroup = rowIndex < rows.Length ? (TableRowGroup)rows[rowIndex].Parent : table.RowGroups.LastOrDefault();
            if (rowGroup is null)
                return;

            var columnCount = Math.Max(table.Columns.Count, 1);
            while (table.Columns.Count < columnCount)
            {
                table.Columns.Add(new TableColumn
                {
                    Width = new GridLength(EditorTables.DefaultColumnWidth)
                });
            }

            var row = new TableRow();
            for (var c = 0; c < columnCount; c++)
                row.Cells.Add(CreateEmptyTableCell());

            var insertAt = rowIndex < rows.Length ? rowGroup.Rows.IndexOf(rows[rowIndex]) : rowGroup.Rows.Count;
            rowGroup.Rows.Insert(insertAt, row);
        }

        private static void InsertTableColumn(Table table, int columnIndex)
        {
            var rowGroup = GetPrimaryTableRowGroup(table);
            if (rowGroup is null)
                return;

            columnIndex = Math.Clamp(columnIndex, 0, table.Columns.Count);
            table.Columns.Insert(columnIndex, new TableColumn
            {
                Width = new GridLength(EditorTables.DefaultColumnWidth)
            });

            foreach (var row in EditorTables.Rows(table))
            {
                var insertAt = Math.Min(columnIndex, row.Cells.Count);
                row.Cells.Insert(insertAt, CreateEmptyTableCell());
            }
        }

        private static void DuplicateTableRow(Table table, int rowIndex)
        {
            var rows = EditorTables.Rows(table);
            if (rowIndex < 0 || rowIndex >= rows.Length)
                return;
            var source = rows[rowIndex];
            var rowGroup = (TableRowGroup)source.Parent;
            var copy = new TableRow { FontWeight = source.FontWeight, Background = source.Background };
            foreach (TableCell cell in source.Cells)
                copy.Cells.Add(CloneTableCell(cell));

            rowGroup.Rows.Insert(rowGroup.Rows.IndexOf(source) + 1, copy);
        }

        private static void DuplicateTableColumn(Table table, int columnIndex)
        {
            var rowGroup = GetPrimaryTableRowGroup(table);
            if (rowGroup is null || columnIndex < 0 || columnIndex >= table.Columns.Count)
                return;

            table.Columns.Insert(columnIndex + 1, new TableColumn
            {
                Width = table.Columns[columnIndex].Width
            });

            foreach (var row in EditorTables.Rows(table))
            {
                if (columnIndex >= row.Cells.Count)
                {
                    row.Cells.Add(CreateEmptyTableCell());
                    continue;
                }

                row.Cells.Insert(columnIndex + 1, CloneTableCell(row.Cells[columnIndex]));
            }
        }

        private static bool MoveTableRow(Table table, int rowIndex, int delta)
        {
            var rows = EditorTables.Rows(table);
            var targetIndex = rowIndex + delta;
            if (rowIndex < 0 || rowIndex >= rows.Length || targetIndex < 0 || targetIndex >= rows.Length)
                return false;
            var row = rows[rowIndex];
            var target = rows[targetIndex];
            ((TableRowGroup)row.Parent).Rows.Remove(row);
            var group = (TableRowGroup)target.Parent;
            group.Rows.Insert(group.Rows.IndexOf(target) + (delta > 0 ? 1 : 0), row);
            return true;
        }

        private static void ClearTableCell(TableCell cell)
        {
            cell.Blocks.Clear();
            cell.Blocks.Add(new Paragraph());
        }

        private static void ClearTableRow(Table table, int rowIndex)
        {
            var rows = EditorTables.Rows(table);
            if (rowIndex < 0 || rowIndex >= rows.Length)
                return;
            foreach (TableCell cell in rows[rowIndex].Cells)
                ClearTableCell(cell);
        }

        private void SelectTableRow(Table table, int rowIndex)
        {
            var rows = EditorTables.Rows(table);
            if (rowIndex < 0 || rowIndex >= rows.Length)
                return;
            var row = rows[rowIndex];
            if (row.Cells.Count == 0)
                return;

            var start = row.Cells[0].ContentStart;
            var end = row.Cells[^1].ContentEnd;
            if (start is null || end is null)
                return;

            ContentTextBox.Selection.Select(start, end);
        }

        private void SelectTableColumn(Table table, int columnIndex)
        {
            var rowGroup = GetPrimaryTableRowGroup(table);
            if (rowGroup is null || rowGroup.Rows.Count == 0 || columnIndex < 0)
                return;

            TextPointer? start = null;
            TextPointer? end = null;
            foreach (var row in EditorTables.Rows(table))
            {
                if (columnIndex >= row.Cells.Count)
                    continue;

                var cell = row.Cells[columnIndex];
                start ??= cell.ContentStart;
                end = cell.ContentEnd;
            }

            if (start is null || end is null)
                return;

            ContentTextBox.Selection.Select(start, end);
        }

        private void SelectTable(Table table)
        {
            if (table.ContentStart is null || table.ContentEnd is null)
                return;

            ContentTextBox.Selection.Select(table.ContentStart, table.ContentEnd);
        }

        private static TableCell CloneTableCell(TableCell source)
        {
            var clone = CreateEmptyTableCell();
            clone.TextAlignment = source.TextAlignment;
            clone.Padding = source.Padding;
            clone.BorderThickness = source.BorderThickness;
            clone.BorderBrush = source.BorderBrush;
            clone.FontWeight = source.FontWeight;
            if (source.Background is not null)
                clone.Background = source.Background;

            var sourceRange = new TextRange(source.ContentStart, source.ContentEnd);
            using var stream = new MemoryStream();
            sourceRange.Save(stream, DataFormats.Xaml);
            stream.Position = 0;
            clone.Blocks.Clear();
            clone.Blocks.Add(new Paragraph());
            new TextRange(clone.ContentStart, clone.ContentEnd).Load(stream, DataFormats.Xaml);
            return clone;
        }

        private static void DeleteTableRow(Table table, int rowIndex)
        {
            var rows = EditorTables.Rows(table);
            if (rowIndex < 0 || rowIndex >= rows.Length)
                return;
            var row = rows[rowIndex];
            ((TableRowGroup)row.Parent).Rows.Remove(row);
        }

        private static void DeleteTableColumn(Table table, int columnIndex)
        {
            var rowGroup = GetPrimaryTableRowGroup(table);
            if (rowGroup is null || columnIndex < 0 || columnIndex >= table.Columns.Count)
                return;

            table.Columns.RemoveAt(columnIndex);
            foreach (var row in EditorTables.Rows(table))
            {
                if (columnIndex < row.Cells.Count)
                    row.Cells.RemoveAt(columnIndex);
            }
        }

        private void DeleteTable(Table table)
        {
            if (ReferenceEquals(table, _tableEditTarget)) { _tableDeleteRequested = true; return; }
            var parentBlocks = GetParentBlockCollection(table);
            parentBlocks?.Remove(table);
        }

        private void FocusTableCell(Table table, int rowIndex, int columnIndex)
        {
            if (ReferenceEquals(table, _tableEditTarget))
            {
                _tableEditFocus = (table, rowIndex, columnIndex);
                return;
            }
            if (!TryGetTableCell(table, rowIndex, columnIndex, out var cell))
                return;

            var pointer = cell.ContentStart?.GetInsertionPosition(LogicalDirection.Forward)
                ?? cell.ContentStart;
            if (pointer is null)
                return;

            ContentTextBox.CaretPosition = pointer;
            ContentTextBox.Selection.Select(pointer, pointer);
        }

        private static TableRowGroup? GetPrimaryTableRowGroup(Table table)
            => table.RowGroups.Count > 0 ? table.RowGroups[0] : null;

        private static int GetTableRowCount(Table table)
        {
            var count = 0;
            foreach (TableRowGroup rowGroup in table.RowGroups)
                count += rowGroup.Rows.Count;
            return count;
        }

        private static bool TryGetTableCell(Table table, int rowIndex, int columnIndex, out TableCell cell)
        {
            cell = null!;
            var absoluteRow = 0;
            foreach (TableRowGroup rowGroup in table.RowGroups)
            {
                foreach (TableRow row in rowGroup.Rows)
                {
                    if (absoluteRow == rowIndex)
                    {
                        if (columnIndex < 0 || columnIndex >= row.Cells.Count)
                            return false;

                        cell = row.Cells[columnIndex];
                        return true;
                    }

                    absoluteRow++;
                }
            }

            return false;
        }

        private static bool TryGetTableCellIndices(Table table, TableCell cell, out int rowIndex, out int columnIndex)
        {
            rowIndex = -1;
            columnIndex = -1;
            var absoluteRow = 0;
            foreach (TableRowGroup rowGroup in table.RowGroups)
            {
                foreach (TableRow row in rowGroup.Rows)
                {
                    for (var c = 0; c < row.Cells.Count; c++)
                    {
                        if (!ReferenceEquals(row.Cells[c], cell))
                            continue;

                        rowIndex = absoluteRow;
                        columnIndex = c;
                        return true;
                    }

                    absoluteRow++;
                }
            }

            return false;
        }

        private static TableCell? FindAncestorTableCell(TextPointer? pointer)
        {
            for (var current = pointer?.Parent as DependencyObject;
                 current is not null;
                 current = GetTextElementParent(current))
            {
                if (current is TableCell cell)
                    return cell;
            }

            return null;
        }

        private static Table? FindAncestorTable(DependencyObject start)
        {
            for (var current = start; current is not null; current = GetTextElementParent(current))
            {
                if (current is Table table)
                    return table;
            }

            return null;
        }

        private static DependencyObject? GetTextElementParent(DependencyObject current)
        {
            return current switch
            {
                FrameworkContentElement contentElement => contentElement.Parent,
                FrameworkElement element => element.Parent,
                _ => LogicalTreeHelper.GetParent(current)
            };
        }

        private void ChooseAndApplySelectionColor(DependencyProperty property)
        {
            var dialog = new ColorPickerDialog { Owner = GetDialogOwnerWindow() };
            if (dialog.ShowDialog() == true && dialog.SelectedColor.HasValue)
                ApplySelectionColor(property, dialog.SelectedColor.Value);
        }

        /// <summary>Shared by text-color and highlight - both are plain TextElement.Foreground/
        /// Background overrides on the selection, same ApplyPropertyValue mechanism Bold/Italic
        /// already use (see ToggleSelectionTextStyle). A null color clears back to the default for
        /// that property. Unlike TextDecorations, TextRange.ApplyPropertyValue does NOT accept
        /// DependencyProperty.UnsetValue for Brush-typed properties - it throws
        /// ArgumentException("'NamedObject' parameter type is not valid for formatting property")
        /// - so clearing needs a real Brush: the theme's actual text color for Foreground (same
        /// brush ApplyRichTextBoxTheme uses), Transparent for Background (highlight's natural
        /// "none").</summary>
        private void ApplySelectionColor(DependencyProperty property, Color? color)
        {
            var selection = ContentTextBox.Selection;
            selection.ApplyPropertyValue(property, ResolveSelectionColorBrush(property, color));

            MarkEditorContentChanged();
            UpdateEditedIndicator();
            ContentTextBox.Focus();
        }

        /// <summary>A real highlighter marker is translucent - it tints the page color through
        /// rather than covering it, which is also what keeps the text underneath readable. The
        /// swatch colors are picked at full opacity (so they look right as solid text-color
        /// swatches too), so highlight specifically knocks the alpha down before applying.
        ///
        /// The same alpha reads very differently depending on what it's blended against: over a
        /// light background a translucent color washes out to an obviously pale tint, but over
        /// this app's dark-theme editor background (#262626, RichTextBoxBackground) any bright
        /// color blended at the same alpha lands on a darker, still fairly saturated result that
        /// looks like a solid patch rather than a see-through one - alpha-blend math, not a bug.
        /// Dark mode needs a noticeably lower alpha to read as translucent rather than opaque.</summary>
        private const byte HighlightAlphaLight = 110; // ~43%
        private const byte HighlightAlphaDark = 55;   // ~22% - less alpha needed since blending
                                                        // toward near-black already darkens it a lot

        private static Brush ResolveSelectionColorBrush(DependencyProperty property, Color? color)
        {
            var isHighlight = property == TextElement.BackgroundProperty;

            if (color.HasValue)
            {
                if (!isHighlight)
                    return new SolidColorBrush(color.Value);

                var alpha = ThemeManager.CurrentTheme == ThemeManager.DarkTheme
                    ? HighlightAlphaDark
                    : HighlightAlphaLight;
                return new SolidColorBrush(Color.FromArgb(alpha, color.Value.R, color.Value.G, color.Value.B));
            }

            if (isHighlight)
                return Brushes.Transparent;

            return TryGetRichTextBoxForeground(out var foregroundBrush) ? foregroundBrush : Brushes.Black;
        }

        private void BulletListButton_Click(object sender, RoutedEventArgs e)
            => RunListEdit(() => ListEditor.ApplyStyle(_lastBulletStyle, toggle: true, explicitStyle: false));

        private void NumberedListButton_Click(object sender, RoutedEventArgs e)
            => RunListEdit(() => ListEditor.ApplyStyle(_lastNumberStyle, toggle: true, explicitStyle: false));

        private static void OpenButtonContextMenu(Button button)
        {
            if (button.ContextMenu == null)
                return;

            UiMotion.OpenContextMenu(button.ContextMenu, button);
        }

        private void ListBulletDiscMenuItem_Click(object sender, RoutedEventArgs e)
            => ApplyListStyle(TextMarkerStyle.Disc);

        private void ListBulletCircleMenuItem_Click(object sender, RoutedEventArgs e)
            => ApplyListStyle(TextMarkerStyle.Circle);

        private void ListBulletSquareMenuItem_Click(object sender, RoutedEventArgs e)
            => ApplyListStyle(TextMarkerStyle.Square);

        private void ListNumberDecimalMenuItem_Click(object sender, RoutedEventArgs e)
            => ApplyListStyle(TextMarkerStyle.Decimal);

        private void ListNumberLowerLatinMenuItem_Click(object sender, RoutedEventArgs e)
            => ApplyListStyle(TextMarkerStyle.LowerLatin);

        private void ListNumberUpperRomanMenuItem_Click(object sender, RoutedEventArgs e)
            => ApplyListStyle(TextMarkerStyle.UpperRoman);

        private void ChecklistButton_Click(object sender, RoutedEventArgs e)
            => RunListEdit(() => ListEditor.ApplyStyle(TextMarkerStyle.None, toggle: true));

        internal static bool TryGetChecklistPrefixEnd(Paragraph paragraph, out TextPointer? prefixEnd, out bool isChecked)
        {
            var found = EditorLists.Checkbox(paragraph, out var textStart, out isChecked);
            prefixEnd = found ? textStart : null;
            return found;
        }

        private bool TryToggleChecklistGlyphAtPoint(Point point) => ListEditor.ToggleAt(point);

        private bool TryHandleChecklistKey(KeyEventArgs e)
        {
            if (e.Handled || _floatingImages.SelectedImageId is not null || !IsEditorKeyboardFocusWithin(e.OriginalSource))
                return false;
            if (e.Key is not (Key.Tab or Key.Return or Key.Back or Key.Delete or Key.Home or Key.Left or Key.L or Key.M or Key.Up or Key.Down))
                return false;
            MaterializeSelectionForAction();
            bool handled = ListEditor.HandleKey(e.Key, Keyboard.Modifiers,
                EditorLists.Ancestor<TableCell>(ContentTextBox.Selection.Start.Parent) is not null);
            if (handled)
            {
                e.Handled = true;
                MarkEditorContentChanged(); UpdateEditedIndicator(); UpdateFormattingToolbarState();
            }
            return handled;
        }

        private void ListRemoveButton_Click(object sender, RoutedEventArgs e)
            => RunListEdit(() => ListEditor.Remove());

        private void ApplyListStyle(TextMarkerStyle markerStyle)
        {
            if (EditorLists.Numbered(markerStyle)) _lastNumberStyle = markerStyle;
            else _lastBulletStyle = markerStyle;
            RunListEdit(() => ListEditor.ApplyStyle(markerStyle));
        }
        private void RefreshEditorSelectionLayout(TextPointer selectionStart, TextPointer selectionEnd)
        {
            ContentTextBox.InvalidateVisual();
            ContentTextBox.UpdateLayout();

            try
            {
                ContentTextBox.Selection.Select(selectionStart, selectionEnd);
            }
            catch
            {
                // Layout refresh should keep pointers valid; formatting itself already succeeded.
            }
        }

        private void UpdateFormattingToolbarState()
        {
            if (ListRemoveButton is null)
                return;

            bool hasImage = _floatingImages?.SelectedImageId is not null;
            bool hasList = !hasImage && ListEditor.HasList;
            ListRemoveButton.IsEnabled = hasList;
            EditorToolbarReveal.SetVisible(ListActionsSlot, hasList);
            EditorToolbarReveal.SetVisible(ClearFormattingSlot, !hasImage && EditorToolbarContext.HasFormattingToClear(ContentTextBox));
            FontToolsGroup.IsEnabled = TextToolsGroup.IsEnabled = SpacingToolsGroup.IsEnabled = !hasImage;
            ListCreationToolsGroup.IsEnabled = IndentStepToolsGroup.IsEnabled = !hasImage;
            var style = hasImage ? null : ListEditor.SelectionStyle;
            SetEditorToolButtonActive(BulletListButton,
                style is TextMarkerStyle.Disc or TextMarkerStyle.Circle or TextMarkerStyle.Square);
            SetEditorToolButtonActive(NumberedListButton, style is { } marker && EditorLists.Numbered(marker));
            SetEditorToolButtonActive(ChecklistButton, style == TextMarkerStyle.None);
            var mixed = ListEditor.MixedSelection ? LocalizationService.GetString("ListMixedStyles") : string.Empty;
            System.Windows.Automation.AutomationProperties.SetItemStatus(BulletListButton, mixed);
            System.Windows.Automation.AutomationProperties.SetItemStatus(NumberedListButton, mixed);
            System.Windows.Automation.AutomationProperties.SetItemStatus(ChecklistButton, mixed);
            BulletListButton.ToolTip = LocalizationService.GetString("BulletListStyle") + (mixed.Length > 0 ? " — " + mixed : string.Empty);
            NumberedListButton.ToolTip = LocalizationService.GetString("NumberedListStyle") + (mixed.Length > 0 ? " — " + mixed : string.Empty);
            ChecklistButton.ToolTip = LocalizationService.GetString("Checklist") + (mixed.Length > 0 ? " — " + mixed : string.Empty);
            ListIncreaseIndentButton.IsEnabled = hasList && ListEditor.CanIndent;
            ListDecreaseIndentButton.IsEnabled = hasList;
            UpdateNumberingMenuState();
        }

        private bool SelectionExtendsOutsideViewport()
        {
            var selection = ContentTextBox.Selection;
            if (selection == null || selection.IsEmpty || ContentTextBox.ActualHeight <= 1)
                return false;

            var top = ContentTextBox.GetPositionFromPoint(new Point(8, 4), snapToText: true);
            var bottom = ContentTextBox.GetPositionFromPoint(
                new Point(Math.Max(8, ContentTextBox.ActualWidth - 8), Math.Max(4, ContentTextBox.ActualHeight - 4)),
                snapToText: true);
            if (top is null || bottom is null)
                return false;

            if (top.CompareTo(bottom) > 0)
                (top, bottom) = (bottom, top);

            return selection.Start.CompareTo(top) < 0 || selection.End.CompareTo(bottom) > 0;
        }

        private void ToggleSelectionTextStyle(DependencyProperty property, object enabledValue, object disabledValue)
        {
            var selection = ContentTextBox.Selection;
            var currentValue = selection.GetPropertyValue(property);
            var shouldEnable = currentValue == DependencyProperty.UnsetValue || !Equals(currentValue, enabledValue);

            selection.ApplyPropertyValue(property, shouldEnable ? enabledValue : disabledValue);
            MarkEditorContentChanged();
            UpdateEditedIndicator();
            ContentTextBox.Focus();
        }

        private readonly record struct EditorTypographyState(
            string FontFamily,
            double FontSize,
            bool HasMixedFontFamily,
            bool HasMixedFontSize);

        private void UpdateFontButtonText()
            => UpdateFontButtonText(ResolveCurrentSelectionTypography());

        private void UpdateFontButtonText(EditorTypographyState typography)
        {
            if (FindName("FontButton") is not Button fontButton)
                return;

            var mixedText = GetMixedSelectionText();
            var fontFamily = typography.HasMixedFontFamily ? mixedText : typography.FontFamily;
            var fontSize = typography.HasMixedFontSize ? mixedText : FormatFontSize(typography.FontSize);
            var tooltip = string.Format(LocalizationService.GetString("FontButtonFormat"), fontFamily, fontSize);
            if (string.Equals(_lastFontButtonTooltip, tooltip, StringComparison.Ordinal))
                return;

            _lastFontButtonTooltip = tooltip;
            fontButton.ToolTip = tooltip;
        }

        private void SyncFontSelectorsFromSelection()
        {
            var typography = ResolveCurrentSelectionTypography();
            _isSyncingFontSelectors = true;

            try
            {
                if (typography.HasMixedFontFamily)
                {
                    if (FontFamilyBox.SelectedIndex != -1)
                        FontFamilyBox.SelectedIndex = -1;
                }
                else
                {
                    SelectOrAddFontFamilyItem(typography.FontFamily);
                }

                if (typography.HasMixedFontSize)
                {
                    if (FontSizeBox.SelectedIndex != -1)
                        FontSizeBox.SelectedIndex = -1;
                }
                else
                {
                    SelectOrAddFontSizeItem(typography.FontSize);
                }
            }
            finally
            {
                _isSyncingFontSelectors = false;
            }

            UpdateFontButtonText(typography);
        }

        private EditorTypographyState ResolveCurrentSelectionTypography()
        {
            var fallbackFamily = GetPrimaryFontFamily(ContentTextBox.FontFamily?.Source);
            var fallbackSize = NormalizeEditorFontSize(ContentTextBox.FontSize);
            var fontFamily = fallbackFamily;
            var fontSize = fallbackSize;
            var hasMixedFontFamily = false;
            var hasMixedFontSize = false;

            var selection = ContentTextBox.Selection;
            if (selection == null)
                return new EditorTypographyState(fontFamily, fontSize, false, false);

            if (TryGetSelectionPropertyValue(selection, TextElement.FontFamilyProperty, out var familyValue)
                && TryGetFontFamily(familyValue, out var selectedFamily))
            {
                fontFamily = selectedFamily;
            }
            else if (selection.IsEmpty)
            {
                TryResolveFontFamilyAtPointer(selection.Start, ref fontFamily);
            }
            else
            {
                hasMixedFontFamily = true;
            }

            if (TryGetSelectionPropertyValue(selection, TextElement.FontSizeProperty, out var sizeValue)
                && TryGetFontSize(sizeValue, out var selectedSize))
            {
                fontSize = NormalizeEditorFontSize(selectedSize);
            }
            else if (selection.IsEmpty)
            {
                TryResolveFontSizeAtPointer(selection.Start, ref fontSize);
            }
            else
            {
                hasMixedFontSize = true;
            }

            return new EditorTypographyState(fontFamily, fontSize, hasMixedFontFamily, hasMixedFontSize);
        }

        private static bool TryGetSelectionPropertyValue(TextSelection selection, DependencyProperty property, out object value)
        {
            try
            {
                value = selection.GetPropertyValue(property);
                return value != DependencyProperty.UnsetValue;
            }
            catch
            {
                value = DependencyProperty.UnsetValue;
                return false;
            }
        }

        private static void TryResolveFontFamilyAtPointer(TextPointer? pointer, ref string fontFamily)
        {
            var current = pointer?.Parent as DependencyObject;
            while (current != null)
            {
                if (TryGetFontFamily(current.GetValue(TextElement.FontFamilyProperty), out var resolvedFamily))
                {
                    fontFamily = resolvedFamily;
                    return;
                }

                current = current switch
                {
                    FrameworkContentElement contentElement => contentElement.Parent,
                    FrameworkElement frameworkElement => frameworkElement.Parent,
                    _ => LogicalTreeHelper.GetParent(current)
                };
            }
        }

        private static void TryResolveFontSizeAtPointer(TextPointer? pointer, ref double fontSize)
        {
            var current = pointer?.Parent as DependencyObject;
            while (current != null)
            {
                if (TryGetFontSize(current.GetValue(TextElement.FontSizeProperty), out var resolvedSize))
                {
                    fontSize = NormalizeEditorFontSize(resolvedSize);
                    return;
                }

                current = current switch
                {
                    FrameworkContentElement contentElement => contentElement.Parent,
                    FrameworkElement frameworkElement => frameworkElement.Parent,
                    _ => LogicalTreeHelper.GetParent(current)
                };
            }
        }

        private static bool TryGetFontFamily(object? value, out string fontFamily)
        {
            fontFamily = string.Empty;

            switch (value)
            {
                case FontFamily family when !string.IsNullOrWhiteSpace(family.Source):
                    fontFamily = GetPrimaryFontFamily(family.Source);
                    return true;
                case string familyName when !string.IsNullOrWhiteSpace(familyName):
                    fontFamily = GetPrimaryFontFamily(familyName);
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryGetFontSize(object? value, out double fontSize)
        {
            switch (value)
            {
                case double size when size > 0:
                    fontSize = size;
                    return true;
                case int size when size > 0:
                    fontSize = size;
                    return true;
                case string sizeText when double.TryParse(sizeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSize) && parsedSize > 0:
                    fontSize = parsedSize;
                    return true;
                default:
                    fontSize = 0;
                    return false;
            }
        }

        private bool TryGetSelectedComboBoxContent(ComboBox comboBox, out string content)
        {
            if (comboBox.SelectedItem is ComboBoxItem { Content: not null } item)
            {
                content = item.Content.ToString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(content);
            }

            content = comboBox.SelectedItem?.ToString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(content);
        }

        private void SelectOrAddFontFamilyItem(string fontFamily)
        {
            if (TrySelectFontFamilyItem(fontFamily))
                return;

            var item = CreateEditorComboBoxItem(fontFamily);
            FontFamilyBox.Items.Add(item);
            _fontFamilyItemsByName[fontFamily] = item;
            FontFamilyBox.SelectedItem = item;
        }

        private void SelectOrAddFontSizeItem(double fontSize)
        {
            var content = FormatFontSize(fontSize);
            if (TrySelectFontSizeItem(content))
                return;

            var item = CreateEditorComboBoxItem(content, fontSize);
            FontSizeBox.Items.Add(item);
            _fontSizeItemsByContent[content] = item;
            FontSizeBox.SelectedItem = item;
        }

        private bool TrySelectFontFamilyItem(string fontFamily)
        {
            if (TryGetSelectedComboBoxContent(FontFamilyBox, out var current)
                && string.Equals(current, fontFamily, StringComparison.OrdinalIgnoreCase)
                && FontFamilyBox.SelectedIndex >= 0)
            {
                return true;
            }

            if (!_fontFamilyItemsByName.TryGetValue(fontFamily, out var item))
                return false;

            if (!ReferenceEquals(FontFamilyBox.SelectedItem, item))
                FontFamilyBox.SelectedItem = item;

            return true;
        }

        private bool TrySelectFontSizeItem(string content)
        {
            if (TryGetSelectedComboBoxContent(FontSizeBox, out var current)
                && string.Equals(current, content, StringComparison.Ordinal)
                && FontSizeBox.SelectedIndex >= 0)
            {
                return true;
            }

            if (!_fontSizeItemsByContent.TryGetValue(content, out var item))
                return false;

            if (!ReferenceEquals(FontSizeBox.SelectedItem, item))
                FontSizeBox.SelectedItem = item;

            return true;
        }

        private static void SelectComboBoxItemByContent(ComboBox comboBox, string preferredContent, string fallbackContent, StringComparison comparison)
        {
            if (!TrySelectComboBoxItemByContent(comboBox, preferredContent, comparison)
                && !TrySelectComboBoxItemByContent(comboBox, fallbackContent, comparison)
                && comboBox.Items.Count > 0)
            {
                comboBox.SelectedIndex = 0;
            }
        }

        private static bool TrySelectComboBoxItemByContent(ComboBox comboBox, string content, StringComparison comparison)
        {
            if (TryGetSelectedComboBoxContentStatic(comboBox, out var current)
                && string.Equals(current, content, comparison)
                && comboBox.SelectedIndex >= 0)
            {
                return true;
            }

            foreach (var option in comboBox.Items.OfType<ComboBoxItem>())
            {
                if (!string.Equals(option.Content?.ToString(), content, comparison))
                    continue;

                if (!ReferenceEquals(comboBox.SelectedItem, option))
                    comboBox.SelectedItem = option;

                return true;
            }

            return false;
        }

        private static bool TryGetSelectedComboBoxContentStatic(ComboBox comboBox, out string content)
        {
            if (comboBox.SelectedItem is ComboBoxItem { Content: not null } item)
            {
                content = item.Content.ToString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(content);
            }

            content = comboBox.SelectedItem?.ToString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(content);
        }

        private static bool TrySelectComboBoxItemByTag(ComboBox comboBox, double tagValue)
        {
            foreach (var option in comboBox.Items.OfType<ComboBoxItem>())
            {
                if (!TryGetFontSize(option.Tag, out var optionValue)
                    || Math.Abs(optionValue - tagValue) >= 0.1)
                {
                    continue;
                }

                if (!ReferenceEquals(comboBox.SelectedItem, option))
                    comboBox.SelectedItem = option;

                return true;
            }

            return false;
        }

        private static string FormatFontSize(double fontSize)
        {
            fontSize = NormalizeEditorFontSize(fontSize);
            return FormatCompactNumber(fontSize);
        }

        private static string FormatIndentSizeLabel(double indentSize, string measurementUnitSystem)
        {
            if (string.Equals(
                    AppSettings.NormalizeMeasurementUnitSystem(measurementUnitSystem),
                    AppSettings.UnitSystemMetric,
                    StringComparison.Ordinal))
            {
                return $"{FormatCompactNumber(indentSize / DipsPerInch * CentimetersPerInch)} cm";
            }

            return $"{FormatCompactNumber(indentSize / DipsPerInch)} in";
        }

        private static string FormatCompactNumber(double value)
            => Math.Abs(value - Math.Round(value)) < 0.01
                ? Math.Round(value).ToString(CultureInfo.InvariantCulture)
                : value.ToString("0.##", CultureInfo.InvariantCulture);

        private static string GetMixedSelectionText()
        {
            var text = LocalizationService.GetString("MixedSelection");
            return string.Equals(text, "MixedSelection", StringComparison.Ordinal) ? "Mixed" : text;
        }


        private static void SaveEditorFontPanelPreference(bool isOpen)
        {
            var settings = AppSettingsService.Load();
            settings.IsEditorFontPanelOpen = isOpen;
            AppSettingsService.Save(settings);
        }

        private void ConvertAiReviewNotice_Dismissed(object sender, RoutedEventArgs e)
        {
            if (_currentDocument is null)
            {
                _aiReviewNoticeDismissedWithoutDocument = true;
            }
            else
            {
                _currentDocument.AiReviewNoticeDismissed = true;
                if (Application.Current?.MainWindow?.DataContext is MainViewModel mainViewModel)
                {
                    if (_workspaceSave is not null && mainViewModel.Notes.FirstOrDefault(n => n.Document.Id == _currentDocument.Id) is { } note)
                        note.Document.AiReviewNoticeDismissed = true;
                    mainViewModel.SaveNotes();
                }
            }

            ApplyAiReviewNoticeVisibility();
        }

        private void ApplyAiReviewNoticeVisibility()
        {
            if (ConvertAiReviewMenuItem is null || ConvertAiReviewSeparator is null)
                return;

            var dismissed = _currentDocument?.AiReviewNoticeDismissed == true
                || (_currentDocument is null && _aiReviewNoticeDismissedWithoutDocument);
            var visibility = dismissed ? Visibility.Collapsed : Visibility.Visible;
            ConvertAiReviewMenuItem.Visibility = visibility;
            ConvertAiReviewSeparator.Visibility = visibility;
        }

        private void PersistCurrentDocumentEditorUiState()
        {
            if (_currentDocument == null)
                return;

            _currentDocument.IsEditorFontPanelOpen = _isFontPanelOpen;
            _currentDocument.IsEditorTagsPanelOpen = _isTagsPanelOpen;
            _currentDocument.IsWordWrapEnabled = _isWordWrapEnabled;
            _lastSavedTagsPanelOpen = _isTagsPanelOpen;

            if (Application.Current.MainWindow?.DataContext is MainViewModel mainViewModel)
                mainViewModel.SaveNotes();
        }

        private void OpenFromFileButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Filter = LocalizationService.GetString("OpenFileDialogFilter");
            if (dlg.ShowDialog() != true)
                return;

            var path = dlg.FileName;
            try
            {
                if (NoteFileService.IsNotePackagePath(path))
                {
                    ImportNotePackageIntoEditor(NoteFileService.LoadNotePackage(path));
                }
                else
                {
                    ApplyContentToEditor(NoteFileService.LoadEditorContentFromFile(path));
                }

                ApplyEditorTextLayout();
                MarkEditorContentChanged();
                UpdateEditedIndicator();
                UpdateCounter();
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("FailedToOpenFile")}\n\n{ex.Message}",
                    LocalizationService.GetString("Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ImportNotePackageIntoEditor(NoteDocument document)
        {
            TitleTextBox.Text = document.Title;
            TagsTextBox.Text = string.Join(", ", document.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()));
            ContentTextBox.FontFamily = CreateEditorFontFamily(document.FontFamily);
            ContentTextBox.FontSize = document.FontSize > 0 ? document.FontSize : 14;
            _isWordWrapEnabled = document.IsWordWrapEnabled ?? _isWordWrapEnabled;
            SetFontPanelOpen(document.IsEditorFontPanelOpen ?? _isFontPanelOpen, animate: false);
            SetTagsPanelOpen(document.IsEditorTagsPanelOpen ?? false, animate: false);
            ApplyContentToEditor(document.Content);
            ApplyRichTextBoxTheme();
            if (!TrySyncLineSpacingFromDocument())
                ApplyCurrentLineSpacingToDocument();
            ApplyEditorTextLayout();
            SyncFontSelectorsFromSelection();
            _floatingImages.MediaDirectory = MainViewModel.NotesMediaDirectory;
            _ = RestorePhotosAsync(document.Images);
        }

        private void ClearContentButton_Click(object sender, RoutedEventArgs e)
        {
            // Show confirmation dialog
            var mainWindow = Application.Current.MainWindow;
            var previousEditorOpacity = Opacity;
            var previousMainOpacity = mainWindow is not null ? mainWindow.Opacity : 1.0;

            try
            {
                Opacity = 0.82;
                if (mainWindow is not null && !ReferenceEquals(mainWindow, this))
                    mainWindow.Opacity = 0.82;

                var result = ModernDialog.Show(
                    GetDialogOwnerWindow(),
                    LocalizationService.GetString("ClearContentTitle"),
                    LocalizationService.GetString("ClearContentPrompt"),
                    ModernDialogTone.Danger,
                    LocalizationService.GetString("ClearContent"),
                    LocalizationService.GetString("Cancel"));

                if (result == ModernDialogResult.Primary)
                {
                    // Clear all content from the RichTextBox
                    TextRange tr = new TextRange(ContentTextBox.Document.ContentStart, ContentTextBox.Document.ContentEnd);
                    ListEditor.ReplaceContent(string.Empty);
                    MarkEditorContentChanged();
                    UpdateEditedIndicator();
                }
            }
            finally
            {
                Opacity = previousEditorOpacity;
                if (mainWindow is not null && !ReferenceEquals(mainWindow, this))
                    mainWindow.Opacity = previousMainOpacity;
            }
        }

        private async void InsertPictureButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isInsertingPicture)
                return;

            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = LocalizationService.GetString("InsertPicture"),
                    Filter = LocalizationService.GetString("ImageOpenFileDialogFilter"),
                    Multiselect = false
                };

                if (dialog.ShowDialog(GetDialogOwnerWindow()) != true)
                    return;

                var dpi = 1.0;
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget is not null)
                    dpi = source.CompositionTarget.TransformToDevice.M11;

                _isInsertingPicture = true;
                InsertPictureButton.IsEnabled = false;

                var (ok, load, error) = await NoteImageLoader.TryLoadFromFileAsync(
                    dialog.FileName,
                    NoteImageLoader.DefaultInsertMaxWidthDip,
                    dpi);

                if (!ok || load is null)
                {
                    ModernMessageBox.Show(
                        $"{LocalizationService.GetString("FailedToInsertPicture")}\n\n{error}",
                        LocalizationService.GetString("InsertPicture"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                var (width, height) = NoteImageLoader.FitWithinBox(
                    load.NaturalPixelWidth,
                    load.NaturalPixelHeight,
                    NoteImageLoader.DefaultInsertMaxWidthDip,
                    NoteImageLoader.DefaultInsertMaxHeightDip);

                InsertFloatingImage(load, width, height);
                ContentTextBox.Focus();
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("FailedToInsertPicture")}\n\n{ex.Message}",
                    LocalizationService.GetString("InsertPicture"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _isInsertingPicture = false;
                if (InsertPictureButton is not null)
                    InsertPictureButton.IsEnabled = true;
            }
        }

        private void UpdateInsertCaptionAvailability()
        {
            if (InsertCaptionButton is null)
                return;

            var shouldShow = _floatingImages.SelectedImageId is not null;
            string captionAction = shouldShow && _floatingImages.GetCaption(_floatingImages.SelectedImageId!.Value) is not null
                ? "EditCaption" : "InsertCaption";
            CaptionActionText.Text = LocalizationService.GetString(captionAction);
            InsertCaptionButton.ToolTip = LocalizationService.GetString(captionAction);
            System.Windows.Automation.AutomationProperties.SetName(InsertCaptionButton, LocalizationService.GetString(captionAction));
            EditorToolbarReveal.SetVisible(CaptionActionsSlot, shouldShow);
        }

        private void FloatingImages_CaptionRequested(object? sender, Guid imageId)
            => ShowCaptionDialog(imageId);

        private void InsertCaptionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_floatingImages.SelectedImageId is { } imageId)
                ShowCaptionDialog(imageId);
        }

        private static readonly string[] DefaultCaptionLabels =
        {
            "Figure", "Illustration", "Table", "Equation", "Drawing", "Chart"
        };

        private void ShowCaptionDialog(Guid imageId)
        {
            var settings = AppSettingsService.Load();
            var labels = DefaultCaptionLabels
                .Concat(settings.CustomCaptionLabels.Where(label => !string.IsNullOrWhiteSpace(label)))
                .Distinct()
                .ToList();
            var caption = _floatingImages.GetCaption(imageId);

            var dialog = new InsertCaptionDialog(
                caption?.Label,
                caption?.Text ?? string.Empty,
                caption?.Numbering ?? CaptionNumbering.Arabic,
                caption?.Separator ?? ": ",
                caption?.Position ?? CaptionPosition.Below,
                caption?.ExcludeLabel ?? false,
                labels,
                DefaultCaptionLabels,
                _floatingImages.GetNextCaptionNumber,
                hasExistingCaption: caption is not null)
            {
                Owner = GetDialogOwnerWindow()
            };

            if (dialog.ShowDialog() != true)
                return;

            if (dialog.ResultRemoved)
            {
                _floatingImages.RemoveCaption(imageId);
            }
            else
            {
                _floatingImages.SetCaption(
                    imageId,
                    new FloatingImageCoordinator.CaptionSettings(
                        dialog.ResultLabel,
                        dialog.ResultText,
                        dialog.ResultNumbering,
                        dialog.ResultSeparator,
                        dialog.ResultPosition,
                        dialog.ResultExcludeLabel));
            }

            // Persist the custom labels (OnlyOffice "Add label" / "Delete label").
            settings.CustomCaptionLabels = dialog.Labels
                .Where(label => !DefaultCaptionLabels.Contains(label))
                .ToList();
            AppSettingsService.Save(settings);
            UpdateInsertCaptionAvailability();
        }

        private void FloatingImages_CopyRequested(object? sender, Guid imageId)
            => CopyPhotoToClipboard(imageId);

        private void FloatingImages_CutRequested(object? sender, Guid imageId)
        {
            // Copy first, then remove the original - same as Word's Cut.
            if (CopyPhotoToClipboard(imageId))
                _floatingImages.RemoveImage(imageId);
        }

        private async void FloatingImages_ChangePictureRequested(object? sender, Guid imageId)
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = LocalizationService.GetString("ChangePicture"),
                    Filter = LocalizationService.GetString("ImageOpenFileDialogFilter"),
                    Multiselect = false
                };
                if (dialog.ShowDialog(GetDialogOwnerWindow()) != true)
                    return;

                var dpi = ResolveEditorDpiScale();
                var (ok, load, error) = await NoteImageLoader.TryLoadFromFileAsync(
                    dialog.FileName,
                    NoteImageLoader.DefaultInsertMaxWidthDip,
                    dpi);
                if (!ok || load is null)
                {
                    ModernMessageBox.Show(
                        $"{LocalizationService.GetString("FailedToInsertPicture")}\n\n{error}",
                        LocalizationService.GetString("ChangePicture"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                _floatingImages.ReplaceImageBytes(imageId, load);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("FailedToInsertPicture")}\n\n{ex.Message}",
                    LocalizationService.GetString("ChangePicture"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>Puts the photo's image data on the clipboard (Bitmap + PNG formats).</summary>
        private bool CopyPhotoToClipboard(Guid imageId)
        {
            if (_floatingImages.GetImageBytes(imageId) is not { } bytes)
                return false;

            try
            {
                using var stream = new MemoryStream(bytes);
                var decoder = BitmapDecoder.Create(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0)
                    return false;

                Clipboard.SetImage(decoder.Frames[0]);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Inserts an image from the clipboard as a new floating photo at the caret.
        /// Returns false when the clipboard image could not be loaded.</summary>
        private double ResolveEditorDpiScale()
        {
            var dpi = 1.0;
            if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
                dpi = target.TransformToDevice.M11;
            return dpi;
        }

        private async void InsertSketchButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isInsertingSketch)
                return;

            var sketchDialog = new SketchDialog
            {
                Owner = GetDialogOwnerWindow()
            };

            if (sketchDialog.ShowDialog() != true
                || sketchDialog.SketchPngBytes is null
                || sketchDialog.SketchPngBytes.Length == 0)
            {
                return;
            }

            try
            {
                var dpi = 1.0;
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget is not null)
                    dpi = source.CompositionTarget.TransformToDevice.M11;

                _isInsertingSketch = true;
                if (InsertSketchButton is not null)
                    InsertSketchButton.IsEnabled = false;

                var (ok, load, error) = await NoteImageLoader.TryLoadFromBytesAsync(
                    sketchDialog.SketchPngBytes,
                    NoteImageLoader.DefaultInsertMaxWidthDip,
                    dpi);

                if (!ok || load is null)
                {
                    ModernMessageBox.Show(
                        $"{LocalizationService.GetString("FailedToInsertSketch")}\n\n{error}",
                        LocalizationService.GetString("InsertSketch"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                var (width, height) = NoteImageLoader.FitWithinBox(
                    load.NaturalPixelWidth,
                    load.NaturalPixelHeight,
                    NoteImageLoader.DefaultInsertMaxWidthDip,
                    NoteImageLoader.DefaultInsertMaxHeightDip);

                InsertFloatingImage(load, width, height);
                ContentTextBox.Focus();
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("FailedToInsertSketch")}\n\n{ex.Message}",
                    LocalizationService.GetString("InsertSketch"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _isInsertingSketch = false;
                if (InsertSketchButton is not null)
                    InsertSketchButton.IsEnabled = true;
            }
        }


        private string SerializeEditorContent()
        {
            if (EditorEdgeSpacing.IsEmpty(ContentTextBox.Document))
                return string.Empty;

            _isSerializingEditorContent = true;
            // The serialization strips the extent pad and the inline photo containers out of
            // the live flow; any layout pass in between clamps the scroll / snaps the caret,
            // making the view jump (the auto-save "teleport to the start"). Preserve the view.
            var viewState = _floatingImages.CaptureViewState();
            try
            {
                using var tableWidths = _tableLayout.UsePreferredWidths(ContentTextBox.Document);
                _floatingImages.StripImageExtentPad();
                SuspendEditorEdgeSpacing();

                var textRange = new TextRange(
                    ContentTextBox.Document.ContentStart,
                    ContentTextBox.Document.ContentEnd);

                using var stream = new MemoryStream();
                // In-line images become text markers for serialization (the flow must not
                // embed UI elements); the containers are restored right after.
                _floatingImages.PrepareInlineImagesForSerialization();
                try
                {
                    textRange.Save(stream, DataFormats.XamlPackage);
                    ListEditor.WriteMetadata(stream);
                }
                finally
                {
                    _floatingImages.RestoreInlineImagesAfterSerialization();
                }

                return Convert.ToBase64String(stream.ToArray());
            }
            finally
            {
                try
                {
                    _floatingImages.EnsureImageExtentPad();
                    ApplyEditorEdgeSpacing();
                }
                finally
                {
                    _isSerializingEditorContent = false;
                    _floatingImages.RestoreViewState(viewState);
                    SchedulePendingFindRefresh();
                }
            }
        }

        private static BlockCollection? GetParentBlockCollection(Block block)
        {
            return block.Parent switch
            {
                FlowDocument document => document.Blocks,
                Section section => section.Blocks,
                ListItem listItem => listItem.Blocks,
                TableCell tableCell => tableCell.Blocks,
                _ => null
            };
        }

        private static T? FindVisualChild<T>(DependencyObject? source)
            where T : DependencyObject
        {
            if (source == null)
                return null;

            var childCount = VisualTreeHelper.GetChildrenCount(source);
            for (var i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(source, i);
                if (child is T match)
                    return match;

                var descendant = FindVisualChild<T>(child);
                if (descendant != null)
                    return descendant;
            }

            return null;
        }

        private double _zoomLevel = 1.0;

        private void ContentTextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Delta > 0)
                {
                    _zoomLevel += 0.1;
                }
                else if (e.Delta < 0)
                {
                    _zoomLevel -= 0.1;
                }

                ApplyZoom();
                e.Handled = true;
            }
        }

        private void ZoomInButton_Click(object sender, RoutedEventArgs e)
        {
            _zoomLevel += 0.1;
            ApplyZoom();
        }

        private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
        {
            _zoomLevel -= 0.1;
            ApplyZoom();
        }

        private void ZoomResetButton_Click(object sender, RoutedEventArgs e)
        {
            _zoomLevel = 1.0;
            ApplyZoom();
        }

        private void ApplyZoom()
        {
            if (_zoomLevel < 0.5) _zoomLevel = 0.5;
            if (_zoomLevel > 3.0) _zoomLevel = 3.0;

            // Zoom the whole editor surface so floating/inline overlay images scale with text.
            EditorSurface.LayoutTransform = new ScaleTransform(_zoomLevel, _zoomLevel);
            UpdateEditorScrollbarMirror();
            UpdateEditorScrollbarCorner();
            InvalidateSelectionTiles();
            InvalidateFindTiles();
            QueueSelectionOverlay();
            if (_findBarOpen)
                QueueFindOverlay(DispatcherPriority.Render);
        }
        private bool _isWordWrapEnabled = true;

        private void ToggleWordWrap_Click(object sender, RoutedEventArgs e)
        {
            _isWordWrapEnabled = !_isWordWrapEnabled;
            PersistCurrentDocumentEditorUiState();
            ApplyEditorTextLayout();
        }

        private enum FindDirection
        {
            Next,
            Previous
        }

        private sealed record FindMatch(int Start, int Length)
        {
            public int End => Start + Length;
        }

        internal sealed record FindReplaceResult(
            bool Found,
            int MatchCount,
            int ActiveIndex,
            bool Wrapped,
            int ReplacedCount)
        {
            public static FindReplaceResult Empty { get; } = new(false, 0, -1, false, 0);
        }

    }
}
