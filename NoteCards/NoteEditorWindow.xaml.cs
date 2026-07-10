using Microsoft.Win32;
using NoteCards.Animations;
using NoteCards.Localization;
using NoteCards.Models;
using NoteCards.Services;
using NoteCards.ViewModels;
using NoteCards.Views;
using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using NoteCards.Controls;

namespace NoteCards
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

        // Auto-save fields
        public event Action<NoteDocument>? DocumentAutoSaved;
        public event Action<NoteEditorWindow>? CloseRequested;
        public static event EventHandler? AiGenerationStateChanged;
        private static int _activeAiGenerationCount;
        private System.Threading.Timer? _autoSaveTimer;
        private bool _isAutoSaveEnabled = true;
        private int _autoSaveIntervalMs = 30000; // 30 seconds (default)
        private DateTime _lastAutoSaveTime = DateTime.MinValue;
        private string _lastSavedContent = string.Empty;
        private string _lastSavedSnapshot = string.Empty;
        private NoteDocument? _currentDocument;
        private const int MaxEditHistoryEntries = 100;
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
        private bool _isLoadingDocument;
        private bool _isSerializingEditorContent;
        private bool _allowCloseWithoutPrompt;
        private bool _isFontPanelOpen;
        private bool _isSyncingLineSpacingSelector;
        private bool _isSyncingIndentSizeSelector;
        private double _lineSpacingMultiplier = 1.0;
        private double _indentSize = DefaultIndentSize;
        private string _measurementUnitSystem = AppSettings.UnitSystemImperial;
        private const string DefaultEditorFontFamily = "Calibri";
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
        private const double FloatingImageAnchorHeight = 80;
        private const double UnwrappedDocumentPageWidth = 10000;
        private const string ImageMarkerPrefix = "[[NoteCardsImage:";
        private const string ImageMarkerSuffix = "]]";
        private ScrollViewer? _contentScrollViewer;
        private INotifyPropertyChanged? _editorSettingsSource;
        private bool _isUpdatingFloatingImageLayout;
        private bool _isFloatingImageOverlayLayoutUpdateQueued;
        private bool _isInlineImageDropPending;

        private static readonly DependencyProperty FloatingDocumentLeftProperty =
            DependencyProperty.RegisterAttached(
                "FloatingDocumentLeft",
                typeof(double),
                typeof(NoteEditorWindow),
                new PropertyMetadata(0d));

        private static readonly DependencyProperty FloatingDocumentTopProperty =
            DependencyProperty.RegisterAttached(
                "FloatingDocumentTop",
                typeof(double),
                typeof(NoteEditorWindow),
                new PropertyMetadata(0d));

        private static readonly DependencyProperty InlineImageIdProperty =
            DependencyProperty.RegisterAttached(
                "InlineImageId",
                typeof(Guid),
                typeof(NoteEditorWindow),
                new PropertyMetadata(Guid.Empty));

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
            NoteCards.Services.WindowThemeService.Register(this);
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
            ContentTextBox.IsDocumentEnabled = true; // Ensure interactive elements (like ResizableImage) can receive pointer events
            ContentTextBox.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, ContentTextBox_CopyExecuted, ContentTextBox_CopyCanExecute));
            ContentTextBox.PreviewMouseDown += ContentTextBox_PreviewMouseDown;
            ContentTextBox.SizeChanged += (_, _) => ScheduleFloatingImageOverlayLayoutUpdate();
            EditorSurface.SizeChanged += (_, _) => ScheduleFloatingImageOverlayLayoutUpdate();
            PreviewKeyDown += NoteEditorWindow_PreviewKeyDown;
            Loaded += NoteEditorWindow_Loaded;
            RootGrid.Loaded += NoteEditorWindow_Loaded;

            // Subscribe to theme changes to update RichTextBox foreground
            ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
        }

        private void InitializeEditorUiPreferences()
        {
            var settings = AppSettingsService.Load();
            _measurementUnitSystem = AppSettings.NormalizeMeasurementUnitSystem(settings.MeasurementUnitSystem);
            _isWordWrapEnabled = settings.IsEditorWordWrapEnabled;
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

        private void PopulateFontFamilyOptions()
        {
            var fontNames = new List<string>();

            void AddFontName(string? fontName)
            {
                if (string.IsNullOrWhiteSpace(fontName))
                    return;

                if (fontNames.Any(existing => string.Equals(existing, fontName, StringComparison.OrdinalIgnoreCase)))
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

            FontFamilyBox.Items.Clear();
            foreach (var fontName in fontNames)
                FontFamilyBox.Items.Add(CreateEditorComboBoxItem(fontName));
        }

        private void PopulateFontSizeOptions()
        {
            FontSizeBox.Items.Clear();
            foreach (var fontSize in WordFontSizes)
                FontSizeBox.Items.Add(CreateEditorComboBoxItem(FormatFontSize(fontSize), fontSize));
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
            ScheduleFloatingImageOverlayLayoutUpdate();
        }

        private void ThemeManager_ThemeChanged(object? sender, EventArgs e)
        {
            NoteCards.Services.WindowThemeService.ApplyTheme(this, ThemeManager.CurrentTheme, rebuildFrame: true);
            ApplyRichTextBoxTheme();
            UpdateEditorToolButtonStates();
            UpdateEditorScrollbarCorner();
        }

        private void NoteEditorWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UiMotion.ConfigurePopupsForWindow(this);
            AttachContentScrollViewer();
            UpdateFloatingImageOverlayLayout();
            ScheduleFloatingImageOverlayLayoutUpdate();
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
            UpdateFloatingImageOverlayLayout();
            ScheduleFloatingImageOverlayLayoutUpdate();
        }

        private void UpdateEditorScrollbarCorner()
        {
            if (_contentScrollViewer == null || EditorScrollbarCorner == null)
                return;

            var bothScrollbarsVisible =
                _contentScrollViewer.ComputedVerticalScrollBarVisibility == Visibility.Visible
                && _contentScrollViewer.ComputedHorizontalScrollBarVisibility == Visibility.Visible;

            EditorScrollbarCorner.Visibility = bothScrollbarsVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void UpdateEditorToolButtonStates()
        {
            var horizontalScrollbarAvailable = ResolveEditorScrollbarSettings().Horizontal;
            SetEditorToolButtonActive(WordWrapButton, _isWordWrapEnabled || !horizontalScrollbarAvailable);
            SetEditorToolButtonActive(FontButton, _isFontPanelOpen);
            SetEditorToolButtonActive(ZoomInButton, _zoomLevel > 1.0001);
            SetEditorToolButtonActive(ZoomOutButton, _zoomLevel < 0.9999);
            SetEditorToolButtonActive(ZoomResetButton, false);
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
            button.SetResourceReference(Control.BorderBrushProperty, "IconButtonBorder");
            button.SetResourceReference(Control.ForegroundProperty, "IconButtonForeground");
            button.BorderThickness = new Thickness(1);
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

        public UIElement? DetachEditorContentForHosting()
        {
            var root = Content as UIElement;
            Content = null;
            return root;
        }

        public void DisposeHostedEditor()
        {
            StopAutoSaveTimer();
            DetachContentScrollViewer();
            Loaded -= NoteEditorWindow_Loaded;
            RootGrid.Loaded -= NoteEditorWindow_Loaded;
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
            return Window.GetWindow(ContentTextBox) ?? this;
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
            DetachContentScrollViewer();
            Loaded -= NoteEditorWindow_Loaded;
            RootGrid.Loaded -= NoteEditorWindow_Loaded;
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
            var doc = ContentTextBox.Document;
            var textRange = new TextRange(doc.ContentStart, doc.ContentEnd);
            textRange.ApplyPropertyValue(TextElement.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
        }

        private void ApplyRichTextBoxTheme()
        {
            try
            {
                // Get the foreground color from the application theme resources
                if (Application.Current.Resources.Contains("RichTextBoxForeground"))
                {
                    var foregroundBrush = (Brush)Application.Current.Resources["RichTextBoxForeground"];
                    var doc = ContentTextBox.Document;

                    // Set default foreground for the entire document
                    doc.Foreground = foregroundBrush;

                    // Apply foreground to entire document text range
                    var textRange = new TextRange(doc.ContentStart, doc.ContentEnd);
                    textRange.ApplyPropertyValue(TextElement.ForegroundProperty, foregroundBrush);
                }
            }
            catch (Exception ex)
            {
                // Log or handle error silently - theme application shouldn't crash the app
                System.Diagnostics.Debug.WriteLine($"Error applying RichTextBox theme: {ex.Message}");
            }
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
            // If selection is empty (deselected), clear highlights
            var sel = ContentTextBox.Selection;
            if (sel == null || sel.IsEmpty)
            {
                ClearAllHighlights();
            }
            else
            {
                // Selection is used directly for online search.
            }

            UpdateOnlineSearchAvailability();
            SyncFontSelectorsFromSelection();
            UpdateListRemoveButtonState();
            UpdateCounter();
        }

        private void ContentTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isSerializingEditorContent)
                return;

            MarkEditorContentChanged();

            // Only update counter, not theme (theme is applied during load and on theme change)
            UpdateCounter();
            UpdateEditedIndicator();
            UpdateListRemoveButtonState();
            ScheduleFloatingImageOverlayLayoutUpdate();
        }

        private void EditorField_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateEditedIndicator();
        }

        private void ContentTextBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var clickedImage = FindVisualAncestor<ResizableImage>(e.OriginalSource as DependencyObject);
            if (clickedImage != null)
            {
                ClearImageSelectionsExcept(clickedImage);
                return;
            }

            ClearImageSelections();
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
            OnlineSearchButton.IsEnabled = !string.IsNullOrWhiteSpace(ResolveOnlineSearchQuery());
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
            if (TryHandleEditorTabKey(e))
                return;

            var isCtrlOnly = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
                && (Keyboard.Modifiers & ModifierKeys.Alt) != ModifierKeys.Alt;

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
            }
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
            ConvertToFlashcardsButton.IsEnabled = isEnabled;
            ConvertToMindMapButton.IsEnabled = isEnabled;
            ConvertToTestButton.IsEnabled = isEnabled;
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
                var preview = new FlashcardsPreviewWindow(
                    flashcards,
                    modelDisplayName: modelDisplayName,
                    title: documentTitle,
                    tags: ParseTags(TagsTextBox.Text),
                    sourceNoteId: _currentDocument?.Id)
                {
                    Owner = GetDialogOwnerWindow()
                };

                if (preview.ShowDialog() == true
                    && Application.Current.MainWindow?.DataContext is MainViewModel mainViewModel)
                {
                    var document = preview.ToDocument();
                    document.SourceNoteId = _currentDocument?.Id;
                    mainViewModel.AddOrUpdateFlashcardSet(document);
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
                var preview = new MindMapPreviewWindow(
                    mindMap,
                    noteOptions: null,
                    modelDisplayName: modelDisplayName,
                    title: documentTitle,
                    tags: ParseTags(TagsTextBox.Text))
                {
                    Owner = GetDialogOwnerWindow()
                };

                if (preview.ShowDialog() == true
                    && Application.Current.MainWindow?.DataContext is MainViewModel mainViewModel)
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

                var preview = new QuizPreviewWindow(quiz, null, modelDisplayName, documentTitle)
                {
                    Owner = GetDialogOwnerWindow()
                };

                if (preview.ShowDialog() == true
                    && Application.Current.MainWindow?.DataContext is MainViewModel mainViewModel)
                {
                    mainViewModel.AddOrUpdateQuiz(preview.ToDocument());
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

                    ContentTextBox.FontFamily = new FontFamily(targetFontFamily);
                    ContentTextBox.FontSize = targetFontSize;

                    document.FontFamily = ContentTextBox.FontFamily.Source;
                    document.FontSize = ContentTextBox.FontSize;

                    SyncFontSelectorsFromSelection();
                    UpdateFontButtonText();

                    // Initialize last saved content
                    MarkCurrentStateSaved();

                    RestoreImagesFromMarkers(document.Images);

                    // Apply theme colors to the loaded content
                    ApplyRichTextBoxTheme();
                    ConfigureResizableImages();
                    ApplyEditorTextLayout();
                    ApplyCurrentLineSpacingToDocument();

                    // Clear any selection and move caret to start
                    ContentTextBox.CaretPosition = ContentTextBox.Document.ContentStart;
                    SyncFontSelectorsFromSelection();
                    MarkCurrentStateSaved();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading document: {ex.Message}");
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
                var previousImages = CloneImageAttachments(document.Images);
                var previousTitle = document.Title ?? string.Empty;
                var previousTags = document.Tags ?? new List<string>();
                var previousFontFamily = document.FontFamily ?? string.Empty;
                var previousFontSize = document.FontSize;

                var newTitle = TitleTextBox.Text;
                var newTags = ParseTags(TagsTextBox.Text);
                var newFontFamily = ContentTextBox.FontFamily.Source;
                var newFontSize = ContentTextBox.FontSize;
                var newImages = BuildImageAttachments();
                ClearAllHighlights();
                var newContent = SerializeEditorContentWithImageMarkers();

                var imagesChanged = !AreImageListsEqual(previousImages, newImages);
                var contentChanged = !string.Equals(previousContent, newContent, StringComparison.Ordinal) || imagesChanged;
                if (contentChanged)
                {
                    AppendEditHistoryVersion(document, previousContent, previousImages);
                }

                var titleChanged = !string.Equals(previousTitle, newTitle, StringComparison.Ordinal);
                var tagsChanged = !AreTagListsEqual(previousTags, newTags);
                var fontChanged = !string.Equals(previousFontFamily, newFontFamily, StringComparison.Ordinal)
                    || !previousFontSize.Equals(newFontSize);

                if (contentChanged || titleChanged || tagsChanged || fontChanged)
                {
                    document.LastModified = DateTime.Now;
                }

                document.Title = newTitle;
                document.Tags = newTags;
                document.Content = newContent;
                document.Images = CloneImageAttachments(newImages);
                document.FontFamily = newFontFamily;
                document.FontSize = newFontSize;
                document.IsEditorFontPanelOpen = _isFontPanelOpen;
                document.IsWordWrapEnabled = _isWordWrapEnabled;
            }
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
            string previousContent,
            IReadOnlyList<NoteImageAttachment>? previousImages)
        {
            document.EditHistory ??= new List<NoteEditHistoryEntry>();
            document.EditHistory.Add(new NoteEditHistoryEntry
            {
                Timestamp = DateTime.UtcNow,
                Content = previousContent,
                Images = CloneImageAttachments(previousImages)
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
                NoteCards.Services.ActivityTracker.RecordTyping(e.Text.Length, 0);
            }
        }

        private void ContentTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (TryHandleEditorTabKey(e))
                return;

            if (e.Key == Key.Space || e.Key == Key.Enter)
            {
                NoteCards.Services.ActivityTracker.RecordTyping(0, 1);
            }
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
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to copy editor selection: {ex.Message}");
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
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to add RTF clipboard data: {ex.Message}");
            }
        }

        private static bool IsTabKey(KeyEventArgs e)
            => e.Key == Key.Tab
                || e.SystemKey == Key.Tab
                || e.ImeProcessedKey == Key.Tab;

        private bool HandleEditorTabKey(bool decreaseIndent)
        {
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
                : new FontFamily(typography.FontFamily);
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

        // Content changed event handler - apply theme and update counter
        private void ContentTextBox_TextChanged_Old(object sender, TextChangedEventArgs e)
        {
            UpdateCounter();
        }

        private void AutoSaveCallback(object? state)
        {
            // Debug: Log timer firing
            System.Diagnostics.Debug.WriteLine($"[AutoSave] Timer fired at {DateTime.Now:HH:mm:ss}");

            // Switch to UI thread
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (_isAutoSaveEnabled && _currentDocument != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[AutoSave] Checking if content changed...");

                    if (HasContentChanged())
                    {
                        System.Diagnostics.Debug.WriteLine($"[AutoSave] Content changed, performing save...");
                        PerformAutoSave();
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[AutoSave] No changes detected since last save");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[AutoSave] Disabled or no document: Enabled={_isAutoSaveEnabled}, Doc={_currentDocument != null}");
                }
            });
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

        private void UpdateEditedIndicator()
        {
            if (_isLoadingDocument || EditedIndicatorText is null)
                return;

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
                ContentTextBox.FontFamily.Source,
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
                    foreach (var rowGroup in table.RowGroups)
                    {
                        foreach (var row in rowGroup.Rows)
                        {
                            foreach (var cell in row.Cells)
                            {
                                foreach (Block child in cell.Blocks)
                                    AppendBlockTextForAi(child, target);
                            }
                        }
                    }
                    target.AppendLine();
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
                // Log error but don't disturb user during auto-save
                System.Diagnostics.Debug.WriteLine($"Auto-save failed: {ex.Message}");
            }
        }

        // Show visual auto-save indicator
        private void ShowAutoSaveIndicator()
        {
            ShowStatusIndicator(LocalizationService.GetString("AutoSaving"));
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
            if (ExportButton.ContextMenu == null)
                return;

            UiMotion.OpenContextMenu(ExportButton.ContextMenu, ExportButton);
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
                    NoteFileService.SaveTextRange(path, GetCurrentEditorTextRange(), DataFormats.Rtf);
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
                    NoteFileService.SaveTextRange(path, GetCurrentEditorTextRange(), DataFormats.XamlPackage);
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
                    foreach (TableCell cell in row.Cells)
                        AppendBlocksPlainTextForFileExport(cell.Blocks, lines);
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

        private NoteDocument CreateExportNoteDocument()
            => new()
            {
                Title = TitleTextBox.Text,
                Content = SerializeEditorContentWithImageMarkers(),
                Images = CloneImageAttachments(BuildImageAttachments()),
                Tags = ParseTags(TagsTextBox.Text),
                FontFamily = ContentTextBox.FontFamily.Source,
                FontSize = ContentTextBox.FontSize,
                IsEditorFontPanelOpen = _isFontPanelOpen,
                IsWordWrapEnabled = _isWordWrapEnabled,
                CreatedAt = _currentDocument?.CreatedAt ?? DateTime.UtcNow,
                LastModified = DateTime.Now
            };

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
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                Margin = new Thickness(0, 0, 0, 30)
            };
            exportDoc.Blocks.Add(titleParagraph);

            // Add separator line
            var separator = new Paragraph(new Run("────────────────────────────────────────────────"))
            {
                FontSize = 10,
                Foreground = Brushes.LightGray,
                Margin = new Thickness(0, 0, 0, 20)
            };
            exportDoc.Blocks.Add(separator);

            var exportNote = CreateExportNoteDocument();
            var contentClone = CreatePdfContentDocument(exportNote, exportDoc.ColumnWidth);
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
                    FontSize = 12,
                    Foreground = Brushes.Gray,
                    FontStyle = FontStyles.Italic
                });
            }

            var printableDocument = CreatePdfFixedDocument(exportDoc, exportNote.Images);
            var printDialog = new PrintDialog();

            var printQueue = new System.Printing.PrintQueue(
                new System.Printing.PrintServer(),
                "Microsoft Print to PDF");

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

        private FlowDocument? CreatePdfContentDocument(NoteDocument exportNote, double maximumImageWidth)
        {
            var document = LoadFlowDocumentFromStoredContent(exportNote.Content);
            if (document == null)
                return null;

            InsertPdfImages(document, exportNote.Images, maximumImageWidth, includeFloatingImages: false);
            return document;
        }

        private FixedDocument CreatePdfFixedDocument(
            FlowDocument sourceDocument,
            IReadOnlyList<NoteImageAttachment>? images)
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

            var pageCount = Math.Max(
                Math.Max(1, paginator.PageCount),
                ResolveRequiredPdfPageCountForFloatingImages(images));
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

                AddFloatingImagesToPdfPage(fixedPage, images, pageIndex);

                fixedPage.Measure(new Size(PdfPageWidth, PdfPageHeight));
                fixedPage.Arrange(new Rect(0, 0, PdfPageWidth, PdfPageHeight));
                fixedPage.UpdateLayout();

                var pageContent = new PageContent();
                ((IAddChild)pageContent).AddChild(fixedPage);
                fixedDocument.Pages.Add(pageContent);
            }

            return fixedDocument;
        }

        private static int ResolveRequiredPdfPageCountForFloatingImages(IReadOnlyList<NoteImageAttachment>? images)
        {
            if (images == null || images.Count == 0)
                return 1;

            var firstPageContentHeight = PdfPageHeight - (PdfPagePadding * 2) - PdfContentTopOffset;
            var laterPageContentHeight = PdfPageHeight - (PdfPagePadding * 2);
            var maxPage = 0;
            foreach (var image in images.Where(image =>
                         string.Equals(image.Layout, NoteImageLayout.Floating, StringComparison.OrdinalIgnoreCase)))
            {
                var top = double.IsNaN(image.Top) ? 0 : Math.Max(0, image.Top);
                maxPage = Math.Max(maxPage, ResolvePdfFloatingPlacement(
                    top,
                    firstPageContentHeight,
                    laterPageContentHeight).PageIndex);
            }

            return maxPage + 1;
        }

        private static void AddFloatingImagesToPdfPage(
            FixedPage fixedPage,
            IReadOnlyList<NoteImageAttachment>? images,
            int pageIndex)
        {
            if (images == null || images.Count == 0)
                return;

            var contentWidth = PdfPageWidth - (PdfPagePadding * 2);
            var firstPageContentHeight = PdfPageHeight - (PdfPagePadding * 2) - PdfContentTopOffset;
            var laterPageContentHeight = PdfPageHeight - (PdfPagePadding * 2);
            foreach (var attachment in images.Where(image =>
                         string.Equals(image.Layout, NoteImageLayout.Floating, StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace(image.Data)))
            {
                var imageTop = double.IsNaN(attachment.Top) ? 0 : Math.Max(0, attachment.Top);
                var placement = ResolvePdfFloatingPlacement(imageTop, firstPageContentHeight, laterPageContentHeight);
                var targetPage = placement.PageIndex;
                if (targetPage != pageIndex)
                    continue;

                var imageElement = CreatePdfImageElement(attachment, contentWidth);
                if (imageElement == null)
                    continue;

                var imageLeft = double.IsNaN(attachment.Left) ? 0 : Math.Max(0, attachment.Left);
                FixedPage.SetLeft(imageElement, PdfPagePadding + Math.Min(imageLeft, contentWidth - 1));
                FixedPage.SetTop(imageElement, PdfPagePadding + placement.TopOffset);
                Panel.SetZIndex(imageElement, 1000);
                fixedPage.Children.Add(imageElement);
            }
        }

        private static (int PageIndex, double TopOffset) ResolvePdfFloatingPlacement(
            double documentTop,
            double firstPageContentHeight,
            double laterPageContentHeight)
        {
            firstPageContentHeight = Math.Max(1, firstPageContentHeight);
            laterPageContentHeight = Math.Max(1, laterPageContentHeight);

            if (documentTop < firstPageContentHeight)
                return (0, PdfContentTopOffset + documentTop);

            var remainingTop = documentTop - firstPageContentHeight;
            var laterPageOffset = remainingTop % laterPageContentHeight;
            var pageIndex = 1 + (int)Math.Floor(remainingTop / laterPageContentHeight);
            return (pageIndex, laterPageOffset);
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
                Debug.WriteLine($"Failed to load PDF export content as rich text: {ex.Message}");
            }

            range.Text = content;
            return document;
        }

        private static void InsertPdfImages(
            FlowDocument document,
            IReadOnlyList<NoteImageAttachment>? images,
            double maximumImageWidth,
            bool includeFloatingImages)
        {
            if (images == null || images.Count == 0)
                return;

            var inserted = new HashSet<Guid>();
            foreach (var image in images.Where(image => image.Id != Guid.Empty && !string.IsNullOrWhiteSpace(image.Data)))
            {
                var marker = FindTextRange(document, CreateImageMarker(image.Id));
                if (marker == null)
                    continue;

                if (string.Equals(image.Layout, NoteImageLayout.Inline, StringComparison.OrdinalIgnoreCase))
                {
                    InsertPdfInlineImage(marker, image, maximumImageWidth);
                }
                else if (includeFloatingImages)
                {
                    InsertPdfBlockImage(marker, image, maximumImageWidth);
                }
                else
                {
                    marker.Text = string.Empty;
                }

                inserted.Add(image.Id);
            }

            if (!includeFloatingImages)
                return;

            foreach (var image in images
                         .Where(image => image.Id != Guid.Empty
                                         && !inserted.Contains(image.Id)
                                         && !string.IsNullOrWhiteSpace(image.Data))
                         .OrderBy(image => double.IsNaN(image.Top) ? 0 : image.Top)
                         .ThenBy(image => double.IsNaN(image.Left) ? 0 : image.Left))
            {
                var imageElement = CreatePdfImageElement(image, maximumImageWidth);
                if (imageElement == null)
                    continue;

                document.Blocks.Add(new BlockUIContainer(imageElement)
                {
                    Margin = new Thickness(
                        Math.Max(0, Math.Min(double.IsNaN(image.Left) ? 0 : image.Left, 96)),
                        8,
                        0,
                        8)
                });
            }
        }

        private static void InsertPdfInlineImage(
            TextRange markerRange,
            NoteImageAttachment attachment,
            double maximumImageWidth)
        {
            var imageElement = CreatePdfImageElement(attachment, maximumImageWidth);
            if (imageElement == null)
            {
                markerRange.Text = string.Empty;
                return;
            }

            var insertionPosition = markerRange.Start;
            markerRange.Text = string.Empty;
            insertionPosition = insertionPosition.GetInsertionPosition(LogicalDirection.Forward) ?? insertionPosition;
            new InlineUIContainer(imageElement, insertionPosition);
        }

        private static void InsertPdfBlockImage(
            TextRange markerRange,
            NoteImageAttachment attachment,
            double maximumImageWidth)
        {
            var imageElement = CreatePdfImageElement(attachment, maximumImageWidth);
            if (imageElement == null)
            {
                markerRange.Text = string.Empty;
                return;
            }

            var markerParagraph = markerRange.Start.Paragraph;
            var parentBlocks = markerParagraph == null ? null : GetParentBlockCollection(markerParagraph);
            if (markerParagraph != null
                && parentBlocks != null
                && IsParagraphMarkerOnly(markerParagraph, CreateImageMarker(attachment.Id)))
            {
                var imageBlock = new BlockUIContainer(imageElement)
                {
                    Margin = new Thickness(
                        Math.Max(0, Math.Min(double.IsNaN(attachment.Left) ? markerParagraph.Margin.Left : attachment.Left, 96)),
                        markerParagraph.Margin.Top,
                        markerParagraph.Margin.Right,
                        Math.Max(markerParagraph.Margin.Bottom, 8))
                };

                parentBlocks.InsertBefore(markerParagraph, imageBlock);
                parentBlocks.Remove(markerParagraph);
                return;
            }

            var insertionPosition = markerRange.Start;
            markerRange.Text = string.Empty;
            insertionPosition = insertionPosition.GetInsertionPosition(LogicalDirection.Forward) ?? insertionPosition;
            new InlineUIContainer(imageElement, insertionPosition);
        }

        private static Image? CreatePdfImageElement(NoteImageAttachment attachment, double maximumImageWidth)
        {
            var source = TryCreateImageSourceFromBase64(attachment.Data);
            if (source == null)
                return null;

            var size = ResolvePdfImageSize(attachment, source, maximumImageWidth);
            return new Image
            {
                Source = source,
                Width = size.Width,
                Height = size.Height,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
                Margin = new Thickness(0, 4, 0, 4)
            };
        }

        private static BitmapImage? TryCreateImageSourceFromBase64(string imageData)
        {
            try
            {
                var bytes = Convert.FromBase64String(imageData);
                using var stream = new MemoryStream(bytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to decode PDF export image: {ex.Message}");
                return null;
            }
        }

        private static Size ResolvePdfImageSize(
            NoteImageAttachment attachment,
            BitmapSource source,
            double maximumImageWidth)
        {
            var width = attachment.Width > 0 ? attachment.Width : source.Width;
            var height = attachment.Height > 0 ? attachment.Height : source.Height;
            if (width <= 0)
                width = Math.Max(1, source.PixelWidth);
            if (height <= 0)
                height = Math.Max(1, source.PixelHeight);

            var maxWidth = maximumImageWidth > 0 ? maximumImageWidth : 680;
            if (width > maxWidth)
            {
                var scale = maxWidth / width;
                width = maxWidth;
                height *= scale;
            }

            const double maxHeight = 880;
            if (height > maxHeight)
            {
                var scale = maxHeight / height;
                height = maxHeight;
                width *= scale;
            }

            return new Size(Math.Max(1, width), Math.Max(1, height));
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

            ApplyContentToEditor(dialog.SelectedVersion.Content, dialog.SelectedVersion.Images);
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

            ShowStatusIndicator(LocalizationService.GetString("Saved"));
            return true;
        }

        private void ApplyContentToEditor(string? content, IReadOnlyList<NoteImageAttachment>? images = null)
        {
            TextRange tr = new TextRange(ContentTextBox.Document.ContentStart, ContentTextBox.Document.ContentEnd);
            FloatingImageOverlay.Children.Clear();

            if (string.IsNullOrEmpty(content))
            {
                tr.Text = string.Empty;
                RestoreImagesFromMarkers(images);
                ConfigureResizableImages();
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

            RestoreImagesFromMarkers(images);
            ConfigureResizableImages();
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
            ContentTextBox.Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, new FontFamily(fontName));

            SavePreferredFontFamily(fontName);
            SyncFontSelectorsFromSelection();

            if (selectionChangesDocument)
            {
                MarkEditorContentChanged();
                UpdateEditedIndicator();
            }

            ContentTextBox.Focus();
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

            size = NormalizeEditorFontSize(size);
            var selectionChangesDocument = ContentTextBox.Selection is { IsEmpty: false };
            ContentTextBox.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, size);

            ApplyLineSpacingToInlineFormattingTarget(size);

            SavePreferredFontSize(size);
            SyncFontSelectorsFromSelection();

            if (selectionChangesDocument)
            {
                MarkEditorContentChanged();
                UpdateEditedIndicator();
            }

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
            ApplyLineSpacingToTargetParagraphs(_lineSpacingMultiplier);
            SavePreferredLineSpacing(_lineSpacingMultiplier);
            MarkEditorContentChanged();
            UpdateEditedIndicator();
            ContentTextBox.Focus();
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
            ContentTextBox.Focus();
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
        {
            var selection = ContentTextBox.Selection;
            var currentValue = selection.GetPropertyValue(Inline.TextDecorationsProperty);
            var isUnderlined = currentValue != DependencyProperty.UnsetValue
                && currentValue is TextDecorationCollection decorations
                && decorations == TextDecorations.Underline;

            selection.ApplyPropertyValue(
                Inline.TextDecorationsProperty,
                isUnderlined ? DependencyProperty.UnsetValue : TextDecorations.Underline);

            MarkEditorContentChanged();
            UpdateEditedIndicator();
            ContentTextBox.Focus();
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

            MarkEditorContentChanged();
            UpdateEditedIndicator();
            UpdateListRemoveButtonState();
            ContentTextBox.Focus();
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
            fontButton.ToolTip = string.Format(LocalizationService.GetString("FontButtonFormat"), fontFamily, fontSize);
        }

        private void SyncFontSelectorsFromSelection()
        {
            var typography = ResolveCurrentSelectionTypography();
            _isSyncingFontSelectors = true;

            try
            {
                if (typography.HasMixedFontFamily)
                    FontFamilyBox.SelectedIndex = -1;
                else
                    SelectOrAddComboBoxItemByContent(FontFamilyBox, typography.FontFamily, null, StringComparison.OrdinalIgnoreCase);

                if (typography.HasMixedFontSize)
                    FontSizeBox.SelectedIndex = -1;
                else
                    SelectOrAddComboBoxItemByContent(FontSizeBox, FormatFontSize(typography.FontSize), typography.FontSize, StringComparison.Ordinal);
            }
            finally
            {
                _isSyncingFontSelectors = false;
            }

            UpdateFontButtonText(typography);
        }

        private EditorTypographyState ResolveCurrentSelectionTypography()
        {
            var fallbackFamily = string.IsNullOrWhiteSpace(ContentTextBox.FontFamily?.Source)
                ? DefaultEditorFontFamily
                : ContentTextBox.FontFamily.Source;
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
                    fontFamily = family.Source;
                    return true;
                case string familyName when !string.IsNullOrWhiteSpace(familyName):
                    fontFamily = familyName.Trim();
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

        private void SelectOrAddComboBoxItemByContent(
            ComboBox comboBox,
            string content,
            object? tag,
            StringComparison comparison)
        {
            if (TrySelectComboBoxItemByContent(comboBox, content, comparison))
                return;

            var item = CreateEditorComboBoxItem(content, tag);
            comboBox.Items.Add(item);
            comboBox.SelectedItem = item;
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
            foreach (var option in comboBox.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(option.Content?.ToString(), content, comparison))
                {
                    comboBox.SelectedItem = option;
                    return true;
                }
            }

            return false;
        }

        private static bool TrySelectComboBoxItemByTag(ComboBox comboBox, double tagValue)
        {
            foreach (var option in comboBox.Items.OfType<ComboBoxItem>())
            {
                if (TryGetFontSize(option.Tag, out var optionValue)
                    && Math.Abs(optionValue - tagValue) < 0.1)
                {
                    comboBox.SelectedItem = option;
                    return true;
                }
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
            ContentTextBox.FontFamily = new FontFamily(string.IsNullOrWhiteSpace(document.FontFamily) ? DefaultEditorFontFamily : document.FontFamily);
            ContentTextBox.FontSize = document.FontSize > 0 ? document.FontSize : 14;
            _isWordWrapEnabled = document.IsWordWrapEnabled ?? _isWordWrapEnabled;
            SetFontPanelOpen(document.IsEditorFontPanelOpen ?? _isFontPanelOpen, animate: false);
            ApplyContentToEditor(document.Content, document.Images);
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
                    FloatingImageOverlay.Children.Clear();
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

        private void InsertImageButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog();
            dlg.Filter = LocalizationService.GetString("ImageOpenFileDialogFilter");
            dlg.Title = LocalizationService.GetString("SelectImageToInsert");

            if (dlg.ShowDialog() != true)
                return;

            try
            {
                InsertImageFromFile(dlg.FileName);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("FailedToInsertImage")}\n\n{ex.Message}",
                    LocalizationService.GetString("ImageInsertError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ConfigureResizableImages()
        {
            ConfigureResizableImages(ContentTextBox.Document.Blocks);
            ConfigureFloatingImageOverlay();
        }

        private void ConfigureResizableImages(BlockCollection blocks)
        {
            foreach (Block block in blocks)
            {
                switch (block)
                {
                    case BlockUIContainer { Child: Canvas canvas }:
                        ConfigureImageCanvas(canvas);
                        break;
                    case BlockUIContainer { Child: ResizableImage image }:
                        ConfigureImageControl(image);
                        break;
                    case Paragraph paragraph:
                        ConfigureResizableImages(paragraph.Inlines);
                        break;
                    case Section section:
                        ConfigureResizableImages(section.Blocks);
                        break;
                    case System.Windows.Documents.List list:
                        foreach (ListItem item in list.ListItems)
                        {
                            ConfigureResizableImages(item.Blocks);
                        }
                        break;
                    case Table table:
                        foreach (TableRowGroup rowGroup in table.RowGroups)
                        {
                            foreach (TableRow row in rowGroup.Rows)
                            {
                                foreach (TableCell cell in row.Cells)
                                {
                                    ConfigureResizableImages(cell.Blocks);
                                }
                            }
                        }
                        break;
                }
            }
        }

        private void ConfigureResizableImages(InlineCollection inlines)
        {
            foreach (Inline inline in inlines)
            {
                switch (inline)
                {
                    case InlineUIContainer { Child: ResizableImage image }:
                        ConfigureImageControl(image);
                        break;
                    case Span span:
                        ConfigureResizableImages(span.Inlines);
                        break;
                    case AnchoredBlock anchoredBlock:
                        ConfigureResizableImages(anchoredBlock.Blocks);
                        break;
                }
            }
        }

        private void ConfigureImageCanvas(Canvas canvas)
        {
            canvas.Background ??= Brushes.Transparent;
            canvas.ClipToBounds = false;
            canvas.PreviewMouseDown -= ImageCanvas_PreviewMouseDown;
            canvas.PreviewMouseDown += ImageCanvas_PreviewMouseDown;

            foreach (ResizableImage image in canvas.Children.OfType<ResizableImage>())
            {
                Panel.SetZIndex(image, 1001);
                image.LayoutMode = NoteImageLayout.Floating;
                ConfigureImageControl(image);
            }

            EnsureImageCanvasSize(canvas);
        }

        private void ConfigureImageControl(ResizableImage image)
        {
            if (image.ImageId == Guid.Empty)
                image.ImageId = Guid.NewGuid();

            image.EditorHost = ContentTextBox;
            image.RefreshVisualState();
            image.PreviewMouseLeftButtonDown -= ResizableImage_PreviewMouseLeftButtonDown;
            image.PreviewMouseLeftButtonDown += ResizableImage_PreviewMouseLeftButtonDown;
            image.ImageBoundsChanged -= ResizableImage_ImageBoundsChanged;
            image.ImageBoundsChanged += ResizableImage_ImageBoundsChanged;
            image.LayoutChangeRequested -= ResizableImage_LayoutChangeRequested;
            image.LayoutChangeRequested += ResizableImage_LayoutChangeRequested;
            image.InlineMoveRequested -= ResizableImage_InlineMoveRequested;
            image.InlineMoveRequested += ResizableImage_InlineMoveRequested;
        }

        private void ConfigureFloatingImageOverlay()
        {
            foreach (ResizableImage image in FloatingImageOverlay.Children.OfType<ResizableImage>())
            {
                Panel.SetZIndex(image, 1001);
                ConfigureImageControl(image);
            }

            UpdateFloatingImageOverlayLayout();
        }

        private void ResizableImage_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is ResizableImage image)
                ClearImageSelectionsExcept(image);
        }

        private void ClearImageSelections()
        {
            foreach (ResizableImage image in FloatingImageOverlay.Children.OfType<ResizableImage>())
                image.IsSelected = false;

            ClearImageSelections(ContentTextBox.Document.Blocks);
        }

        private void ClearImageSelectionsExcept(ResizableImage selectedImage)
        {
            foreach (ResizableImage image in FloatingImageOverlay.Children.OfType<ResizableImage>())
            {
                if (!ReferenceEquals(image, selectedImage))
                    image.IsSelected = false;
            }

            ClearImageSelectionsExcept(ContentTextBox.Document.Blocks, selectedImage);
        }

        private static void ClearImageSelections(BlockCollection blocks)
        {
            foreach (Block block in blocks)
            {
                switch (block)
                {
                    case BlockUIContainer { Child: Canvas canvas }:
                        foreach (ResizableImage image in canvas.Children.OfType<ResizableImage>())
                            image.IsSelected = false;
                        break;
                    case BlockUIContainer { Child: ResizableImage image }:
                        image.IsSelected = false;
                        break;
                    case Paragraph paragraph:
                        ClearImageSelections(paragraph.Inlines);
                        break;
                    case Section section:
                        ClearImageSelections(section.Blocks);
                        break;
                    case System.Windows.Documents.List list:
                        foreach (ListItem item in list.ListItems)
                            ClearImageSelections(item.Blocks);
                        break;
                    case Table table:
                        foreach (TableRowGroup rowGroup in table.RowGroups)
                        {
                            foreach (TableRow row in rowGroup.Rows)
                            {
                                foreach (TableCell cell in row.Cells)
                                    ClearImageSelections(cell.Blocks);
                            }
                        }
                        break;
                }
            }
        }

        private static void ClearImageSelectionsExcept(BlockCollection blocks, ResizableImage selectedImage)
        {
            foreach (Block block in blocks)
            {
                switch (block)
                {
                    case BlockUIContainer { Child: Canvas canvas }:
                        foreach (ResizableImage image in canvas.Children.OfType<ResizableImage>())
                        {
                            if (!ReferenceEquals(image, selectedImage))
                                image.IsSelected = false;
                        }
                        break;
                    case BlockUIContainer { Child: ResizableImage image }:
                        if (!ReferenceEquals(image, selectedImage))
                            image.IsSelected = false;
                        break;
                    case Paragraph paragraph:
                        ClearImageSelectionsExcept(paragraph.Inlines, selectedImage);
                        break;
                    case Section section:
                        ClearImageSelectionsExcept(section.Blocks, selectedImage);
                        break;
                    case System.Windows.Documents.List list:
                        foreach (ListItem item in list.ListItems)
                            ClearImageSelectionsExcept(item.Blocks, selectedImage);
                        break;
                    case Table table:
                        foreach (TableRowGroup rowGroup in table.RowGroups)
                        {
                            foreach (TableRow row in rowGroup.Rows)
                            {
                                foreach (TableCell cell in row.Cells)
                                    ClearImageSelectionsExcept(cell.Blocks, selectedImage);
                            }
                        }
                        break;
                }
            }
        }

        private static void ClearImageSelections(InlineCollection inlines)
        {
            foreach (Inline inline in inlines)
            {
                switch (inline)
                {
                    case InlineUIContainer { Child: ResizableImage image }:
                        image.IsSelected = false;
                        break;
                    case Span span:
                        ClearImageSelections(span.Inlines);
                        break;
                    case AnchoredBlock anchoredBlock:
                        ClearImageSelections(anchoredBlock.Blocks);
                        break;
                }
            }
        }

        private static void ClearImageSelectionsExcept(InlineCollection inlines, ResizableImage selectedImage)
        {
            foreach (Inline inline in inlines)
            {
                switch (inline)
                {
                    case InlineUIContainer { Child: ResizableImage image }:
                        if (!ReferenceEquals(image, selectedImage))
                            image.IsSelected = false;
                        break;
                    case Span span:
                        ClearImageSelectionsExcept(span.Inlines, selectedImage);
                        break;
                    case AnchoredBlock anchoredBlock:
                        ClearImageSelectionsExcept(anchoredBlock.Blocks, selectedImage);
                        break;
                }
            }
        }

        private static T? FindVisualAncestor<T>(DependencyObject? source)
            where T : DependencyObject
        {
            while (source != null)
            {
                if (source is T match)
                    return match;

                DependencyObject? parent = null;
                if (source is Visual or System.Windows.Media.Media3D.Visual3D)
                    parent = VisualTreeHelper.GetParent(source);

                parent ??= source switch
                {
                    FrameworkElement element => element.Parent,
                    FrameworkContentElement contentElement => contentElement.Parent,
                    _ => null
                };

                source = parent;
            }

            return null;
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

        private static double GetFloatingDocumentLeft(DependencyObject image)
        {
            return (double)image.GetValue(FloatingDocumentLeftProperty);
        }

        private static void SetFloatingDocumentLeft(DependencyObject image, double value)
        {
            image.SetValue(FloatingDocumentLeftProperty, value);
        }

        private static double GetFloatingDocumentTop(DependencyObject image)
        {
            return (double)image.GetValue(FloatingDocumentTopProperty);
        }

        private static void SetFloatingDocumentTop(DependencyObject image, double value)
        {
            image.SetValue(FloatingDocumentTopProperty, value);
        }

        private static Guid GetInlineImageId(DependencyObject element)
        {
            return (Guid)element.GetValue(InlineImageIdProperty);
        }

        private static void SetInlineImageId(DependencyObject element, Guid value)
        {
            element.SetValue(InlineImageIdProperty, value);
        }

        private bool IsFloatingOverlayImage(ResizableImage image)
        {
            return ReferenceEquals(image.Parent, FloatingImageOverlay);
        }

        private bool IsInlineOverlayImage(ResizableImage image)
        {
            return IsFloatingOverlayImage(image)
                && string.Equals(image.LayoutMode, NoteImageLayout.Inline, StringComparison.OrdinalIgnoreCase);
        }

        private void AddFloatingImageToOverlay(ResizableImage image, double documentLeft, double documentTop)
        {
            if (image.Parent is Canvas currentCanvas)
                currentCanvas.Children.Remove(image);

            image.LayoutMode = NoteImageLayout.Floating;
            SetFloatingDocumentLeft(image, Math.Max(0, documentLeft));
            SetFloatingDocumentTop(image, Math.Max(0, documentTop));
            Panel.SetZIndex(image, 1001);
            FloatingImageOverlay.Children.Add(image);
            ConfigureImageControl(image);
            UpdateFloatingImageCanvasPosition(image);
        }

        private void AddInlineImageToOverlay(ResizableImage image)
        {
            if (image.Parent is Canvas currentCanvas)
                currentCanvas.Children.Remove(image);

            image.LayoutMode = NoteImageLayout.Inline;
            Panel.SetZIndex(image, 1001);
            FloatingImageOverlay.Children.Add(image);
            ConfigureImageControl(image);
            UpdateInlineImagePlaceholderSize(image);
            UpdateInlineImageCanvasPosition(image);
            ScheduleFloatingImageOverlayLayoutUpdate();
        }

        private Point ResolveFloatingInsertionPosition()
        {
            var documentLeft = 12d;
            var documentTop = ContentTextBox.VerticalOffset + 12d;

            try
            {
                var caretRect = ContentTextBox.CaretPosition.GetCharacterRect(LogicalDirection.Forward);
                if (!caretRect.IsEmpty && caretRect.Top >= 0)
                {
                    documentLeft = Math.Max(0, caretRect.Left + ContentTextBox.HorizontalOffset - ContentTextBox.Padding.Left);
                    documentTop = Math.Max(0, caretRect.Bottom + ContentTextBox.VerticalOffset - ContentTextBox.Padding.Top + 8);
                }
            }
            catch
            {
                // The caret can be outside the realized viewport; the visible top fallback is good enough.
            }

            return new Point(documentLeft, documentTop);
        }

        private Point ResolveFloatingDocumentPosition(ResizableImage image)
        {
            try
            {
                var visualPosition = image.TransformToAncestor(ContentTextBox).Transform(new Point(0, 0));
                return new Point(
                    Math.Max(0, visualPosition.X + ContentTextBox.HorizontalOffset - ContentTextBox.Padding.Left),
                    Math.Max(0, visualPosition.Y + ContentTextBox.VerticalOffset - ContentTextBox.Padding.Top));
            }
            catch
            {
                return ResolveFloatingInsertionPosition();
            }
        }

        private void UpdateFloatingImageDocumentPositionFromCanvas(ResizableImage image)
        {
            if (!IsFloatingOverlayImage(image) || _isUpdatingFloatingImageLayout)
                return;

            var canvasLeft = Canvas.GetLeft(image);
            if (double.IsNaN(canvasLeft))
                canvasLeft = 0;

            var canvasTop = Canvas.GetTop(image);
            if (double.IsNaN(canvasTop))
                canvasTop = 0;

            SetFloatingDocumentLeft(image, Math.Max(0, canvasLeft + ContentTextBox.HorizontalOffset - ContentTextBox.Padding.Left));
            SetFloatingDocumentTop(image, Math.Max(0, canvasTop + ContentTextBox.VerticalOffset - ContentTextBox.Padding.Top));
        }

        private void UpdateFloatingImageCanvasPosition(ResizableImage image)
        {
            var canvasLeft = GetFloatingDocumentLeft(image) - ContentTextBox.HorizontalOffset + ContentTextBox.Padding.Left;
            var canvasTop = GetFloatingDocumentTop(image) - ContentTextBox.VerticalOffset + ContentTextBox.Padding.Top;
            Canvas.SetLeft(image, canvasLeft);
            Canvas.SetTop(image, canvasTop);
        }

        private void UpdateInlineImageCanvasPosition(ResizableImage image, bool queueRetry = true)
        {
            if (!IsInlineOverlayImage(image))
                return;

            if (TryResolveInlineImageOverlayPosition(image.ImageId, out var position))
            {
                image.Visibility = Visibility.Visible;
                Canvas.SetLeft(image, position.X);
                Canvas.SetTop(image, position.Y);
                return;
            }

            if (!double.IsNaN(Canvas.GetLeft(image)) && !double.IsNaN(Canvas.GetTop(image)))
                image.Visibility = Visibility.Visible;

            if (queueRetry)
                ScheduleFloatingImageOverlayLayoutUpdate();
        }

        private bool TryResolveInlineImageOverlayPosition(Guid imageId, out Point position)
        {
            position = new Point();
            var placeholder = FindInlineImagePlaceholder(imageId);
            if (placeholder == null)
                return false;

            try
            {
                if (!placeholder.IsMeasureValid || !placeholder.IsArrangeValid)
                    ContentTextBox.UpdateLayout();

                position = placeholder.TransformToAncestor(EditorSurface).Transform(new Point(0, 0));
                return IsFinitePoint(position);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to position inline image overlay: {ex.Message}");
            }

            var container = FindInlineImagePlaceholderContainer(imageId);
            return container != null && TryResolveInlineImageTextPosition(container, out position);
        }

        private bool TryResolveInlineImageTextPosition(InlineUIContainer container, out Point position)
        {
            position = new Point();

            try
            {
                var rect = container.ElementStart.GetCharacterRect(LogicalDirection.Forward);
                if (rect.IsEmpty)
                    rect = container.ElementEnd.GetCharacterRect(LogicalDirection.Backward);

                if (rect.IsEmpty)
                    return false;

                position = ContentTextBox.TranslatePoint(new Point(rect.Left, rect.Top), EditorSurface);
                return IsFinitePoint(position);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to resolve inline image text position: {ex.Message}");
                return false;
            }
        }

        private static bool IsFinitePoint(Point point)
        {
            return !double.IsNaN(point.X)
                && !double.IsInfinity(point.X)
                && !double.IsNaN(point.Y)
                && !double.IsInfinity(point.Y);
        }

        private void ScheduleFloatingImageOverlayLayoutUpdate()
        {
            if (_isFloatingImageOverlayLayoutUpdateQueued)
                return;

            _isFloatingImageOverlayLayoutUpdateQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _isFloatingImageOverlayLayoutUpdateQueued = false;
                UpdateFloatingImageOverlayLayout();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void UpdateFloatingImageOverlayLayout()
        {
            if (_isUpdatingFloatingImageLayout)
                return;

            try
            {
                _isUpdatingFloatingImageLayout = true;
                foreach (ResizableImage image in FloatingImageOverlay.Children.OfType<ResizableImage>())
                {
                    if (string.Equals(image.LayoutMode, NoteImageLayout.Inline, StringComparison.OrdinalIgnoreCase))
                        UpdateInlineImageCanvasPosition(image, queueRetry: false);
                    else
                        UpdateFloatingImageCanvasPosition(image);
                }
            }
            finally
            {
                _isUpdatingFloatingImageLayout = false;
            }
        }

        private Border CreateInlineImagePlaceholder(ResizableImage image)
        {
            if (image.ImageId == Guid.Empty)
                image.ImageId = Guid.NewGuid();

            var placeholder = new Border
            {
                Width = ResolveElementWidth(image),
                Height = ResolveElementHeight(image),
                Background = Brushes.Transparent,
                IsHitTestVisible = false,
                Focusable = false,
                SnapsToDevicePixels = true
            };

            SetInlineImageId(placeholder, image.ImageId);
            return placeholder;
        }

        private void UpdateInlineImagePlaceholderSize(ResizableImage image)
        {
            var placeholder = FindInlineImagePlaceholder(image.ImageId);
            if (placeholder == null)
                return;

            placeholder.Width = ResolveElementWidth(image);
            placeholder.Height = ResolveElementHeight(image);
        }

        private bool TryInsertInlineImagePlaceholder(
            TextPointer insertionPosition,
            ResizableImage image,
            out InlineUIContainer container)
        {
            container = new InlineUIContainer();

            try
            {
                var normalizedPosition = insertionPosition.GetInsertionPosition(LogicalDirection.Forward)
                    ?? insertionPosition;
                container = new InlineUIContainer(CreateInlineImagePlaceholder(image), normalizedPosition);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to insert inline image placeholder: {ex.Message}");
                return false;
            }
        }

        private bool TryInsertInlineImagePlaceholderAtPoint(
            Point editorPoint,
            ResizableImage image,
            out InlineUIContainer container)
        {
            var insertionPosition = ResolveInlineInsertionPosition(editorPoint)
                ?? ContentTextBox.CaretPosition;

            if (TryInsertInlineImagePlaceholder(insertionPosition, image, out container))
                return true;

            return TryInsertInlineImagePlaceholderAtDocumentEnd(image, out container);
        }

        private bool TryInsertInlineImagePlaceholderAtDocumentEnd(
            ResizableImage image,
            out InlineUIContainer container)
        {
            container = new InlineUIContainer();

            try
            {
                var paragraph = ContentTextBox.Document.Blocks.LastBlock as Paragraph;
                if (paragraph == null)
                {
                    paragraph = new Paragraph();
                    ContentTextBox.Document.Blocks.Add(paragraph);
                }

                container = new InlineUIContainer(CreateInlineImagePlaceholder(image));
                paragraph.Inlines.Add(container);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to insert fallback inline image placeholder: {ex.Message}");
                return false;
            }
        }

        private TextPointer? ResolveInlineInsertionPosition(Point editorPoint)
        {
            var insertionPosition = ContentTextBox.GetPositionFromPoint(editorPoint, snapToText: true);
            if (insertionPosition != null)
                return insertionPosition;

            if (ContentTextBox.ActualWidth > 0 && ContentTextBox.ActualHeight > 0)
            {
                var minimumX = Math.Max(0, ContentTextBox.Padding.Left + 1);
                var maximumX = Math.Max(minimumX, ContentTextBox.ActualWidth - ContentTextBox.Padding.Right - 1);
                var minimumY = Math.Max(0, ContentTextBox.Padding.Top + 1);
                var maximumY = Math.Max(minimumY, ContentTextBox.ActualHeight - ContentTextBox.Padding.Bottom - 1);
                var clampedPoint = new Point(
                    Math.Min(Math.Max(editorPoint.X, minimumX), maximumX),
                    Math.Min(Math.Max(editorPoint.Y, minimumY), maximumY));

                insertionPosition = ContentTextBox.GetPositionFromPoint(clampedPoint, snapToText: true);
                if (insertionPosition != null)
                    return insertionPosition;
            }

            return ContentTextBox.CaretPosition?.GetInsertionPosition(LogicalDirection.Forward)
                ?? ContentTextBox.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
        }

        private FrameworkElement? FindInlineImagePlaceholder(Guid imageId)
        {
            return FindInlineImagePlaceholder(ContentTextBox.Document.Blocks, imageId);
        }

        private InlineUIContainer? FindInlineImagePlaceholderContainer(Guid imageId)
        {
            return FindInlineImagePlaceholderContainer(ContentTextBox.Document.Blocks, imageId);
        }

        private static FrameworkElement? FindInlineImagePlaceholder(BlockCollection blocks, Guid imageId)
        {
            foreach (Block block in blocks)
            {
                switch (block)
                {
                    case Paragraph paragraph:
                    {
                        var match = FindInlineImagePlaceholder(paragraph.Inlines, imageId);
                        if (match != null)
                            return match;
                        break;
                    }
                    case Section section:
                    {
                        var match = FindInlineImagePlaceholder(section.Blocks, imageId);
                        if (match != null)
                            return match;
                        break;
                    }
                    case System.Windows.Documents.List list:
                        foreach (ListItem item in list.ListItems)
                        {
                            var match = FindInlineImagePlaceholder(item.Blocks, imageId);
                            if (match != null)
                                return match;
                        }
                        break;
                    case Table table:
                        foreach (TableRowGroup rowGroup in table.RowGroups)
                        {
                            foreach (TableRow row in rowGroup.Rows)
                            {
                                foreach (TableCell cell in row.Cells)
                                {
                                    var match = FindInlineImagePlaceholder(cell.Blocks, imageId);
                                    if (match != null)
                                        return match;
                                }
                            }
                        }
                        break;
                }
            }

            return null;
        }

        private static FrameworkElement? FindInlineImagePlaceholder(InlineCollection inlines, Guid imageId)
        {
            foreach (Inline inline in inlines)
            {
                switch (inline)
                {
                    case InlineUIContainer { Child: FrameworkElement element }
                        when GetInlineImageId(element) == imageId:
                        return element;
                    case Span span:
                    {
                        var match = FindInlineImagePlaceholder(span.Inlines, imageId);
                        if (match != null)
                            return match;
                        break;
                    }
                    case AnchoredBlock anchoredBlock:
                    {
                        var match = FindInlineImagePlaceholder(anchoredBlock.Blocks, imageId);
                        if (match != null)
                            return match;
                        break;
                    }
                }
            }

            return null;
        }

        private static InlineUIContainer? FindInlineImagePlaceholderContainer(BlockCollection blocks, Guid imageId)
        {
            foreach (Block block in blocks)
            {
                switch (block)
                {
                    case Paragraph paragraph:
                    {
                        var match = FindInlineImagePlaceholderContainer(paragraph.Inlines, imageId);
                        if (match != null)
                            return match;
                        break;
                    }
                    case Section section:
                    {
                        var match = FindInlineImagePlaceholderContainer(section.Blocks, imageId);
                        if (match != null)
                            return match;
                        break;
                    }
                    case System.Windows.Documents.List list:
                        foreach (ListItem item in list.ListItems)
                        {
                            var match = FindInlineImagePlaceholderContainer(item.Blocks, imageId);
                            if (match != null)
                                return match;
                        }
                        break;
                    case Table table:
                        foreach (TableRowGroup rowGroup in table.RowGroups)
                        {
                            foreach (TableRow row in rowGroup.Rows)
                            {
                                foreach (TableCell cell in row.Cells)
                                {
                                    var match = FindInlineImagePlaceholderContainer(cell.Blocks, imageId);
                                    if (match != null)
                                        return match;
                                }
                            }
                        }
                        break;
                }
            }

            return null;
        }

        private static InlineUIContainer? FindInlineImagePlaceholderContainer(InlineCollection inlines, Guid imageId)
        {
            foreach (Inline inline in inlines)
            {
                switch (inline)
                {
                    case InlineUIContainer { Child: FrameworkElement element }
                        when GetInlineImageId(element) == imageId:
                        return (InlineUIContainer)inline;
                    case Span span:
                    {
                        var match = FindInlineImagePlaceholderContainer(span.Inlines, imageId);
                        if (match != null)
                            return match;
                        break;
                    }
                    case AnchoredBlock anchoredBlock:
                    {
                        var match = FindInlineImagePlaceholderContainer(anchoredBlock.Blocks, imageId);
                        if (match != null)
                            return match;
                        break;
                    }
                }
            }

            return null;
        }

        private static void RemoveInlineContainer(InlineUIContainer container)
        {
            var parentInlines = GetParentInlineCollection(container);
            if (parentInlines == null)
                return;

            parentInlines.Remove(container);
        }

        private void ImageCanvas_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Canvas canvas || e.OriginalSource != canvas)
                return;

            ClearImageSelections();

            Keyboard.ClearFocus();
            e.Handled = true;
        }

        private void ResizableImage_ImageBoundsChanged(object? sender, EventArgs e)
        {
            if (sender is ResizableImage image && IsInlineOverlayImage(image))
            {
                UpdateInlineImagePlaceholderSize(image);
                UpdateInlineImageCanvasPosition(image);
                ScheduleFloatingImageOverlayLayoutUpdate();
            }
            else if (sender is ResizableImage floatingImage && IsFloatingOverlayImage(floatingImage))
            {
                UpdateFloatingImageDocumentPositionFromCanvas(floatingImage);
            }
            else if (sender is ResizableImage { Parent: Canvas canvas })
            {
                EnsureImageCanvasSize(canvas);
            }

            ContentTextBox.InvalidateMeasure();
            ContentTextBox.InvalidateArrange();
            MarkEditorContentChanged();
            UpdateEditedIndicator();
        }

        private void ResizableImage_LayoutChangeRequested(object? sender, ImageLayoutChangeRequestedEventArgs e)
        {
            if (sender is not ResizableImage image)
                return;

            try
            {
                if (string.Equals(e.LayoutMode, NoteImageLayout.Inline, StringComparison.OrdinalIgnoreCase))
                {
                    MoveImageInline(image);
                }
                else
                {
                    MoveImageFloating(image);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to change image layout: {ex.Message}");
                return;
            }

            ConfigureResizableImages();
            ContentTextBox.InvalidateMeasure();
            ContentTextBox.InvalidateArrange();
            MarkEditorContentChanged();
            UpdateEditedIndicator();
        }

        private void ResizableImage_InlineMoveRequested(object? sender, InlineImageMoveRequestedEventArgs e)
        {
            if (sender is not ResizableImage image || !IsInlineOverlayImage(image) || _isInlineImageDropPending)
                return;

            var insertionPosition = ResolveInlineInsertionPosition(e.EditorPoint);
            if (insertionPosition == null)
                return;

            _isInlineImageDropPending = true;
            try
            {
                MoveInlineImageToTextPosition(image.ImageId, insertionPosition);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to move inline image by mouse: {ex.Message}");
            }
            finally
            {
                _isInlineImageDropPending = false;
            }
        }

        private void MoveImageInline(ResizableImage image)
        {
            if (IsFloatingOverlayImage(image))
            {
                var canvasLeft = Canvas.GetLeft(image);
                var canvasTop = Canvas.GetTop(image);
                var editorPoint = new Point(
                    (double.IsNaN(canvasLeft) ? 0 : canvasLeft) + ResolveElementWidth(image) / 2,
                    (double.IsNaN(canvasTop) ? 0 : canvasTop) + ResolveElementHeight(image) / 2);

                if (!TryInsertInlineImagePlaceholderAtPoint(editorPoint, image, out _))
                    return;

                image.LayoutMode = NoteImageLayout.Inline;
                image.IsSelected = true;
                UpdateInlineImageCanvasPosition(image);
                ScheduleFloatingImageOverlayLayoutUpdate();
                return;
            }

            if (image.Parent is not Canvas canvas)
                return;

            var block = canvas.Parent as BlockUIContainer;
            var parentBlocks = block == null ? null : GetParentBlockCollection(block);
            if (block == null || parentBlocks == null)
                return;

            canvas.Children.Remove(image);

            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, 4, 0, 4)
            };
            paragraph.Inlines.Add(new InlineUIContainer(CreateInlineImagePlaceholder(image)));

            parentBlocks.InsertBefore(block, paragraph);
            parentBlocks.Remove(block);
            AddInlineImageToOverlay(image);
            image.IsSelected = true;
        }

        private void MoveImageFloating(ResizableImage image)
        {
            if (IsInlineOverlayImage(image))
            {
                var placeholderContainer = FindInlineImagePlaceholderContainer(image.ImageId);
                var canvasLeft = Canvas.GetLeft(image);
                var canvasTop = Canvas.GetTop(image);
                var documentLeft = Math.Max(0, (double.IsNaN(canvasLeft) ? 0 : canvasLeft) + ContentTextBox.HorizontalOffset - ContentTextBox.Padding.Left);
                var documentTop = Math.Max(0, (double.IsNaN(canvasTop) ? 0 : canvasTop) + ContentTextBox.VerticalOffset - ContentTextBox.Padding.Top);

                if (placeholderContainer != null)
                    RemoveInlineContainer(placeholderContainer);

                AddFloatingImageToOverlay(image, documentLeft, documentTop);
                image.IsSelected = true;
                return;
            }

            var inlineContainer = FindInlineContainerForImage(image);
            if (inlineContainer == null)
                return;

            var paragraph = FindParentParagraph(inlineContainer);
            var parentBlocks = paragraph == null ? null : GetParentBlockCollection(paragraph);
            var parentInlines = GetParentInlineCollection(inlineContainer);
            if (paragraph == null || parentBlocks == null || parentInlines == null)
                return;

            var floatingPosition = ResolveFloatingDocumentPosition(image);

            try
            {
                inlineContainer.Child = null;
                parentInlines.Remove(inlineContainer);

                AddFloatingImageToOverlay(image, floatingPosition.X, floatingPosition.Y);

                if (IsParagraphEffectivelyEmpty(paragraph))
                    parentBlocks.Remove(paragraph);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to change inline image to floating: {ex.Message}");
                if (image.Parent == null && inlineContainer.Child == null)
                    inlineContainer.Child = image;

                if (inlineContainer.Parent == null)
                    parentInlines.Add(inlineContainer);
            }
        }

        private void MoveInlineImageToTextPosition(Guid imageId, TextPointer insertionPosition)
        {
            var image = FloatingImageOverlay.Children
                .OfType<ResizableImage>()
                .FirstOrDefault(candidate => candidate.ImageId == imageId);
            if (image == null || !IsInlineOverlayImage(image))
                return;

            var oldPlaceholderContainer = FindInlineImagePlaceholderContainer(imageId);
            if (oldPlaceholderContainer == null)
                return;

            if (IsTextPointerInsideElement(insertionPosition, oldPlaceholderContainer))
                return;

            if (!TryInsertInlineImagePlaceholder(insertionPosition, image, out var newPlaceholderContainer)
                && !TryInsertInlineImagePlaceholderAtDocumentEnd(image, out newPlaceholderContainer))
                return;

            try
            {
                image.LayoutMode = NoteImageLayout.Inline;
                image.IsSelected = true;
                RemoveInlineContainer(oldPlaceholderContainer);
                UpdateInlineImageCanvasPosition(image);
                ScheduleFloatingImageOverlayLayoutUpdate();

                ConfigureResizableImages();
                ContentTextBox.Focus();
                MarkEditorContentChanged();
                UpdateEditedIndicator();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to place inline image: {ex.Message}");
                RemoveInlineContainer(newPlaceholderContainer);
            }
        }

        private static bool IsTextPointerInsideElement(TextPointer pointer, TextElement element)
        {
            try
            {
                return pointer.CompareTo(element.ElementStart) >= 0
                    && pointer.CompareTo(element.ElementEnd) <= 0;
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureImageCanvasSize(Canvas canvas)
        {
            if (double.IsNaN(canvas.Height) || Math.Abs(canvas.Height - FloatingImageAnchorHeight) > 0.5)
            {
                canvas.Height = FloatingImageAnchorHeight;
            }

            canvas.MinHeight = FloatingImageAnchorHeight;
            canvas.InvalidateMeasure();
            canvas.InvalidateArrange();
        }

        private Canvas CreateImageCanvas(ResizableImage image, double height)
        {
            var canvas = new Canvas
            {
                Width = double.NaN,
                Height = Math.Max(FloatingImageAnchorHeight, height),
                Background = Brushes.Transparent,
                ClipToBounds = false
            };

            Panel.SetZIndex(canvas, 1000);
            Panel.SetZIndex(image, 1001);
            canvas.Children.Add(image);
            ConfigureImageCanvas(canvas);
            return canvas;
        }

        private string SerializeEditorContentWithImageMarkers()
        {
            var replacements = new List<ImageMarkerReplacement>();
            _isSerializingEditorContent = true;

            try
            {
                ReplaceImagesWithMarkers(ContentTextBox.Document.Blocks, replacements);

                var textRange = new TextRange(
                    ContentTextBox.Document.ContentStart,
                    ContentTextBox.Document.ContentEnd);

                using var stream = new MemoryStream();
                textRange.Save(stream, DataFormats.XamlPackage);
                return Convert.ToBase64String(stream.ToArray());
            }
            finally
            {
                for (var i = replacements.Count - 1; i >= 0; i--)
                {
                    replacements[i].Restore();
                }

                _isSerializingEditorContent = false;
                ConfigureResizableImages();
            }
        }

        private void ReplaceImagesWithMarkers(BlockCollection blocks, List<ImageMarkerReplacement> replacements)
        {
            for (var block = blocks.FirstBlock; block != null;)
            {
                var nextBlock = block.NextBlock;

                switch (block)
                {
                    case BlockUIContainer { Child: Canvas canvas }:
                    {
                        var image = canvas.Children.OfType<ResizableImage>().FirstOrDefault();
                        if (image != null)
                            ReplaceBlockWithImageMarker(blocks, block, image, replacements);
                        break;
                    }
                    case BlockUIContainer { Child: ResizableImage image }:
                        ReplaceBlockWithImageMarker(blocks, block, image, replacements);
                        break;
                    case Paragraph paragraph:
                        ReplaceInlineImagesWithMarkers(paragraph.Inlines, replacements);
                        break;
                    case Section section:
                        ReplaceImagesWithMarkers(section.Blocks, replacements);
                        break;
                    case System.Windows.Documents.List list:
                        foreach (ListItem item in list.ListItems)
                            ReplaceImagesWithMarkers(item.Blocks, replacements);
                        break;
                    case Table table:
                        foreach (TableRowGroup rowGroup in table.RowGroups)
                        {
                            foreach (TableRow row in rowGroup.Rows)
                            {
                                foreach (TableCell cell in row.Cells)
                                    ReplaceImagesWithMarkers(cell.Blocks, replacements);
                            }
                        }
                        break;
                }

                block = nextBlock;
            }
        }

        private void ReplaceInlineImagesWithMarkers(InlineCollection inlines, List<ImageMarkerReplacement> replacements)
        {
            foreach (Inline inline in inlines.ToList())
            {
                switch (inline)
                {
                    case InlineUIContainer { Child: ResizableImage image }:
                        ReplaceInlineWithImageMarker(inlines, inline, image, replacements);
                        break;
                    case InlineUIContainer { Child: FrameworkElement element } when GetInlineImageId(element) is var imageId && imageId != Guid.Empty:
                        ReplaceInlinePlaceholderWithImageMarker(inlines, inline, imageId, replacements);
                        break;
                    case Span span:
                        ReplaceInlineImagesWithMarkers(span.Inlines, replacements);
                        break;
                    case AnchoredBlock anchoredBlock:
                        ReplaceImagesWithMarkers(anchoredBlock.Blocks, replacements);
                        break;
                }
            }
        }

        private static void ReplaceBlockWithImageMarker(
            BlockCollection blocks,
            Block originalBlock,
            ResizableImage image,
            List<ImageMarkerReplacement> replacements)
        {
            if (image.ImageId == Guid.Empty)
                image.ImageId = Guid.NewGuid();

            var markerBlock = new Paragraph(new Run(CreateImageMarker(image.ImageId)))
            {
                Margin = originalBlock.Margin
            };

            blocks.InsertBefore(originalBlock, markerBlock);
            blocks.Remove(originalBlock);
            replacements.Add(new ImageMarkerReplacement(() =>
            {
                blocks.InsertBefore(markerBlock, originalBlock);
                blocks.Remove(markerBlock);
            }));
        }

        private static void ReplaceInlineWithImageMarker(
            InlineCollection inlines,
            Inline originalInline,
            ResizableImage image,
            List<ImageMarkerReplacement> replacements)
        {
            if (image.ImageId == Guid.Empty)
                image.ImageId = Guid.NewGuid();

            var markerRun = new Run(CreateImageMarker(image.ImageId));
            inlines.InsertBefore(originalInline, markerRun);
            inlines.Remove(originalInline);
            replacements.Add(new ImageMarkerReplacement(() =>
            {
                inlines.InsertBefore(markerRun, originalInline);
                inlines.Remove(markerRun);
            }));
        }

        private static void ReplaceInlinePlaceholderWithImageMarker(
            InlineCollection inlines,
            Inline originalInline,
            Guid imageId,
            List<ImageMarkerReplacement> replacements)
        {
            var markerRun = new Run(CreateImageMarker(imageId));
            inlines.InsertBefore(originalInline, markerRun);
            inlines.Remove(originalInline);
            replacements.Add(new ImageMarkerReplacement(() =>
            {
                inlines.InsertBefore(markerRun, originalInline);
                inlines.Remove(markerRun);
            }));
        }

        private void RestoreImagesFromMarkers(IReadOnlyList<NoteImageAttachment>? images)
        {
            if (images == null || images.Count == 0)
                return;

            foreach (var image in images.Where(image => image.Id != Guid.Empty && !string.IsNullOrWhiteSpace(image.Data)))
            {
                var markerRange = FindTextRange(CreateImageMarker(image.Id));
                var resizableImage = CreateResizableImage(image);
                if (markerRange == null)
                {
                    AppendFloatingImageAtEnd(resizableImage, image);
                    continue;
                }

                if (string.Equals(image.Layout, NoteImageLayout.Inline, StringComparison.OrdinalIgnoreCase))
                {
                    ReplaceMarkerWithInlineImage(markerRange, resizableImage);
                }
                else
                {
                    ReplaceMarkerWithFloatingImage(markerRange, resizableImage, image);
                }
            }
        }

        private void AppendFloatingImageAtEnd(ResizableImage image, NoteImageAttachment attachment)
        {
            AddFloatingImageToOverlay(
                image,
                double.IsNaN(attachment.Left) ? 12 : attachment.Left,
                double.IsNaN(attachment.Top) ? 12 : attachment.Top);
        }

        private void ReplaceMarkerWithInlineImage(TextRange markerRange, ResizableImage image)
        {
            image.LayoutMode = NoteImageLayout.Inline;
            var insertionPosition = markerRange.Start;
            markerRange.Text = string.Empty;
            insertionPosition = insertionPosition.GetInsertionPosition(LogicalDirection.Forward) ?? insertionPosition;
            new InlineUIContainer(CreateInlineImagePlaceholder(image), insertionPosition);
            AddInlineImageToOverlay(image);
        }

        private void ReplaceMarkerWithFloatingImage(
            TextRange markerRange,
            ResizableImage image,
            NoteImageAttachment attachment)
        {
            var markerParagraph = markerRange.Start.Paragraph;
            var parentBlocks = markerParagraph == null ? null : GetParentBlockCollection(markerParagraph);

            if (markerParagraph != null && parentBlocks != null && IsParagraphMarkerOnly(markerParagraph, CreateImageMarker(attachment.Id)))
            {
                parentBlocks.Remove(markerParagraph);
            }
            else
            {
                markerRange.Text = string.Empty;
            }

            AddFloatingImageToOverlay(
                image,
                double.IsNaN(attachment.Left) ? 12 : attachment.Left,
                double.IsNaN(attachment.Top) ? 12 : attachment.Top);
        }

        private ResizableImage CreateResizableImage(NoteImageAttachment attachment)
        {
            var image = new ResizableImage
            {
                ImageId = attachment.Id == Guid.Empty ? Guid.NewGuid() : attachment.Id,
                ImageData = attachment.Data,
                LayoutMode = NormalizeImageLayout(attachment.Layout),
                Width = Math.Max(50, attachment.Width),
                Height = Math.Max(50, attachment.Height),
                PreserveAspectRatio = attachment.PreserveAspectRatio
            };

            ConfigureImageControl(image);
            return image;
        }

        private TextRange? FindTextRange(string text)
        {
            var navigator = ContentTextBox.Document.ContentStart;
            while (navigator != null && navigator.CompareTo(ContentTextBox.Document.ContentEnd) < 0)
            {
                var runText = navigator.GetTextInRun(LogicalDirection.Forward);
                if (!string.IsNullOrEmpty(runText))
                {
                    var index = runText.IndexOf(text, StringComparison.Ordinal);
                    if (index >= 0)
                    {
                        var start = navigator.GetPositionAtOffset(index);
                        var end = start?.GetPositionAtOffset(text.Length);
                        if (start != null && end != null)
                            return new TextRange(start, end);
                    }
                }

                navigator = navigator.GetNextContextPosition(LogicalDirection.Forward);
            }

            return null;
        }

        private List<NoteImageAttachment> BuildImageAttachments()
        {
            var images = new List<NoteImageAttachment>();
            var seen = new HashSet<Guid>();
            AppendImageAttachments(ContentTextBox.Document.Blocks, images, seen);
            foreach (ResizableImage image in FloatingImageOverlay.Children.OfType<ResizableImage>())
                AddImageAttachment(image, images, seen);

            return images;
        }

        private void AppendImageAttachments(
            BlockCollection blocks,
            List<NoteImageAttachment> images,
            HashSet<Guid> seen)
        {
            foreach (Block block in blocks)
            {
                switch (block)
                {
                    case BlockUIContainer { Child: Canvas canvas }:
                        foreach (ResizableImage image in canvas.Children.OfType<ResizableImage>())
                            AddImageAttachment(image, images, seen);
                        break;
                    case BlockUIContainer { Child: ResizableImage image }:
                        AddImageAttachment(image, images, seen);
                        break;
                    case Paragraph paragraph:
                        AppendImageAttachments(paragraph.Inlines, images, seen);
                        break;
                    case Section section:
                        AppendImageAttachments(section.Blocks, images, seen);
                        break;
                    case System.Windows.Documents.List list:
                        foreach (ListItem item in list.ListItems)
                            AppendImageAttachments(item.Blocks, images, seen);
                        break;
                    case Table table:
                        foreach (TableRowGroup rowGroup in table.RowGroups)
                        {
                            foreach (TableRow row in rowGroup.Rows)
                            {
                                foreach (TableCell cell in row.Cells)
                                    AppendImageAttachments(cell.Blocks, images, seen);
                            }
                        }
                        break;
                }
            }
        }

        private void AppendImageAttachments(
            InlineCollection inlines,
            List<NoteImageAttachment> images,
            HashSet<Guid> seen)
        {
            foreach (Inline inline in inlines)
            {
                switch (inline)
                {
                    case InlineUIContainer { Child: ResizableImage image }:
                        AddImageAttachment(image, images, seen);
                        break;
                    case Span span:
                        AppendImageAttachments(span.Inlines, images, seen);
                        break;
                    case AnchoredBlock anchoredBlock:
                        AppendImageAttachments(anchoredBlock.Blocks, images, seen);
                        break;
                }
            }
        }

        private void AddImageAttachment(
            ResizableImage image,
            List<NoteImageAttachment> images,
            HashSet<Guid> seen)
        {
            var attachment = CreateImageAttachment(image);
            if (attachment == null || !seen.Add(attachment.Id))
                return;

            images.Add(attachment);
        }

        private NoteImageAttachment? CreateImageAttachment(ResizableImage image, string? forcedLayout = null)
        {
            ConfigureImageControl(image);

            if (image.ImageId == Guid.Empty)
                image.ImageId = Guid.NewGuid();

            var width = ResolveElementWidth(image);
            var height = ResolveElementHeight(image);
            var encodedSource = TryEncodeImageSource(image.Source, width, height);
            var existingData = image.ImageData;
            var data = SelectCompactImageData(existingData, encodedSource);
            if (string.IsNullOrWhiteSpace(data))
                return null;

            if (!string.Equals(image.ImageData, data, StringComparison.Ordinal))
                image.ImageData = data;

            var layout = forcedLayout ?? NormalizeImageLayout(image.LayoutMode);
            var isFloating = string.Equals(layout, NoteImageLayout.Floating, StringComparison.OrdinalIgnoreCase);
            var isOverlayImage = IsFloatingOverlayImage(image);
            if (isOverlayImage && isFloating)
                UpdateFloatingImageDocumentPositionFromCanvas(image);

            var left = isFloating && isOverlayImage
                ? GetFloatingDocumentLeft(image)
                : isFloating && image.Parent is Canvas ? Canvas.GetLeft(image) : 0;
            var top = isFloating && isOverlayImage
                ? GetFloatingDocumentTop(image)
                : isFloating && image.Parent is Canvas ? Canvas.GetTop(image) : 0;

            return new NoteImageAttachment
            {
                Id = image.ImageId,
                Data = data,
                Layout = NormalizeImageLayout(layout),
                Width = width,
                Height = height,
                Left = double.IsNaN(left) ? 0 : left,
                Top = double.IsNaN(top) ? 0 : top,
                PreserveAspectRatio = image.PreserveAspectRatio
            };
        }

        private string GetImageSnapshot()
        {
            var images = BuildImageAttachments();
            if (images.Count == 0)
                return string.Empty;

            return string.Join(
                '\u001D',
                images.Select(image => string.Join(
                    '\u001C',
                    image.Id.ToString("D"),
                    NormalizeImageLayout(image.Layout),
                    image.Width.ToString("R", CultureInfo.InvariantCulture),
                    image.Height.ToString("R", CultureInfo.InvariantCulture),
                    image.Left.ToString("R", CultureInfo.InvariantCulture),
                    image.Top.ToString("R", CultureInfo.InvariantCulture),
                    image.PreserveAspectRatio ? "1" : "0",
                    image.Data)));
        }

        private static string SelectCompactImageData(string? existingData, string? encodedSource)
        {
            if (string.IsNullOrWhiteSpace(existingData))
                return encodedSource ?? string.Empty;

            if (string.IsNullOrWhiteSpace(encodedSource))
                return existingData;

            if (ImageDataHasAlpha(existingData))
                return encodedSource;

            return encodedSource.Length < existingData.Length
                ? encodedSource
                : existingData;
        }

        private static string TryEncodeImageSource(ImageSource? source, double targetWidth, double targetHeight)
        {
            if (source is not BitmapSource bitmapSource)
                return string.Empty;

            try
            {
                var resized = ResizeBitmapForStorage(bitmapSource, targetWidth, targetHeight);
                var encoder = CreateStorageEncoder(resized);
                using var stream = new MemoryStream();
                encoder.Save(stream);
                return Convert.ToBase64String(stream.ToArray());
            }
            catch
            {
                return string.Empty;
            }
        }

        private static BitmapSource ResizeBitmapForStorage(BitmapSource source, double targetWidth, double targetHeight)
        {
            var maxWidth = targetWidth > 0 ? targetWidth : source.PixelWidth;
            var maxHeight = targetHeight > 0 ? targetHeight : source.PixelHeight;

            maxWidth = Math.Min(maxWidth, 1200);
            maxHeight = Math.Min(maxHeight, 1200);

            var scale = Math.Min(maxWidth / source.PixelWidth, maxHeight / source.PixelHeight);
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0 || scale >= 0.98)
                return source;

            var resized = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            resized.Freeze();
            return resized;
        }

        private static BitmapEncoder CreateStorageEncoder(BitmapSource source)
        {
            var storageSource = HasAlpha(source.Format)
                ? CreateOpaqueStorageSource(source)
                : source;

            var converted = storageSource.Format == PixelFormats.Bgr24
                ? storageSource
                : new FormatConvertedBitmap(storageSource, PixelFormats.Bgr24, null, 0);

            if (converted.CanFreeze)
                converted.Freeze();

            var jpeg = new JpegBitmapEncoder
            {
                QualityLevel = 86
            };
            jpeg.Frames.Add(BitmapFrame.Create(converted));
            return jpeg;
        }

        private static BitmapSource CreateOpaqueStorageSource(BitmapSource source)
        {
            try
            {
                var pixelWidth = Math.Max(1, source.PixelWidth);
                var pixelHeight = Math.Max(1, source.PixelHeight);
                var visual = new DrawingVisual();

                using (var context = visual.RenderOpen())
                {
                    var bounds = new Rect(0, 0, pixelWidth, pixelHeight);
                    context.DrawRectangle(ResolveOpaqueImageBackgroundBrush(), null, bounds);
                    context.DrawImage(source, bounds);
                }

                var rendered = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
                rendered.Render(visual);

                var opaque = new FormatConvertedBitmap(rendered, PixelFormats.Bgr24, null, 0);
                if (opaque.CanFreeze)
                    opaque.Freeze();

                return opaque;
            }
            catch
            {
                return source;
            }
        }

        private static Brush ResolveOpaqueImageBackgroundBrush()
        {
            var brush = Application.Current?.TryFindResource("RichTextBoxBackground") as SolidColorBrush
                ?? Application.Current?.TryFindResource("CardBackground") as SolidColorBrush;
            if (brush == null)
                return Brushes.White;

            var color = brush.Color;
            color.A = 255;
            var opaqueBrush = new SolidColorBrush(color);
            opaqueBrush.Freeze();
            return opaqueBrush;
        }

        private static bool HasAlpha(PixelFormat format)
        {
            return format == PixelFormats.Bgra32
                || format == PixelFormats.Pbgra32
                || format == PixelFormats.Prgba64
                || format == PixelFormats.Rgba64;
        }

        private static bool ImageDataHasAlpha(string? imageData)
        {
            if (string.IsNullOrWhiteSpace(imageData))
                return false;

            try
            {
                var bytes = Convert.FromBase64String(imageData);
                using var stream = new MemoryStream(bytes);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.FirstOrDefault();
                return frame != null && HasAlpha(frame.Format);
            }
            catch
            {
                return false;
            }
        }

        private static List<NoteImageAttachment> CloneImageAttachments(IReadOnlyList<NoteImageAttachment>? images)
        {
            if (images == null || images.Count == 0)
                return new List<NoteImageAttachment>();

            return images
                .Where(image => image != null)
                .Select(image => new NoteImageAttachment
                {
                    Id = image.Id,
                    Data = image.Data ?? string.Empty,
                    Layout = NormalizeImageLayout(image.Layout),
                    Width = image.Width,
                    Height = image.Height,
                    Left = image.Left,
                    Top = image.Top,
                    PreserveAspectRatio = image.PreserveAspectRatio
                })
                .ToList();
        }

        private static bool AreImageListsEqual(
            IReadOnlyList<NoteImageAttachment>? left,
            IReadOnlyList<NoteImageAttachment>? right)
        {
            left ??= Array.Empty<NoteImageAttachment>();
            right ??= Array.Empty<NoteImageAttachment>();

            if (left.Count != right.Count)
                return false;

            for (var i = 0; i < left.Count; i++)
            {
                if (left[i].Id != right[i].Id
                    || !string.Equals(left[i].Data, right[i].Data, StringComparison.Ordinal)
                    || !string.Equals(NormalizeImageLayout(left[i].Layout), NormalizeImageLayout(right[i].Layout), StringComparison.Ordinal)
                    || !NearlyEqual(left[i].Width, right[i].Width)
                    || !NearlyEqual(left[i].Height, right[i].Height)
                    || !NearlyEqual(left[i].Left, right[i].Left)
                    || !NearlyEqual(left[i].Top, right[i].Top)
                    || left[i].PreserveAspectRatio != right[i].PreserveAspectRatio)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool NearlyEqual(double left, double right)
        {
            return Math.Abs(left - right) < 0.5;
        }

        private static string NormalizeImageLayout(string? layout)
        {
            return string.Equals(layout, NoteImageLayout.Inline, StringComparison.OrdinalIgnoreCase)
                ? NoteImageLayout.Inline
                : NoteImageLayout.Floating;
        }

        private static string CreateImageMarker(Guid id)
        {
            return $"{ImageMarkerPrefix}{id:D}{ImageMarkerSuffix}";
        }

        private static bool IsParagraphMarkerOnly(Paragraph paragraph, string marker)
        {
            var text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
            return string.Equals(text.Trim(), marker, StringComparison.Ordinal);
        }

        private static bool IsParagraphEffectivelyEmpty(Paragraph paragraph)
        {
            return string.IsNullOrWhiteSpace(new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text);
        }

        private static double ResolveElementWidth(FrameworkElement element)
        {
            if (!double.IsNaN(element.Width) && element.Width > 0)
                return element.Width;

            if (element.ActualWidth > 0)
                return element.ActualWidth;

            return Math.Max(50, element.MinWidth);
        }

        private static double ResolveElementHeight(FrameworkElement element)
        {
            if (!double.IsNaN(element.Height) && element.Height > 0)
                return element.Height;

            if (element.ActualHeight > 0)
                return element.ActualHeight;

            return Math.Max(50, element.MinHeight);
        }

        private InlineUIContainer? FindInlineContainerForImage(ResizableImage image)
        {
            return FindInlineContainerForImage(ContentTextBox.Document.Blocks, image);
        }

        private InlineUIContainer? FindInlineContainerForImage(Guid imageId)
        {
            return FindInlineContainerForImage(ContentTextBox.Document.Blocks, imageId);
        }

        private static InlineUIContainer? FindInlineContainerForImage(BlockCollection blocks, ResizableImage image)
        {
            foreach (Block block in blocks)
            {
                switch (block)
                {
                    case Paragraph paragraph:
                    {
                        var match = FindInlineContainerForImage(paragraph.Inlines, image);
                        if (match != null)
                            return match;
                        break;
                    }
                    case Section section:
                    {
                        var match = FindInlineContainerForImage(section.Blocks, image);
                        if (match != null)
                            return match;
                        break;
                    }
                    case System.Windows.Documents.List list:
                        foreach (ListItem item in list.ListItems)
                        {
                            var match = FindInlineContainerForImage(item.Blocks, image);
                            if (match != null)
                                return match;
                        }
                        break;
                    case Table table:
                        foreach (TableRowGroup rowGroup in table.RowGroups)
                        {
                            foreach (TableRow row in rowGroup.Rows)
                            {
                                foreach (TableCell cell in row.Cells)
                                {
                                    var match = FindInlineContainerForImage(cell.Blocks, image);
                                    if (match != null)
                                        return match;
                                }
                            }
                        }
                        break;
                }
            }

            return null;
        }

        private static InlineUIContainer? FindInlineContainerForImage(BlockCollection blocks, Guid imageId)
        {
            foreach (Block block in blocks)
            {
                switch (block)
                {
                    case Paragraph paragraph:
                    {
                        var match = FindInlineContainerForImage(paragraph.Inlines, imageId);
                        if (match != null)
                            return match;
                        break;
                    }
                    case Section section:
                    {
                        var match = FindInlineContainerForImage(section.Blocks, imageId);
                        if (match != null)
                            return match;
                        break;
                    }
                    case System.Windows.Documents.List list:
                        foreach (ListItem item in list.ListItems)
                        {
                            var match = FindInlineContainerForImage(item.Blocks, imageId);
                            if (match != null)
                                return match;
                        }
                        break;
                    case Table table:
                        foreach (TableRowGroup rowGroup in table.RowGroups)
                        {
                            foreach (TableRow row in rowGroup.Rows)
                            {
                                foreach (TableCell cell in row.Cells)
                                {
                                    var match = FindInlineContainerForImage(cell.Blocks, imageId);
                                    if (match != null)
                                        return match;
                                }
                            }
                        }
                        break;
                }
            }

            return null;
        }

        private static InlineUIContainer? FindInlineContainerForImage(InlineCollection inlines, ResizableImage image)
        {
            foreach (Inline inline in inlines)
            {
                switch (inline)
                {
                    case InlineUIContainer { Child: ResizableImage child } when ReferenceEquals(child, image):
                        return (InlineUIContainer)inline;
                    case Span span:
                    {
                        var match = FindInlineContainerForImage(span.Inlines, image);
                        if (match != null)
                            return match;
                        break;
                    }
                    case AnchoredBlock anchoredBlock:
                    {
                        var match = FindInlineContainerForImage(anchoredBlock.Blocks, image);
                        if (match != null)
                            return match;
                        break;
                    }
                }
            }

            return null;
        }

        private static InlineUIContainer? FindInlineContainerForImage(InlineCollection inlines, Guid imageId)
        {
            foreach (Inline inline in inlines)
            {
                switch (inline)
                {
                    case InlineUIContainer { Child: ResizableImage child } when child.ImageId == imageId:
                        return (InlineUIContainer)inline;
                    case Span span:
                    {
                        var match = FindInlineContainerForImage(span.Inlines, imageId);
                        if (match != null)
                            return match;
                        break;
                    }
                    case AnchoredBlock anchoredBlock:
                    {
                        var match = FindInlineContainerForImage(anchoredBlock.Blocks, imageId);
                        if (match != null)
                            return match;
                        break;
                    }
                }
            }

            return null;
        }

        private static Paragraph? FindParentParagraph(TextElement element)
        {
            DependencyObject? current = element;
            while (current != null)
            {
                if (current is Paragraph paragraph)
                    return paragraph;

                current = current is TextElement textElement ? textElement.Parent : null;
            }

            return null;
        }

        private static InlineCollection? GetParentInlineCollection(Inline inline)
        {
            return inline.Parent switch
            {
                Paragraph paragraph => paragraph.Inlines,
                Span span => span.Inlines,
                _ => null
            };
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

        private sealed class ImageMarkerReplacement
        {
            private readonly Action _restore;

            public ImageMarkerReplacement(Action restore)
            {
                _restore = restore;
            }

            public void Restore() => _restore();
        }

        private void InsertImageFromFile(string imagePath)
        {
            try
            {
                // Create image from file
                var bitmap = LoadBitmapFromFile(imagePath);

                // Set a max width/height for the image to fit in the note
                const double maxWidth = 400;
                const double maxHeight = 300;

                double displayWidth = bitmap.PixelWidth;
                double displayHeight = bitmap.PixelHeight;

                // Scale down if too large
                if (displayWidth > maxWidth || displayHeight > maxHeight)
                {
                    double aspectRatio = displayWidth / displayHeight;
                    if (displayWidth > maxWidth)
                    {
                        displayWidth = maxWidth;
                        displayHeight = displayWidth / aspectRatio;
                    }
                    if (displayHeight > maxHeight)
                    {
                        displayHeight = maxHeight;
                        displayWidth = displayHeight * aspectRatio;
                    }
                }

                var displayBitmap = LoadBitmapFromFile(imagePath, (int)Math.Ceiling(displayWidth));
                var imageData = TryEncodeImageSource(displayBitmap, displayWidth, displayHeight);
                if (string.IsNullOrWhiteSpace(imageData))
                    imageData = Convert.ToBase64String(File.ReadAllBytes(imagePath));

                var resizableImage = new ResizableImage
                {
                    ImageId = Guid.NewGuid(),
                    Source = displayBitmap,
                    ImageData = imageData,
                    LayoutMode = NoteImageLayout.Floating,
                    Width = displayWidth,
                    Height = displayHeight
                };

                var insertionPosition = ResolveFloatingInsertionPosition();
                AddFloatingImageToOverlay(resizableImage, insertionPosition.X, insertionPosition.Y);
                resizableImage.IsSelected = true;
                MarkEditorContentChanged();
                UpdateEditedIndicator();
            }
            catch (Exception ex)
            {
                throw new Exception($"{LocalizationService.GetString("ErrorLoadingImage")}: {ex.Message}", ex);
            }
        }

        private static BitmapImage LoadBitmapFromFile(string imagePath, int decodePixelWidth = 0)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (decodePixelWidth > 0)
                bitmap.DecodePixelWidth = decodePixelWidth;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
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

            ContentTextBox.LayoutTransform = new ScaleTransform(_zoomLevel, _zoomLevel);
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
