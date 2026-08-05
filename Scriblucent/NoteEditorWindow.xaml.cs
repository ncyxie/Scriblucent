using Microsoft.Win32;
using Scriblucent.Animations;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;
using Scriblucent.Views;
using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Scriblucent.Controls;
using WeCantSpell.Hunspell;

namespace Scriblucent
{
    public partial class NoteEditorWindow : Window
    {
        private bool? _pendingDialogResult = null;
        private bool _isHostedInTab;
        // Last search/replace state for Find Next / Replace Next functionality
        private string? _lastSearchQuery = null;
        private string? _lastReplacementText = null;
        private bool _lastFindMatchCase;
        private bool _lastFindWholeWord;
        private bool _lastFindWrapAround = true;
        private bool _hasFindHighlights;

        // Spell check - see SpellCheckDictionaryService/SpellCheckService for the Hunspell-backed
        // engine this drives. Squiggles are real in-document TextDecorations
        // (TextDecorationLocation.Baseline, unused by any other feature in this editor) applied
        // per misspelled-word TextRange and tracked here so they can be cleared precisely
        // (including before every save/export) without disturbing Underline/Strikethrough or
        // Find/Replace's own Background highlighting, which use different properties entirely.
        private bool _isSpellCheckEnabled;
        private DispatcherTimer? _spellCheckDebounceTimer;
        private readonly List<TextRange> _spellCheckDecoratedRanges = new();
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
        private NoteDocument? _currentDocument;
        private readonly FloatingImageCoordinator _floatingImages = new();
        private bool _isInsertingPicture;
        private bool _isInsertingSketch;
        private const int MaxEditHistoryEntries = 100;
        // Checklist items are plain text (glyph + separator) followed by the item's own runs, not
        // a real embedded control - see ChecklistButton_Click for why. That keeps them round-
        // tripping through the existing XamlPackage save/load automatically, same as Bold/Italic/
        // Underline, with no custom-UIElement marker-swap serialization needed.
        private const string ChecklistUncheckedGlyph = "☐";
        private const string ChecklistCheckedGlyph = "☑";
        private const string ChecklistPrefixSeparator = " ";
        private static readonly SolidColorBrush ChecklistCompletedForegroundBrush = CreateChecklistCompletedForegroundBrush();

        private static SolidColorBrush CreateChecklistCompletedForegroundBrush()
        {
            var brush = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
            brush.Freeze();
            return brush;
        }

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

