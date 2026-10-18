using Scriblucent.Localization;
using Scriblucent.Animations;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace Scriblucent.Views;

/// <summary>
/// Log-free flashcard statistics - a read-only snapshot computed once at construction from each
/// card's current stored state (see FlashcardStatisticsCalculator), never from a review history
/// this app doesn't keep. Two scopes mirror FlashcardStudyHistoryWindow's existing pattern: one
/// set, or every set.
/// </summary>
public partial class FlashcardStatisticsWindow : Window
{
    private const double StatBarTrackFallbackWidth = 280;

    public sealed class StatBarRow
    {
        public string Label { get; init; } = string.Empty;
        public int Count { get; init; }
        public double Ratio { get; init; }
        public string CountDescription => $"{Label}: {Count}";
        public string ChipKind { get; init; } = "Neutral";
    }

    public string HeaderSubtitleText { get; private set; } = string.Empty;
    public string TotalCountText { get; private set; } = "0";
    public string DueTodayCountText { get; private set; } = "0";
    public string NewCountText { get; private set; } = "0";
    public string LearningCountText { get; private set; } = "0";
    public string MatureCountText { get; private set; } = "0";
    public string DifficultySignalTitleText { get; private set; } = string.Empty;

    public IReadOnlyList<StatBarRow> MaturityRows { get; private set; } = Array.Empty<StatBarRow>();
    public IReadOnlyList<StatBarRow> ForecastRows { get; private set; } = Array.Empty<StatBarRow>();
    public IReadOnlyList<StatBarRow> IntervalRows { get; private set; } = Array.Empty<StatBarRow>();
    public IReadOnlyList<StatBarRow> DifficultySignalRows { get; private set; } = Array.Empty<StatBarRow>();

    /// <summary>Statistics across every flashcard set.</summary>
    public FlashcardStatisticsWindow(MainViewModel mainViewModel)
    {
        InitializeComponent();
        Scriblucent.Services.WindowThemeService.Register(this);
        Scriblucent.Services.FeatureWindowBounds.Attach(this);

        var cards = mainViewModel.FlashcardSets.SelectMany(set => set.Document.Cards ?? new List<FlashcardItem>());
        Initialize(cards, LocalizationService.GetString("PremiumAllFlashcardSets"));

        WindowContentEnterHelper.AnimateOnLoaded(this);
    }

    /// <summary>Statistics for a single flashcard set.</summary>
    public FlashcardStatisticsWindow(Func<FlashcardSetDocument> documentProvider)
    {
        InitializeComponent();
        Scriblucent.Services.WindowThemeService.Register(this);
        Scriblucent.Services.FeatureWindowBounds.Attach(this);

        var document = documentProvider();
        Initialize(document.Cards ?? new List<FlashcardItem>(), document.Title);

        WindowContentEnterHelper.AnimateOnLoaded(this);
    }

    private void Initialize(IEnumerable<FlashcardItem> cards, string scopeTitle)
    {
        var algorithm = AppSettingsService.Load().FlashcardSchedulingAlgorithm;
        var snapshot = FlashcardStatisticsCalculator.Calculate(cards, algorithm, DateTime.Now);

        HeaderSubtitleText = scopeTitle;
        TotalCountText = snapshot.TotalCount.ToString();
        DueTodayCountText = snapshot.DueTodayCount.ToString();
        NewCountText = snapshot.NewCount.ToString();
        LearningCountText = snapshot.LearningCount.ToString();
        MatureCountText = snapshot.MatureCount.ToString();
        DifficultySignalTitleText = LocalizationService.GetString(snapshot.DifficultySignalLabel == "Difficulty"
            ? "PremiumDifficulty" : "PremiumEaseFactor");

        var maturityMax = Math.Max(snapshot.NewCount, Math.Max(snapshot.LearningCount, snapshot.MatureCount));
        MaturityRows = new List<StatBarRow>
        {
            BuildRow(LocalizationService.GetString("PremiumNew"), snapshot.NewCount, maturityMax, "New"),
            BuildRow(LocalizationService.GetString("PremiumLearning"), snapshot.LearningCount, maturityMax, "Learning"),
            BuildRow(LocalizationService.GetString("PremiumMature"), snapshot.MatureCount, maturityMax, "Mature")
        };

        ForecastRows = BuildRows(snapshot.Forecast.Select((day, index) => (index switch
        {
            0 => LocalizationService.GetString("PremiumToday"),
            1 => LocalizationService.GetString("PremiumTomorrow"),
            _ => day.Date.ToString("ddd", System.Globalization.CultureInfo.CurrentCulture)
        }, day.Count)));
        IntervalRows = BuildRows(snapshot.IntervalBuckets.Select(bucket => (bucket.Label, bucket.Count)));
        DifficultySignalRows = BuildRows(snapshot.DifficultySignalBuckets.Select(bucket => (bucket.Label, bucket.Count)));

        DataContext = this;
    }

    private static List<StatBarRow> BuildRows(IEnumerable<(string Label, int Count)> items)
    {
        var list = items.ToList();
        var maxCount = list.Count == 0 ? 0 : list.Max(item => item.Count);
        return list.Select(item => BuildRow(item.Label, item.Count, maxCount)).ToList();
    }

    private static StatBarRow BuildRow(string label, int count, int maxCount, string chipKind = "Neutral")
    {
        var ratio = maxCount > 0 ? Math.Clamp((double)count / maxCount, 0, 1) : 0;
        return new StatBarRow { Label = label, Count = count, Ratio = ratio, ChipKind = chipKind };
    }

    /// <summary>Fires exactly once per row - ItemsSource is set once at construction and never
    /// rebound, so there's no re-entrancy/re-animate-on-scroll risk here (unlike a virtualized,
    /// recycled list).</summary>
    private void StatBarTrack_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border { Child: Border fill, DataContext: StatBarRow row } track)
            return;

        var trackWidth = track.ActualWidth > 1 ? track.ActualWidth : StatBarTrackFallbackWidth;
        UiMotion.EnsureScale(fill).ScaleX = 0;
        UiMotion.AnimateProgress(fill, trackWidth, row.Ratio);
        track.SizeChanged -= StatBarTrack_SizeChanged;
        track.SizeChanged += StatBarTrack_SizeChanged;
    }

    private void StatBarTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Border { Child: Border fill, DataContext: StatBarRow row } track)
            UiMotion.AnimateProgress(fill, track.ActualWidth, row.Ratio, animate: false);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
