namespace Scriblucent.Models;

public class AppSettings
{
    public const string UnitSystemImperial = "Imperial";
    public const string UnitSystemMetric = "Metric";

    public string Language { get; set; } = "en";

    public string Theme { get; set; } = "Light";

    public string MeasurementUnitSystem { get; set; } = UnitSystemImperial;

    public string NoteSortOptionKey { get; set; } = "last-modified-desc";

    public bool EnableScrollbar { get; set; } = true;

    public bool EnableVerticalScrollbar { get; set; } = true;

    public bool EnableHorizontalScrollbar { get; set; } = true;
    
    public bool EnableAutoSave { get; set; } = true;

    /// <summary>
    /// When true, UI motion is minimized regardless of Windows accessibility settings.
    /// When false, Windows system animation preferences are respected.
    /// </summary>
    public bool ReduceAnimations { get; set; }
    
    public int AutoSaveIntervalSeconds { get; set; } = 300;

    public bool IsRecentSectionExpanded { get; set; } = true;

    public bool IsGroupsSectionExpanded { get; set; } = true;

    public bool IsUngroupedSectionExpanded { get; set; } = true;

    public bool IsNotesDashboardSectionVisible { get; set; } = true;

    public bool IsNotesDashboardSectionExpanded { get; set; } = true;

    public bool IsRecentSectionVisible { get; set; } = true;

    public bool IsGroupsSectionVisible { get; set; } = true;

    public bool IsUngroupedSectionVisible { get; set; } = true;

    public bool IsCalendarSectionExpanded { get; set; } = true;

    public bool IsCalendarSectionVisible { get; set; } = true;

    public bool IsFlashcardGroupsSectionExpanded { get; set; } = true;

    public bool IsFlashcardGroupsSectionVisible { get; set; } = true;

    public bool IsFlashcardDashboardGroupsSectionVisible { get; set; } = true;

    public bool IsFlashcardDashboardUngroupedSectionVisible { get; set; } = true;

    public bool IsFlashcardDashboardGroupsSectionExpanded { get; set; } = true;

    public bool IsFlashcardDashboardUngroupedSectionExpanded { get; set; } = true;

    public bool IsFlashcardDashboardGroupsFirst { get; set; } = true;

    public bool IsMindMapGroupsSectionExpanded { get; set; } = true;

    public bool IsMindMapDashboardGroupsSectionVisible { get; set; } = true;

    public bool IsMindMapDashboardUngroupedSectionVisible { get; set; } = true;

    public bool IsMindMapDashboardGroupsSectionExpanded { get; set; } = true;

    public bool IsMindMapDashboardUngroupedSectionExpanded { get; set; } = true;

    public bool IsMindMapDashboardGroupsFirst { get; set; } = true;

    public bool IsQuizGroupsSectionVisible { get; set; } = true;

    public bool IsQuizGroupsSectionExpanded { get; set; } = true;

    public bool IsQuizDashboardGroupsSectionVisible { get; set; } = true;

    public bool IsQuizDashboardUngroupedSectionVisible { get; set; } = true;

    public bool IsQuizDashboardGroupsSectionExpanded { get; set; } = true;

    public bool IsQuizDashboardUngroupedSectionExpanded { get; set; } = true;

    public bool IsQuizDashboardGroupsFirst { get; set; } = true;

    public bool IsMindMapGroupsSectionVisible { get; set; } = true;

    public bool IsCalendarFirst { get; set; } = true;

    public string MindMapSortOptionKey { get; set; } = "last-modified-desc";

    public string FlashcardSortOptionKey { get; set; } = "last-modified-desc";

    public string QuizSortOptionKey { get; set; } = "last-modified-desc";

    public string DefaultViewMode { get; set; } = "Grid";

    public int RecentItemsLimit { get; set; } = 20;

    /// <summary>Most-recent color picker choices as #RRGGBB hex (newest first).</summary>
    public List<string> RecentColors { get; set; } = new();

    public bool IsGroupsFirst { get; set; } = true;

    public string PreferredFontFamily { get; set; } = "Calibri";

    public double PreferredFontSize { get; set; } = 14;

    public bool IsEditorFontPanelOpen { get; set; } = false;

