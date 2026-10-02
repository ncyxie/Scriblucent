using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.Services.AI;
using Scriblucent.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
namespace Scriblucent.Views
{
    public partial class SettingsPanel : UserControl
    {
        private sealed class ManagedAiToolItem
        {
            public string Key { get; init; } = string.Empty;
            public string Name { get; init; } = string.Empty;
            public bool IsEnabled { get; set; }
            public bool IsDownloaded { get; set; }
            public bool IsSupported { get; init; } = true;
        }

        private sealed class ManagedSpellCheckLanguageItem
        {
            public string Key { get; init; } = string.Empty;
            public string DisplayName { get; init; } = string.Empty;
            public bool IsEnabled { get; set; }
            public bool IsDownloaded { get; set; }
        }

        private sealed class FlashcardModelOption
        {
            public string Key { get; init; } = string.Empty;
            public string Label { get; init; } = string.Empty;
            public bool ShowBetaBadge { get; init; }
            public bool ShowClozeOnlyBadge { get; init; }

            public override string ToString() => Label;
        }

        private const int MinAutoSaveIntervalSeconds = 5;
        private const int MaxAutoSaveIntervalSeconds = 86400;

        private readonly OverlayPanelAnimator _overlayAnimator;
        private bool _isApplyingSettings;
        private static readonly double[] DefaultNoteFontSizes = [8, 9, 10, 11, 12, 14, 16, 18, 20, 22, 24, 26, 28, 36, 48, 72];
        private static readonly double[] DefaultNoteLineSpacings = [1.0, 1.15, 1.5, 2.0];
        private static readonly double[] DefaultNoteIndentSizes = [24, 48, 72, 96, 120, 144];
        private static readonly string[] PreferredTemplateFonts =
        [
            "Calibri", "Aptos", "Segoe UI", "Arial", "Times New Roman", "Verdana", "Tahoma",
            "Georgia", "Trebuchet MS", "Cambria", "Garamond", "Consolas", "Courier New"
        ];
        private static List<string>? _templateFontNames;
        private bool _isDownloadingAiModel;
        private bool _isCheckingForUpdates;
        private string _lastSelectedFlashcardModelKey = "Qwen3.5-0.8B";
        private readonly ObservableCollection<ManagedAiToolItem> _managedAiTools = new();
        private readonly ObservableCollection<ManagedSpellCheckLanguageItem> _managedSpellCheckLanguages = new();
        private bool _isDownloadingSpellCheckLanguage;
        private bool _isCheckingSpellCheckUpdates;
        private readonly GitHubUpdateService _updateService = new();

        public SettingsPanel()
        {
            InitializeComponent();
            UiMotion.ConfigurePopupsForElement(this);
            _overlayAnimator = OverlayPanelAnimator.Bind(this, OverlayDim, PanelMotionHost, BackdropSnapshot, ScrimHost);
            AiToolsListBox.ItemsSource = _managedAiTools;
            SpellCheckLanguagesListBox.ItemsSource = _managedSpellCheckLanguages;
            LocalizationProvider.Instance.PropertyChanged += LocalizationProvider_PropertyChanged;
            Unloaded += (_, _) => LocalizationProvider.Instance.PropertyChanged -= LocalizationProvider_PropertyChanged;
            NoteEditorWindow.AiGenerationStateChanged += NoteEditorWindow_AiGenerationStateChanged;
            Unloaded += (_, _) => NoteEditorWindow.AiGenerationStateChanged -= NoteEditorWindow_AiGenerationStateChanged;
            ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
            Unloaded += (_, _) => ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;
        }

        private void ThemeManager_ThemeChanged(object? sender, EventArgs e)
        {
            if (Visibility != Visibility.Visible || _overlayAnimator.IsClosing)
                return;

            // Wait until theme brushes and layout settle, then recapture at 1:1 DIP size.
            Dispatcher.BeginInvoke(() =>
            {
                if (Visibility != Visibility.Visible || _overlayAnimator.IsClosing)
                    return;

                Window.GetWindow(this)?.UpdateLayout();
                _overlayAnimator.RefreshBackdrop(
                    () => OverlayBackdropCapture.TryCaptureBehindOverlays(Window.GetWindow(this)));
            }, DispatcherPriority.ApplicationIdle);
        }

        private void NoteEditorWindow_AiGenerationStateChanged(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(UpdateAutoSaveCheckboxAvailability);
        }

        private void LocalizationProvider_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.Equals(e.PropertyName, "Item[]", StringComparison.Ordinal))
                return;

            var machineMemoryBytes = BundledModelHostService.GetTotalPhysicalMemoryBytesForCurrentMachine();
            RefreshFlashcardModelOptions(machineMemoryBytes, AppSettingsService.Load());