        private readonly FlashcardConversionService _flashcardConversionService = new();
        private readonly MindMapConversionService _mindMapConversionService = new();
        private readonly QuizConversionService _quizConversionService = new();
        private bool _isConvertingToFlashcards;
        private CancellationTokenSource? _flashcardConversionCancellationSource;
        private bool _isConvertingToMindMap;
        private CancellationTokenSource? _mindMapConversionCancellationSource;
        private bool _isConvertingToTest;
        private CancellationTokenSource? _testConversionCancellationSource;
        private bool _isSyncingFontSelectors;
        private bool _isApplyingTypography;
        private bool _isApplyingHyperlinkCtrlHints;
        private bool _hyperlinkCtrlHintsVisible;
        private bool _isLoadingDocument;
        private bool _isSerializingEditorContent;
        private bool _allowCloseWithoutPrompt;
        private bool _isFontPanelOpen;
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
        private int _fontPanelAnimationVersion;
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
        private const double StatusIndicatorExpandedHeight = 20;
        private const double UnwrappedDocumentPageWidth = 10000;
        private ScrollViewer? _contentScrollViewer;
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
            // Note editors are hosted inside NoteEditorTabsWindow (never shown as their own
            // window). Do not attach ShellWindowChrome here — that would nest a second caption.
            InitializeEditorFormattingOptions();
            InitializeEditorUiPreferences();
            InitializeAutoSave();
            UpdateCounter();
            UpdateOnlineSearchAvailability();
            DataContextChanged += NoteEditorWindow_DataContextChanged;
            AttachEditorSettingsSource(DataContext as INotifyPropertyChanged);
            ApplyEditorTextLayout();
            ApplyCurrentLineSpacingToDocument();
            UpdateEditorToolButtonStates();
            // Spell check picks its dictionary from the effective Language - the editor's Content
            // gets reparented into NoteEditorTabsWindow's tree (see that window's host.Content
            // swap), so setting it on this Window instance wouldn't survive; set it directly on
            // the RichTextBox instead.
            ContentTextBox.Language = XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag);
            ContentTextBox.IsDocumentEnabled = true;
            // IsDocumentEnabled also makes Hyperlinks raise RequestNavigate on click - always handle
            // it ourselves so unknown schemes (scriblucent-note://) and default navigation cannot crash.
            ContentTextBox.AddHandler(
                Hyperlink.RequestNavigateEvent,
                new System.Windows.Navigation.RequestNavigateEventHandler(ContentTextBox_HyperlinkRequestNavigate),
                handledEventsToo: true);
            ContentTextBox.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, ContentTextBox_CopyExecuted, ContentTextBox_CopyCanExecute));
            DataObject.AddPastingHandler(ContentTextBox, ContentTextBox_Pasting);
            ContentTextBox.PreviewMouseDown += ContentTextBox_PreviewMouseDown;
            ContentTextBox.PreviewKeyDown += ContentTextBox_HyperlinkHintPreviewKeyDown;
            ContentTextBox.PreviewKeyUp += ContentTextBox_HyperlinkHintPreviewKeyUp;
            // The static Cut/Copy/Paste menu is set via XAML; captured here so
            // ContentTextBox_ContextMenuOpening can restore it after showing a dynamic
            // spell-check suggestions menu for a right-click that landed on a misspelled word.
            _defaultContentTextBoxContextMenu = ContentTextBox.ContextMenu;
            ContentTextBox.ContextMenuOpening += ContentTextBox_ContextMenuOpening;
            PreviewKeyDown += NoteEditorWindow_PreviewKeyDown;
            PreviewKeyUp += NoteEditorWindow_PreviewKeyUp;
            // RootGrid is reparented into NoteEditorTabsWindow; keep image shortcuts on the body
            // so Delete/Back/Escape still reach the coordinator when this Window is not in the route.
            RootGrid.PreviewKeyDown += RootGrid_PreviewKeyDown;
            Deactivated += (_, _) => SetHyperlinkCtrlHintsVisible(false);
            Loaded += NoteEditorWindow_Loaded;
            RootGrid.Loaded += NoteEditorWindow_Loaded;

            _floatingImages.Attach(FloatingImageOverlay, ContentTextBox, EditorSurface);
            _floatingImages.ImagesChanged += (_, _) =>
            {
                MarkEditorContentChanged();
                UpdateEditedIndicator();
            };

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

        private void ApplyEditorTextLayout()
        {
            var scrollbars = ResolveEditorScrollbarSettings();
            var horizontalScrollEnabled = scrollbars.Horizontal && !_isWordWrapEnabled;

            ContentTextBox.VerticalScrollBarVisibility = scrollbars.Vertical
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Hidden;

            ContentTextBox.HorizontalScrollBarVisibility = horizontalScrollEnabled
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled;

            ContentTextBox.Document.PageWidth = horizontalScrollEnabled
                ? UnwrappedDocumentPageWidth
                : double.NaN;

            ContentTextBox.InvalidateMeasure();
            _contentScrollViewer?.InvalidateScrollInfo();
            UpdateEditorToolButtonStates();
            UpdateEditorScrollbarCorner();
            Dispatcher.InvokeAsync(UpdateEditorScrollbarCorner);
        }

        private void ThemeManager_ThemeChanged(object? sender, EventArgs e)
        {
            Scriblucent.Services.WindowThemeService.ApplyTheme(this, ThemeManager.CurrentTheme, rebuildFrame: true);
            ApplyRichTextBoxTheme();
            UpdateEditorToolButtonStates();
            UpdateEditorScrollbarCorner();
        }

        private void NoteEditorWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UiMotion.ConfigurePopupsForWindow(this);
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
                _contentScrollViewer.SetResourceReference(ScrollViewer.BackgroundProperty, "RichTextBoxBackground");
                _contentScrollViewer.ScrollChanged += ContentScrollViewer_ScrollChanged;
                UpdateEditorScrollbarCorner();
            }
        }

        private void DetachContentScrollViewer()
        {
            if (_contentScrollViewer != null)
                _contentScrollViewer.ScrollChanged -= ContentScrollViewer_ScrollChanged;

            _contentScrollViewer = null;
        }

        private void ContentScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            UpdateEditorScrollbarCorner();
        }

        private void UpdateEditorScrollbarCorner()
        {
            if (_contentScrollViewer == null)
                return;

            var verticalVisible =
                _contentScrollViewer.ComputedVerticalScrollBarVisibility == Visibility.Visible;
            var horizontalVisible =
                _contentScrollViewer.ComputedHorizontalScrollBarVisibility == Visibility.Visible;

            if (EditorScrollbarCorner != null)
            {
                EditorScrollbarCorner.Visibility = verticalVisible && horizontalVisible
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            // Keep floating images in the content viewport — never paint over scrollbar gutters.
            if (FloatingImageOverlay != null)
            {
                var right = verticalVisible ? ResolveVerticalScrollBarWidth() : 0;
                var bottom = horizontalVisible ? ResolveHorizontalScrollBarHeight() : 0;
                var margin = new Thickness(0, 0, right, bottom);
                if (FloatingImageOverlay.Margin != margin)
                    FloatingImageOverlay.Margin = margin;
            }
        }

        private double ResolveVerticalScrollBarWidth()
            => TryFindResource("GlobalVerticalScrollBarWidth") is double width && width > 0 ? width : 6;

        private double ResolveHorizontalScrollBarHeight()
            => TryFindResource("GlobalHorizontalScrollBarHeight") is double height && height > 0 ? height : 6;

        private void UpdateEditorToolButtonStates()
        {
            var horizontalScrollbarAvailable = ResolveEditorScrollbarSettings().Horizontal;
            SetEditorToolButtonActive(WordWrapButton, _isWordWrapEnabled || !horizontalScrollbarAvailable);
            SetEditorToolButtonActive(FontButton, _isFontPanelOpen);
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
                    case NoteEditorTabsWindow tabsWindow:
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

        public void DisposeHostedEditor()
        {
            StopAutoSaveTimer();
            _counterUpdateDebounceTimer?.Stop();
            DetachContentScrollViewer();
            _floatingImages.Detach();
            Loaded -= NoteEditorWindow_Loaded;
            RootGrid.Loaded -= NoteEditorWindow_Loaded;
            RootGrid.PreviewKeyDown -= RootGrid_PreviewKeyDown;
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
        }

        private Window GetDialogOwnerWindow()
        {
            // Hosted editors live inside NoteEditorTabsWindow; this window itself is never
            // shown, so it must never be used as a dialog Owner (that throws). Fall back to
            // the main window when the hosted content is already detached.
            return Window.GetWindow(ContentTextBox)
                ?? Application.Current?.MainWindow
                ?? this;
        }

        private void NoteEditorWindow_Closing(object sender, CancelEventArgs e)
        {
            if (!_allowCloseWithoutPrompt && !ConfirmCloseIfNeeded())
            {
                e.Cancel = true;
                return;
            }

            PerformEditorCloseCleanup();
        }

        private void PerformEditorCloseCleanup()
        {
            StopAutoSaveTimer();
            _counterUpdateDebounceTimer?.Stop();
            DetachContentScrollViewer();
            _floatingImages.Detach();
            Loaded -= NoteEditorWindow_Loaded;
            RootGrid.Loaded -= NoteEditorWindow_Loaded;
            RootGrid.PreviewKeyDown -= RootGrid_PreviewKeyDown;
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

            return result switch
            {
                ModernDialogResult.Primary => true,
                ModernDialogResult.Secondary => SaveCurrentDocument(),
                _ => false
            };
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
            if (!_hasFindHighlights)
                return;

            var doc = ContentTextBox.Document;
            var textRange = new TextRange(doc.ContentStart, doc.ContentEnd);
            textRange.ApplyPropertyValue(TextElement.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
            _hasFindHighlights = false;
        }

        private void ApplyRichTextBoxTheme()
        {
            try
            {
                // Get the foreground color from the application theme resources
                if (TryGetRichTextBoxForeground(out var foregroundBrush))
                {
                    var doc = ContentTextBox.Document;

                    // Set default foreground for the entire document
                    doc.Foreground = foregroundBrush;

                    // Apply foreground to entire document text range
                    var textRange = new TextRange(doc.ContentStart, doc.ContentEnd);
                    textRange.ApplyPropertyValue(TextElement.ForegroundProperty, foregroundBrush);

                    // List markers use List/ListItem Foreground, not Run colors from the TextRange.
                    SyncListMarkerAppearance(CollectAllDocumentLists(), foreground: foregroundBrush);
                }
            }
            catch (Exception)
            {
                // Non-critical - theme application shouldn't crash the app.
            }
        }

        private static bool TryGetRichTextBoxForeground(out Brush foregroundBrush)
        {
            if (Application.Current.Resources.Contains("RichTextBoxForeground")
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
            // If selection is empty (deselected), clear find highlights when present.
            // Gated so toolbar ComboBox Focus() does not rewrite the whole document background.
            var sel = ContentTextBox.Selection;
            if (_hasFindHighlights && (sel == null || sel.IsEmpty))
            {
                ClearAllHighlights();
            }
            else
            {
                // Selection is used directly for online search.
            }

            UpdateOnlineSearchAvailability();
            // Tooltip only — do not sync ComboBox SelectedItem here; that invalidates FontPanel
            // and the shared RootGrid, which visually refreshes the RichTextBox.
            UpdateFontButtonText();
            UpdateListRemoveButtonState();
        }

        private void ContentTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isSerializingEditorContent
                || _isApplyingHyperlinkCtrlHints
                || _floatingImages.IsAdjustingExtentPad)
                return;

            MarkEditorContentChanged();

            // Dropdown typography ApplyPropertyValue fires TextChanged; skip chrome that the
            // Changed handlers already refresh explicitly to avoid a double flash/jump.
            if (_isApplyingTypography)
                return;

            // Only update counter, not theme (theme is applied during load and on theme change)
            ScheduleCounterUpdate();
            UpdateEditedIndicator();
            UpdateListRemoveButtonState();

            // Applying/clearing spell-check squiggles is itself a document-content change (real
            // TextDecorations, not an overlay) - guarded by _isApplyingSpellCheckDecorations so
            // that doesn't schedule another pass against itself forever.
            if (_isSpellCheckEnabled && !_isApplyingSpellCheckDecorations)
                ScheduleSpellCheck();
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
                var customWords = new HashSet<string>(settings.SpellCheckCustomWords, StringComparer.OrdinalIgnoreCase);
                customWords.UnionWith(_sessionIgnoredWords);
                var spans = SpellCheckService.FindMisspelledWordSpans(text, dictionary, customWords);
                ApplySpellCheckDecorations(spans);
            }
            finally
            {
                _isSpellCheckScanning = false;
            }
        }

        private void ApplySpellCheckDecorations(IReadOnlyList<SpellCheckService.WordSpan> spans)
        {
            _isApplyingSpellCheckDecorations = true;
            try
            {
                ClearSpellCheckDecorations();

                foreach (var span in spans)
                {
                    var start = GetTextPointerAtTextOffset(span.Start);
                    var end = GetTextPointerAtTextOffset(span.Start + span.Length);
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
            }
        }

        /// <summary>Clears squiggles range-by-range using the exact TextRange objects they were
        /// applied to (TextPointers auto-track document edits elsewhere), rather than one call
        /// across the whole document - SetRangeTextDecoration's read-current-then-diff logic
        /// assumes a uniform range, which a mixed clear-everything range is not.</summary>
        private void ClearSpellCheckDecorations()
        {
            if (_spellCheckDecoratedRanges.Count == 0)
                return;

            _isApplyingSpellCheckDecorations = true;
            try
            {
                foreach (var range in _spellCheckDecoratedRanges)
                    SetRangeTextDecoration(range, SpellCheckSquiggleDecoration, enabled: false);

                _spellCheckDecoratedRanges.Clear();
            }
            finally
            {
                _isApplyingSpellCheckDecorations = false;
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
            if (_isLoadingDocument || _isSerializingEditorContent)
                return;

            _editorChangeVersion++;
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
            OpenFindReplaceDialog(focusReplace: false);
        }

        private void OpenFindReplaceDialog(bool focusReplace)
        {
            var initialSearch = ResolveInitialFindText();
            var dlg = new Views.SearchReplaceDialogLocalized(
                this,
                initialSearch,
                _lastReplacementText,
                _lastFindMatchCase,
                _lastFindWholeWord,
                _lastFindWrapAround,
                focusReplace)
            {
                Owner = GetDialogOwnerWindow()
            };

            dlg.ShowDialog();

            _lastSearchQuery = dlg.SearchText;
            _lastReplacementText = dlg.ReplacementText;
            _lastFindMatchCase = dlg.MatchCase;
            _lastFindWholeWord = dlg.WholeWord;
            _lastFindWrapAround = dlg.WrapAround;
        }

        private string? ResolveInitialFindText()
        {
            var selectedText = ContentTextBox.Selection?.Text?.Trim();
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
            UpdateHyperlinkCtrlHintsFromKeyEvent(e, isDown: true);

            if (TryHandleFloatingImageKey(e))
                return;

            if (TryHandleEditorTabKey(e))
                return;

            if (TryHandleChecklistEnterKey(e))
                return;

            var isCtrlOnly = IsCtrlHeldForHyperlinkHints();

            if (isCtrlOnly && e.Key == Key.F)
            {
                OpenFindReplaceDialog(focusReplace: false);
                e.Handled = true;
                return;
            }

            if (isCtrlOnly && e.Key == Key.H)
            {
                OpenFindReplaceDialog(focusReplace: true);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F3 && !string.IsNullOrWhiteSpace(_lastSearchQuery))
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                    PerformFindPrevious(_lastSearchQuery, _lastFindMatchCase, _lastFindWholeWord, _lastFindWrapAround);
                else
                    PerformFindNext(_lastSearchQuery, _lastFindMatchCase, _lastFindWholeWord, _lastFindWrapAround);

                e.Handled = true;
                return;
            }

        }

        private void NoteEditorWindow_PreviewKeyUp(object sender, KeyEventArgs e)
            => UpdateHyperlinkCtrlHintsFromKeyEvent(e, isDown: false);

        private void RootGrid_PreviewKeyDown(object sender, KeyEventArgs e)
            => TryHandleFloatingImageKey(e);

        /// <summary>
        /// Handles Escape/Delete/Back for a selected floating image.
        /// Selection chrome is the gate — RTB often keeps keyboard focus after image mouse-down.
        /// Public so <see cref="NoteEditorTabsWindow"/> can forward keys when content is hosted.
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

        private void SetAiConversionButtonsEnabled(bool isEnabled)
        {
            ConvertMenuButton.IsEnabled = isEnabled;
        }

        private void ConvertMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { ContextMenu: { } menu } button)
                UiMotion.OpenContextMenu(menu, button);
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
            if (IsAnyAiConversionInProgress())
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

            _isConvertingToFlashcards = true;
            BeginAiGeneration();
            SetAiConversionButtonsEnabled(false);
            ShowPersistentStatusIndicator(LocalizationService.GetString("ConvertToFlashcardsInProgress"));

            _flashcardConversionCancellationSource?.Dispose();
            _flashcardConversionCancellationSource = new CancellationTokenSource();

            var progress = new Progress<BundledModelHostService.FlashcardProgress>(status =>
            {
                var text = BuildAiProgressText(status, null, null, out var useAnimation);

                if (useAnimation)
                    ShowPersistentStatusIndicator(text, pulse: true);
                else
                    ShowPersistentStatusIndicatorWithoutAnimation(text);
            });

            var restoreAutoSave = false;
            try
            {
                restoreAutoSave = TemporarilyDisableAutoSaveForAi();
                var documentTitle = ResolveEditorTitleForPrompt();
                var flashcards = await _flashcardConversionService.ConvertToFlashcardsAsync(
                    plainText,
                    progress,
                    _flashcardConversionCancellationSource.Token);
                RestoreAutoSaveAfterAi(ref restoreAutoSave);

                if (flashcards.Count == 0)
                {
                    HideStatusIndicator();
                    ModernMessageBox.Show(
                        LocalizationService.GetString("ConvertToFlashcardsParseFailed"),
                        LocalizationService.GetString("Error"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                var modelDisplayName = BundledModelHostService.Instance.GetSelectedModelDisplayName();
                var flashcardsMainWindow = Application.Current.MainWindow as MainWindow;
                var flashcardsMainViewModel = flashcardsMainWindow?.DataContext as MainViewModel;

                var preview = new FlashcardsPreviewWindow(
                    flashcards,
                    noteOptions: flashcardsMainViewModel?.Notes.Select(note => new FlashcardsPreviewWindow.FlashcardNoteLinkOption(note.Document.Id, note.Document.Title)),
                    modelDisplayName: modelDisplayName,
                    title: documentTitle,
                    tags: ParseTags(TagsTextBox.Text),
                    sourceNoteId: _currentDocument?.Id,
                    openNoteAction: flashcardsMainWindow is not null && flashcardsMainViewModel is not null
                        ? noteId => flashcardsMainWindow.OpenNoteById(flashcardsMainViewModel, noteId)
                        : null)
                {
                    Owner = GetDialogOwnerWindow()
                };

                if (preview.ShowDialog() == true && flashcardsMainViewModel is not null)
                {
                    var document = preview.ToDocument();
                    document.SourceNoteId = _currentDocument?.Id;
                    flashcardsMainViewModel.AddOrUpdateFlashcardSet(document);
                }

                ShowStatusIndicator(LocalizationService.GetString("ConvertToFlashcardsSuccess"));
            }
            catch (OperationCanceledException ex)
            {
                HideStatusIndicator();
                if (_flashcardConversionCancellationSource?.IsCancellationRequested == true)
                    return;

                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("ConvertToFlashcardsFailed")}\n\n{ex.Message}",
                    LocalizationService.GetString("Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (AiInputRejectedException ex)
            {
                HideStatusIndicator();
                ModernMessageBox.Show(
                    ex.Message,
                    LocalizationService.GetString("AiInputRejectedTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (TimeoutException ex)
            {
                HideStatusIndicator();
                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("AiGenerationTimedOut")}\n\n{ex.Message}",
                    LocalizationService.GetString("Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                HideStatusIndicator();
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
                _flashcardConversionCancellationSource?.Dispose();
                _flashcardConversionCancellationSource = null;
            }
        }

        private async void ConvertToMindMapButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsAnyAiConversionInProgress())
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

            _isConvertingToMindMap = true;
            BeginAiGeneration();
            SetAiConversionButtonsEnabled(false);
            ShowPersistentStatusIndicator(LocalizationService.GetString("ConvertToMindMapInProgress"));

            _mindMapConversionCancellationSource?.Dispose();
            _mindMapConversionCancellationSource = new CancellationTokenSource();

            var progress = new Progress<BundledModelHostService.FlashcardProgress>(status =>
            {
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

            var restoreAutoSave = false;
            try
            {
                restoreAutoSave = TemporarilyDisableAutoSaveForAi();
                var documentTitle = ResolveEditorTitleForPrompt();
                var mindMap = await _mindMapConversionService.ConvertToMindMapAsync(
                    documentTitle,
                    plainText,
                    progress,
                    _mindMapConversionCancellationSource.Token);
                RestoreAutoSaveAfterAi(ref restoreAutoSave);

                if (mindMap is null || mindMap.Children.Count == 0)
                {
                    HideStatusIndicator();
                    ModernMessageBox.Show(
                        LocalizationService.GetString("ConvertToMindMapParseFailed"),
                        LocalizationService.GetString("Error"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                var modelDisplayName = BundledModelHostService.Instance.GetSelectedModelDisplayName();

                // Resolved once and reused both for construction (note-link picker options/open
                // action) and after ShowDialog (saving) - the AI-review window otherwise had no
                // way to populate or act on either linked-note picker (document-level or
                // per-node), unlike every other place a MindMapPreviewWindow gets opened.
                var mainWindow = Application.Current.MainWindow as MainWindow;
                var mainViewModel = mainWindow?.DataContext as MainViewModel;

                var preview = new MindMapPreviewWindow(
                    mindMap,
                    noteOptions: mainViewModel?.Notes.Select(note => new MindMapPreviewWindow.MindMapNoteLinkOption(note.Document.Id, note.Document.Title)),
                    modelDisplayName: modelDisplayName,
                    title: documentTitle,
                    tags: ParseTags(TagsTextBox.Text),
                    sourceNoteId: _currentDocument?.Id,
                    openNoteAction: mainWindow is not null && mainViewModel is not null
                        ? noteId => mainWindow.OpenNoteById(mainViewModel, noteId)
                        : null)
                {
                    Owner = GetDialogOwnerWindow()
                };

                if (preview.ShowDialog() == true && mainViewModel is not null)
                {
                    var document = preview.ToDocument();
                    document.SourceNoteId = _currentDocument?.Id;
                    mainViewModel.AddOrUpdateMindMap(document);
                }

                ShowStatusIndicator(LocalizationService.GetString("ConvertToMindMapSuccess"));
            }
            catch (OperationCanceledException ex)
            {
                HideStatusIndicator();
                if (_mindMapConversionCancellationSource?.IsCancellationRequested == true)
                    return;

                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("ConvertToMindMapFailed")}\n\n{ex.Message}",
                    LocalizationService.GetString("Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (AiInputRejectedException ex)
            {
                HideStatusIndicator();
                ModernMessageBox.Show(
                    ex.Message,
                    LocalizationService.GetString("AiInputRejectedTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (TimeoutException ex)
            {
                HideStatusIndicator();
                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("AiGenerationTimedOut")}\n\n{ex.Message}",
                    LocalizationService.GetString("Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                HideStatusIndicator();
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
                _mindMapConversionCancellationSource?.Dispose();
                _mindMapConversionCancellationSource = null;
            }
        }

        private async void ConvertToTestButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsAnyAiConversionInProgress())
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

            _isConvertingToTest = true;
            BeginAiGeneration();
            SetAiConversionButtonsEnabled(false);
            ShowPersistentStatusIndicator(LocalizationService.GetString("ConvertToTestInProgress"));

            _testConversionCancellationSource?.Dispose();
            _testConversionCancellationSource = new CancellationTokenSource();

            var progress = new Progress<BundledModelHostService.FlashcardProgress>(status =>
            {
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

            var restoreAutoSave = false;
            try
            {
                restoreAutoSave = TemporarilyDisableAutoSaveForAi();
                var documentTitle = ResolveEditorTitleForPrompt();
                var quiz = await _quizConversionService.ConvertToQuizAsync(
                    documentTitle,
                    plainText,
                    _currentDocument?.Id,
                    progress,
                    _testConversionCancellationSource.Token);
                RestoreAutoSaveAfterAi(ref restoreAutoSave);

                if (quiz is null || quiz.Questions.Count == 0)
                {
                    HideStatusIndicator();
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
                var quizMainViewModel = quizMainWindow?.DataContext as MainViewModel;

                var preview = new QuizPreviewWindow(
                    quiz,
                    noteOptions: quizMainViewModel?.Notes.Select(note => new QuizPreviewWindow.QuizNoteLinkOption(note.Document.Id, note.Document.Title)),
                    modelDisplayName: modelDisplayName,
                    title: documentTitle,
                    openNoteAction: quizMainWindow is not null && quizMainViewModel is not null
                        ? noteId => quizMainWindow.OpenNoteById(quizMainViewModel, noteId)
                        : null)
                {
                    Owner = GetDialogOwnerWindow()
                };

                if (preview.ShowDialog() == true && quizMainViewModel is not null)
                {
                    quizMainViewModel.AddOrUpdateQuiz(preview.ToDocument());
                }

                ShowStatusIndicator(LocalizationService.GetString("ConvertToTestSuccess"));
            }
            catch (OperationCanceledException ex)
            {
                HideStatusIndicator();
                if (_testConversionCancellationSource?.IsCancellationRequested == true)
                    return;

                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("ConvertToTestFailed")}\n\n{ex.Message}",
                    LocalizationService.GetString("Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (AiInputRejectedException ex)
            {
                HideStatusIndicator();
                ModernMessageBox.Show(
                    ex.Message,
                    LocalizationService.GetString("AiInputRejectedTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (TimeoutException ex)
            {
                HideStatusIndicator();
                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("AiGenerationTimedOut")}\n\n{ex.Message}",
                    LocalizationService.GetString("Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                HideStatusIndicator();
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
                _testConversionCancellationSource?.Dispose();
                _testConversionCancellationSource = null;
            }
        }

        private static string BuildAiProgressText(
            BundledModelHostService.FlashcardProgress status,
            string? processingStatusKey,
            string? finalizingStatusKey,
            out bool useAnimation)
        {
            var statusKey = status.StatusKey;
            if (processingStatusKey is not null
                && string.Equals(statusKey, "ConvertToFlashcardsStatusProcessing", StringComparison.Ordinal))
            {
                statusKey = processingStatusKey;
            }
            else if (finalizingStatusKey is not null
                && string.Equals(statusKey, "ConvertToFlashcardsStatusFinalizing", StringComparison.Ordinal))
            {
                statusKey = finalizingStatusKey;
            }

            var baseText = LocalizationService.GetString(statusKey);
            useAnimation = true;

            if (status.ChunkIndex.HasValue
                && status.ChunkCount.HasValue
                && status.ChunkCount.Value > 0)
            {
                var chunkCount = Math.Max(1, status.ChunkCount.Value);
                var chunkIndex = Math.Clamp(status.ChunkIndex.Value, 1, chunkCount);
                baseText = $"({chunkIndex}/{chunkCount}) {baseText}";
            }

            if (status.GeneratedChars.HasValue)
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

        private void ShowPersistentStatusIndicator(string message, bool pulse = false)
        {
            AnimateStatusIndicatorRow(expand: true);
            UiMotion.Clear(StatusIndicatorText, UIElement.OpacityProperty);
            StatusIndicatorText.Text = message;
            StatusIndicatorText.Visibility = Visibility.Visible;
            StatusIndicatorText.Opacity = 0;
            UiMotion.Fade(StatusIndicatorText, 1, UiMotion.OpenMs, UiMotion.OutQuad);
            if (pulse)
                UiMotion.PulseOpacity(StatusIndicatorText, StatusIndicatorText, 0.72, 1.0);
        }

    private void ShowPersistentStatusIndicatorWithoutAnimation(string message)
    {
        AnimateStatusIndicatorRow(expand: true);
        UiMotion.Clear(StatusIndicatorText, UIElement.OpacityProperty);
        StatusIndicatorText.Opacity = 1;
        StatusIndicatorText.Text = message;
        StatusIndicatorText.Visibility = Visibility.Visible;
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
                UiMotion.SectionMs,
                UiMotion.OutCubic,
                () =>
                {
                    StatusIndicatorText.Visibility = Visibility.Collapsed;
                    StatusIndicatorText.Text = string.Empty;
                    AnimateStatusIndicatorRow(expand: false);
                });
        }

        private void AnimateStatusIndicatorRow(bool expand)
        {
            if (StatusIndicatorRow == null)
                return;

            var targetHeight = expand ? StatusIndicatorExpandedHeight : 0;
            var currentHeight = StatusIndicatorRow.ActualHeight;
            if (currentHeight <= 0)
                currentHeight = StatusIndicatorRow.Height.Value;

            if (Math.Abs(currentHeight - targetHeight) < 0.5)
            {
                UiMotion.Clear(StatusIndicatorRow, RowDefinition.HeightProperty);
                StatusIndicatorRow.Height = new GridLength(targetHeight, GridUnitType.Pixel);
                return;
            }

            UiMotion.AnimateRowHeight(
                StatusIndicatorRow,
                StatusIndicatorRow,
                currentHeight,
                targetHeight,
                UiMotion.OpenMs,
                UiMotion.OutQuad);
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

        internal FindReplaceResult PerformReplaceNext(
            string query,
            string replacement,
            bool matchCase = false,
            bool wholeWord = false,
            bool wrapAround = true)
        {
            if (string.IsNullOrEmpty(query))
                return FindReplaceResult.Empty;

            var matches = FindTextMatches(query, matchCase, wholeWord);
            if (matches.Count == 0)
            {
                ClearAllHighlights();
                return new FindReplaceResult(false, 0, -1, false, 0);
            }

            var currentMatch = GetCurrentSelectionMatch(matches);
            if (currentMatch is null)
                return PerformFindNext(query, matchCase, wholeWord, wrapAround);

            var start = GetTextPointerAtTextOffset(currentMatch.Start);
            var end = GetTextPointerAtTextOffset(currentMatch.End);
            if (start is null || end is null)
                return PerformFindNext(query, matchCase, wholeWord, wrapAround);

            new TextRange(start, end).Text = replacement ?? string.Empty;
            MarkEditorContentChanged();

            var result = PerformFindNext(query, matchCase, wholeWord, wrapAround);
            return result with { ReplacedCount = 1 };
        }

        internal FindReplaceResult PerformReplaceAll(
            string query,
            string replacement,
            bool matchCase = false,
            bool wholeWord = false)
        {
            if (string.IsNullOrEmpty(query))
                return FindReplaceResult.Empty;

            var matches = FindTextMatches(query, matchCase, wholeWord);
            if (matches.Count == 0)
            {
                ClearAllHighlights();
                return new FindReplaceResult(false, 0, -1, false, 0);
            }

            ClearAllHighlights();
            for (var i = matches.Count - 1; i >= 0; i--)
            {
                var match = matches[i];
                var start = GetTextPointerAtTextOffset(match.Start);
                var end = GetTextPointerAtTextOffset(match.End);
                if (start is null || end is null)
                    continue;

                new TextRange(start, end).Text = replacement ?? string.Empty;
            }

            MarkEditorContentChanged();
            ContentTextBox.Focus();
            return new FindReplaceResult(false, 0, -1, false, matches.Count);
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

            var matches = FindTextMatches(query, matchCase, wholeWord);
            if (matches.Count == 0)
            {
                ClearAllHighlights();
                return new FindReplaceResult(false, 0, -1, false, 0);
            }

            var selection = ContentTextBox.Selection;
            var selectionStart = GetTextOffsetForPointer(selection?.Start ?? ContentTextBox.Document.ContentStart);
            var selectionEnd = GetTextOffsetForPointer(selection?.End ?? ContentTextBox.Document.ContentStart);

            var activeIndex = direction == FindDirection.Next
                ? matches.FindIndex(match => match.Start >= selectionEnd)
                : FindLastMatchIndexBefore(matches, selection?.IsEmpty == false ? selectionStart : selectionEnd);

            var wrapped = false;
            if (activeIndex < 0 && wrapAround)
            {
                activeIndex = direction == FindDirection.Next ? 0 : matches.Count - 1;
                wrapped = true;
            }

            if (activeIndex < 0)
            {
                HighlightMatches(matches, activeIndex: -1);
                return new FindReplaceResult(false, matches.Count, -1, false, 0);
            }

            HighlightMatches(matches, activeIndex);
            SelectMatch(matches[activeIndex]);
            return new FindReplaceResult(true, matches.Count, activeIndex, wrapped, 0);
        }

        private static int FindLastMatchIndexBefore(IReadOnlyList<FindMatch> matches, int offset)
        {
            for (var i = matches.Count - 1; i >= 0; i--)
            {
                if (matches[i].Start < offset)
                    return i;
            }

            return -1;
        }

        private FindMatch? GetCurrentSelectionMatch(IReadOnlyList<FindMatch> matches)
        {
            var selection = ContentTextBox.Selection;
            if (selection is null || selection.IsEmpty)
                return null;

            var startOffset = GetTextOffsetForPointer(selection.Start);
            var endOffset = GetTextOffsetForPointer(selection.End);
            return matches.FirstOrDefault(match => match.Start == startOffset && match.End == endOffset);
        }

        private List<FindMatch> FindTextMatches(string query, bool matchCase, bool wholeWord)
        {
            var documentText = GetDocumentSearchText();
            var matches = new List<FindMatch>();
            if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(documentText))
                return matches;

            var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var searchStart = 0;
            while (searchStart <= documentText.Length - query.Length)
            {
                var index = documentText.IndexOf(query, searchStart, comparison);
                if (index < 0)
                    break;

                if (!wholeWord || IsWholeWordMatch(documentText, index, query.Length))
                    matches.Add(new FindMatch(index, query.Length));

                searchStart = index + Math.Max(1, query.Length);
            }

            return matches;
        }

        private string GetDocumentSearchText()
        {
            var sb = new StringBuilder();
            var navigator = ContentTextBox.Document.ContentStart;
            while (navigator is not null && navigator.CompareTo(ContentTextBox.Document.ContentEnd) < 0)
            {
                if (navigator.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                    sb.Append(navigator.GetTextInRun(LogicalDirection.Forward));

                navigator = navigator.GetNextContextPosition(LogicalDirection.Forward);
            }

            return sb.ToString();
        }

        private TextPointer? GetTextPointerAtTextOffset(int targetOffset)
        {
            targetOffset = Math.Max(0, targetOffset);
            var traversed = 0;
            var navigator = ContentTextBox.Document.ContentStart;

            while (navigator is not null && navigator.CompareTo(ContentTextBox.Document.ContentEnd) < 0)
            {
                if (navigator.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                {
                    var text = navigator.GetTextInRun(LogicalDirection.Forward);
                    if (targetOffset <= traversed + text.Length)
                        return navigator.GetPositionAtOffset(targetOffset - traversed, LogicalDirection.Forward);

                    traversed += text.Length;
                }

                navigator = navigator.GetNextContextPosition(LogicalDirection.Forward);
            }

            return ContentTextBox.Document.ContentEnd;
        }

        private int GetTextOffsetForPointer(TextPointer pointer)
        {
            var traversed = 0;
            var navigator = ContentTextBox.Document.ContentStart;

            while (navigator is not null && navigator.CompareTo(ContentTextBox.Document.ContentEnd) < 0)
            {
                if (navigator.CompareTo(pointer) >= 0)
                    return traversed;

                if (navigator.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                {
                    var text = navigator.GetTextInRun(LogicalDirection.Forward);
                    var runEnd = navigator.GetPositionAtOffset(text.Length, LogicalDirection.Forward);
                    if (runEnd is not null && runEnd.CompareTo(pointer) >= 0)
                        return traversed + Math.Max(0, navigator.GetOffsetToPosition(pointer));

                    traversed += text.Length;
                }

                navigator = navigator.GetNextContextPosition(LogicalDirection.Forward);
            }

            return traversed;
        }

        private void HighlightMatches(IReadOnlyList<FindMatch> matches, int activeIndex)
        {
            ClearAllHighlights();

            var matchBrush = TryFindResource("EditorFindMatchBackground") as Brush
                ?? new SolidColorBrush(Color.FromRgb(254, 243, 199));
            var activeBrush = TryFindResource("EditorFindActiveMatchBackground") as Brush
                ?? new SolidColorBrush(Color.FromRgb(251, 191, 36));

            for (var i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                var start = GetTextPointerAtTextOffset(match.Start);
                var end = GetTextPointerAtTextOffset(match.End);
                if (start is null || end is null)
                    continue;

                new TextRange(start, end).ApplyPropertyValue(
                    TextElement.BackgroundProperty,
                    i == activeIndex ? activeBrush : matchBrush);
            }

            _hasFindHighlights = matches.Count > 0;
        }

        private void SelectMatch(FindMatch match)
        {
            var start = GetTextPointerAtTextOffset(match.Start);
            var end = GetTextPointerAtTextOffset(match.End);
            if (start is null || end is null)
                return;

            ContentTextBox.Selection.Select(start, end);
            ContentTextBox.Focus();
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
            try
            {
                if (document != null)
                {
                    _isLoadingDocument = true;
                    _currentDocument = document; // Set current document
                    TitleTextBox.Text = document.Title;
                    TagsTextBox.Text = string.Join(", ", document.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()));

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
                                        tr.Load(ms, DataFormats.Rtf);
                                    }
                                }
                                else if (bytes[0] == 0x50 && bytes[1] == 0x4B) // ZIP / XamlPackage
                                {
                                    using (MemoryStream ms = new MemoryStream(bytes))
                                    {
                                        tr.Load(ms, DataFormats.XamlPackage);
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

                    var settings = AppSettingsService.Load();
                    _isWordWrapEnabled = document.IsWordWrapEnabled ?? settings.IsEditorWordWrapEnabled;
                    SetFontPanelOpen(
                        document.IsEditorFontPanelOpen ?? settings.IsEditorFontPanelOpen,
                        animate: false,
                        persist: false);

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

                    // Apply theme colors to the loaded content
                    ApplyRichTextBoxTheme();
                    ApplyEditorTextLayout();
                    ApplyCurrentLineSpacingToDocument();

                    _floatingImages.Restore(document.Images);

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
                _isLoadingDocument = false;
                UpdateEditedIndicator();
            }
        }

        // Print functionality
        private void PrintButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Use the FlowDocument from RichTextBox
                FlowDocument documentToPrint = ContentTextBox.Document;

                // Format the document for printing
                documentToPrint.PagePadding = new Thickness(50);

                // Create a PrintDialog
                PrintDialog pd = new PrintDialog();
                if (pd.ShowDialog() == true)
                {
                    documentToPrint.ColumnWidth = pd.PrintableAreaWidth;

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
            if (document != null)
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
                ClearAllHighlights();
                ClearSpellCheckDecorations();
                var newContent = SerializeEditorContent();
                var newImages = _floatingImages.BuildAttachments().ToList();
                // Squiggles are real TextDecorations - must never end up saved/exported (stale on
                // reload, and nobody wants spell-check marks baked into a shared PDF/RTF). Content
                // is already captured into newContent above, so reapplying now doesn't affect what
                // gets persisted; fire-and-forget since dictionaries are normally already cached
                // from the previous scan, making this effectively instant.
                _ = RunSpellCheckPassAsync();

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

                if (contentChanged || imagesChanged || titleChanged || tagsChanged || fontChanged)
                {
                    document.LastModified = DateTime.Now;
                }

                document.Title = newTitle;
                document.Tags = newTags;
                document.Content = newContent;
                document.Images = newImages;
                document.FontFamily = newFontFamily;
                document.FontSize = newFontSize;
                document.IsEditorFontPanelOpen = _isFontPanelOpen;
                document.IsWordWrapEnabled = _isWordWrapEnabled;
            }
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
                    || !string.Equals(a.Data, b.Data, StringComparison.Ordinal)
                    || !a.Width.Equals(b.Width)
                    || !a.Height.Equals(b.Height)
                    || !a.Left.Equals(b.Left)
                    || !a.Top.Equals(b.Top)
                    || a.LockAspectRatio != b.LockAspectRatio)
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

            // Hook into content changes to track modifications
            ContentTextBox.TextChanged += ContentTextBox_TextChanged;
            ContentTextBox.PreviewTextInput += ContentTextBox_PreviewTextInput;
            ContentTextBox.AddHandler(
                Keyboard.PreviewKeyDownEvent,
                new KeyEventHandler(ContentTextBox_PreviewKeyDown),
                true);
        }

        private void ContentTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Text))
            {
                Scriblucent.Services.ActivityTracker.RecordTyping(e.Text.Length, 0);
            }
        }

        private void ContentTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (TryHandleEditorTabKey(e))
                return;

            if (e.Key == Key.Space || e.Key == Key.Enter)
            {
                Scriblucent.Services.ActivityTracker.RecordTyping(0, 1);
            }
        }

        private void ContentTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
        }

        private void ContentTextBox_CopyCanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = ContentTextBox.Selection is { IsEmpty: false };
            e.Handled = true;
        }

        private void ContentTextBox_CopyExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (CopyEditorSelectionToClipboard())
                e.Handled = true;
        }

        private bool CopyEditorSelectionToClipboard()
        {
            var selection = ContentTextBox.Selection;
            if (selection == null || selection.IsEmpty)
                return false;

            try
            {
                var dataObject = new DataObject();
                var plainText = GetClipboardPlainText(selection, out var convertedIndentationForPlainText);
                dataObject.SetText(plainText, TextDataFormat.UnicodeText);
                dataObject.SetText(plainText, TextDataFormat.Text);
                if (!convertedIndentationForPlainText)
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
            if (TryGetWholeParagraphSelectionParagraphs(selection, out var selectedParagraphs))
                return BuildWholeParagraphClipboardText(selectedParagraphs, out convertedIndentationForPlainText);

            return NormalizeIndentationForPlainTextExport(selection.Text, out convertedIndentationForPlainText);
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

                var paragraphText = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
                paragraphText = TrimSingleTrailingLineBreak(paragraphText)
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
                var range = new TextRange(selection.Start, selection.End);
                using var stream = new MemoryStream();
                range.Save(stream, DataFormats.Rtf);
                dataObject.SetData(DataFormats.Rtf, Encoding.ASCII.GetString(stream.ToArray()));
            }
            catch (Exception)
            {
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
            _activeAiGenerationCount++;
            if (_activeAiGenerationCount == 1)
                AiGenerationStateChanged?.Invoke(null, EventArgs.Empty);
        }

        private static void EndAiGeneration()
        {
            if (_activeAiGenerationCount <= 0)
                return;

            _activeAiGenerationCount--;
            if (_activeAiGenerationCount == 0)
                AiGenerationStateChanged?.Invoke(null, EventArgs.Empty);
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

        // Check if content has changed since last save
        private bool HasContentChanged()
        {
            return HasUnsavedChanges();
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
            _lastSavedContent = GetContentAsText();
            _lastSavedEditorChangeVersion = _editorChangeVersion;
            _lastSavedSnapshot = GetEditorSnapshot();
            UpdateEditedIndicator();
        }

        private bool _isFlashingTitleStatus;

        private void UpdateEditedIndicator()
        {
            if (_isLoadingDocument || EditedIndicatorText is null || _isFlashingTitleStatus)
                return;

            EditedIndicatorText.Text = LocalizationService.GetString("EditedIndicator");
            var shouldShow = HasUnsavedChanges();
            if (shouldShow)
                UiMotion.AnimateInlineReveal(EditedIndicatorText, true);
            else
                UiMotion.AnimateInlineReveal(EditedIndicatorText, false);
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
            var text = GetContentAsText();

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
                AppendBlockTextForAi(block, sb);

            var text = sb.ToString().Replace('\uFFFC', ' ');

            if (text.EndsWith("\r\n", StringComparison.Ordinal))
                text = text[..^2];
            else if (text.EndsWith("\n", StringComparison.Ordinal) || text.EndsWith("\r", StringComparison.Ordinal))
                text = text[..^1];

            return text.Trim();
        }

        private static void AppendBlockTextForAi(Block block, StringBuilder target)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    AppendInlineTextForAi(paragraph.Inlines, target);
                    target.AppendLine();
                    break;
                case Section section:
                    foreach (Block child in section.Blocks)
                        AppendBlockTextForAi(child, target);
                    target.AppendLine();
                    break;
                case System.Windows.Documents.List list:
                    foreach (ListItem item in list.ListItems)
                    {
                        foreach (Block child in item.Blocks)
                            AppendBlockTextForAi(child, target);
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

        private static void AppendInlineTextForAi(InlineCollection inlines, StringBuilder target)
        {
            foreach (Inline inline in inlines)
            {
                switch (inline)
                {
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
                            AppendBlockTextForAi(block, target);
                        break;
                    case InlineUIContainer:
                        break;
                }
            }
        }

        private static void AppendTableTextForAi(Table table, StringBuilder target)
        {
            foreach (TableRowGroup rowGroup in table.RowGroups)
            {
                foreach (TableRow row in rowGroup.Rows)
                {
                    var cells = new List<string>();
                    foreach (TableCell cell in row.Cells)
                        cells.Add(GetTableCellPlainText(cell));

                    target.AppendLine(string.Join('\t', cells));
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
                    AppendBlockTextForAi(child, sb);
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
                    // Save to document
                    SaveToDocument(_currentDocument);

                    // Update last saved content
                    MarkCurrentStateSaved();
                    _lastAutoSaveTime = DateTime.Now;

                    // Show visual indicator
                    ShowAutoSaveIndicator();

                    // Raise event so MainWindow can refresh the note card
                    DocumentAutoSaved?.Invoke(_currentDocument);

                    // Save notes to disk (via MainViewModel)
                    if (Application.Current.MainWindow?.DataContext is MainViewModel mainVm)
                    {
                        mainVm.RefreshTagFiltersAfterNoteEdit();
                        mainVm.SaveNotes();
                    }
                }
            }
            catch (Exception ex)
            {
                // Don't disturb the user during auto-save; log for later diagnosis instead.
                CrashLog.Append("AutoSave", ex);
            }
        }

        // Show visual auto-save indicator beside the title (same slot as Edited).
        private void ShowAutoSaveIndicator()
        {
            if (EditedIndicatorText is null)
                return;

            _isFlashingTitleStatus = true;
            UiMotion.FlashInlineStatus(
                EditedIndicatorText,
                LocalizationService.GetString("AutoSaving"),
                () =>
                {
                    _isFlashingTitleStatus = false;
                    UpdateEditedIndicator();
                });
        }

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

        private void ExportMarkdownMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ExportFile(
                "ExportMarkdownDialogFilter",
                ".md",
                path => NoteFileService.SavePlainText(path, GetContentTextForFileExport()));
        }

        private void ExportRtfMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ExportFile(
                "ExportRtfDialogFilter",
                ".rtf",
                path =>
                {
                    ClearAllHighlights();
                    ClearSpellCheckDecorations();
                    NoteFileService.SaveTextRange(path, GetCurrentEditorTextRange(), DataFormats.Rtf);
                    _ = RunSpellCheckPassAsync();
                });
        }

        private void ExportXamlPackageMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ExportFile(
                "ExportXamlPackageDialogFilter",
                ".xamlpackage",
                path =>
                {
                    ClearAllHighlights();
                    ClearSpellCheckDecorations();
                    NoteFileService.SaveTextRange(path, GetCurrentEditorTextRange(), DataFormats.XamlPackage);
                    _ = RunSpellCheckPassAsync();
                });
        }

        private void ExportNotePackageMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ExportFile(
                "ExportNotePackageDialogFilter",
                ".notecard",
                path =>
                {
                    ClearAllHighlights();
                    NoteFileService.SaveNotePackage(path, CreateExportNoteDocument());
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
            var paragraphText = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
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
            ClearSpellCheckDecorations();
            var document = new NoteDocument
            {
                Title = TitleTextBox.Text,
                Content = SerializeEditorContent(),
                Images = _floatingImages.BuildAttachments().ToList(),
                Tags = ParseTags(TagsTextBox.Text),
                FontFamily = GetPrimaryFontFamily(ContentTextBox.FontFamily.Source),
                FontSize = ContentTextBox.FontSize,
                IsEditorFontPanelOpen = _isFontPanelOpen,
                IsWordWrapEnabled = _isWordWrapEnabled,
                CreatedAt = _currentDocument?.CreatedAt ?? DateTime.UtcNow,
                LastModified = DateTime.Now
            };
            _ = RunSpellCheckPassAsync();
            return document;
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
            if (ContentTextBox.CanUndo)
                ContentTextBox.Undo();
        }

        private void RedoButton_Click(object sender, RoutedEventArgs e)
        {
            if (ContentTextBox.CanRedo)
                ContentTextBox.Redo();
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
            SaveCurrentDocument();

            // Close the window after saving
            _allowCloseWithoutPrompt = true;
            if (_isHostedInTab)
                CloseRequested?.Invoke(this);
            else
                Close();
        }

        private bool SaveCurrentDocument()
        {
            if (_currentDocument == null)
                return true;

            SaveToDocument(_currentDocument);
            MarkCurrentStateSaved();

            DocumentAutoSaved?.Invoke(_currentDocument);

            if (Application.Current.MainWindow?.DataContext is MainViewModel mainVm)
            {
                mainVm.RefreshTagFiltersAfterNoteEdit();
                mainVm.SaveNotes();
            }

            return true;
        }

        private void ApplyContentToEditor(string? content)
        {
            TextRange tr = new TextRange(ContentTextBox.Document.ContentStart, ContentTextBox.Document.ContentEnd);

            if (string.IsNullOrEmpty(content))
            {
                tr.Text = string.Empty;
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
                            tr.Load(ms, DataFormats.Rtf);
                        }
                    }
                    else if (bytes[0] == 0x50 && bytes[1] == 0x4B) // ZIP / XamlPackage
                    {
                        using (MemoryStream ms = new MemoryStream(bytes))
                        {
                            tr.Load(ms, DataFormats.XamlPackage);
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
                tr.Text = content;
            }

            SyncFontSelectorsFromSelection();
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

            var selectionChangesDocument = ContentTextBox.Selection is { IsEmpty: false };
            _isApplyingTypography = true;
            try
            {
                ContentTextBox.Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, CreateEditorFontFamily(fontName));
            }
            finally
            {
                _isApplyingTypography = false;
            }

            SavePreferredFontFamily(GetPrimaryFontFamily(fontName));
            UpdateFontButtonText();

            if (selectionChangesDocument)
            {
                MarkEditorContentChanged();
                UpdateEditedIndicator();
            }
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

            ApplyEditorFontSize(NormalizeEditorFontSize(size), persistPreference: true);
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

            ApplyEditorFontSize(nextSize, persistPreference: true);
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

        private void ApplyEditorFontSize(double size, bool persistPreference)
        {
            size = NormalizeEditorFontSize(size);
            var selection = ContentTextBox.Selection;
            var selectionStart = selection?.Start;
            var selectionEnd = selection?.End;
            var selectionChangesDocument = selection is { IsEmpty: false };
            var activeLists = GetActiveDocumentLists();

            ContentTextBox.Focus();
            _isApplyingTypography = true;
            try
            {
                ContentTextBox.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, size);
                Brush? markerForeground = TryGetRichTextBoxForeground(out var brush) ? brush : null;
                SyncListMarkerAppearance(activeLists, size, markerForeground);
                ApplyLineSpacingToInlineFormattingTargetIfNeeded(size);
            }
            finally
            {
                _isApplyingTypography = false;
            }

            if (selectionStart is not null && selectionEnd is not null)
                RefreshEditorSelectionLayout(selectionStart, selectionEnd);

            if (persistPreference)
                SavePreferredFontSize(size);

            UpdateFontButtonText();

            if (selectionChangesDocument || activeLists.Count > 0)
            {
                MarkEditorContentChanged();
                UpdateEditedIndicator();
            }
        }

        private void ClearFormattingButton_Click(object sender, RoutedEventArgs e)
        {
            var selection = ContentTextBox.Selection;
            selection.ClearAllProperties();

            // ClearAllProperties leaves some brushes as UnsetValue; normalize colors/decorations
            // the same way the color-clear menu items do so theme defaults apply cleanly.
            ApplySelectionColor(TextElement.ForegroundProperty, null);
            ApplySelectionColor(TextElement.BackgroundProperty, null);
            selection.ApplyPropertyValue(Inline.TextDecorationsProperty, new TextDecorationCollection());

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

            SavePreferredLineSpacing(_lineSpacingMultiplier);
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

            _indentSize = NormalizeIndentSize(indentSize);
            SavePreferredIndentSize(_indentSize);
        }

        private void FontSettings_Click(object sender, RoutedEventArgs e)
        {
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
                UiMotion.Stop(FontPanel);
                UiMotion.Clear(FontPanel, UIElement.OpacityProperty);
                UiMotion.Clear(EnsureFontPanelTransform(), TranslateTransform.YProperty);
                FontPanel.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
                FontPanel.Opacity = isOpen ? 1 : 0;
                EnsureFontPanelTransform().Y = isOpen ? 0 : -6;
                return;
            }

            AnimateFontPanel(isOpen);
        }

        private void AnimateFontPanel(bool show)
        {
            var animationVersion = ++_fontPanelAnimationVersion;
            var transform = EnsureFontPanelTransform();
            var easing = show ? UiMotion.OutQuad : UiMotion.InQuad;
            var opacityFrom = FontPanel.Opacity;
            var yFrom = transform.Y;

            if (show)
                FontPanel.Visibility = Visibility.Visible;

            UiMotion.Stop(FontPanel);
            UiMotion.RunComposited(
                FontPanel,
                new[]
                {
                    UiMotion.Track(UiMotion.FastMs, easing, opacityFrom, show ? 1 : 0, value => FontPanel.Opacity = value),
                    UiMotion.Track(UiMotion.FastMs, easing, yFrom, show ? 0 : -6, value => transform.Y = value)
                },
                () =>
                {
                    if (!show && animationVersion == _fontPanelAnimationVersion && !_isFontPanelOpen)
                        FontPanel.Visibility = Visibility.Collapsed;
                });
        }

        private TranslateTransform EnsureFontPanelTransform()
        {
            var translate = UiMotion.EnsureTranslate(FontPanel);
            if (FontPanel.RenderTransform is TranslateTransform { IsFrozen: false }
                && Math.Abs(translate.Y) < 0.001
                && !_isFontPanelOpen)
                translate.Y = -6;

            return translate;
        }

        private void ApplyLineSpacingToTargetParagraphs(double spacingMultiplier)
        {
            NormalizeVerticalMarginsForLineSpacingTarget();

            var paragraphs = GetTargetParagraphsForParagraphFormatting();
            foreach (var paragraph in paragraphs)
            {
                ApplyLineSpacingToParagraph(paragraph, spacingMultiplier);
            }
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
                if (paragraph.LineStackingStrategy == LineStackingStrategy.BlockLineHeight
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

            NormalizeVerticalBlockMargins(
                ContentTextBox.Document.Blocks,
                ContentTextBox.Document.ContentStart,
                ContentTextBox.Document.ContentEnd);

            var paragraphs = new List<Paragraph>();
            CollectParagraphs(
                ContentTextBox.Document.Blocks,
                ContentTextBox.Document.ContentStart,
                ContentTextBox.Document.ContentEnd,
                paragraphs);

            foreach (var paragraph in paragraphs)
                ApplyLineSpacingToParagraph(paragraph, _lineSpacingMultiplier);
        }

        private void ApplyLineSpacingToParagraph(Paragraph paragraph, double spacingMultiplier, double? minimumFontSize = null)
        {
            paragraph.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            paragraph.LineHeight = ResolveParagraphLineHeight(paragraph, spacingMultiplier, minimumFontSize);
            paragraph.Margin = NormalizeVerticalBlockMargin(paragraph.Margin);
        }

        private void NormalizeVerticalMarginsForLineSpacingTarget()
        {
            var selection = ContentTextBox.Selection;
            var start = selection == null || selection.IsEmpty
                ? ContentTextBox.Document.ContentStart
                : selection.Start;
            var end = selection == null || selection.IsEmpty
                ? ContentTextBox.Document.ContentEnd
                : selection.End;

            NormalizeVerticalBlockMargins(ContentTextBox.Document.Blocks, start, end);
        }

        private static void NormalizeVerticalBlockMargins(
            BlockCollection blocks,
            TextPointer start,
            TextPointer end)
        {
            foreach (Block block in blocks)
            {
                switch (block)
                {
                    case Paragraph paragraph when TextElementIntersectsRange(paragraph, start, end):
                        paragraph.Margin = NormalizeVerticalBlockMargin(paragraph.Margin);
                        break;
                    case BlockUIContainer blockContainer when TextElementIntersectsRange(blockContainer, start, end):
                        blockContainer.Margin = NormalizeVerticalBlockMargin(blockContainer.Margin);
                        break;
                    case System.Windows.Documents.List list when TextElementIntersectsRange(list, start, end):
                        list.Margin = NormalizeVerticalBlockMargin(list.Margin);
                        foreach (ListItem item in list.ListItems)
                            NormalizeVerticalBlockMargins(item.Blocks, start, end);
                        break;
                    case Section section:
                        NormalizeVerticalBlockMargins(section.Blocks, start, end);
                        break;
                    case Table table:
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

        private static Thickness NormalizeVerticalBlockMargin(Thickness margin)
            => new(margin.Left, 0, margin.Right, 0);

        private List<Paragraph> GetTargetParagraphsForParagraphFormatting()
        {
            var selection = ContentTextBox.Selection;
            var start = selection == null || selection.IsEmpty
                ? ContentTextBox.Document.ContentStart
                : selection.Start;
            var end = selection == null || selection.IsEmpty
                ? ContentTextBox.Document.ContentEnd
                : selection.End;

            var paragraphs = new List<Paragraph>();
            CollectParagraphs(ContentTextBox.Document.Blocks, start, end, paragraphs);
            return paragraphs;
        }

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

        private double ResolveParagraphLineHeight(Paragraph paragraph, double spacingMultiplier, double? minimumFontSize = null)
        {
            var fontSize = ResolveLargestParagraphFontSize(paragraph);
            if (minimumFontSize.HasValue && minimumFontSize.Value > fontSize)
                fontSize = minimumFontSize.Value;

            if (double.IsNaN(fontSize) || fontSize <= 0)
                fontSize = ContentTextBox.FontSize;

            if (double.IsNaN(fontSize) || fontSize <= 0)
                fontSize = 14;

            return Math.Max(1, fontSize * spacingMultiplier);
        }

        private double ResolveLargestParagraphFontSize(Paragraph paragraph)
        {
            var fontSize = NormalizeFontSizeValue(paragraph.FontSize);
            foreach (var inlineFontSize in EnumerateInlineFontSizes(paragraph.Inlines))
            {
                if (inlineFontSize > fontSize)
                    fontSize = inlineFontSize;
            }

            return fontSize;
        }

        private static IEnumerable<double> EnumerateInlineFontSizes(InlineCollection inlines)
        {
            foreach (Inline inline in inlines)
            {
                var fontSize = NormalizeFontSizeValue(inline.FontSize);
                if (fontSize > 0)
                    yield return fontSize;

                switch (inline)
                {
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

        private static IEnumerable<double> EnumerateBlockFontSizes(BlockCollection blocks)
        {
            foreach (Block block in blocks)
            {
                var fontSize = NormalizeFontSizeValue(block.FontSize);
                if (fontSize > 0)
                    yield return fontSize;

                switch (block)
                {
                    case Paragraph paragraph:
                        foreach (var nestedFontSize in EnumerateInlineFontSizes(paragraph.Inlines))
                            yield return nestedFontSize;
                        break;
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
            var currentValue = range.GetPropertyValue(Inline.TextDecorationsProperty);
            var currentDecorations = currentValue as TextDecorationCollection ?? new TextDecorationCollection();
            var hasDecoration = currentDecorations.Any(d => d.Location == decoration.Location);
            if (hasDecoration == enabled)
                return;

            var newDecorations = new TextDecorationCollection(
                currentDecorations.Where(d => d.Location != decoration.Location));
            if (enabled)
                newDecorations.Add(decoration);

            // TextRange.ApplyPropertyValue rejects DependencyProperty.UnsetValue for
            // TextDecorations too (same ArgumentException("'NamedObject' parameter type is not
            // valid...") already hit and fixed for Foreground/Background) - an empty
            // TextDecorationCollection is a properly-typed value that means the same thing ("no
            // decorations") and is accepted.
            range.ApplyPropertyValue(Inline.TextDecorationsProperty, newDecorations);
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

        private static IReadOnlyList<InsertHyperlinkDialog.NoteLinkOption> ResolveNoteLinkOptions()
        {
            if (Application.Current.MainWindow is not MainWindow mainWindow || mainWindow.DataContext is not MainViewModel mainViewModel)
                return Array.Empty<InsertHyperlinkDialog.NoteLinkOption>();

            return mainViewModel.Notes
                .Select(note => new InsertHyperlinkDialog.NoteLinkOption(note.Document.Id, note.Document.Title))
                .ToList();
        }

        /// <summary>Replaces the current selection (if any) with a Hyperlink, or inserts one at the
        /// caret otherwise. Uses Hyperlink's own (Inline, TextPointer) constructor to insert directly
        /// at a position rather than manipulating BlockCollections - selection.Text is cleared first
        /// so the TextPointer captured beforehand tracks the correct now-collapsed insertion spot
        /// (same TextPointer-auto-tracks-edits guarantee the spell-check feature already relies on).</summary>
        private void InsertOrReplaceHyperlink(string displayText, Uri uri)
        {
            var selection = ContentTextBox.Selection;
            var insertionPoint = selection.IsEmpty ? ContentTextBox.CaretPosition : selection.Start;

            if (!selection.IsEmpty)
                selection.Text = string.Empty;

            var hyperlink = new Hyperlink(new Run(displayText), insertionPoint)
            {
                NavigateUri = uri,
                // Keep links accent-colored and undecorated; Ctrl-hint underlines are drawn on
                // HyperlinkHintOverlay so we never mutate TextDecorations via TextRange (that
                // corrupted older links and could crash on click).
                TextDecorations = null,
                Foreground = ResolveHyperlinkForeground()
            };

            ContentTextBox.CaretPosition = hyperlink.ElementEnd;
            MarkEditorContentChanged();
            UpdateEditedIndicator();
            ContentTextBox.Focus();
            if (_hyperlinkCtrlHintsVisible)
                ApplyHyperlinkCtrlHintDecorations(true);
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
        private const int MaxTableSizePickerDimension = 8;
        private bool _tableSizePickerInitialized;
        private int _tableSizePickerHoverRows = DefaultInsertedTableRows;
        private int _tableSizePickerHoverColumns = DefaultInsertedTableColumns;

        private void InsertTableButton_Click(object sender, RoutedEventArgs e)
        {
            EnsureTableSizePickerInitialized();
            HighlightTableSizePicker(DefaultInsertedTableRows, DefaultInsertedTableColumns);
            TableSizePickerPopup.IsOpen = true;
        }

        private void EnsureTableSizePickerInitialized()
        {
            if (_tableSizePickerInitialized || TableSizePickerGrid is null)
                return;

            TableSizePickerGrid.Children.Clear();
            for (var row = 1; row <= MaxTableSizePickerDimension; row++)
            {
                for (var column = 1; column <= MaxTableSizePickerDimension; column++)
                {
                    var cell = new Border
                    {
                        Width = 18,
                        Height = 18,
                        Margin = new Thickness(2),
                        BorderThickness = new Thickness(1),
                        BorderBrush = TryFindResource("BorderColor") as Brush ?? Brushes.Gray,
                        Background = TryFindResource("CardBackground") as Brush ?? Brushes.White,
                        Cursor = Cursors.Hand,
                        Tag = (row, column)
                    };
                    cell.MouseEnter += TableSizePickerCell_MouseEnter;
                    cell.MouseLeftButtonUp += TableSizePickerCell_MouseLeftButtonUp;
                    TableSizePickerGrid.Children.Add(cell);
                }
            }

            _tableSizePickerInitialized = true;
        }

        private void TableSizePickerCell_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is not Border { Tag: (int rows, int columns) })
                return;

            HighlightTableSizePicker(rows, columns);
        }

        private void TableSizePickerCell_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border { Tag: (int rows, int columns) })
                return;

            TableSizePickerPopup.IsOpen = false;
            InsertTableOfSize(rows, columns);
            e.Handled = true;
        }

        private void HighlightTableSizePicker(int rows, int columns)
        {
            _tableSizePickerHoverRows = rows;
            _tableSizePickerHoverColumns = columns;

            if (TableSizePickerLabel is not null)
            {
                TableSizePickerLabel.Text = string.Format(
                    LocalizationService.GetString("InsertTableSizeFormat"),
                    rows,
                    columns);
            }

            var selectedBackground = TryFindResource("HoverBackground") as Brush ?? Brushes.LightGray;
            var idleBackground = TryFindResource("CardBackground") as Brush ?? Brushes.White;
            var selectedBorder = TryFindResource("TextColor") as Brush ?? Brushes.Black;
            var idleBorder = TryFindResource("BorderColor") as Brush ?? Brushes.Gray;

            foreach (Border cell in TableSizePickerGrid.Children.OfType<Border>())
            {
                if (cell.Tag is not (int cellRows, int cellColumns))
                    continue;

                var selected = cellRows <= rows && cellColumns <= columns;
                cell.Background = selected ? selectedBackground : idleBackground;
                cell.BorderBrush = selected ? selectedBorder : idleBorder;
            }
        }

        private void InsertTableOfSize(int rows, int columns)
        {
            var table = BuildTable(rows, columns);
            InsertTableAtCaret(table);
            FocusTableCell(table, 0, 0);
            MarkEditorContentChanged();
            UpdateEditedIndicator();
            ContentTextBox.Focus();
        }

        private static Table BuildTable(int rows, int columns)
        {
            rows = Math.Clamp(rows, 1, MaxTableSizePickerDimension);
            columns = Math.Clamp(columns, 1, MaxTableSizePickerDimension);

            var table = new Table();
            for (var c = 0; c < columns; c++)
            {
                table.Columns.Add(new TableColumn
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });
            }

            var rowGroup = new TableRowGroup();
            for (var r = 0; r < rows; r++)
            {
                var row = new TableRow();
                for (var c = 0; c < columns; c++)
                    row.Cells.Add(CreateEmptyTableCell());

                rowGroup.Rows.Add(row);
            }

            table.RowGroups.Add(rowGroup);
            return table;
        }

        private static TableCell CreateEmptyTableCell()
        {
            return new TableCell(new Paragraph())
            {
                BorderBrush = ResolveTableCellBorderBrush(),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4, 6, 4)
            };
        }

        private static Brush ResolveTableCellBorderBrush()
        {
            if (Application.Current.Resources.Contains("BorderColor")
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
                    InsertTableRow(table, rowCount);
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

            var rowCount = GetTableRowCount(table);
            var columnCount = Math.Max(1, table.Columns.Count);

            var insertMenu = CreateTableSubMenu("TableInsert");
            insertMenu.Items.Add(CreateTableMenuItem(
                "TableInsertRowAbove",
                () =>
                {
                    InsertTableRow(table, rowIndex);
                    FocusTableCell(table, rowIndex, columnIndex);
                }));
            insertMenu.Items.Add(CreateTableMenuItem(
                "TableInsertRowBelow",
                () =>
                {
                    InsertTableRow(table, rowIndex + 1);
                    FocusTableCell(table, rowIndex + 1, columnIndex);
                }));
            insertMenu.Items.Add(new Separator());
            insertMenu.Items.Add(CreateTableMenuItem(
                "TableInsertColumnLeft",
                () =>
                {
                    InsertTableColumn(table, columnIndex);
                    FocusTableCell(table, rowIndex, columnIndex);
                }));
            insertMenu.Items.Add(CreateTableMenuItem(
                "TableInsertColumnRight",
                () =>
                {
                    InsertTableColumn(table, columnIndex + 1);
                    FocusTableCell(table, rowIndex, columnIndex + 1);
                }));
            menu.Items.Add(insertMenu);

            var duplicateMenu = CreateTableSubMenu("TableDuplicate");
            duplicateMenu.Items.Add(CreateTableMenuItem(
                "TableDuplicateRow",
                () =>
                {
                    DuplicateTableRow(table, rowIndex);
                    FocusTableCell(table, rowIndex + 1, columnIndex);
                }));
            duplicateMenu.Items.Add(CreateTableMenuItem(
                "TableDuplicateColumn",
                () =>
                {
                    DuplicateTableColumn(table, columnIndex);
                    FocusTableCell(table, rowIndex, columnIndex + 1);
                }));
            menu.Items.Add(duplicateMenu);

            var moveMenu = CreateTableSubMenu("TableMove");
            var moveUpItem = CreateTableMenuItem(
                "TableMoveRowUp",
                () =>
                {
                    if (MoveTableRow(table, rowIndex, -1))
                        FocusTableCell(table, rowIndex - 1, columnIndex);
                });
            moveUpItem.IsEnabled = rowIndex > 0;
            moveMenu.Items.Add(moveUpItem);

            var moveDownItem = CreateTableMenuItem(
                "TableMoveRowDown",
                () =>
                {
                    if (MoveTableRow(table, rowIndex, 1))
                        FocusTableCell(table, rowIndex + 1, columnIndex);
                });
            moveDownItem.IsEnabled = rowIndex < rowCount - 1;
            moveMenu.Items.Add(moveDownItem);
            menu.Items.Add(moveMenu);

            var clearMenu = CreateTableSubMenu("TableClear");
            clearMenu.Items.Add(CreateTableMenuItem(
                "TableClearCell",
                () =>
                {
                    ClearTableCell(cell);
                    FocusTableCell(table, rowIndex, columnIndex);
                }));
            clearMenu.Items.Add(CreateTableMenuItem(
                "TableClearRow",
                () =>
                {
                    ClearTableRow(table, rowIndex);
                    FocusTableCell(table, rowIndex, columnIndex);
                }));
            menu.Items.Add(clearMenu);

            var alignMenu = CreateTableSubMenu("TableAlign");
            alignMenu.Items.Add(CreateTableMenuItem(
                "TableAlignLeft",
                () => SetTableCellAlignment(cell, TextAlignment.Left)));
            alignMenu.Items.Add(CreateTableMenuItem(
                "TableAlignCenter",
                () => SetTableCellAlignment(cell, TextAlignment.Center)));
            alignMenu.Items.Add(CreateTableMenuItem(
                "TableAlignRight",
                () => SetTableCellAlignment(cell, TextAlignment.Right)));
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
            deleteMenu.Items.Add(CreateTableMenuItem(
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
            deleteMenu.Items.Add(CreateTableMenuItem(
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
            deleteMenu.Items.Add(CreateTableMenuItem("TableDeleteTable", () => DeleteTable(table)));
            menu.Items.Add(deleteMenu);

            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = LocalizationService.GetString("Cut"), Command = ApplicationCommands.Cut });
            menu.Items.Add(new MenuItem { Header = LocalizationService.GetString("Copy"), Command = ApplicationCommands.Copy });
            menu.Items.Add(new MenuItem { Header = LocalizationService.GetString("Paste"), Command = ApplicationCommands.Paste });
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = LocalizationService.GetString("SelectAll"), Command = ApplicationCommands.SelectAll });
            return menu;
        }

        private static MenuItem CreateTableSubMenu(string localizationKey)
            => new() { Header = LocalizationService.GetString(localizationKey) };

        private MenuItem CreateTableMenuItem(string localizationKey, Action action)
        {
            var item = new MenuItem { Header = LocalizationService.GetString(localizationKey) };
            item.Click += (_, _) =>
            {
                // Run after the context menu closes so FlowDocument edits are not discarded.
                Dispatcher.BeginInvoke(() =>
                {
                    action();
                    MarkEditorContentChanged();
                    UpdateEditedIndicator();
                    ContentTextBox.Focus();
                }, DispatcherPriority.Input);
            };
            return item;
        }

        private static void InsertTableRow(Table table, int rowIndex)
        {
            var rowGroup = GetPrimaryTableRowGroup(table);
            if (rowGroup is null)
                return;

            var columnCount = Math.Max(table.Columns.Count, 1);
            while (table.Columns.Count < columnCount)
            {
                table.Columns.Add(new TableColumn
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });
            }

            var row = new TableRow();
            for (var c = 0; c < columnCount; c++)
                row.Cells.Add(CreateEmptyTableCell());

            rowIndex = Math.Clamp(rowIndex, 0, rowGroup.Rows.Count);
            rowGroup.Rows.Insert(rowIndex, row);
        }

        private static void InsertTableColumn(Table table, int columnIndex)
        {
            var rowGroup = GetPrimaryTableRowGroup(table);
            if (rowGroup is null)
                return;

            columnIndex = Math.Clamp(columnIndex, 0, table.Columns.Count);
            table.Columns.Insert(columnIndex, new TableColumn
            {
                Width = new GridLength(1, GridUnitType.Star)
            });

            foreach (TableRow row in rowGroup.Rows)
            {
                var insertAt = Math.Min(columnIndex, row.Cells.Count);
                row.Cells.Insert(insertAt, CreateEmptyTableCell());
            }
        }

        private static void DuplicateTableRow(Table table, int rowIndex)
        {
            var rowGroup = GetPrimaryTableRowGroup(table);
            if (rowGroup is null || rowIndex < 0 || rowIndex >= rowGroup.Rows.Count)
                return;

            var source = rowGroup.Rows[rowIndex];
            var copy = new TableRow();
            foreach (TableCell cell in source.Cells)
                copy.Cells.Add(CloneTableCell(cell));

            rowGroup.Rows.Insert(rowIndex + 1, copy);
        }

        private static void DuplicateTableColumn(Table table, int columnIndex)
        {
            var rowGroup = GetPrimaryTableRowGroup(table);
            if (rowGroup is null || columnIndex < 0 || columnIndex >= table.Columns.Count)
                return;

            table.Columns.Insert(columnIndex + 1, new TableColumn
            {
                Width = new GridLength(1, GridUnitType.Star)
            });

            foreach (TableRow row in rowGroup.Rows)
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
            var rowGroup = GetPrimaryTableRowGroup(table);
            if (rowGroup is null)
                return false;

            var targetIndex = rowIndex + delta;
            if (rowIndex < 0 || rowIndex >= rowGroup.Rows.Count || targetIndex < 0 || targetIndex >= rowGroup.Rows.Count)
                return false;

            var row = rowGroup.Rows[rowIndex];
            rowGroup.Rows.RemoveAt(rowIndex);
            rowGroup.Rows.Insert(targetIndex, row);
            return true;
        }

        private static void ClearTableCell(TableCell cell)
        {
            cell.Blocks.Clear();
            cell.Blocks.Add(new Paragraph());
        }

        private static void ClearTableRow(Table table, int rowIndex)
        {
            var rowGroup = GetPrimaryTableRowGroup(table);
            if (rowGroup is null || rowIndex < 0 || rowIndex >= rowGroup.Rows.Count)
                return;

            foreach (TableCell cell in rowGroup.Rows[rowIndex].Cells)
                ClearTableCell(cell);
        }

        private static void SetTableCellAlignment(TableCell cell, TextAlignment alignment)
        {
            cell.TextAlignment = alignment;
            foreach (Block block in cell.Blocks)
            {
                if (block is Paragraph paragraph)
                    paragraph.TextAlignment = alignment;
            }
        }

        private void SelectTableRow(Table table, int rowIndex)
        {
            var rowGroup = GetPrimaryTableRowGroup(table);
            if (rowGroup is null || rowIndex < 0 || rowIndex >= rowGroup.Rows.Count)
                return;

            var row = rowGroup.Rows[rowIndex];
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
            foreach (TableRow row in rowGroup.Rows)
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
            if (source.Background is not null)
                clone.Background = source.Background;

            try
            {
                var sourceRange = new TextRange(source.ContentStart, source.ContentEnd);
                using var stream = new MemoryStream();
                sourceRange.Save(stream, DataFormats.Xaml);
                stream.Position = 0;
                clone.Blocks.Clear();
                clone.Blocks.Add(new Paragraph());
                new TextRange(clone.ContentStart, clone.ContentEnd).Load(stream, DataFormats.Xaml);
            }
            catch
            {
                // Keep empty cell on clone failure.
            }

            return clone;
        }

        private static void DeleteTableRow(Table table, int rowIndex)
        {
            var rowGroup = GetPrimaryTableRowGroup(table);
            if (rowGroup is null || rowIndex < 0 || rowIndex >= rowGroup.Rows.Count)
                return;

            rowGroup.Rows.RemoveAt(rowIndex);
        }

        private static void DeleteTableColumn(Table table, int columnIndex)
        {
            var rowGroup = GetPrimaryTableRowGroup(table);
            if (rowGroup is null || columnIndex < 0 || columnIndex >= table.Columns.Count)
                return;

            table.Columns.RemoveAt(columnIndex);
            foreach (TableRow row in rowGroup.Rows)
            {
                if (columnIndex < row.Cells.Count)
                    row.Cells.RemoveAt(columnIndex);
            }
        }

        private void DeleteTable(Table table)
        {
            var parentBlocks = GetParentBlockCollection(table);
            parentBlocks?.Remove(table);
        }

        private void FocusTableCell(Table table, int rowIndex, int columnIndex)
        {
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
            => OpenButtonContextMenu(BulletListButton);

        private void NumberedListButton_Click(object sender, RoutedEventArgs e)
            => OpenButtonContextMenu(NumberedListButton);

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

        /// <summary>Toggle semantics matching bullet/numbered lists: if every targeted paragraph
        /// is already a checklist item, strip the prefix from all of them; otherwise add it to
        /// whichever ones don't have it yet (leaving already-checklist paragraphs untouched).</summary>
        private void ChecklistButton_Click(object sender, RoutedEventArgs e)
        {
            var paragraphs = GetTargetParagraphsForInlineFormatting();
            if (paragraphs.Count == 0)
                return;

            var allAreChecklistItems = paragraphs.All(IsChecklistParagraph);
            var changeStarted = false;

            try
            {
                ContentTextBox.BeginChange();
                changeStarted = true;

                foreach (var paragraph in paragraphs)
                {
                    if (allAreChecklistItems)
                        RemoveChecklistPrefix(paragraph);
                    else if (!IsChecklistParagraph(paragraph))
                        InsertChecklistPrefix(paragraph, ChecklistUncheckedGlyph);
                }
            }
            finally
            {
                if (changeStarted)
                    ContentTextBox.EndChange();
            }

            MarkEditorContentChanged();
            UpdateEditedIndicator();
            ContentTextBox.Focus();
        }

        private static bool IsChecklistParagraph(Paragraph paragraph)
            => TryGetChecklistPrefixEnd(paragraph, out _, out _);

        /// <summary>True if the paragraph starts with a checklist glyph + separator. When true,
        /// prefixEnd is the position right after the separator (start of the item's own text) and
        /// isChecked reflects which glyph. Reads via plain text (TextRange.Text), not by assuming
        /// the glyph stays its own dedicated Run - WPF coalesces adjacent same-formatted Runs as
        /// the user types, so a Run-identity check would break the moment someone typed past it.</summary>
        private static bool TryGetChecklistPrefixEnd(Paragraph paragraph, out TextPointer? prefixEnd, out bool isChecked)
        {
            prefixEnd = null;
            isChecked = false;

            var start = paragraph.ContentStart;
            var text = new TextRange(start, paragraph.ContentEnd).Text;
            if (text.StartsWith(ChecklistUncheckedGlyph + ChecklistPrefixSeparator, StringComparison.Ordinal))
            {
                isChecked = false;
            }
            else if (text.StartsWith(ChecklistCheckedGlyph + ChecklistPrefixSeparator, StringComparison.Ordinal))
            {
                isChecked = true;
            }
            else
            {
                return false;
            }

            prefixEnd = start.GetPositionAtOffset(2, LogicalDirection.Forward);
            return prefixEnd != null;
        }

        private static void InsertChecklistPrefix(Paragraph paragraph, string glyph)
        {
            var start = paragraph.ContentStart;
            new TextRange(start, start).Text = glyph + ChecklistPrefixSeparator;
        }

        private static void RemoveChecklistPrefix(Paragraph paragraph)
        {
            if (!TryGetChecklistPrefixEnd(paragraph, out var prefixEnd, out _) || prefixEnd is null)
                return;

            // Clear completion styling before the prefix text goes away - once IsChecklistParagraph
            // can no longer see the glyph there is nothing left to target it with.
            ApplyChecklistCompletionStyle(paragraph, isChecked: false);
            new TextRange(paragraph.ContentStart, prefixEnd).Text = string.Empty;
        }

        /// <summary>Strikethrough + dimmed foreground on the item's own text (after the prefix),
        /// reusing the same per-decoration-Location toggle Underline/Strikethrough already use so
        /// this never clobbers a decoration the user applied manually. The dimmed color is a fixed
        /// neutral gray rather than a theme resource - simpler than re-walking every checked item
        /// on ThemeManager.ThemeChanged, and reads acceptably in both themes.</summary>
        private static void ApplyChecklistCompletionStyle(Paragraph paragraph, bool isChecked)
        {
            if (!TryGetChecklistPrefixEnd(paragraph, out var prefixEnd, out _) || prefixEnd is null)
                return;

            var itemRange = new TextRange(prefixEnd, paragraph.ContentEnd);
            SetRangeTextDecoration(itemRange, TextDecorations.Strikethrough[0], isChecked);
            itemRange.ApplyPropertyValue(
                TextElement.ForegroundProperty,
                isChecked ? ChecklistCompletedForegroundBrush : ResolveSelectionColorBrush(TextElement.ForegroundProperty, null));
        }

        /// <summary>Hit-tests a mouse-down against the checklist glyph at the very start of
        /// whatever paragraph was clicked. Clicking the glyph itself toggles checked/unchecked;
        /// clicking anywhere else on a checklist line just moves the caret as normal, same as any
        /// other text.</summary>
        private bool TryToggleChecklistGlyphAtPoint(Point point)
        {
            var pointer = ContentTextBox.GetPositionFromPoint(point, snapToText: true);
            var paragraph = pointer?.Paragraph;
            if (pointer is null || paragraph is null)
                return false;

            if (!TryGetChecklistPrefixEnd(paragraph, out var prefixEnd, out var isChecked) || prefixEnd is null)
                return false;

            if (pointer.CompareTo(paragraph.ContentStart) < 0 || pointer.CompareTo(prefixEnd) >= 0)
                return false;

            var changeStarted = false;
            try
            {
                ContentTextBox.BeginChange();
                changeStarted = true;
                new TextRange(paragraph.ContentStart, prefixEnd).Text =
                    (isChecked ? ChecklistUncheckedGlyph : ChecklistCheckedGlyph) + ChecklistPrefixSeparator;
                ApplyChecklistCompletionStyle(paragraph, !isChecked);
            }
            finally
            {
                if (changeStarted)
                    ContentTextBox.EndChange();
            }

            MarkEditorContentChanged();
            UpdateEditedIndicator();
            return true;
        }

        /// <summary>Enter on a non-empty checklist item continues the list with a fresh unchecked
        /// item, matching bullet/numbered lists. Enter on an EMPTY checklist item exits the list
        /// instead (strips the prefix, then falls through to a normal paragraph break) - same
        /// escape hatch Word/most editors give empty list items. Runs the native paragraph-break
        /// command itself (rather than manually splitting Inlines) so arbitrary rich content -
        /// images, mixed formatting - splits exactly the way native Enter would.</summary>
        private bool TryHandleChecklistEnterKey(KeyEventArgs e)
        {
            if (e.Key != Key.Return
                || (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift
                || !IsEditorKeyboardFocusWithin(e.OriginalSource))
            {
                return false;
            }

            var selection = ContentTextBox.Selection;
            if (selection is null || !selection.IsEmpty)
                return false;

            var paragraph = ContentTextBox.CaretPosition?.Paragraph;
            if (paragraph is null || !TryGetChecklistPrefixEnd(paragraph, out var prefixEnd, out _) || prefixEnd is null)
                return false;

            var itemIsEmpty = string.IsNullOrWhiteSpace(new TextRange(prefixEnd, paragraph.ContentEnd).Text);

            var changeStarted = false;
            try
            {
                ContentTextBox.BeginChange();
                changeStarted = true;

                if (itemIsEmpty)
                {
                    RemoveChecklistPrefix(paragraph);
                    return false; // let the native Enter handler create a plain new paragraph
                }

                if (EditingCommands.EnterParagraphBreak.CanExecute(null, ContentTextBox))
                    EditingCommands.EnterParagraphBreak.Execute(null, ContentTextBox);

                var newParagraph = ContentTextBox.CaretPosition?.Paragraph;
                if (newParagraph is not null && !IsChecklistParagraph(newParagraph))
                {
                    InsertChecklistPrefix(newParagraph, ChecklistUncheckedGlyph);
                    ContentTextBox.CaretPosition = newParagraph.ContentStart.GetPositionAtOffset(2, LogicalDirection.Forward)
                        ?? newParagraph.ContentStart;
                    ContentTextBox.Selection.Select(ContentTextBox.CaretPosition, ContentTextBox.CaretPosition);
                }
            }
            finally
            {
                if (changeStarted)
                    ContentTextBox.EndChange();
            }

            MarkEditorContentChanged();
            UpdateEditedIndicator();
            e.Handled = true;
            return true;
        }

        private void ListRemoveButton_Click(object sender, RoutedEventArgs e)
        {
            ContentTextBox.Focus();
            var activeLists = GetActiveDocumentLists();
            if (activeLists.Count == 0)
                return;

            var markerStyle = activeLists.First().MarkerStyle;
            var command = IsNumberedMarkerStyle(markerStyle)
                ? EditingCommands.ToggleNumbering
                : EditingCommands.ToggleBullets;

            if (command.CanExecute(null, ContentTextBox))
                command.Execute(null, ContentTextBox);

            MarkEditorContentChanged();
            UpdateEditedIndicator();
            UpdateListRemoveButtonState();
            ContentTextBox.Focus();
        }

        private void ApplyListStyle(TextMarkerStyle markerStyle)
        {
            ContentTextBox.Focus();

            var selection = ContentTextBox.Selection;
            var selectionStart = selection?.Start;
            var selectionEnd = selection?.End;

            var activeLists = GetActiveDocumentLists();
            if (activeLists.Count == 0)
            {
                var command = IsNumberedMarkerStyle(markerStyle)
                    ? EditingCommands.ToggleNumbering
                    : EditingCommands.ToggleBullets;

                if (command.CanExecute(null, ContentTextBox))
                    command.Execute(null, ContentTextBox);

                activeLists = GetActiveDocumentLists();
            }

            foreach (var list in activeLists)
            {
                list.MarkerStyle = markerStyle;
            }

            var markerSize = ResolveCurrentSelectionTypography().FontSize;
            if (markerSize <= 0)
                markerSize = ContentTextBox.FontSize;
            Brush? markerForeground = TryGetRichTextBoxForeground(out var brush) ? brush : null;
            SyncListMarkerAppearance(activeLists, markerSize, markerForeground);

            if (selectionStart is not null && selectionEnd is not null)
                RefreshEditorSelectionLayout(selectionStart, selectionEnd);

            MarkEditorContentChanged();
            UpdateEditedIndicator();
            UpdateListRemoveButtonState();
            ContentTextBox.Focus();
        }

        /// <summary>
        /// WPF draws list markers from List/ListItem FontSize and Foreground, not from Runs inside the paragraph.
        /// Keep those in sync so bullets/numbers match the text size and theme.
        /// </summary>
        private static void SyncListMarkerAppearance(
            IEnumerable<System.Windows.Documents.List> lists,
            double? fontSize = null,
            Brush? foreground = null)
        {
            double? normalizedSize = fontSize is double size
                ? NormalizeEditorFontSize(size)
                : null;

            foreach (var list in lists)
            {
                if (normalizedSize.HasValue)
                    list.FontSize = normalizedSize.Value;

                if (foreground != null)
                    list.Foreground = foreground;

                list.ClearValue(System.Windows.Documents.List.MarkerOffsetProperty);

                foreach (ListItem item in list.ListItems)
                {
                    if (normalizedSize.HasValue)
                        item.FontSize = normalizedSize.Value;

                    if (foreground != null)
                        item.Foreground = foreground;
                }
            }
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

        private void UpdateListRemoveButtonState()
        {
            if (ListRemoveButton is null)
                return;

            ListRemoveButton.IsEnabled = GetActiveDocumentLists().Count > 0;
        }

        private HashSet<System.Windows.Documents.List> GetActiveDocumentLists()
        {
            var lists = new HashSet<System.Windows.Documents.List>();
            var selection = ContentTextBox.Selection;

            if (selection is null)
                return lists;

            if (selection.IsEmpty)
            {
                AddActiveDocumentList(selection.Start, lists);
            }
            else
            {
                CollectDocumentLists(ContentTextBox.Document.Blocks, selection.Start, selection.End, lists);
                AddActiveDocumentList(selection.Start, lists);
                AddActiveDocumentList(selection.End, lists);
            }

            AddActiveDocumentList(ContentTextBox.CaretPosition, lists);
            return lists;
        }

        private HashSet<System.Windows.Documents.List> CollectAllDocumentLists()
        {
            var lists = new HashSet<System.Windows.Documents.List>();
            CollectAllDocumentLists(ContentTextBox.Document.Blocks, lists);
            return lists;
        }

        private static void CollectAllDocumentLists(
            BlockCollection blocks,
            ISet<System.Windows.Documents.List> lists)
        {
            foreach (Block block in blocks)
            {
                switch (block)
                {
                    case System.Windows.Documents.List list:
                        lists.Add(list);
                        foreach (ListItem item in list.ListItems)
                            CollectAllDocumentLists(item.Blocks, lists);
                        break;
                    case Section section:
                        CollectAllDocumentLists(section.Blocks, lists);
                        break;
                    case Table table:
                        foreach (TableRowGroup rowGroup in table.RowGroups)
                        {
                            foreach (TableRow row in rowGroup.Rows)
                            {
                                foreach (TableCell cell in row.Cells)
                                    CollectAllDocumentLists(cell.Blocks, lists);
                            }
                        }

                        break;
                }
            }
        }

        private static void CollectDocumentLists(
            BlockCollection blocks,
            TextPointer start,
            TextPointer end,
            ISet<System.Windows.Documents.List> lists)
        {
            foreach (Block block in blocks)
            {
                switch (block)
                {
                    case System.Windows.Documents.List list:
                        if (TextElementIntersectsRange(list, start, end))
                            lists.Add(list);

                        foreach (ListItem item in list.ListItems)
                            CollectDocumentLists(item.Blocks, start, end, lists);

                        break;
                    case Section section:
                        CollectDocumentLists(section.Blocks, start, end, lists);
                        break;
                    case Table table:
                        foreach (TableRowGroup rowGroup in table.RowGroups)
                        {
                            foreach (TableRow row in rowGroup.Rows)
                            {
                                foreach (TableCell cell in row.Cells)
                                    CollectDocumentLists(cell.Blocks, start, end, lists);
                            }
                        }

                        break;
                }
            }
        }

        private static void AddActiveDocumentList(
            TextPointer? pointer,
            ISet<System.Windows.Documents.List> lists)
        {
            var list = FindAncestorDocumentList(pointer);
            if (list != null)
                lists.Add(list);
        }

        private static System.Windows.Documents.List? FindAncestorDocumentList(TextPointer? pointer)
        {
            DependencyObject? current = pointer?.Parent as DependencyObject;
            while (current != null)
            {
                if (current is System.Windows.Documents.List list)
                    return list;

                current = current switch
                {
                    FrameworkContentElement contentElement => contentElement.Parent,
                    FrameworkElement frameworkElement => frameworkElement.Parent,
                    _ => LogicalTreeHelper.GetParent(current)
                };
            }

            return null;
        }

        private static bool IsNumberedMarkerStyle(TextMarkerStyle markerStyle)
            => markerStyle == TextMarkerStyle.Decimal
                || markerStyle == TextMarkerStyle.LowerLatin
                || markerStyle == TextMarkerStyle.UpperLatin
                || markerStyle == TextMarkerStyle.LowerRoman
                || markerStyle == TextMarkerStyle.UpperRoman;

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

        private static void SavePreferredFontFamily(string fontFamily)
        {
            var settings = AppSettingsService.Load();
            settings.PreferredFontFamily = string.IsNullOrWhiteSpace(fontFamily) ? DefaultEditorFontFamily : fontFamily;
            AppSettingsService.Save(settings);
        }

        private static void SavePreferredFontSize(double fontSize)
        {
            var settings = AppSettingsService.Load();
            settings.PreferredFontSize = NormalizeEditorFontSize(fontSize);
            AppSettingsService.Save(settings);
        }

        private static void SavePreferredLineSpacing(double lineSpacing)
        {
            var settings = AppSettingsService.Load();
            settings.PreferredLineSpacing = NormalizeLineSpacingMultiplier(lineSpacing);
            AppSettingsService.Save(settings);
        }

        private static void SavePreferredIndentSize(double indentSize)
        {
            var settings = AppSettingsService.Load();
            settings.PreferredIndentSize = NormalizeIndentSize(indentSize);
            AppSettingsService.Save(settings);
        }

        private static void SaveEditorFontPanelPreference(bool isOpen)
        {
            var settings = AppSettingsService.Load();
            settings.IsEditorFontPanelOpen = isOpen;
            AppSettingsService.Save(settings);
        }

        private static void SaveEditorWordWrapPreference(bool isEnabled)
        {
            var settings = AppSettingsService.Load();
            settings.IsEditorWordWrapEnabled = isEnabled;
            AppSettingsService.Save(settings);
        }

        private void PersistCurrentDocumentEditorUiState()
        {
            if (_currentDocument == null)
                return;

            _currentDocument.IsEditorFontPanelOpen = _isFontPanelOpen;
            _currentDocument.IsWordWrapEnabled = _isWordWrapEnabled;

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
            ApplyContentToEditor(document.Content);
            ApplyEditorTextLayout();
            SyncFontSelectorsFromSelection();
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
                    tr.Text = string.Empty;
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

                _floatingImages.Insert(load, width, height);
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

                _floatingImages.Insert(load, width, height);
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
            _isSerializingEditorContent = true;
            try
            {
                _floatingImages.StripImageExtentPad();

                var textRange = new TextRange(
                    ContentTextBox.Document.ContentStart,
                    ContentTextBox.Document.ContentEnd);

                using var stream = new MemoryStream();
                textRange.Save(stream, DataFormats.XamlPackage);
                return Convert.ToBase64String(stream.ToArray());
            }
            finally
            {
                _isSerializingEditorContent = false;
                _floatingImages.EnsureImageExtentPad();
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
            ContentTextBox.LayoutTransform = Transform.Identity;
            EditorSurface.LayoutTransform = new ScaleTransform(_zoomLevel, _zoomLevel);
            UpdateEditorToolButtonStates();
        }
        private bool _isWordWrapEnabled = true;

        private void ToggleWordWrap_Click(object sender, RoutedEventArgs e)
        {
            _isWordWrapEnabled = !_isWordWrapEnabled;
            SaveEditorWordWrapPreference(_isWordWrapEnabled);
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