    public bool IsEditorWordWrapEnabled { get; set; } = true;

    /// <summary>Master on/off switch for the note editor's spell checker - on by default, matching
    /// the app's own initial hardcoded behavior before this became a setting. Which single
    /// language is actually checked against is SpellCheckLanguageKey.</summary>
    public bool EnableSpellCheck { get; set; } = true;

    /// <summary>The one active spell-check language, matching Word/LibreOffice's model of a single
    /// proofing language rather than checking against several dictionaries at once - empty means
    /// no language is selected yet. Key into SpellCheckDictionaryService's catalog (e.g. "en_US").</summary>
    public string SpellCheckLanguageKey { get; set; } = string.Empty;

    /// <summary>Words added via the spell-check right-click menu's "Add to Dictionary" - excluded
    /// from misspelling checks app-wide regardless of which languages are enabled.</summary>
    public List<string> SpellCheckCustomWords { get; set; } = new();

    public double PreferredLineSpacing { get; set; } = 1.0;

    public double PreferredIndentSize { get; set; } = 48.0;

    public string FlashcardModelKey { get; set; } = "AutoSelect";
    public List<AiToolSettingsItem> AiTools { get; set; } = new();
    public string? LastView { get; set; }

    public const int MinFlashcardFlipDelayMilliseconds = 100;
    public const int MaxFlashcardFlipDelayMilliseconds = 1000;
    public const int FlashcardFlipDelayStepMilliseconds = 100;
    public const int DefaultFlashcardFlipDelayMilliseconds = 400;

    public int FlashcardFlipDelayMilliseconds { get; set; } = DefaultFlashcardFlipDelayMilliseconds;

    public const int MinFlashcardAudioVolumePercent = 0;
    public const int MaxFlashcardAudioVolumePercent = 100;
    public const int DefaultFlashcardAudioVolumePercent = 100;

    /// <summary>A single, remembered playback volume for flashcard audio - not per-clip, so it's
    /// set once rather than fiddled with on every short clip you play.</summary>
    public int FlashcardAudioVolumePercent { get; set; } = DefaultFlashcardAudioVolumePercent;

    /// <summary>Which spaced-repetition engine schedules every flashcard set - a single collection-wide
    /// choice, matching Anki's own FSRS toggle ("shared by all presets", not per-deck).</summary>
    public SpacedRepetitionAlgorithm FlashcardSchedulingAlgorithm { get; set; } = SpacedRepetitionAlgorithm.Sm2;

    public static int NormalizeFlashcardFlipDelayMilliseconds(int milliseconds)
    {
        // 0 / missing JSON values must not clamp to Min — treat as default.
        if (milliseconds <= 0)
            return DefaultFlashcardFlipDelayMilliseconds;

        var clamped = Math.Clamp(milliseconds, MinFlashcardFlipDelayMilliseconds, MaxFlashcardFlipDelayMilliseconds);
        var steps = (int)Math.Round(clamped / (double)FlashcardFlipDelayStepMilliseconds);
        var snapped = steps * FlashcardFlipDelayStepMilliseconds;
        return Math.Clamp(snapped, MinFlashcardFlipDelayMilliseconds, MaxFlashcardFlipDelayMilliseconds);
    }

    // Activity Tracking
    public long TotalTimeSpentSeconds { get; set; } = 0;
    public long TotalWordsTyped { get; set; } = 0;
    public long TotalCharactersTyped { get; set; } = 0;
    public DateTime? LastActiveDate { get; set; }

    public const int MinRecentItemsLimit = 1;
    public const int MaxRecentItemsLimit = 50;
    public const int DefaultRecentItemsLimit = 20;

    public static string NormalizeMeasurementUnitSystem(string? unitSystem)
        => string.Equals(unitSystem, UnitSystemMetric, StringComparison.OrdinalIgnoreCase)
            ? UnitSystemMetric
            : UnitSystemImperial;

    public static int NormalizeRecentItemsLimit(int limit)
        => Math.Clamp(limit, MinRecentItemsLimit, MaxRecentItemsLimit);
}

public class AiToolSettingsItem
{
    public string Key { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public bool IsRemoved { get; set; }
}