            var selectedKey = GetSelectedFlashcardModelKey() ?? _lastSelectedFlashcardModelKey;
            UpdateFlashcardModelWarning(selectedKey, machineMemoryBytes);
            UpdateAiToolsStatusText();
        }

        public void ShowAnimated()
        {
            _overlayAnimator.Show(
                () => _overlayAnimator.RunAfterFirstRender(PopulatePanelContent, DispatcherPriority.ApplicationIdle),
                () => OverlayBackdropCapture.TryCaptureForDimAnimation(Window.GetWindow(this)));
        }

        private void PopulatePanelContent()
        {
            var settings = AppSettingsService.Load();
            var machineMemoryBytes = BundledModelHostService.GetTotalPhysicalMemoryBytesForCurrentMachine();
            var recommendedModelKey = BundledModelHostService.GetRecommendedFlashcardModelKey(machineMemoryBytes);
            var flashcardModelBox = FindName("FlashcardModelBox") as ComboBox;

            _isApplyingSettings = true;
            EnableScrollbarCheckBox.IsChecked = settings.EnableScrollbar;
            EnableVerticalScrollbarCheckBox.IsChecked = settings.EnableVerticalScrollbar;
            EnableHorizontalScrollbarCheckBox.IsChecked = settings.EnableHorizontalScrollbar;
            EnableAutoSaveCheckBox.IsChecked = settings.EnableAutoSave;

            UpdateAutoSaveIntervalControls(settings.AutoSaveIntervalSeconds);

            UpdateAutoSaveCheckboxAvailability();
            LoadManagedAiTools(settings);
            RefreshFlashcardModelOptions(machineMemoryBytes, settings);
            RefreshManagedAiToolDownloadState();
            if (flashcardModelBox is not null)
                SelectComboBoxItemByTag(flashcardModelBox, settings.FlashcardModelKey, recommendedModelKey);
            _lastSelectedFlashcardModelKey = GetSelectedFlashcardModelKey() ?? recommendedModelKey;
            UpdateFlashcardModelWarning(_lastSelectedFlashcardModelKey, machineMemoryBytes);
            SelectComboBoxItemByTag(
                StudyCoverageBox,
                settings.StudyCoverage.ToString(),
                StudyCoverageMode.Auto.ToString());

            SelectComboBoxItemByTag(
                MeasurementUnitSystemBox,
                AppSettings.NormalizeMeasurementUnitSystem(settings.MeasurementUnitSystem),
                AppSettings.UnitSystemImperial);
            SelectComboBoxItemByTag(
                FlashcardSchedulingAlgorithmBox,
                settings.FlashcardSchedulingAlgorithm == SpacedRepetitionAlgorithm.Fsrs6 ? "Fsrs6" : "Sm2",
                "Sm2");
            FlashcardAudioVolumeSlider.Value = Math.Clamp(
                settings.FlashcardAudioVolumePercent,
                AppSettings.MinFlashcardAudioVolumePercent,
                AppSettings.MaxFlashcardAudioVolumePercent);
            UpdateFlashcardAudioVolumeText((int)FlashcardAudioVolumeSlider.Value);
            UpdateRecentItemsLimitControls(settings.RecentItemsLimit);
            if (ReduceAnimationsCheckBox is not null)
                ReduceAnimationsCheckBox.IsChecked = settings.ReduceAnimations;
            if (EnableSpellCheckCheckBox is not null)
                EnableSpellCheckCheckBox.IsChecked = settings.EnableSpellCheck;
            PopulateDefaultNoteTemplate(settings);
            if (DefaultPasteModeBox is not null)
                SelectComboBoxItemByTag(
                    DefaultPasteModeBox,
                    settings.DefaultPasteMode == EditorPasteMode.TextOnly ? "TextOnly" : "KeepFormatting",
                    "KeepFormatting");
            if (UseGpuAccelerationCheckBox is not null)
                UseGpuAccelerationCheckBox.IsChecked = settings.UseGpuAcceleration;
            if (EnableAiFeaturesCheckBox is not null)
                EnableAiFeaturesCheckBox.IsChecked = settings.EnableAiFeatures;
            if (EnableDiagnosticLoggingCheckBox is not null)
                EnableDiagnosticLoggingCheckBox.IsChecked = settings.EnableDiagnosticLogging;
            if (EnableAiContentDiagnosticsCheckBox is not null)
                EnableAiContentDiagnosticsCheckBox.IsChecked = settings.EnableAiContentDiagnostics;
            LoadManagedSpellCheckLanguages(settings);
            ApplyAiFeaturesVisibility(animate: false);
            _isApplyingSettings = false;
        }

        private void LoadManagedAiTools(AppSettings settings)
        {
            var machineMemoryBytes = BundledModelHostService.GetTotalPhysicalMemoryBytesForCurrentMachine();
            var supportedTools = BundledModelHostService.GetSupportedFlashcardTools()
                .ToDictionary(tool => tool.Key, StringComparer.OrdinalIgnoreCase);

            var orderedItems = new List<ManagedAiToolItem>();
            foreach (var item in settings.AiTools ?? new List<AiToolSettingsItem>())
            {
                if (string.IsNullOrWhiteSpace(item.Key) || !supportedTools.TryGetValue(item.Key, out var supported))
                    continue;

                orderedItems.Add(new ManagedAiToolItem
                {
                    Key = supported.Key,
                    Name = supported.DisplayName,
                    IsEnabled = item.IsEnabled
                                && BundledModelHostService.IsFlashcardModelSupportedWithMemory(supported.Key, machineMemoryBytes),
                    IsSupported = BundledModelHostService.IsFlashcardModelSupportedWithMemory(supported.Key, machineMemoryBytes),
                    IsDownloaded = BundledModelHostService.IsModelDownloaded(supported.Key)
                });
            }

            foreach (var supported in supportedTools.Values)
            {
                if (orderedItems.Any(item => string.Equals(item.Key, supported.Key, StringComparison.OrdinalIgnoreCase)))
                    continue;

                orderedItems.Add(new ManagedAiToolItem
                {
                    Key = supported.Key,
                    Name = supported.DisplayName,
                    IsEnabled = BundledModelHostService.IsFlashcardModelSupportedWithMemory(supported.Key, machineMemoryBytes),
                    IsSupported = BundledModelHostService.IsFlashcardModelSupportedWithMemory(supported.Key, machineMemoryBytes),
                    IsDownloaded = BundledModelHostService.IsModelDownloaded(supported.Key)
                });
            }

            if (!orderedItems.Any(item => item.IsEnabled) && orderedItems.Count > 0)
                orderedItems[0].IsEnabled = true;

            _managedAiTools.Clear();
            foreach (var item in orderedItems)
                _managedAiTools.Add(item);

            if (_managedAiTools.Count > 0 && AiToolsListBox.SelectedItem is null)
                AiToolsListBox.SelectedIndex = 0;

            UpdateAiToolsStatusText();
            UpdateAiActionButtons();
        }

        private void PersistManagedAiTools()
        {
            if (!_managedAiTools.Any(tool => tool.IsEnabled) && _managedAiTools.Count > 0)
                _managedAiTools[0].IsEnabled = true;

            var settings = AppSettingsService.Load();
            settings.AiTools = _managedAiTools
                .Select(item => new AiToolSettingsItem
                {
                    Key = item.Key,
                    IsEnabled = item.IsEnabled,
                    IsRemoved = false
                })
                .ToList();

            if (!settings.AiTools.Any(tool => tool.IsEnabled) && settings.AiTools.Count > 0)
                settings.AiTools[0].IsEnabled = true;

            var enabledKeys = BundledModelHostService.GetEnabledFlashcardModelKeys(settings);
            var selectedModelKey = GetSelectedFlashcardModelKey();

            if (string.IsNullOrWhiteSpace(selectedModelKey)
                || !enabledKeys.Any(key => string.Equals(key, selectedModelKey, StringComparison.OrdinalIgnoreCase)))
            {
                selectedModelKey = enabledKeys.FirstOrDefault()
                    ?? BundledModelHostService.GetRecommendedFlashcardModelKeyForCurrentMachine();
            }

            settings.FlashcardModelKey = selectedModelKey;
            AppSettingsService.Save(settings);

            _lastSelectedFlashcardModelKey = selectedModelKey;
            var machineMemoryBytes = BundledModelHostService.GetTotalPhysicalMemoryBytesForCurrentMachine();
            RefreshFlashcardModelOptions(machineMemoryBytes, settings);
            UpdateFlashcardModelWarning(_lastSelectedFlashcardModelKey, machineMemoryBytes);
            UpdateAiToolsStatusText();
            UpdateAiActionButtons();
        }

        private void LoadManagedSpellCheckLanguages(AppSettings settings)
        {
            _managedSpellCheckLanguages.Clear();
            foreach (var language in SpellCheckDictionaryService.GetSupportedLanguages())
            {
                _managedSpellCheckLanguages.Add(new ManagedSpellCheckLanguageItem
                {
                    Key = language.Key,
                    DisplayName = SpellCheckDictionaryService.GetFullLanguageLabel(language.Key),
                    IsEnabled = string.Equals(language.Key, settings.SpellCheckLanguageKey, StringComparison.OrdinalIgnoreCase),
                    IsDownloaded = SpellCheckDictionaryService.IsDictionaryDownloaded(language.Key)
                });
            }
        }

        /// <summary>Single-select, matching Word/LibreOffice's one-proofing-language model -
        /// at most one item ever has IsEnabled true, so the persisted setting is just that item's
        /// key (or empty when none is selected).</summary>
        private void PersistManagedSpellCheckLanguages()
        {
            var settings = AppSettingsService.Load();
            settings.SpellCheckLanguageKey = _managedSpellCheckLanguages
                .FirstOrDefault(item => item.IsEnabled)?.Key ?? string.Empty;
            AppSettingsService.Save(settings);
            RefreshOpenEditorSpellCheckLanguages();
        }

        private static void RefreshOpenEditorSpellCheckLanguages()
        {
            foreach (var window in Application.Current.Windows.OfType<Window>())
            {
                switch (window)
                {
                    case NoteEditorWindow editorWindow:
                        editorWindow.RefreshSpellCheckLanguages();
                        break;
                    case NoteEditorTabsWindow tabsWindow:
                        tabsWindow.RefreshSpellCheckLanguages();
                        break;
                }
            }
        }

        /// <summary>Only wired to RadioButton.Checked, not Unchecked - explicitly clearing every
        /// other item here rather than relying on WPF's own radio-group unchecking to propagate
        /// through each item's binding avoids depending on the order Checked/Unchecked fire in.</summary>
        private void SpellCheckLanguageRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            if (_isApplyingSettings)
                return;

            if (sender is not RadioButton radioButton || radioButton.Tag is not string key)
                return;

            foreach (var language in _managedSpellCheckLanguages)
                language.IsEnabled = string.Equals(language.Key, key, StringComparison.OrdinalIgnoreCase);

            SpellCheckLanguagesListBox.Items.Refresh();
            PersistManagedSpellCheckLanguages();
        }

        private async void DownloadSpellCheckLanguage_Click(object sender, RoutedEventArgs e)
        {
            if (_isDownloadingSpellCheckLanguage || sender is not Button { Tag: string key } || SpellCheckLanguageStatusText is null)
                return;

            var item = _managedSpellCheckLanguages.FirstOrDefault(language => string.Equals(language.Key, key, StringComparison.OrdinalIgnoreCase));
            if (item is null)
                return;

            _isDownloadingSpellCheckLanguage = true;
            SpellCheckLanguageStatusText.Text = string.Format(LocalizationService.GetString("SpellCheckLanguageDownloadingFormat"), item.DisplayName);

            try
            {
                await SpellCheckDictionaryService.DownloadDictionaryAsync(key);
                item.IsDownloaded = true;
                SpellCheckLanguagesListBox.Items.Refresh();
                SpellCheckLanguageStatusText.Text = string.Format(LocalizationService.GetString("SpellCheckLanguageDownloadSuccessFormat"), item.DisplayName);
            }
            catch (Exception ex)
            {
                SpellCheckLanguageStatusText.Text = string.Format(LocalizationService.GetString("SpellCheckLanguageDownloadFailedFormat"), item.DisplayName);
                ModernDialog.ShowError(
                    Window.GetWindow(this),
                    LocalizationService.GetString("Error"),
                    string.Format(LocalizationService.GetString("SpellCheckLanguageDownloadErrorDialogFormat"), item.DisplayName, ex.Message));
            }
            finally
            {
                _isDownloadingSpellCheckLanguage = false;
            }
        }

        private void DeleteSpellCheckLanguage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string key } || SpellCheckLanguageStatusText is null)
                return;

            var item = _managedSpellCheckLanguages.FirstOrDefault(language => string.Equals(language.Key, key, StringComparison.OrdinalIgnoreCase));
            if (item is null)
                return;

            SpellCheckDictionaryService.DeleteDictionary(key);
            item.IsDownloaded = false;
            item.IsEnabled = false;
            SpellCheckLanguagesListBox.Items.Refresh();
            PersistManagedSpellCheckLanguages();
            SpellCheckLanguageStatusText.Text = string.Format(LocalizationService.GetString("SpellCheckLanguageDeletedFormat"), item.DisplayName);
        }

        /// <summary>Combines check-and-update into one action for every downloaded language -
        /// re-checking against the LibreOffice source and re-downloading whatever changed,
        /// rather than a separate check-only step most users would never act on anyway.</summary>
        private async void CheckSpellCheckLanguageUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (_isCheckingSpellCheckUpdates || _isDownloadingSpellCheckLanguage || SpellCheckLanguageStatusText is null)
                return;

            _isCheckingSpellCheckUpdates = true;
            SpellCheckLanguageStatusText.Text = LocalizationService.GetString("SpellCheckCheckingForUpdates");

            try
            {
                var progress = new Progress<string>(languageName =>
                    SpellCheckLanguageStatusText.Text = string.Format(LocalizationService.GetString("SpellCheckCheckingLanguageFormat"), languageName));

                var (checkedCount, updatedCount) = await SpellCheckDictionaryService.CheckAndUpdateAllAsync(progress);

                SpellCheckLanguageStatusText.Text = updatedCount > 0
                    ? string.Format(LocalizationService.GetString("SpellCheckUpdatesAppliedFormat"), updatedCount)
                    : string.Format(LocalizationService.GetString("SpellCheckNoUpdatesFormat"), checkedCount);

                RefreshOpenEditorSpellCheckLanguages();
            }
            catch (Exception ex)
            {
                SpellCheckLanguageStatusText.Text = LocalizationService.GetString("SpellCheckCheckForUpdatesFailed");
                ModernDialog.ShowError(
                    Window.GetWindow(this),
                    LocalizationService.GetString("Error"),
                    ex.Message);
            }
            finally
            {
                _isCheckingSpellCheckUpdates = false;
            }
        }

        private void UpdateAiToolsStatusText()
        {
            if (AiToolsStatusText is null)
                return;

            if (_isDownloadingAiModel)
                return;

            if (AiToolsListBox.SelectedItem is not ManagedAiToolItem selected)
            {
                AiToolsStatusText.Text = _managedAiTools.Count == 0
                    ? LocalizationService.GetString("AiToolsNoModelsConfigured")
                    : LocalizationService.GetString("AiToolsSelectModelToManage");
                UpdateAiActionButtons();
                return;
            }

            selected.IsDownloaded = BundledModelHostService.IsModelDownloaded(selected.Key);

            if (!selected.IsSupported)
            {
                var requiredGb = BundledModelHostService.GetFlashcardModelMinimumSupportedMemoryBytes(selected.Key)
                                 / (1024L * 1024L * 1024L);
                AiToolsStatusText.Text = string.Format(
                    LocalizationService.GetString("AiToolsModelUnsupportedMemoryFormat"),
                    selected.Name,
                    requiredGb);
            }
            else
            {
                AiToolsStatusText.Text = selected.IsDownloaded
                    ? string.Format(LocalizationService.GetString("AiToolsModelReadyFormat"), selected.Name)
                    : string.Format(LocalizationService.GetString("AiToolsModelNotDownloadedFormat"), selected.Name);
            }

            AiToolsListBox.Items.Refresh();
            UpdateAiActionButtons();
        }

        private void UpdateAiActionButtons()
        {
            var selected = AiToolsListBox.SelectedItem as ManagedAiToolItem;
            var hasSelection = selected is not null;

            var isSelectedModelDownloaded = false;
            if (selected is not null)
            {
                isSelectedModelDownloaded = BundledModelHostService.IsModelDownloaded(selected.Key);
                selected.IsDownloaded = isSelectedModelDownloaded;
            }

            if (FindName("DownloadSelectedAiToolButton") is Button downloadModelButton)
                downloadModelButton.IsEnabled = hasSelection
                                                && selected!.IsSupported
                                                && !_isDownloadingAiModel
                                                && !isSelectedModelDownloaded;

            if (FindName("DeleteSelectedAiToolButton") is Button deleteModelButton)
                deleteModelButton.IsEnabled = hasSelection && !_isDownloadingAiModel && isSelectedModelDownloaded;
        }

        private void RefreshManagedAiToolDownloadState()
        {
            foreach (var item in _managedAiTools)
                item.IsDownloaded = BundledModelHostService.IsModelDownloaded(item.Key);

            AiToolsListBox.Items.Refresh();
            UpdateAiToolsStatusText();
            UpdateRemoveDownloadedModelsButton();
        }

        private void RefreshFlashcardModelOptions(long machineMemoryBytes, AppSettings settings)
        {
            var flashcardModelBox = FindName("FlashcardModelBox") as ComboBox;
            if (flashcardModelBox is null)
                return;

            _isApplyingSettings = true;
            flashcardModelBox.Items.Clear();

            var enabledKeys = BundledModelHostService.GetEnabledFlashcardModelKeys(settings);
            foreach (var key in enabledKeys)
            {
                if (!BundledModelHostService.IsFlashcardModelSupportedWithMemory(key, machineMemoryBytes))
                    continue;

                var isCompatible = BundledModelHostService.IsFlashcardModelCompatibleWithMemory(key, machineMemoryBytes);
                flashcardModelBox.Items.Add(CreateFlashcardModelOption(key, isCompatible));
            }

            if (FindName("FlashcardModelContextMenu") is ContextMenu flashcardModelContextMenu)
            {
                flashcardModelContextMenu.Items.Clear();
                foreach (var key in enabledKeys)
                {
                    if (!BundledModelHostService.IsFlashcardModelSupportedWithMemory(key, machineMemoryBytes))
                        continue;

                    var isCompatible = BundledModelHostService.IsFlashcardModelCompatibleWithMemory(key, machineMemoryBytes);
                    var menuItem = new MenuItem
                    {
                        Tag = key,
                        Header = CreateFlashcardModelOptionVisual(key, isCompatible)
                    };
                    menuItem.Click += FlashcardModelMenuItem_Click;
                    flashcardModelContextMenu.Items.Add(menuItem);
                }
            }

            SelectComboBoxItemByTag(flashcardModelBox, settings.FlashcardModelKey, enabledKeys.FirstOrDefault() ?? _lastSelectedFlashcardModelKey);
            _isApplyingSettings = false;

            return;
        }

        private static FlashcardModelOption CreateFlashcardModelOption(string key, bool isCompatible)
        {
            return new FlashcardModelOption
            {
                Key = key,
                Label = BundledModelHostService.GetFlashcardModelDisplayLabel(key, includeWarningPrefix: true, isCompatible: isCompatible),
                ShowBetaBadge = IsCompactBetaModel(key),
                ShowClozeOnlyBadge = IsBasicModel(key)
            };
        }

        private static FrameworkElement CreateFlashcardModelOptionVisual(string key, bool isCompatible)
        {
            var option = CreateFlashcardModelOption(key, isCompatible);
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock
            {
                Text = option.Label,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (option.ShowBetaBadge)
                panel.Children.Add(CreateModelBadge(LocalizationService.GetString("AiModelBetaBadge")));
            if (option.ShowClozeOnlyBadge)
                panel.Children.Add(CreateModelBadge(LocalizationService.GetString("AiModelClozeOnlyBadge")));

            return panel;
        }

        private static Border CreateModelBadge(string text)
        {
            var badgeText = new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            badgeText.SetResourceReference(TextBlock.ForegroundProperty, "AccentPrimaryForeground");

            var badge = new Border
            {
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(6, 1, 6, 1),
                CornerRadius = new CornerRadius(8),
                VerticalAlignment = VerticalAlignment.Center,
                Child = badgeText
            };
            badge.SetResourceReference(Border.BackgroundProperty, "AccentPrimary");
            return badge;
        }

        private static bool IsCompactBetaModel(string key)
            => string.Equals(key, AiModelManifest.CompactModelKey, StringComparison.OrdinalIgnoreCase);

        private static bool IsBasicModel(string key)
            => string.Equals(key, AiModelManifest.BasicModelKey, StringComparison.OrdinalIgnoreCase);

        private string? GetSelectedFlashcardModelKey()
        {
            return FindName("FlashcardModelBox") is ComboBox flashcardModelBox
                ? GetComboItemTag(flashcardModelBox.SelectedItem)
                : null;
        }

        private void UpdateFlashcardModelWarning(string selectedKey, long machineMemoryBytes)
        {
            var warningTextBlock = FindName("FlashcardModelWarningText") as TextBlock;
            if (warningTextBlock is null)
                return;

            var isCompatible = BundledModelHostService.IsFlashcardModelCompatibleWithMemory(selectedKey, machineMemoryBytes);
            if (isCompatible)
            {
                UiMotion.AnimateInlineReveal(
                    warningTextBlock,
                    false,
                    () => warningTextBlock.Text = string.Empty);
                return;
            }

            warningTextBlock.Text = string.Format(
                LocalizationService.GetString("FlashcardModelWarningText"),
                BundledModelHostService.GetFlashcardModelDisplayName(selectedKey));
            UiMotion.AnimateInlineReveal(warningTextBlock, true);
        }

        private static string? GetComboItemTag(object? item)
        {
            return item switch
            {
                ComboBoxItem comboBoxItem => comboBoxItem.Tag?.ToString(),
                FlashcardModelOption option => option.Key,
                _ => null
            };
        }

        private static void SelectComboBoxItemByTag(ComboBox comboBox, string? tag, string fallbackTag)
        {
            var selectedTag = string.IsNullOrWhiteSpace(tag) ? fallbackTag : tag;
            if (TrySelectComboItem(comboBox, selectedTag))
                return;

            TrySelectComboItem(comboBox, fallbackTag);
        }

        private static bool TrySelectComboItem(ComboBox comboBox, string tag)
        {
            foreach (var item in comboBox.Items)
            {
                if (!string.Equals(GetComboItemTag(item), tag, StringComparison.OrdinalIgnoreCase))
                    continue;

                comboBox.SelectedItem = item;
                return true;
            }

            return false;
        }

        // auto-save checkbox handlers
        private void UpdateAutoSaveCheckboxAvailability()
        {
            if (EnableAutoSaveCheckBox is null)
                return;

            EnableAutoSaveCheckBox.IsEnabled = !NoteEditorWindow.IsAiGenerationInProgress;
        }

        private void EnableAutoSaveCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_isApplyingSettings)
                return;

            // Enable auto-save in all open editor windows
            foreach (var window in Application.Current.Windows.OfType<NoteEditorWindow>())
            {
                window.SetAutoSaveEnabled(true);
            }

            // Save preference
            var settings = AppSettingsService.Load();
            settings.EnableAutoSave = true;
            AppSettingsService.Save(settings);
            RefreshMainAutoSaveSettings();
        }

        private void EnableAutoSaveCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isApplyingSettings)
                return;

            // Disable auto-save in all open editor windows
            foreach (var window in Application.Current.Windows.OfType<NoteEditorWindow>())
            {
                window.SetAutoSaveEnabled(false);
            }

            // Save preference
            var settings = AppSettingsService.Load();
            settings.EnableAutoSave = false;
            AppSettingsService.Save(settings);
            RefreshMainAutoSaveSettings();
        }

        private void AutoSaveIntervalTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isApplyingSettings || sender is not TextBox textBox)
                return;

            var digitsOnly = new string(textBox.Text.Where(char.IsDigit).ToArray());
            if (textBox.Text != digitsOnly)
            {
                var caretIndex = Math.Min(textBox.CaretIndex, digitsOnly.Length);
                textBox.Text = digitsOnly;
                textBox.CaretIndex = caretIndex;
                return;
            }
        }

        private void AutoSaveIntervalTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitAutoSaveIntervalTextBox();
        }

        private void AutoSaveIntervalUnitBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isApplyingSettings)
                return;

            CommitAutoSaveIntervalTextBox();
        }

        private void AutoSaveIntervalTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            CommitAutoSaveIntervalTextBox();
            e.Handled = true;
        }

        private void CommitAutoSaveIntervalTextBox()
        {
            if (_isApplyingSettings || AutoSaveIntervalTextBox is null)
                return;

            if (!int.TryParse(AutoSaveIntervalTextBox.Text, out var intervalSeconds))
            {
                intervalSeconds = AppSettingsService.Load().AutoSaveIntervalSeconds;
            }
            else
            {
                intervalSeconds *= GetSelectedAutoSaveIntervalMultiplier();
            }

            ApplyAutoSaveInterval(intervalSeconds);
        }

        private void ApplyAutoSaveInterval(int intervalSeconds)
        {
            intervalSeconds = Math.Clamp(intervalSeconds, MinAutoSaveIntervalSeconds, MaxAutoSaveIntervalSeconds);
            UpdateAutoSaveIntervalControls(intervalSeconds);

            var settings = AppSettingsService.Load();
            settings.AutoSaveIntervalSeconds = intervalSeconds;
            AppSettingsService.Save(settings);

            foreach (var window in Application.Current.Windows.OfType<NoteEditorWindow>())
            {
                window.SetAutoSaveInterval(intervalSeconds);
            }

            RefreshMainAutoSaveSettings();
        }

        private static void RefreshMainAutoSaveSettings()
        {
            if (Application.Current?.MainWindow?.DataContext is MainViewModel mainVm)
                mainVm.RefreshAutoSaveSettings();
        }

        private void UpdateAutoSaveIntervalControls(int intervalSeconds)
        {
            intervalSeconds = Math.Clamp(intervalSeconds, MinAutoSaveIntervalSeconds, MaxAutoSaveIntervalSeconds);

            var wasApplyingSettings = _isApplyingSettings;
            _isApplyingSettings = true;

            if (AutoSaveIntervalTextBox is not null)
            {
                var unit = GetBestAutoSaveIntervalUnit(intervalSeconds);
                SelectAutoSaveIntervalUnit(unit);
                AutoSaveIntervalTextBox.Text = (intervalSeconds / GetAutoSaveIntervalMultiplier(unit))
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            _isApplyingSettings = wasApplyingSettings;
        }

        private static string GetBestAutoSaveIntervalUnit(int intervalSeconds)
        {
            if (intervalSeconds >= 3600 && intervalSeconds % 3600 == 0)
                return "Hours";

            if (intervalSeconds >= 60 && intervalSeconds % 60 == 0)
                return "Minutes";

            return "Seconds";
        }

        private int GetSelectedAutoSaveIntervalMultiplier()
        {
            return AutoSaveIntervalUnitBox?.SelectedItem is ComboBoxItem item
                ? GetAutoSaveIntervalMultiplier(item.Tag?.ToString())
                : 1;
        }

        private static int GetAutoSaveIntervalMultiplier(string? unit)
        {
            return unit switch
            {
                "Hours" => 3600,
                "Minutes" => 60,
                _ => 1
            };
        }

        private void SelectAutoSaveIntervalUnit(string unit)
        {
            if (AutoSaveIntervalUnitBox is null)
                return;

            AutoSaveIntervalUnitBox.SelectedItem = AutoSaveIntervalUnitBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), unit, StringComparison.OrdinalIgnoreCase));
        }

        private void FlashcardModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            HandleFlashcardModelSelection(sender);
        }

        private void FlashcardModelMenuItem_Click(object sender, RoutedEventArgs e)
        {
            HandleFlashcardModelSelection(sender);
        }

        private void HandleFlashcardModelSelection(object sender)
        {
            if (_isApplyingSettings)
                return;

            var selected = sender switch
            {
                ComboBox comboBox => GetComboItemTag(comboBox.SelectedItem),
                MenuItem menuItem => menuItem.Tag?.ToString(),
                _ => null
            };

            if (string.IsNullOrWhiteSpace(selected))
                return;

            var machineMemoryBytes = BundledModelHostService.GetTotalPhysicalMemoryBytesForCurrentMachine();
            var isCompatible = BundledModelHostService.IsFlashcardModelCompatibleWithMemory(selected, machineMemoryBytes);
            if (!isCompatible)
            {
                var warning = string.Format(
                    LocalizationService.GetString("FlashcardModelWarningPrompt"),
                    BundledModelHostService.GetFlashcardModelDisplayName(selected));

                var accepted = ModernDialog.ConfirmWarning(
                    Window.GetWindow(this),
                    LocalizationService.GetString("FlashcardModelWarningTitle"),
                    warning,
                    LocalizationService.GetString("ContinueAnyway"));

                if (!accepted)
                {
                    if (FindName("FlashcardModelBox") is ComboBox flashcardModelBox)
                    {
                        _isApplyingSettings = true;
                        SelectComboBoxItemByTag(flashcardModelBox, _lastSelectedFlashcardModelKey, _lastSelectedFlashcardModelKey);
                        _isApplyingSettings = false;
                    }

                    UpdateFlashcardModelWarning(_lastSelectedFlashcardModelKey, machineMemoryBytes);
                    return;
                }
            }

            if (FindName("FlashcardModelBox") is ComboBox flashcardModelBox2)
            {
                _isApplyingSettings = true;
                SelectComboBoxItemByTag(flashcardModelBox2, selected, selected);
                _isApplyingSettings = false;
            }

            var settings = AppSettingsService.Load();
            settings.FlashcardModelKey = selected;
            AppSettingsService.Save(settings);

            _lastSelectedFlashcardModelKey = selected;
            UpdateFlashcardModelWarning(selected, machineMemoryBytes);
        }

        private void AiToolEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isApplyingSettings)
                return;

            if (sender is not CheckBox checkBox || checkBox.Tag is not string key)
                return;

            var item = _managedAiTools.FirstOrDefault(tool => string.Equals(tool.Key, key, StringComparison.OrdinalIgnoreCase));
            if (item is null)
                return;

            item.IsEnabled = checkBox.IsChecked == true;
            PersistManagedAiTools();
        }

        private void MoveAiToolUp_Click(object sender, RoutedEventArgs e)
        {
            if (AiToolsListBox.SelectedItem is not ManagedAiToolItem selected)
                return;

            var currentIndex = _managedAiTools.IndexOf(selected);
            if (currentIndex <= 0)
                return;

            _managedAiTools.Move(currentIndex, currentIndex - 1);
            AiToolsListBox.SelectedItem = selected;
            PersistManagedAiTools();
        }

        private void MoveAiToolDown_Click(object sender, RoutedEventArgs e)
        {
            if (AiToolsListBox.SelectedItem is not ManagedAiToolItem selected)
                return;

            var currentIndex = _managedAiTools.IndexOf(selected);
            if (currentIndex < 0 || currentIndex >= _managedAiTools.Count - 1)
                return;

            _managedAiTools.Move(currentIndex, currentIndex + 1);
            AiToolsListBox.SelectedItem = selected;
            PersistManagedAiTools();
        }

        private void DeleteSelectedAiTool_Click(object sender, RoutedEventArgs e)
        {
            if (AiToolsListBox.SelectedItem is not ManagedAiToolItem selected)
                return;

            var confirmed = ModernDialog.ConfirmDanger(
                Window.GetWindow(this),
                LocalizationService.GetString("AiToolsDeleteTitle"),
                string.Format(LocalizationService.GetString("AiToolsDeletePromptFormat"), selected.Name),
                LocalizationService.GetString("Confirm"));

            if (!confirmed)
                return;

            BundledModelHostService.DeleteModelArtifacts(selected.Key);

            selected.IsDownloaded = false;

            PersistManagedAiTools();
            RefreshManagedAiToolDownloadState();
        }

        private async void DownloadSelectedAiTool_Click(object sender, RoutedEventArgs e)
        {
            if (_isDownloadingAiModel)
                return;

            if (!AiFeatureGate.IsEnabled)
                return;

            if (AiToolsListBox.SelectedItem is not ManagedAiToolItem selected)
                return;

            _isDownloadingAiModel = true;
            AiToolsStatusText.Text = string.Format(LocalizationService.GetString("AiToolsDownloadStartingFormat"), selected.Name);
            UpdateAiActionButtons();

            try
            {
                var progress = new Progress<BundledModelHostService.FlashcardProgress>(p =>
                {
                    if (p.StatusKey == BundledModelHostService.ModelVerificationStatusKey)
                        AiToolsStatusText.Text = LocalizationService.GetString(BundledModelHostService.ModelVerificationStatusKey);
                    else if (p.Percent.HasValue)
                        AiToolsStatusText.Text = string.Format(LocalizationService.GetString("AiToolsDownloadingPercentFormat"), selected.Name, p.Percent.Value);
                    else
                        AiToolsStatusText.Text = string.Format(LocalizationService.GetString("AiToolsDownloadPreparingFormat"), selected.Name);
                });

                await BundledModelHostService.Instance.EnsureModelAvailableAsync(selected.Key, progress);
                RefreshManagedAiToolDownloadState();
                AiToolsStatusText.Text = string.Format(LocalizationService.GetString("AiToolsDownloadSuccessFormat"), selected.Name);
            }
            catch (Exception ex)
            {
                AiToolsStatusText.Text = string.Format(LocalizationService.GetString("AiToolsDownloadFailedFormat"), selected.Name);

                ModernDialog.ShowError(
                    Window.GetWindow(this),
                    LocalizationService.GetString("Error"),
                    string.Format(LocalizationService.GetString("AiToolsDownloadErrorDialogFormat"), selected.Name, ex.Message));
            }
            finally
            {
                _isDownloadingAiModel = false;
                UpdateAiToolsStatusText();
                UpdateAiActionButtons();
            }
        }

        private void AiToolsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateAiToolsStatusText();
            UpdateAiActionButtons();
        }

        private void OpenAiToolsFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var modelsPath = BundledModelHostService.GetModelsDirectoryPath();
                Directory.CreateDirectory(modelsPath);

                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = modelsPath,
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }

        private void OpenSpellCheckDictionariesFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dictionariesPath = SpellCheckDictionaryService.GetDictionariesRootDirectoryPath();
                Directory.CreateDirectory(dictionariesPath);

                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = dictionariesPath,
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }

        public void HideAnimated()
        {
            _overlayAnimator.Hide();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            TryCloseAnimated();
        }

        private void OverlayDim_MouseDown(object sender, MouseButtonEventArgs e)
        {
            TryCloseAnimated();
        }

        private bool TryCloseAnimated()
        {
            if (RecentItemsLimitTextBox is not null && !TryGetValidRecentItemsLimit(out _))
            {
                UpdateRecentItemsLimitWarning(false);
                RecentItemsLimitTextBox.Focus();
                RecentItemsLimitTextBox.SelectAll();
                return false;
            }

            HideAnimated();
            return true;
        }

        private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (_isCheckingForUpdates)
                return;

            _isCheckingForUpdates = true;
            var button = sender as Button;
            var previousContent = button?.Content;

            if (button is not null)
            {
                button.IsEnabled = false;
                button.Content = LocalizationService.GetString("CheckingForUpdates");
            }

            try
            {
                var result = await _updateService.CheckForUpdatesAsync();
                var dialog = new UpdateCheckDialog(result)
                {
                    Owner = Window.GetWindow(this)
                };

                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                var dialog = new UpdateCheckDialog(
                    LocalizationService.GetString("UpdateCheckFailedTitle"),
                    string.Format(LocalizationService.GetString("UpdateCheckFailedMessageFormat"), ex.Message),
                    GitHubUpdateService.ReleasesUrl)
                {
                    Owner = Window.GetWindow(this)
                };

                dialog.ShowDialog();
            }
            finally
            {
                _isCheckingForUpdates = false;

                if (button is not null)
                {
                    button.Content = previousContent;
                    button.IsEnabled = true;
                }
            }
        }

        private void ResetActivitySummary_Click(object sender, RoutedEventArgs e)
        {
            var confirmed = ModernDialog.ConfirmWarning(
                Window.GetWindow(this),
                LocalizationService.GetString("ResetActivitySummaryTitle"),
                LocalizationService.GetString("ResetActivitySummaryPrompt"),
                LocalizationService.GetString("ResetActivitySummary"));

            if (!confirmed)
                return;

            ActivityTracker.Reset();

            if (Application.Current?.MainWindow?.DataContext is MainViewModel mainVm)
                mainVm.RefreshActivityStats();

            ModernDialog.ShowSuccess(
                Window.GetWindow(this),
                LocalizationService.GetString("Success"),
                LocalizationService.GetString("ResetActivitySummarySuccess"));
        }

        private void ResetFactorySettings_Click(object sender, RoutedEventArgs e)
        {
            var message = string.Concat(
                LocalizationService.GetString("ResetFactorySettingsWarning"),
                Environment.NewLine,
                Environment.NewLine,
                LocalizationService.GetString("ResetFactorySettingsPrompt"));

            var confirmed = ModernDialog.ConfirmDanger(
                Window.GetWindow(this),
                LocalizationService.GetString("ResetFactorySettingsTitle"),
                message,
                LocalizationService.GetString("ResetFactorySettings"));

            if (confirmed)
            {
                PerformFactoryReset();
            }
        }

        private void PerformFactoryReset()
        {
            try
            {
                // Reset all settings to defaults
                var defaultSettings = new AppSettings
                {
                    FlashcardModelKey = BundledModelHostService.GetRecommendedFlashcardModelKeyForCurrentMachine()
                };

                AppSettingsService.Save(defaultSettings);

                // Apply language change to reflect new settings
                LocalizationService.SetCulture(defaultSettings.Language);

                // Apply theme change
                ThemeManager.SetTheme(defaultSettings.Theme);

                // Reload the settings in this panel to reflect the reset
                HideAnimated();

                // Show confirmation message
                ModernDialog.ShowSuccess(
                    Window.GetWindow(this),
                    LocalizationService.GetString("Success"),
                    LocalizationService.GetString("ResetFactorySettingsSuccess"));

                // Restart the application
                System.Diagnostics.Process.Start(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "");
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                ModernDialog.ShowError(
                    Window.GetWindow(this),
                    LocalizationService.GetString("Error"),
                    string.Format(LocalizationService.GetString("ResetFactorySettingsFailedFormat"), ex.Message));
            }
        }

        private void RecentItemsLimitTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isApplyingSettings || sender is not TextBox textBox)
                return;

            var digitsOnly = new string(textBox.Text.Where(char.IsDigit).ToArray());
            if (textBox.Text != digitsOnly)
            {
                var caretIndex = Math.Min(textBox.CaretIndex, digitsOnly.Length);
                textBox.Text = digitsOnly;
                textBox.CaretIndex = caretIndex;
            }

            UpdateRecentItemsLimitWarning(TryGetValidRecentItemsLimit(out _));
        }

        private bool TryGetValidRecentItemsLimit(out int limit)
        {
            if (RecentItemsLimitTextBox is null || !int.TryParse(RecentItemsLimitTextBox.Text, out limit))
            {
                limit = default;
                return false;
            }

            return limit >= AppSettings.MinRecentItemsLimit && limit <= AppSettings.MaxRecentItemsLimit;
        }

        private void UpdateRecentItemsLimitWarning(bool isValid)
        {
            if (RecentItemsLimitFieldBorder is not null)
            {
                if (isValid)
                {
                    RecentItemsLimitFieldBorder.ClearValue(BorderBrushProperty);
                    RecentItemsLimitFieldBorder.ClearValue(BorderThicknessProperty);
                }
                else
                {
                    RecentItemsLimitFieldBorder.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentDanger");
                    RecentItemsLimitFieldBorder.BorderThickness = new Thickness(2);
                }
            }

            if (RecentItemsLimitWarningText is null)
                return;

            if (isValid)
            {
                UiMotion.AnimateInlineReveal(RecentItemsLimitWarningText, false, () => RecentItemsLimitWarningText.Text = string.Empty);
                return;
            }

            RecentItemsLimitWarningText.Text = string.Format(
                LocalizationService.GetString("RecentItemsLimitRangeWarning"),
                AppSettings.MinRecentItemsLimit,
                AppSettings.MaxRecentItemsLimit);
            UiMotion.AnimateInlineReveal(RecentItemsLimitWarningText, true);
        }

        private void RecentItemsLimitTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitRecentItemsLimitTextBox();
        }

        private void RecentItemsLimitTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            CommitRecentItemsLimitTextBox();
            e.Handled = true;
        }

        private void CommitRecentItemsLimitTextBox()
        {
            if (_isApplyingSettings || RecentItemsLimitTextBox is null)
                return;

            if (TryGetValidRecentItemsLimit(out var limit))
            {
                ApplyRecentItemsLimit(limit);
                return;
            }

            // Out-of-range/unparseable input is left as typed (not silently clamped) so the
            // warning stays visible and accurate; the close handlers refuse to close while
            // this state persists, so the user has to actually fix it.
            UpdateRecentItemsLimitWarning(false);
        }

        private void ApplyRecentItemsLimit(int limit)
        {
            limit = AppSettings.NormalizeRecentItemsLimit(limit);
            UpdateRecentItemsLimitControls(limit);

            var settings = AppSettingsService.Load();
            settings.RecentItemsLimit = limit;
            AppSettingsService.Save(settings);

            if (Application.Current?.MainWindow?.DataContext is MainViewModel mainVm)
                mainVm.RefreshRecentItemsFromSettings();
        }

        private void UpdateRecentItemsLimitControls(int limit)
        {
            limit = AppSettings.NormalizeRecentItemsLimit(limit);

            var wasApplyingSettings = _isApplyingSettings;
            _isApplyingSettings = true;

            if (RecentItemsLimitTextBox is not null)
            {
                RecentItemsLimitTextBox.Text = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            _isApplyingSettings = wasApplyingSettings;

            UpdateRecentItemsLimitWarning(true);
        }

        private void ReduceAnimationsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isApplyingSettings || ReduceAnimationsCheckBox is null)
                return;

            ApplyReduceAnimations(ReduceAnimationsCheckBox.IsChecked == true);
        }

        private static void ApplyReduceAnimations(bool reduceAnimations)
        {
            var settings = AppSettingsService.Load();
            settings.ReduceAnimations = reduceAnimations;
            AppSettingsService.Save(settings);
            ReducedMotionPreferences.Configure(reduceAnimations);
            ReducedMotionPreferences.Refresh();
        }

        private void EnableSpellCheckCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isApplyingSettings || EnableSpellCheckCheckBox is null)
                return;

            var enabled = EnableSpellCheckCheckBox.IsChecked == true;
            var settings = AppSettingsService.Load();
            settings.EnableSpellCheck = enabled;
            AppSettingsService.Save(settings);
            RefreshOpenEditorSpellCheck(enabled);
        }

        private void EnableDiagnosticLoggingCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isApplyingSettings || EnableDiagnosticLoggingCheckBox is null)
                return;

            var settings = AppSettingsService.Load();
            settings.EnableDiagnosticLogging = EnableDiagnosticLoggingCheckBox.IsChecked == true;
            AppSettingsService.Save(settings);
        }

        private void StudyCoverageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isApplyingSettings || StudyCoverageBox.SelectedItem is not ComboBoxItem item)
                return;
            if (!Enum.TryParse<StudyCoverageMode>(item.Tag?.ToString(), ignoreCase: true, out var coverage))
                coverage = StudyCoverageMode.Auto;

            var settings = AppSettingsService.Load();
            settings.StudyCoverage = StudyPlanCoordinator.NormalizeCoverage(coverage);
            AppSettingsService.Save(settings);
        }

        private void UseGpuAccelerationCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isApplyingSettings || UseGpuAccelerationCheckBox is null)
                return;

            var settings = AppSettingsService.Load();
            settings.UseGpuAcceleration = UseGpuAccelerationCheckBox.IsChecked == true;
            AppSettingsService.Save(settings);
        }

        private void EnableAiFeaturesCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isApplyingSettings || EnableAiFeaturesCheckBox is null)
                return;

            var settings = AppSettingsService.Load();
            settings.EnableAiFeatures = EnableAiFeaturesCheckBox.IsChecked == true;
            AppSettingsService.Save(settings);
            AiFeatureGate.NotifyChanged();
            ApplyAiFeaturesVisibility(animate: true);
        }

        private void ApplyAiFeaturesVisibility(bool animate)
        {
            var enabled = AiFeatureGate.IsEnabled;
            if (AiToolsDetailPanel is not null)
            {
                if (animate)
                    UiMotion.AnimateInlineReveal(AiToolsDetailPanel, enabled);
                else
                    AiToolsDetailPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            }

            if (AiContentDiagnosticsPanel is not null)
            {
                if (animate)
                    UiMotion.AnimateInlineReveal(AiContentDiagnosticsPanel, enabled);
                else
                    AiContentDiagnosticsPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            }

            UpdateRemoveDownloadedModelsButton();
        }

        private void UpdateRemoveDownloadedModelsButton()
        {
            if (RemoveDownloadedAiModelsButton is null)
                return;

            var show = !AiFeatureGate.IsEnabled && HasDownloadedAiModel();
            RemoveDownloadedAiModelsButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private static bool HasDownloadedAiModel()
        {
            return BundledModelHostService.GetSupportedFlashcardTools()
                .Any(tool => BundledModelHostService.IsModelDownloaded(tool.Key));
        }

        private void RemoveDownloadedAiModelsButton_Click(object sender, RoutedEventArgs e)
        {
            var confirmed = ModernDialog.ConfirmDanger(
                Window.GetWindow(this),
                LocalizationService.GetString("AiToolsRemoveDownloadedModelsTitle"),
                LocalizationService.GetString("AiToolsRemoveDownloadedModelsPrompt"),
                LocalizationService.GetString("Confirm"));

            if (!confirmed)
                return;

            foreach (var tool in BundledModelHostService.GetSupportedFlashcardTools())
                BundledModelHostService.DeleteModelArtifacts(tool.Key);

            RefreshManagedAiToolDownloadState();
            UpdateRemoveDownloadedModelsButton();
        }

        private void EnableAiContentDiagnosticsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isApplyingSettings || EnableAiContentDiagnosticsCheckBox is null)
                return;

            var settings = AppSettingsService.Load();
            settings.EnableAiContentDiagnostics = EnableAiContentDiagnosticsCheckBox.IsChecked == true;
            AppSettingsService.Save(settings);
        }

        private void ClearStudyPlanCache_Click(object sender, RoutedEventArgs e)
        {
            StudyPlanCache.Shared.Clear();
            AiToolsStatusText.Text = LocalizationService.GetString("StudyCoveragePlansCleared");
        }

        private static void RefreshOpenEditorSpellCheck(bool enabled)
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

        private void MeasurementUnitSystemBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isApplyingSettings)
                return;

            if (MeasurementUnitSystemBox.SelectedItem is not ComboBoxItem item)
                return;

            var selected = AppSettings.NormalizeMeasurementUnitSystem(item.Tag?.ToString());
            var settings = AppSettingsService.Load();
            settings.MeasurementUnitSystem = selected;
            AppSettingsService.Save(settings);
            RefreshOpenEditorMeasurementUnits(selected);
            PopulateDefaultNoteTemplate(settings);
        }

        private void DefaultNoteTemplate_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => SaveDefaultNoteTemplate();

        private void DefaultNoteWordWrapCheckBox_Changed(object sender, RoutedEventArgs e)
            => SaveDefaultNoteTemplate();

        private void DefaultPasteModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isApplyingSettings || DefaultPasteModeBox?.SelectedItem is not ComboBoxItem item)
                return;

            var settings = AppSettingsService.Load();
            settings.DefaultPasteMode = string.Equals(item.Tag?.ToString(), "TextOnly", StringComparison.Ordinal)
                ? EditorPasteMode.TextOnly
                : EditorPasteMode.KeepFormatting;
            AppSettingsService.Save(settings);
        }

        private void PopulateDefaultNoteTemplate(AppSettings settings)
        {
            if (DefaultNoteFontFamilyBox is null)
                return;

            var wasApplying = _isApplyingSettings;
            _isApplyingSettings = true;
            try
            {
                if (DefaultNoteFontFamilyBox.Items.Count == 0)
                {
                    foreach (var fontName in TemplateFontNames())
                        DefaultNoteFontFamilyBox.Items.Add(new ComboBoxItem { Content = fontName });
                }

                var font = string.IsNullOrWhiteSpace(settings.PreferredFontFamily) ? "Calibri" : settings.PreferredFontFamily;
                SelectOrInsertComboContent(DefaultNoteFontFamilyBox, font);

                if (DefaultNoteFontSizeBox.Items.Count == 0)
                {
                    foreach (var size in DefaultNoteFontSizes)
                        DefaultNoteFontSizeBox.Items.Add(new ComboBoxItem { Content = FormatTemplateNumber(size), Tag = size });
                }

                var fontSize = settings.PreferredFontSize > 0 ? settings.PreferredFontSize : 14;
                SelectOrInsertComboTag(DefaultNoteFontSizeBox, fontSize, FormatTemplateNumber(fontSize));

                if (DefaultNoteLineSpacingBox.Items.Count == 0)
                {
                    foreach (var spacing in DefaultNoteLineSpacings)
                        DefaultNoteLineSpacingBox.Items.Add(new ComboBoxItem { Content = FormatTemplateNumber(spacing), Tag = spacing });
                }

                var lineSpacing = settings.PreferredLineSpacing > 0 ? settings.PreferredLineSpacing : 1;
                SelectOrInsertComboTag(DefaultNoteLineSpacingBox, lineSpacing, FormatTemplateNumber(lineSpacing));

                var indent = settings.PreferredIndentSize > 0 ? settings.PreferredIndentSize : 48;
                DefaultNoteIndentBox.Items.Clear();
                foreach (var step in DefaultNoteIndentSizes)
                {
                    DefaultNoteIndentBox.Items.Add(new ComboBoxItem
                    {
                        Content = FormatTemplateIndent(step, settings.MeasurementUnitSystem),
                        Tag = step
                    });
                }

                SelectOrInsertComboTag(DefaultNoteIndentBox, indent, FormatTemplateIndent(indent, settings.MeasurementUnitSystem));
                if (DefaultNoteWordWrapCheckBox is not null)
                    DefaultNoteWordWrapCheckBox.IsChecked = settings.IsEditorWordWrapEnabled;
            }
            finally
            {
                _isApplyingSettings = wasApplying;
            }
        }

        private void SaveDefaultNoteTemplate()
        {
            if (_isApplyingSettings
                || DefaultNoteFontFamilyBox?.SelectedItem is not ComboBoxItem fontItem
                || DefaultNoteFontSizeBox?.SelectedItem is not ComboBoxItem sizeItem
                || DefaultNoteLineSpacingBox?.SelectedItem is not ComboBoxItem spacingItem
                || DefaultNoteIndentBox?.SelectedItem is not ComboBoxItem indentItem
                || DefaultNoteWordWrapCheckBox is null)
                return;

            var settings = AppSettingsService.Load();
            settings.PreferredFontFamily = fontItem.Content?.ToString() ?? "Calibri";
            settings.PreferredFontSize = TemplateTag(sizeItem, 14);
            settings.PreferredLineSpacing = TemplateTag(spacingItem, 1);
            settings.PreferredIndentSize = TemplateTag(indentItem, 48);
            settings.IsEditorWordWrapEnabled = DefaultNoteWordWrapCheckBox.IsChecked == true;
            AppSettingsService.Save(settings);
            if (Application.Current?.MainWindow?.DataContext is MainViewModel mainViewModel)
                mainViewModel.InvalidatePreferredTypographyCache();
        }

        private static List<string> TemplateFontNames()
        {
            if (_templateFontNames is { } cached)
                return cached;

            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fontName in PreferredTemplateFonts)
            {
                if (seen.Add(fontName))
                    names.Add(fontName);
            }

            foreach (var fontFamily in Fonts.SystemFontFamilies
                         .Select(fontFamily => fontFamily.Source)
                         .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(fontFamily) && seen.Add(fontFamily))
                    names.Add(fontFamily);
            }

            _templateFontNames = names;
            return names;
        }

        private static void SelectOrInsertComboContent(ComboBox comboBox, string content)
        {
            foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
            {
                if (!string.Equals(item.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase))
                    continue;
                comboBox.SelectedItem = item;
                return;
            }

            var added = new ComboBoxItem { Content = content };
            comboBox.Items.Insert(0, added);
            comboBox.SelectedItem = added;
        }

        private static void SelectOrInsertComboTag(ComboBox comboBox, double value, string content)
        {
            foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
            {
                if (Math.Abs(TemplateTag(item, double.NaN) - value) >= 0.1)
                    continue;
                comboBox.SelectedItem = item;
                return;
            }

            var added = new ComboBoxItem { Content = content, Tag = value };
            comboBox.Items.Add(added);
            comboBox.SelectedItem = added;
        }

        private static double TemplateTag(ComboBoxItem item, double fallback)
            => item.Tag is double value && !double.IsNaN(value) && !double.IsInfinity(value) ? value : fallback;

        private static string FormatTemplateNumber(double value)
            => Math.Abs(value - Math.Round(value)) < 0.01
                ? Math.Round(value).ToString(CultureInfo.InvariantCulture)
                : value.ToString("0.##", CultureInfo.InvariantCulture);

        private static string FormatTemplateIndent(double indentSize, string? unitSystem)
        {
            if (string.Equals(AppSettings.NormalizeMeasurementUnitSystem(unitSystem), AppSettings.UnitSystemMetric, StringComparison.Ordinal))
                return $"{FormatTemplateNumber(indentSize / 96.0 * 2.54)} cm";
            return $"{FormatTemplateNumber(indentSize / 96.0)} in";
        }

        private static void RefreshOpenEditorMeasurementUnits(string unitSystem)
        {
            foreach (var window in Application.Current.Windows.OfType<Window>())
            {
                switch (window)
                {
                    case NoteEditorWindow editorWindow:
                        editorWindow.ApplyMeasurementUnitSystemPreference(unitSystem);
                        break;
                    case NoteEditorTabsWindow tabsWindow:
                        tabsWindow.ApplyMeasurementUnitSystemPreference(unitSystem);
                        break;
                }
            }
        }

        /// <summary>A single, collection-wide choice - matches Anki's own FSRS toggle, which is
        /// "shared by all presets" rather than configured per deck. SM-2's ease/interval state and
        /// FSRS's difficulty/stability state have no meaningful conversion between them, so
        /// switching resets every flashcard set's scheduling progress, after explicit confirmation
        /// since it's destructive to in-progress review history.</summary>
        private void FlashcardSchedulingAlgorithmBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isApplyingSettings)
                return;

            if (FlashcardSchedulingAlgorithmBox.SelectedItem is not ComboBoxItem item)
                return;

            var settings = AppSettingsService.Load();
            var newAlgorithm = string.Equals(item.Tag?.ToString(), "Fsrs6", StringComparison.OrdinalIgnoreCase)
                ? SpacedRepetitionAlgorithm.Fsrs6
                : SpacedRepetitionAlgorithm.Sm2;

            if (newAlgorithm == settings.FlashcardSchedulingAlgorithm)
                return;

            var result = ModernMessageBox.Show(
                LocalizationService.GetString("FlashcardSchedulingAlgorithmSwitchConfirmation"),
                LocalizationService.GetString("FlashcardSchedulingAlgorithmSwitchTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (result != MessageBoxResult.Yes)
            {
                _isApplyingSettings = true;
                SelectComboBoxItemByTag(
                    FlashcardSchedulingAlgorithmBox,
                    settings.FlashcardSchedulingAlgorithm == SpacedRepetitionAlgorithm.Fsrs6 ? "Fsrs6" : "Sm2",
                    "Sm2");
                _isApplyingSettings = false;
                return;
            }

            ResetAllFlashcardSetsSchedulingProgress();

            settings.FlashcardSchedulingAlgorithm = newAlgorithm;
            AppSettingsService.Save(settings);
        }

        private void FlashcardAudioVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            var percent = (int)Math.Round(e.NewValue);
            UpdateFlashcardAudioVolumeText(percent);

            if (_isApplyingSettings)
                return;

            var settings = AppSettingsService.Load();
            settings.FlashcardAudioVolumePercent = percent;
            AppSettingsService.Save(settings);
        }

        private void UpdateFlashcardAudioVolumeText(int percent)
        {
            if (FlashcardAudioVolumeValueText is not null)
                FlashcardAudioVolumeValueText.Text = $"{percent}%";
        }

        private static void ResetAllFlashcardSetsSchedulingProgress()
        {
            if (Application.Current?.MainWindow?.DataContext is not MainViewModel mainVm)
                return;

            foreach (var set in mainVm.FlashcardSets)
            {
                FlashcardStudyHistoryWindow.ClearDocumentProgress(set.Document);
                set.NotifyChanged();
            }

            mainVm.SaveFlashcardSets();
        }
    }
}
