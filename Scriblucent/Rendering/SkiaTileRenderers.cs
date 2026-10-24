using SkiaSharp;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.ViewModels;
using Scriblucent.Views;

namespace Scriblucent.Rendering;

/// <summary>
/// Rasterizes one dashboard card kind into an SKBitmap off the UI thread (see
/// SkiaCardTileCache). Every dashboard card kind has one renderer so the scroll-tile
/// architecture is uniform across dashboards.
/// </summary>
internal abstract class SkiaTileRenderer
{
    /// <param name="widthDip">Host width for fluid-width rows (window lists); fixed-size
    /// renderers ignore it and compute their own footprint.</param>
    public abstract SKBitmap Render(object viewModel, double dpiScale, float widthDip, out double heightDip);
    public abstract SKBitmap RenderPlaceholder(double dpiScale);
    public abstract string BuildSignature(object viewModel);
}

/// <summary>Registry of per-kind tile renderers, keyed by the DashboardTileHost.RendererKey.</summary>
internal static class SkiaTileRenderers
{
    public const string KeyNotes = "Notes";
    public const string KeyFlashcardSet = "FlashcardSet";
    public const string KeyFlashcardSetUngrouped = "FlashcardSetUngrouped";
    public const string KeyQuiz = "Quiz";
    public const string KeyQuizUngrouped = "QuizUngrouped";
    public const string KeyMindMap = "MindMap";
    public const string KeyMindMapUngrouped = "MindMapUngrouped";
    public const string KeyRecentNotes = "RecentNotes";
    public const string KeyRecentFlashcardSet = "RecentFlashcardSet";
    public const string KeyRecentQuiz = "RecentQuiz";
    public const string KeyRecentMindMap = "RecentMindMap";
    public const string KeyCalendarItem = "CalendarItem";
    public const string KeyTrashRow = "TrashRow";
    public const string KeyQuizLibraryRow = "QuizLibraryRow";
    public const string KeyCombineRow = "CombineRow";
    public const string KeyQuizQuestion = "QuizQuestion";
    public const string KeyQuizResultRow = "QuizResultRow";
    public const string KeyStudyHistoryRow = "StudyHistoryRow";
    public const string KeyAttemptHistoryRow = "AttemptHistoryRow";
    public const string KeyFlashcardGridCard = "FlashcardGridCard";

    private static string FormatDate(DateTime dt) => dt.ToString("dd MMM, HH:mm", System.Globalization.CultureInfo.CurrentCulture);

    public static readonly SkiaTileRenderer Notes = new NotesTileRenderer();

    public static readonly SkiaTileRenderer FlashcardGridCard = new FlashcardGridCardRenderer();


    public static readonly SkiaTileRenderer FlashcardSet = new ListingTileRenderer(
        new ListingCardSpec
        {
            Key = KeyFlashcardSet,
            BitmapWidthDip = 240,
            BitmapHeightDip = 128,
            CardWidthDip = 236,
            CardHeightDip = 112,
            PaddingDip = 12,
            Scale = 1f,
            Title = vm => ((FlashcardSetViewModel)vm).Title,
            CountLine = vm => ((FlashcardSetViewModel)vm).CardCountText,
            SecondaryCountLine = null,
            HasDueChip = vm => ((FlashcardSetViewModel)vm).HasDueCards,
            DueChipText = vm => ((FlashcardSetViewModel)vm).DueStatusText ?? string.Empty,
            ShowNextReview = vm => ((FlashcardSetViewModel)vm).ShowNextReviewText,
            IsAi = vm => ((FlashcardSetViewModel)vm).ShowAiGeneratedChrome,
            IsPinned = vm => ((FlashcardSetViewModel)vm).IsPinned,
            HasSchedule = vm => ((FlashcardSetViewModel)vm).HasSchedule,
            Tags = vm => ((FlashcardSetViewModel)vm).TagsDisplay,
        });

    public static readonly SkiaTileRenderer Quiz = new ListingTileRenderer(
        new ListingCardSpec
        {
            Key = KeyQuiz,
            BitmapWidthDip = 240,
            BitmapHeightDip = 128,
            CardWidthDip = 236,
            CardHeightDip = 112,
            PaddingDip = 12,
            Scale = 1f,
            Title = vm => ((QuizViewModel)vm).Title,
            CountLine = vm => ((QuizViewModel)vm).QuestionCountText,
            SecondaryCountLine = null,
            HasDueChip = _ => false,
            DueChipText = _ => string.Empty,
            ShowNextReview = _ => false,
            IsAi = vm => ((QuizViewModel)vm).ShowAiGeneratedChrome,
            IsPinned = vm => ((QuizViewModel)vm).IsPinned,
            HasSchedule = vm => ((QuizViewModel)vm).HasSchedule,
            Tags = vm => ((QuizViewModel)vm).TagsDisplay,
        });

    public static readonly SkiaTileRenderer MindMap = new ListingTileRenderer(
        new ListingCardSpec
        {
            Key = KeyMindMap,
            BitmapWidthDip = 240,
            BitmapHeightDip = 152,
            CardWidthDip = 236,
            CardHeightDip = 136,
            PaddingDip = 12,
            Scale = 1f,
            Title = vm => ((MindMapViewModel)vm).Title,
            CountLine = vm => ((MindMapViewModel)vm).NodeCountText,
            SecondaryCountLine = null,
            HasDueChip = _ => false,
            DueChipText = _ => string.Empty,
            ShowNextReview = _ => false,
            IsAi = vm => ((MindMapViewModel)vm).ShowAiGeneratedChrome,
            IsPinned = vm => ((MindMapViewModel)vm).IsPinned,
            HasSchedule = vm => ((MindMapViewModel)vm).HasSchedule,
            Tags = vm => ((MindMapViewModel)vm).TagsDisplay,
        });

    public static readonly SkiaTileRenderer FlashcardSetUngrouped = new ListingTileRenderer(
        new ListingCardSpec
        {
            Key = KeyFlashcardSetUngrouped,
            BitmapWidthDip = 330,
            BitmapHeightDip = 204,
            CardWidthDip = 320,
            CardHeightDip = 184,
            PaddingDip = 16,
            Scale = 1f,
            TitleSize = 16f,
            DetailSize = 14f,
            BadgeSize = 12f,
            Title = vm => ((FlashcardSetViewModel)vm).Title,
            CountLine = vm => ((FlashcardSetViewModel)vm).CardCountText,
            SecondaryCountLine = vm => ((FlashcardSetViewModel)vm).SetCountText,
            HasDueChip = vm => ((FlashcardSetViewModel)vm).HasDueCards,
            DueChipText = vm => ((FlashcardSetViewModel)vm).DueStatusText ?? string.Empty,
            ShowNextReview = vm => ((FlashcardSetViewModel)vm).ShowNextReviewText,
            IsAi = vm => ((FlashcardSetViewModel)vm).ShowAiGeneratedChrome,
            IsPinned = vm => ((FlashcardSetViewModel)vm).IsPinned,
            HasSchedule = vm => ((FlashcardSetViewModel)vm).HasSchedule,
            Tags = vm => ((FlashcardSetViewModel)vm).TagsDisplay,
        });

    public static readonly SkiaTileRenderer QuizUngrouped = new ListingTileRenderer(
        new ListingCardSpec
        {
            Key = KeyQuizUngrouped,
            BitmapWidthDip = 330,
            BitmapHeightDip = 194,
            CardWidthDip = 320,
            CardHeightDip = 174,
            PaddingDip = 16,
            Scale = 1f,
            TitleSize = 16f,
            DetailSize = 14f,
            BadgeSize = 12f,
            Title = vm => ((QuizViewModel)vm).Title,
            CountLine = vm => ((QuizViewModel)vm).QuestionCountText,
            SecondaryCountLine = null,
            HasDueChip = _ => false,
            DueChipText = _ => string.Empty,
            ShowNextReview = _ => false,
            IsAi = vm => ((QuizViewModel)vm).ShowAiGeneratedChrome,
            IsPinned = vm => ((QuizViewModel)vm).IsPinned,
            HasSchedule = vm => ((QuizViewModel)vm).HasSchedule,
            Tags = vm => ((QuizViewModel)vm).TagsDisplay,
        });

    public static readonly SkiaTileRenderer MindMapUngrouped = new ListingTileRenderer(
        new ListingCardSpec
        {
            Key = KeyMindMapUngrouped,
            BitmapWidthDip = 360,
            BitmapHeightDip = 224,
            CardWidthDip = 350,
            CardHeightDip = 204,
            PaddingDip = 16,
            Scale = 1f,
            TitleSize = 16f,
            DetailSize = 14f,
            BadgeSize = 12f,
            Title = vm => ((MindMapViewModel)vm).Title,
            CountLine = vm => ((MindMapViewModel)vm).NodeCountText,
            SecondaryCountLine = null,
            HasDueChip = _ => false,
            DueChipText = _ => string.Empty,
            ShowNextReview = _ => false,
            IsAi = vm => ((MindMapViewModel)vm).ShowAiGeneratedChrome,
            IsPinned = vm => ((MindMapViewModel)vm).IsPinned,
            HasSchedule = vm => ((MindMapViewModel)vm).HasSchedule,
            Tags = vm => ((MindMapViewModel)vm).TagsDisplay,
        });

    public static readonly SkiaTileRenderer RecentNotes = new RecentCardRenderer(
        new RecentCardSpec
        {
            Key = KeyRecentNotes,
            TitleSize = 14.5f,
            Title = vm => ((NoteCardViewModel)vm).Document?.Title ?? ((NoteCardViewModel)vm).Title ?? string.Empty,
            Secondary = null,
            Date = vm => FormatDate(((NoteCardViewModel)vm).Document?.LastModified ?? DateTime.MinValue),
            DateSecondaryColor = false,
        });

    public static readonly SkiaTileRenderer RecentFlashcardSet = new RecentCardRenderer(
        new RecentCardSpec
        {
            Key = KeyRecentFlashcardSet,
            TitleSize = 13f,
            Title = vm => ((FlashcardSetViewModel)vm).Title,
            Secondary = vm => ((FlashcardSetViewModel)vm).CardCountText,
            Date = vm => FormatDate(((FlashcardSetViewModel)vm).Document.LastModified),
            DateSecondaryColor = true,
        });

    public static readonly SkiaTileRenderer RecentQuiz = new RecentCardRenderer(
        new RecentCardSpec
        {
            Key = KeyRecentQuiz,
            TitleSize = 13f,
            Title = vm => ((QuizViewModel)vm).Title,
            Secondary = vm => ((QuizViewModel)vm).QuestionCountText,
            Date = vm => FormatDate(((QuizViewModel)vm).Document.LastModified),
            DateSecondaryColor = true,
        });

    public static readonly SkiaTileRenderer RecentMindMap = new RecentCardRenderer(
        new RecentCardSpec
        {
            Key = KeyRecentMindMap,
            TitleSize = 13f,
            Title = vm => ((MindMapViewModel)vm).Title,
            Secondary = vm => ((MindMapViewModel)vm).NodeCountText,
            Date = vm => FormatDate(((MindMapViewModel)vm).Document.LastModified),
            DateSecondaryColor = true,
        });

    private static readonly SkiaTileRenderer CalendarItem = new FluidRowRenderer(
        new FluidRowSpec
        {
            Key = KeyCalendarItem,
            CardBackgroundKey = "CardBackground",
            CardBorderKey = "NoteCardBorder",
            RadiusDip = 14,
            PaddingDip = 11,
            MarginBottomDip = 8,
            TitleSize = 14f,
            Title = vm => ((CalendarScheduledItemViewModel)vm).Title,
            Lines =
            [
                (vm => ((CalendarScheduledItemViewModel)vm).ScheduleNote, 13f, false),
            ],
            IsVisible = _ => true,
            Chip = null,
            Checkbox = null,
            LeftGlyph = _ => "file",
            RightText = vm => ((CalendarScheduledItemViewModel)vm).TimeText,
            RightTextSemiBold = true,
            ShowActionPills = false,
            Question = null,
            Options = null,
            ResultText = null,
            BorderColorKey = "Transparent",
            BorderThicknessDip = 0,
        });

    private static readonly SkiaTileRenderer TrashRow = new FluidRowRenderer(
        new FluidRowSpec
        {
            Key = KeyTrashRow,
            CardBackgroundKey = "InputSurface",
            CardBorderKey = "InputRestBorder",
            RadiusDip = 14,
            PaddingDip = 12,
            MarginBottomDip = 8,
            TitleSize = 14f,
            Title = vm => ((TrashItemViewModel)vm).Title,
            Lines =
            [
                (vm => string.Join("  ·  ", ((TrashItemViewModel)vm).KindDisplayName, ((TrashItemViewModel)vm).DeletedAtDisplay, ((TrashItemViewModel)vm).DaysLeftDisplay), 12f, false),
            ],
            IsVisible = _ => true,
            Chip = null,
            Checkbox = null,
            LeftGlyph = _ => "kind",
            RightText = null,
            RightTextSemiBold = false,
            ShowActionPills = true,
            Question = null,
            Options = null,
            ResultText = null,
            BorderColorKey = "Transparent",
            BorderThicknessDip = 0,
        });

    private static readonly SkiaTileRenderer QuizLibraryRow = new FluidRowRenderer(
        new FluidRowSpec
        {
            Key = KeyQuizLibraryRow,
            SelectedSurface = vm => ((QuizLibraryItemViewModel)vm).IsSelected,
            SelectionThicknessDip = 2,
            CardBackgroundKey = "CardBackground",
            CardBorderKey = "NoteCardBorder",
            RadiusDip = 10,
            PaddingDip = 12,
            MarginBottomDip = 8,
            TitleSize = 14f,
            Title = vm => ((QuizLibraryItemViewModel)vm).Title,
            Lines =
            [
                (vm => ((QuizLibraryItemViewModel)vm).ItemCountText, 13f, false),
                (vm => ((QuizLibraryItemViewModel)vm).TagsDisplay, 13f, false),
            ],
            IsVisible = vm => ((QuizLibraryItemViewModel)vm).IsVisible,
            Chip = null,
            Checkbox = vm => ((QuizLibraryItemViewModel)vm).IsSelected,
            LeftGlyph = null,
            RightText = null,
            RightTextSemiBold = false,
            ShowActionPills = false,
            Question = null,
            Options = null,
            ResultText = null,
            BorderColorKey = "Transparent",
            BorderThicknessDip = 0,
        });

    private static readonly SkiaTileRenderer CombineRow = new FluidRowRenderer(
        new FluidRowSpec
        {
            Key = KeyCombineRow,
            SelectedSurface = vm => ((FlashcardCombineWindow.FlashcardCombineItem)vm).IsSelected,
            CardBackgroundKey = "CardBackground",
            CardBorderKey = "NoteCardBorder",
            RadiusDip = 10,
            PaddingDip = 12,
            MarginBottomDip = 8,
            TitleSize = 14f,
            Title = vm => ((FlashcardCombineWindow.FlashcardCombineItem)vm).Title,
            Lines =
            [
                (vm => ((FlashcardCombineWindow.FlashcardCombineItem)vm).ItemCountText, 13f, false),
                (vm => ((FlashcardCombineWindow.FlashcardCombineItem)vm).TagsDisplay, 13f, false),
            ],
            IsVisible = _ => true,
            Chip = null,
            Checkbox = vm => ((FlashcardCombineWindow.FlashcardCombineItem)vm).IsSelected,
            LeftGlyph = null,
            RightText = null,
            RightTextSemiBold = false,
            ShowActionPills = false,
            Question = null,
            Options = null,
            ResultText = null,
            BorderColorKey = "Transparent",
            BorderThicknessDip = 0,
        });

    private static readonly SkiaTileRenderer QuizQuestion = new FluidRowRenderer(
        new FluidRowSpec
        {
            Key = KeyQuizQuestion,
            SelectedSurface = vm => ((QuizPreviewWindow.QuizPreviewQuestion)vm).IsFocused,
            TintSelectedSurface = false,
            CardBackgroundKey = "CardBackground",
            CardBorderKey = "Transparent",
            RadiusDip = 14,
            PaddingDip = 16,
            MarginBottomDip = 0,
            TitleSize = 16f,
            Title = vm => ((QuizPreviewWindow.QuizPreviewQuestion)vm).Question,
            Lines = [],
            IsVisible = _ => true,
            Chip = null,
            Checkbox = null,
            LeftGlyph = null,
            RightText = null,
            RightTextSemiBold = false,
            ShowActionPills = false,
            Question = null,
            Options = vm => ((QuizPreviewWindow.QuizPreviewQuestion)vm).Options.Select(o => o.Text).ToList(),
            ResultText = vm => ((QuizPreviewWindow.QuizPreviewQuestion)vm).ResultText,
            BorderColorKey = "Transparent",
            BorderThicknessDip = 0,
        });

    private static readonly SkiaTileRenderer QuizResultRow = new QuizResultCardRenderer();

    private static readonly SkiaTileRenderer StudyHistoryRow = new FluidRowRenderer(
        new FluidRowSpec
        {
            Key = KeyStudyHistoryRow,
            HistoryLayout = true,
            CardBackgroundKey = "CardBackground",
            CardBorderKey = "Transparent",
            RadiusDip = 14,
            PaddingDip = 16,
            MarginBottomDip = 10,
            TitleSize = 15f,
            Title = vm => ((FlashcardStudyHistoryWindow.StudyDisplayItem)vm).Title,
            Lines =
            [
                (vm => ((FlashcardStudyHistoryWindow.StudyDisplayItem)vm).Details, 13f, false),
            ],
            IsVisible = _ => true,
            Chip = vm => (((FlashcardStudyHistoryWindow.StudyDisplayItem)vm).StatusText, StudyStatusBgKey(((FlashcardStudyHistoryWindow.StudyDisplayItem)vm).StatusKind), StudyStatusFgKey(((FlashcardStudyHistoryWindow.StudyDisplayItem)vm).StatusKind)),
            Checkbox = null,
            LeftGlyph = null,
            RightText = null,
            RightTextSemiBold = false,
            ShowActionPills = false,
            Question = null,
            Options = null,
            ResultText = null,
            BorderColorKey = "Transparent",
            BorderThicknessDip = 0,
        });

    private static readonly SkiaTileRenderer AttemptHistoryRow = new FluidRowRenderer(
        new FluidRowSpec
        {
            Key = KeyAttemptHistoryRow,
            HistoryLayout = true,
            CardBackgroundKey = "CardBackground",
            CardBorderKey = "Transparent",
            RadiusDip = 14,
            PaddingDip = 16,
            MarginBottomDip = 10,
            TitleSize = 15f,
            Title = vm => ((QuizAttemptHistoryWindow.AttemptDisplayItem)vm).QuizTitle,
            Lines =
            [
                (vm => string.Join("  ·  ", ((QuizAttemptHistoryWindow.AttemptDisplayItem)vm).DateDisplay, ((QuizAttemptHistoryWindow.AttemptDisplayItem)vm).ScoreDisplay, ((QuizAttemptHistoryWindow.AttemptDisplayItem)vm).TimeDisplay), 13f, false),
            ],
            IsVisible = _ => true,
            Chip = vm => (((QuizAttemptHistoryWindow.AttemptDisplayItem)vm).StatusText, ((QuizAttemptHistoryWindow.AttemptDisplayItem)vm).Passed ? "AccentSuccessSubtle" : "AccentDangerSubtle", ((QuizAttemptHistoryWindow.AttemptDisplayItem)vm).Passed ? "AccentSuccessSubtleForeground" : "AccentDangerSubtleForeground"),
            Checkbox = null,
            LeftGlyph = null,
            RightText = null,
            RightTextSemiBold = false,
            ShowActionPills = false,
            Question = null,
            Options = null,
            ResultText = null,
            BorderColorKey = "Transparent",
            BorderThicknessDip = 0,
        });

    private static string StudyStatusBgKey(string statusKind) => statusKind switch
    {
        "Known" or "Complete" => "FlashcardKnownChipBackground",
        "Unknown" => "FlashcardUnknownChipBackground",
        _ => "FlashcardNewChipBackground",
    };

    private static string StudyStatusFgKey(string statusKind) => statusKind switch
    {
        "Known" or "Complete" => "FlashcardKnownChipForeground",
        "Unknown" => "FlashcardUnknownChipForeground",
        _ => "FlashcardNewChipForeground",
    };

    public static SkiaTileRenderer? ForKey(string key) => key switch
    {
        KeyFlashcardGridCard => FlashcardGridCard,
        KeyNotes => Notes,
        KeyFlashcardSet => FlashcardSet,
        KeyFlashcardSetUngrouped => FlashcardSetUngrouped,
        KeyQuiz => Quiz,
        KeyQuizUngrouped => QuizUngrouped,
        KeyMindMap => MindMap,
        KeyMindMapUngrouped => MindMapUngrouped,
        KeyRecentNotes => RecentNotes,
        KeyRecentFlashcardSet => RecentFlashcardSet,
        KeyRecentQuiz => RecentQuiz,
        KeyRecentMindMap => RecentMindMap,
        KeyCalendarItem => CalendarItem,
        KeyTrashRow => TrashRow,
        KeyQuizLibraryRow => QuizLibraryRow,
        KeyCombineRow => CombineRow,
        KeyQuizQuestion => QuizQuestion,
        KeyQuizResultRow => QuizResultRow,
        KeyStudyHistoryRow => StudyHistoryRow,
        KeyAttemptHistoryRow => AttemptHistoryRow,
        _ => null,
    };
}

/// <summary>Tile renderer for the notes dashboard cards (delegates to SkiaCardRenderer).</summary>
internal sealed class NotesTileRenderer : SkiaTileRenderer
{
    private CardRenderColors _colors;
    private string _colorsKey = string.Empty;

    public override SKBitmap Render(object viewModel, double dpiScale, float widthDip, out double heightDip)
        => SkiaCardRenderer.Render((NoteCardViewModel)viewModel, dpiScale, ResolveColors(), out heightDip);

    public override SKBitmap RenderPlaceholder(double dpiScale)
    {
        var scale = (float)dpiScale;
        var colors = ResolveColors();
        var width = (int)Math.Ceiling((SkiaCardRenderer.CardWidthDip + SkiaCardRenderer.CardMarginDip * 2) * scale);
        var height = (int)Math.Ceiling((SkiaCardRenderer.MinCardHeightDip + SkiaCardRenderer.CardMarginDip * 2) * scale);
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        var rect = SKRect.Create(
            SkiaCardRenderer.CardMarginDip * scale,
            SkiaCardRenderer.CardMarginDip * scale,
            SkiaCardRenderer.CardWidthDip * scale,
            SkiaCardRenderer.MinCardHeightDip * scale);
        using var paint = new SKPaint { Color = colors.Background, IsAntialias = true };
        var radius = SkiaCardRenderer.CardCornerRadiusDip * scale;
        canvas.DrawRoundRect(rect, radius, radius, paint);
        return bitmap;
    }

    public override string BuildSignature(object viewModel)
    {
        var vm = (NoteCardViewModel)viewModel;
        var doc = vm.Document;
        // Document fields and the generation counter only. Content parses a FlowDocument and
        // must not run on the scroll path that asks for this signature.
        return $"{ThemeManager.CurrentTheme}|surface-v2|preview-v1|{doc.Title}|{doc.LastModified.Ticks}|{vm.ContentGeneration}|{vm.TagsDisplay}|{doc.IsPinned}|{vm.HasSchedule}";
    }

    private CardRenderColors ResolveColors()
    {
        var theme = (ThemeManager.CurrentTheme ?? string.Empty) + "|surface-v2|" + SurfaceMaterialRaster.ResourceDark;
        if (_colorsKey == theme)
            return _colors;

        System.Windows.Media.Color Get(string key, System.Windows.Media.Color fallback)
        {
            return DashboardSelectionPaint.ResolveColor(key, fallback);
        }

        var background = Get("NoteCardBackground", System.Windows.Media.Colors.White);
        var border = Get("NoteCardBorder", System.Windows.Media.Colors.Transparent);
        var title = Get("NoteCardTitle", System.Windows.Media.Color.FromRgb(28, 28, 28));
        var text = Get("TextColor", System.Windows.Media.Color.FromRgb(60, 60, 60));
        var tagBackground = Get("TagChipBackground", System.Windows.Media.Color.FromRgb(245, 247, 251));
        var tagText = Get("TagChipForeground", System.Windows.Media.Color.FromRgb(74, 93, 128));

        _colors = new CardRenderColors(
            ToSkia(background),
            ToSkia(border),
            ToSkia(title),
            ToSkia(text),
            ToSkia(tagBackground),
            ToSkia(tagText), Dark: SurfaceMaterialRaster.ResourceDark);
        _colorsKey = theme;
        return _colors;
    }

    private static SKColor ToSkia(System.Windows.Media.Color c)
        => new(c.R, c.G, c.B, c.A);
}

/// <summary>Shared layout spec for the flashcard-set / quiz / mind-map listing cards.</summary>
internal sealed class ListingCardSpec
{
    public float TitleSize = 14.5f;
    public float DetailSize = 12f;
    public float BadgeSize = 11f;
    public required string Key;
    public required float BitmapWidthDip;
    public required float BitmapHeightDip;
    public required float CardWidthDip;
    public required float CardHeightDip;
    public required float PaddingDip;
    public required float Scale;
    public required Func<object, string> Title;
    public required Func<object, string> CountLine;
    public required Func<object, string>? SecondaryCountLine;
    public required Func<object, bool> HasDueChip;
    public required Func<object, string> DueChipText;
    public required Func<object, bool> ShowNextReview;
    public required Func<object, bool> IsAi;
    public required Func<object, bool> IsPinned;
    public required Func<object, bool> HasSchedule;
    public required Func<object, string> Tags;
}

/// <summary>Tile renderer for the three listing card kinds (grouped + ungrouped variants).</summary>
internal sealed class ListingTileRenderer : SkiaTileRenderer
{
    private readonly ListingCardSpec _spec;
    private readonly Dictionary<string, ListingCardColors> _colorsByTheme = new();

    public ListingTileRenderer(ListingCardSpec spec)
    {
        _spec = spec;
    }

    public override SKBitmap Render(object viewModel, double dpiScale, float widthDip, out double heightDip)
    {
        var dpi = (float)dpiScale;
        var scale = dpi;
        var colors = ResolveColors();

        var width = (int)Math.Ceiling(_spec.BitmapWidthDip * dpi);
        var height = (int)Math.Ceiling(_spec.BitmapHeightDip * dpi);
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(_spec.Scale, _spec.Scale);

        // canvas.Scale(_spec.Scale) above already folds the card spec scale into every draw
        // (round rect, radius, fonts, paddings, strokes); keeping `scale` DPI-only avoids
        // double-scaling ungrouped tiles to ~25% smaller than their live card.
        var cardRect = SKRect.Create(0, 0, _spec.CardWidthDip * scale, _spec.CardHeightDip * scale);
        using (var background = new SKPaint { Color = colors.CardBackground, IsAntialias = true })
        {
            var radius = 14f * scale;
            SurfaceMaterialRaster.Draw(canvas, cardRect, radius, radius, background, colors.Dark, scale,
                SurfaceMaterialKind.Raised, frameInset: scale);
        }

        if (colors.Border.Alpha > 0)
        {
            using var border = new SKPaint
            {
                Color = colors.Border,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = scale,
            };
            var radius = 14.5f * scale;
            var rim = cardRect;
            rim.Inflate(-border.StrokeWidth / 2, -border.StrokeWidth / 2);
            canvas.DrawRoundRect(rim, Math.Max(0, radius - border.StrokeWidth / 2), Math.Max(0, radius - border.StrokeWidth / 2), border);
        }
        SurfaceMaterialRaster.LightRaisedFrame(canvas, cardRect, 14, colors.Border, colors.Dark, scale);


        var fontManager = SKFontManager.Default;
        Func<SKTypeface, SkiaSharp.HarfBuzz.SKShaper> shaperFactory = SkiaCardRenderer.GetShaper;

        var titleTypeface = SkiaCardRenderer.GetTypeface(ResolveFontFamily(), 600);
        var bodyTypeface = SkiaCardRenderer.GetTypeface(ResolveFontFamily(), 400);
        var padding = _spec.PaddingDip * scale;

        var x = padding;
        var y = padding;

        // Title row: AI badge + title (+ pin/schedule markers inline).
        var ai = _spec.IsAi(viewModel);
        var pinned = _spec.IsPinned(viewModel);
        var scheduled = _spec.HasSchedule(viewModel);
        var title = _spec.Title(viewModel);

        var titleMaxWidth = _spec.CardWidthDip * scale - padding * 2 - (ai ? 30f * scale : 0) - (pinned ? 16f * scale : 0) - (scheduled ? 15f * scale : 0) - 20f * scale;

        var titleLayout = SkiaTextLayout.Layout(title, titleMaxWidth, titleTypeface, _spec.TitleSize * scale, 1, fontManager, shaperFactory);
        var titleX = x;
        if (ai)
        {
            using var badge = new SKPaint { Color = colors.IconButtonBackground, IsAntialias = true };
            var badgeSize = 24f * scale;
            var badgeRect = SKRect.Create(x, y, badgeSize, badgeSize);
            canvas.DrawRoundRect(badgeRect, 8f * scale, 8f * scale, badge);
            // Sparkle-ish glyph inside the badge.
            using var glyph = new SKPaint { Color = colors.TextSecondary, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f * scale, StrokeCap = SKStrokeCap.Round };
            var s = badgeSize;
            canvas.DrawLine(x + s * 0.30f, y + s * 0.68f, x + s * 0.50f, y + s * 0.32f, glyph);
            canvas.DrawLine(x + s * 0.50f, y + s * 0.32f, x + s * 0.70f, y + s * 0.68f, glyph);
            canvas.DrawLine(x + s * 0.42f, y + s * 0.55f, x + s * 0.58f, y + s * 0.55f, glyph);
            titleX = x + badgeSize + 6f * scale;
        }

        SkiaCardRenderer.PaintLayout(canvas, titleLayout, titleX, y, colors.Text, scale);

        // Pin / schedule markers trailing the title.
        var markerX = titleX + Math.Min(titleLayout.MaxIntrinsicWidth, titleMaxWidth) + 6f * scale;
        if (pinned)
        {
            using var marker = new SKPaint { Color = colors.Text, IsAntialias = true, Style = SKPaintStyle.Fill };
            DrawPinGlyph(canvas, markerX, y + 3f * scale, 11f * scale, colors.Text);
            markerX += 15f * scale;
        }

        if (scheduled)
        {
            DrawScheduleGlyph(canvas, markerX, y + 3f * scale, 12f * scale, colors.Text);
        }

        y += titleLayout.Height + 6f * scale;

        // Count line (+ optional secondary count + due chip + next-review).
        var countText = _spec.CountLine(viewModel);
        var countFontSize = _spec.DetailSize;
        var countLayout = SkiaTextLayout.Layout(countText, _spec.CardWidthDip * scale - padding * 2, bodyTypeface, countFontSize * scale, 1, fontManager, shaperFactory);
        SkiaCardRenderer.PaintLayout(canvas, countLayout, x, y, colors.TextSecondary, scale);
        var countEndX = x + countLayout.MaxIntrinsicWidth;

        var secondary = _spec.SecondaryCountLine?.Invoke(viewModel);
        if (!string.IsNullOrEmpty(secondary))
        {
            var secondaryLayout = SkiaTextLayout.Layout("  •  " + secondary, _spec.CardWidthDip * scale - padding * 2 - countLayout.MaxIntrinsicWidth, bodyTypeface, countFontSize * scale, 1, fontManager, shaperFactory);
            SkiaCardRenderer.PaintLayout(canvas, secondaryLayout, countEndX, y, colors.TextSecondary, scale);
            countEndX += secondaryLayout.MaxIntrinsicWidth;
        }

        if (_spec.HasDueChip(viewModel))
        {
            var chipText = _spec.DueChipText(viewModel);
            using var chipBg = new SKPaint { Color = colors.DueChipBackground, IsAntialias = true };
            using var chipFont = new SKFont(bodyTypeface, _spec.BadgeSize * scale);
            var chipWidth = chipFont.MeasureText(chipText);
            var chipRect = SKRect.Create(countEndX + 6f * scale, y - 1f * scale, chipWidth + 12f * scale, (_spec.BadgeSize + 5f) * scale);
            SurfaceMaterialRaster.Draw(canvas, chipRect, 8f * scale, 8f * scale, chipBg, colors.Dark, scale, SurfaceMaterialKind.Tinted);
            var chipLayout = SkiaTextLayout.Layout(chipText, chipWidth + 12f * scale, bodyTypeface, _spec.BadgeSize * scale, 1, fontManager, shaperFactory);
            SkiaCardRenderer.PaintLayout(canvas, chipLayout, chipRect.Left + 6f * scale, y - 1f * scale, colors.DueChipForeground, scale);
        }
        else if (_spec.ShowNextReview(viewModel))
        {
            var next = _spec.DueChipText(viewModel);
            var nextLayout = SkiaTextLayout.Layout(next, _spec.CardWidthDip * scale - padding * 2 - countLayout.MaxIntrinsicWidth, bodyTypeface, _spec.DetailSize * scale, 1, fontManager, shaperFactory);
            SkiaCardRenderer.PaintLayout(canvas, nextLayout, countEndX + 6f * scale, y, colors.TextSecondary, scale);
        }

        y += 18f * scale + 6f * scale;

        // Tags chip.
        var tags = _spec.Tags(viewModel);
        if (tags.Length > 0)
        {
            using var tagBg = new SKPaint { Color = colors.TagBackground, IsAntialias = true };
            using var tagFont = new SKFont(bodyTypeface, 11f * scale);
            var tagWidth = tagFont.MeasureText(tags);
            var tagRect = SKRect.Create(x, y, tagWidth + 16f * scale, 22f * scale);
            SurfaceMaterialRaster.Draw(canvas, tagRect, 11f * scale, 11f * scale, tagBg, colors.Dark, scale, SurfaceMaterialKind.Tinted);
            var tagLayout = SkiaTextLayout.Layout(tags, tagWidth + 16f * scale, bodyTypeface, 11f * scale, 1, fontManager, shaperFactory);
            SkiaCardRenderer.PaintLayout(canvas, tagLayout, tagRect.Left + 8f * scale, y + 3f * scale, colors.TagForeground, scale);
        }

        heightDip = _spec.BitmapHeightDip;
        return bitmap;
    }

    public override SKBitmap RenderPlaceholder(double dpiScale)
    {
        var dpi = (float)dpiScale;
        var colors = ResolveColors();

        // The placeholder must match the full-fidelity tile (and the live card), so it applies
        // the same _spec.Scale the tile folds into its canvas; otherwise it pops in bigger.
        var scale = dpi * _spec.Scale;
        var bitmap = new SKBitmap(
            (int)Math.Ceiling(_spec.BitmapWidthDip * dpi),
            (int)Math.Ceiling(_spec.BitmapHeightDip * dpi),
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = colors.CardBackground, IsAntialias = true };
        var radius = 14f * scale;
        canvas.DrawRoundRect(SKRect.Create(0, 0, _spec.CardWidthDip * scale, _spec.CardHeightDip * scale), radius, radius, paint);
        return bitmap;
    }

    public override string BuildSignature(object viewModel)
    {
        var pinned = _spec.IsPinned(viewModel);
        var scheduled = _spec.HasSchedule(viewModel);
        var ai = _spec.IsAi(viewModel);
        var due = _spec.HasDueChip(viewModel);
        return $"{ThemeManager.CurrentTheme}|surface-v2|{_spec.Title(viewModel)}|{_spec.CountLine(viewModel)}|{_spec.Tags(viewModel)}|{pinned}|{scheduled}|{ai}|{due}";
    }

    private static void DrawPinGlyph(SKCanvas canvas, float x, float y, float size, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var path = new SKPathBuilder();
        var s = size;
        path.MoveTo(x + s * 0.5f, y);
        path.LineTo(x + s * 0.5f, y + s * 0.65f);
        path.LineTo(x + s, y + s * 0.65f);
        path.LineTo(x + s * 0.5f, y + s);
        path.LineTo(x, y + s * 0.65f);
        path.LineTo(x + s * 0.5f, y + s * 0.65f);
        path.LineTo(x + s * 0.5f, y);
        path.Close();
        canvas.DrawPath(path.Detach(), paint);
    }

    private static void DrawScheduleGlyph(SKCanvas canvas, float x, float y, float size, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var path = new SKPathBuilder();
        var s = size;
        path.MoveTo(x + s * 0.5f, y + s * 0.12f);
        path.LineTo(x + s * 0.5f, y);
        path.LineTo(x + s * 0.38f, y);
        path.LineTo(x + s * 0.38f, y + s * 0.25f);
        path.LineTo(x + s * 0.62f, y + s * 0.25f);
        path.LineTo(x + s * 0.62f, y);
        path.LineTo(x + s * 0.5f, y);
        path.Close();
        canvas.DrawPath(path.Detach(), paint);
        using var face = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f * size / 12f };
        canvas.DrawOval(new SKRect(x, y + s * 0.15f, x + s, y + s * 0.95f), face);
        using var hand = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.1f * size / 12f, StrokeCap = SKStrokeCap.Round };
        canvas.DrawLine(x + s * 0.5f, y + s * 0.45f, x + s * 0.68f, y + s * 0.62f, hand);
    }

    private string ResolveFontFamily()
    {
        try
        {
            var app = System.Windows.Application.Current;
            var family = app?.Resources["FontFamily.UI"] as System.Windows.Media.FontFamily;
            var source = family?.Source ?? "Segoe UI Variable Text";
            return source.Split(',')[0].Trim();
        }
        catch
        {
            return "Segoe UI";
        }
    }

    private ListingCardColors ResolveColors()
    {
        var theme = (ThemeManager.CurrentTheme ?? string.Empty) + "|surface-v2|" + SurfaceMaterialRaster.ResourceDark;
        if (_colorsByTheme.TryGetValue(theme, out var cached))
            return cached;

        System.Windows.Media.Color Get(string key, System.Windows.Media.Color fallback)
        {
            return DashboardSelectionPaint.ResolveColor(key, fallback);
        }

        var colors = new ListingCardColors
        {
            Dark = SurfaceMaterialRaster.ResourceDark,
            CardBackground = ToSkia(Get("CardBackground", System.Windows.Media.Colors.White)),
            Border = ToSkia(Get("NoteCardBorder", System.Windows.Media.Colors.Transparent)),
            Text = ToSkia(Get("TextColor", System.Windows.Media.Color.FromRgb(28, 28, 28))),
            TextSecondary = ToSkia(Get("TextColorSecondary", System.Windows.Media.Color.FromRgb(96, 100, 110))),
            IconButtonBackground = ToSkia(Get("IconButtonBackground", System.Windows.Media.Color.FromRgb(240, 242, 246))),
            DueChipBackground = ToSkia(Get("FlashcardUnknownChipBackground", System.Windows.Media.Color.FromRgb(255, 244, 224))),
            DueChipForeground = ToSkia(Get("FlashcardUnknownChipForeground", System.Windows.Media.Color.FromRgb(138, 82, 0))),
            TagBackground = ToSkia(Get("TagChipBackground", System.Windows.Media.Color.FromRgb(245, 247, 251))),
            TagForeground = ToSkia(Get("TagChipForeground", System.Windows.Media.Color.FromRgb(74, 93, 128))),
        };

        _colorsByTheme[theme] = colors;
        return colors;
    }

    private static SKColor ToSkia(System.Windows.Media.Color c)
        => new(c.R, c.G, c.B, c.A);
}

internal sealed class ListingCardColors
{
    public bool Dark { get; init; }
    public required SKColor CardBackground;
    public required SKColor Border;
    public required SKColor Text;
    public required SKColor TextSecondary;
    public required SKColor IconButtonBackground;
    public required SKColor DueChipBackground;
    public required SKColor DueChipForeground;
    public required SKColor TagBackground;
    public required SKColor TagForeground;
}

/// <summary>Layout spec for a recent-strip card (RecentNoteCardStyle button content).</summary>
internal sealed class RecentCardSpec
{
    public required string Key;
    public required float TitleSize;
    public required Func<object, string> Title;
    public required Func<object, string>? Secondary;
    public required Func<object, string> Date;
    public required bool DateSecondaryColor;
}

/// <summary>Tile renderer for the recent strips (Notes / FlashcardSet / Quiz / MindMap). The
/// live strip cards are auto-width (118-176px content), so the tile width is measured from the
/// title per card to avoid a resize jump when bitmap mode engages.</summary>
internal sealed class RecentCardRenderer : SkiaTileRenderer
{
    private const float MinContentWidthDip = 118f;
    private const float MaxContentWidthDip = 176f;
    private const float CardPaddingDip = 11f;
    private const float CardVerticalPaddingDip = 8f;
    private const float CardVerticalMarginDip = 5f;

    private readonly RecentCardSpec _spec;
    private readonly Dictionary<string, RecentCardColors> _colorsByTheme = new();

    public RecentCardRenderer(RecentCardSpec spec)
    {
        _spec = spec;
    }

    public override SKBitmap Render(object viewModel, double dpiScale, float widthDip, out double heightDip)
    {
        var dpi = (float)dpiScale;
        var colors = ResolveColors();
        var fontManager = SKFontManager.Default;
        Func<SKTypeface, SkiaSharp.HarfBuzz.SKShaper> shaperFactory = SkiaCardRenderer.GetShaper;
        var titleTypeface = SkiaCardRenderer.GetTypeface(ResolveFontFamily(), 600);
        var bodyTypeface = SkiaCardRenderer.GetTypeface(ResolveFontFamily(), 400);

        var title = _spec.Title(viewModel);
        var titleLayout = SkiaTextLayout.Layout(title, MaxContentWidthDip * dpi, titleTypeface, _spec.TitleSize * dpi, 1, fontManager, shaperFactory);
        var contentWidth = Math.Clamp((float)Math.Ceiling(titleLayout.MaxIntrinsicWidth), MinContentWidthDip, MaxContentWidthDip);
        var cardWidth = contentWidth + CardPaddingDip * 2;
        var secondary = _spec.Secondary?.Invoke(viewModel);
        var secondaryLayout = string.IsNullOrEmpty(secondary)
            ? null
            : SkiaTextLayout.Layout(secondary, contentWidth * dpi, bodyTypeface, 13f * dpi, 1, fontManager, shaperFactory);
        var date = _spec.Date(viewModel);
        var dateLayout = SkiaTextLayout.Layout(date, contentWidth * dpi, bodyTypeface, 13f * dpi, 1, fontManager, shaperFactory);
        var innerHeight = CardVerticalPaddingDip
            + titleLayout.Height + 3f * dpi
            + (secondaryLayout is null ? 0 : secondaryLayout.Height + 2f * dpi)
            + dateLayout.Height
            + CardVerticalPaddingDip;
        var cardHeight = innerHeight;
        var bitmapWidth = (int)Math.Ceiling((cardWidth + 7f) * dpi);
        var bitmapHeight = (int)Math.Ceiling((cardHeight + CardVerticalMarginDip * 2) * dpi);
        heightDip = cardHeight + CardVerticalMarginDip * 2;

        var bitmap = new SKBitmap(bitmapWidth, bitmapHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        var cardRect = SKRect.Create(0, CardVerticalMarginDip * dpi, cardWidth * dpi, cardHeight * dpi);
        using (var background = new SKPaint { Color = colors.CardBackground, IsAntialias = true })
        {
            var radius = 10f * dpi;
            SurfaceMaterialRaster.Draw(canvas, cardRect, radius, radius, background, colors.Dark, dpi, SurfaceMaterialKind.Raised, frameInset: dpi);
        }

        if (colors.Border.Alpha > 0)
        {
            using var border = new SKPaint
            {
                Color = colors.Border,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f * dpi,
            };
            var radius = 10f * dpi;
            canvas.DrawRoundRect(cardRect, radius, radius, border);
        }
        SurfaceMaterialRaster.LightRaisedFrame(canvas, cardRect, 10, colors.Border, colors.Dark, dpi);


        var x = CardPaddingDip * dpi;
        var y = (CardVerticalMarginDip + CardVerticalPaddingDip) * dpi;
        SkiaCardRenderer.PaintLayout(canvas, titleLayout, x, y, colors.Text, dpi);
        y += titleLayout.Height + 3f * dpi;

        if (secondaryLayout is not null)
        {
            SkiaCardRenderer.PaintLayout(canvas, secondaryLayout, x, y, colors.TextSecondary, dpi);
            y += secondaryLayout.Height + 2f * dpi;
        }

        SkiaCardRenderer.PaintLayout(canvas, dateLayout, x, y, _spec.DateSecondaryColor ? colors.TextSecondary : colors.Text, dpi);

        return bitmap;
    }

    public override SKBitmap RenderPlaceholder(double dpiScale)
    {
        var dpi = (float)dpiScale;
        var colors = ResolveColors();
        var bitmap = new SKBitmap(
            (int)Math.Ceiling((MaxContentWidthDip + CardPaddingDip * 2 + 7f) * dpi),
            (int)Math.Ceiling(57f * dpi),
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = colors.CardBackground, IsAntialias = true };
        canvas.DrawRoundRect(
            SKRect.Create(0, 0, (MaxContentWidthDip + CardPaddingDip * 2) * dpi, 57f * dpi),
            10f * dpi,
            10f * dpi,
            paint);
        return bitmap;
    }

    public override string BuildSignature(object viewModel)
        => $"{ThemeManager.CurrentTheme}|surface-v2|{_spec.Title(viewModel)}|{_spec.Secondary?.Invoke(viewModel)}|{_spec.Date(viewModel)}";

    private string ResolveFontFamily()
    {
        try
        {
            var app = System.Windows.Application.Current;
            var family = app?.Resources["FontFamily.UI"] as System.Windows.Media.FontFamily;
            var source = family?.Source ?? "Segoe UI Variable Text";
            return source.Split(',')[0].Trim();
        }
        catch
        {
            return "Segoe UI";
        }
    }

    private RecentCardColors ResolveColors()
    {
        var theme = (ThemeManager.CurrentTheme ?? string.Empty) + "|surface-v2|" + SurfaceMaterialRaster.ResourceDark;
        if (_colorsByTheme.TryGetValue(theme, out var cached))
            return cached;

        System.Windows.Media.Color Get(string key, System.Windows.Media.Color fallback)
        {
            return DashboardSelectionPaint.ResolveColor(key, fallback);
        }

        var colors = new RecentCardColors
        {
            Dark = SurfaceMaterialRaster.ResourceDark,
            CardBackground = ToSkia(Get("CardBackground", System.Windows.Media.Colors.White)),
            Border = ToSkia(Get("NoteCardBorder", System.Windows.Media.Colors.Transparent)),
            Text = ToSkia(Get("TextColor", System.Windows.Media.Color.FromRgb(28, 28, 28))),
            TextSecondary = ToSkia(Get("TextColorSecondary", System.Windows.Media.Color.FromRgb(96, 100, 110))),
        };

        _colorsByTheme[theme] = colors;
        return colors;
    }

    private static SKColor ToSkia(System.Windows.Media.Color c)
        => new(c.R, c.G, c.B, c.A);
}

internal sealed class RecentCardColors
{
    public bool Dark { get; init; }
    public required SKColor CardBackground;
    public required SKColor Border;
    public required SKColor Text;
    public required SKColor TextSecondary;
}

/// <summary>
/// Tile renderer for quiz results-review cards: mirrors the live card look (green/red subtle
/// background by correctness, question header + status icon, your-answer, correct-answer,
/// explanation). Painted at the host's current width so rows never scale or jump.
/// </summary>
internal sealed class QuizResultCardRenderer : SkiaTileRenderer
{
    private const float DefaultWidthDip = 760f;
    private const float RadiusDip = 10f;
    private const float PaddingDip = 16f;
    private const float MarginBottomDip = 12f;

    private readonly Dictionary<string, QuizResultColors> _colorsByTheme = new();

    public override SKBitmap Render(object viewModel, double dpiScale, float widthDip, out double heightDip)
    {
        var item = (Views.QuizModeWindow.QuizResultItem)viewModel;
        var dpi = (float)dpiScale;
        var colors = ResolveColors(item.IsCorrect);
        var width = Math.Max(100f, widthDip > 0 ? widthDip : DefaultWidthDip) * dpi;
        var innerWidth = width - PaddingDip * 2 * dpi;

        var fontManager = SKFontManager.Default;
        Func<SKTypeface, SkiaSharp.HarfBuzz.SKShaper> shaperFactory = SkiaCardRenderer.GetShaper;
        var titleTypeface = SkiaCardRenderer.GetTypeface(ResolveFontFamily(), 600);
        var bodyTypeface = SkiaCardRenderer.GetTypeface(ResolveFontFamily(), 400);

        var headerLayout = SkiaTextLayout.Layout(item.HeaderText, innerWidth - 28f * dpi, titleTypeface, 15f * dpi, 3, fontManager, shaperFactory);

        List<(string Text, float Size, bool SemiBold, SKColor Color)> details = new();
        if (item.UserAnswerText.Length > 0)
            details.Add((item.UserAnswerText, 13f, false, colors.Text));
        if (item.CorrectAnswerText.Length > 0)
            details.Add((item.CorrectAnswerText, 13f, false, colors.SuccessForeground));
        if (item.Explanation.Length > 0)
            details.Add((item.Explanation, 12f, false, colors.TextSecondary));

        var contentHeightDip = headerLayout.Height;
        var lineLayouts = new List<SkiaTextLayout>(details.Count);
        foreach (var detail in details)
        {
            var layout = SkiaTextLayout.Layout(detail.Text, innerWidth, bodyTypeface, detail.Size * dpi, 3, fontManager, shaperFactory);
            lineLayouts.Add(layout);
            contentHeightDip += 4f * dpi + layout.Height;
        }

        var rowHeightDip = Math.Max(PaddingDip * 2 * dpi + headerLayout.Height, PaddingDip * 2 * dpi + contentHeightDip);
        heightDip = (rowHeightDip + MarginBottomDip * dpi) / dpi;

        var bitmapWidth = (int)Math.Ceiling(width);
        var bitmapHeight = (int)Math.Ceiling(rowHeightDip + MarginBottomDip * dpi);
        var bitmap = new SKBitmap(bitmapWidth, bitmapHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        var cardRect = SKRect.Create(0, 0, width, rowHeightDip);
        using (var background = new SKPaint { Color = colors.CardBackground, IsAntialias = true })
            SurfaceMaterialRaster.Draw(canvas, cardRect, RadiusDip * dpi, RadiusDip * dpi, background, colors.Dark, dpi, SurfaceMaterialKind.Tinted);

        var x = PaddingDip * dpi;
        var y = PaddingDip * dpi;

        // Status icon (checkmark / close), right-aligned on the header row.
        var glyphSize = 18f * dpi;
        var glyphX = width - PaddingDip * dpi - glyphSize;
        var glyphY = y;
        using (var glyphPaint = new SKPaint
        {
            Color = colors.AccentForeground,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f * dpi,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        })
        {
            if (item.IsCorrect)
            {
                using var path = new SKPathBuilder();
                path.MoveTo(glyphX + glyphSize * 0.2f, glyphY + glyphSize * 0.52f);
                path.LineTo(glyphX + glyphSize * 0.44f, glyphY + glyphSize * 0.76f);
                path.LineTo(glyphX + glyphSize * 0.82f, glyphY + glyphSize * 0.28f);
                canvas.DrawPath(path.Detach(), glyphPaint);
            }
            else
            {
                using var path = new SKPathBuilder();
                path.MoveTo(glyphX + glyphSize * 0.22f, glyphY + glyphSize * 0.22f);
                path.LineTo(glyphX + glyphSize * 0.78f, glyphY + glyphSize * 0.78f);
                path.MoveTo(glyphX + glyphSize * 0.78f, glyphY + glyphSize * 0.22f);
                path.LineTo(glyphX + glyphSize * 0.22f, glyphY + glyphSize * 0.78f);
                canvas.DrawPath(path.Detach(), glyphPaint);
            }
        }

        SkiaCardRenderer.PaintLayout(canvas, headerLayout, x, y, colors.AccentForeground, dpi);
        y += headerLayout.Height;

        for (var i = 0; i < lineLayouts.Count; i++)
        {
            y += 4f * dpi;
            SkiaCardRenderer.PaintLayout(canvas, lineLayouts[i], x, y, details[i].Color, dpi);
            y += lineLayouts[i].Height;
        }

        return bitmap;
    }

    public override SKBitmap RenderPlaceholder(double dpiScale)
    {
        var dpi = (float)dpiScale;
        var colors = ResolveColors(isCorrect: true);
        var bitmap = new SKBitmap(
            (int)Math.Ceiling(DefaultWidthDip * dpi),
            (int)Math.Ceiling(96f * dpi),
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = colors.CardBackground, IsAntialias = true };
        canvas.DrawRoundRect(SKRect.Create(0, 0, DefaultWidthDip * dpi, 96f * dpi), RadiusDip * dpi, RadiusDip * dpi, paint);
        return bitmap;
    }

    public override string BuildSignature(object viewModel)
    {
        var item = (Views.QuizModeWindow.QuizResultItem)viewModel;
        return $"{ThemeManager.CurrentTheme}|surface-v2|{item.IsCorrect}|{item.HeaderText}|{item.UserAnswerText}|{item.CorrectAnswerText}|{item.Explanation}";
    }

    private string ResolveFontFamily()
    {
        try
        {
            var app = System.Windows.Application.Current;
            var family = app?.Resources["FontFamily.UI"] as System.Windows.Media.FontFamily;
            var source = family?.Source ?? "Segoe UI Variable Text";
            return source.Split(',')[0].Trim();
        }
        catch
        {
            return "Segoe UI";
        }
    }

    private QuizResultColors ResolveColors(bool isCorrect)
    {
        var theme = (ThemeManager.CurrentTheme ?? string.Empty) + "|surface-v2|" + SurfaceMaterialRaster.ResourceDark;
        var key = $"{theme}|{isCorrect}";
        if (_colorsByTheme.TryGetValue(key, out var cached))
            return cached;

        System.Windows.Media.Color Get(string resKey, System.Windows.Media.Color fallback)
            => System.Windows.Application.Current?.TryFindResource(resKey) is System.Windows.Media.SolidColorBrush brush
                ? brush.Color
                : fallback;

        var backgroundKey = isCorrect ? "AccentSuccessSubtle" : "AccentDangerSubtle";
        var foregroundKey = isCorrect ? "AccentSuccessSubtleForeground" : "AccentDangerSubtleForeground";
        var colors = new QuizResultColors
        {
            Dark = SurfaceMaterialRaster.ResourceDark,
            CardBackground = ToSkia(Get(backgroundKey, System.Windows.Media.Color.FromRgb(isCorrect ? (byte)230 : (byte)250, isCorrect ? (byte)246 : (byte)228, isCorrect ? (byte)235 : (byte)227))),
            AccentForeground = ToSkia(Get(foregroundKey, System.Windows.Media.Colors.Black)),
            SuccessForeground = ToSkia(Get("AccentSuccessSubtleForeground", System.Windows.Media.Color.FromRgb(0, 128, 70))),
            Text = ToSkia(Get("TextColor", System.Windows.Media.Color.FromRgb(28, 28, 28))),
            TextSecondary = ToSkia(Get("TextColorSecondary", System.Windows.Media.Color.FromRgb(96, 100, 110))),
        };

        _colorsByTheme[key] = colors;
        return colors;
    }

    private static SKColor ToSkia(System.Windows.Media.Color c)
        => new(c.R, c.G, c.B, c.A);

    private sealed class QuizResultColors
    {
    public bool Dark { get; init; }
        public required SKColor CardBackground;
        public required SKColor AccentForeground;
        public required SKColor SuccessForeground;
        public required SKColor Text;
        public required SKColor TextSecondary;
    }
}

/// <summary>Replays the browse-grid card's frozen native paint, including its buttons,
/// question/answer face, selection and current typography.</summary>
internal sealed class FlashcardGridCardRenderer : SkiaTileRenderer
{
    // The live card supplies its immutable paint instructions. Keeping a separate
    // hand-written skin here previously omitted buttons and diverged from typography.
    public override SKBitmap Render(object input, double dpiScale, float widthDip, out double heightDip)
        => ((FrozenCardDrawing)input).Render(dpiScale, out heightDip);

    public override SKBitmap RenderPlaceholder(double dpiScale)
    {
        var bitmap = new SKBitmap((int)Math.Ceiling(316 * dpiScale), (int)Math.Ceiling(224 * dpiScale), SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent); return bitmap;
    }

    public override string BuildSignature(object viewModel)
    {
        var item = (Views.FlashcardsPreviewWindow.FlashcardPreviewItem)viewModel;
        return $"{ThemeManager.CurrentTheme}|{System.Globalization.CultureInfo.CurrentUICulture.Name}|native-paint-v1|{item.IsFlipped}|{item.Question.Length}:{item.Question}|{item.Answer.Length}:{item.Answer}|{item.Category.Length}:{item.Category}|" +
               $"{item.IsStarred}|{item.IsSelected}|{item.IsMaturityNew}|{item.IsMaturityLearning}|{item.IsMaturityMature}|" +
               $"{item.QuestionImageFileName}|{item.AnswerImageFileName}|{item.HasQuestionAudio}|{item.HasAnswerAudio}|{item.IsQuestionAudioPlaying}|{item.IsAnswerAudioPlaying}";
    }
}

/// <summary>Layout spec for a fluid-width row (window lists + calendar rail).</summary>
internal sealed class FluidRowSpec
{
    public required string Key;
    public required string CardBackgroundKey;
    public required string CardBorderKey;
    public required float RadiusDip;
    public required float PaddingDip;
    public required float MarginBottomDip;
    public required float TitleSize;
    public required Func<object, string> Title;
    public required List<(Func<object, string> Text, float Size, bool SemiBold)> Lines;
    public required Func<object, bool> IsVisible;
    public required Func<object, (string Text, string BgKey, string FgKey)?>? Chip;
    public required Func<object, bool>? Checkbox;
    public Func<object, bool>? SelectedSurface;
    public float SelectionThicknessDip = 2;
    public bool TintSelectedSurface = true;
    // The history live template has a 22 DIP title, 6 DIP gap, 18 DIP metadata and 14 DIP vertical padding.
    public bool HistoryLayout;
    public required Func<object, string>? LeftGlyph;
    public required Func<object, string>? RightText;
    public required bool RightTextSemiBold;
    public required bool ShowActionPills;
    public required Func<object, string>? Question;
    public required Func<object, IReadOnlyList<string>>? Options;
    public required Func<object, string>? ResultText;
    public required string BorderColorKey;
    public required float BorderThicknessDip;
}

/// <summary>Tile renderer for fluid-width card rows: calendar rail, trash, quiz library,
/// combine list, quiz preview questions, study/attempt history. The bitmap is rendered at the
/// host's current width (cache key includes it) so rows never scale or jump.</summary>
internal sealed class FluidRowRenderer : SkiaTileRenderer
{
    private const float DefaultWidthDip = 760f;

    private readonly FluidRowSpec _spec;
    private readonly Dictionary<string, FluidRowColors> _colorsByTheme = new();

    public FluidRowRenderer(FluidRowSpec spec)
    {
        _spec = spec;
    }

    public override SKBitmap Render(object viewModel, double dpiScale, float widthDip, out double heightDip)
    {
        var dpi = (float)dpiScale;
        var colors = ResolveColors();
        var width = Math.Max(100f, widthDip > 0 ? widthDip : DefaultWidthDip) * dpi;
        var visible = _spec.IsVisible(viewModel);

        var fontManager = SKFontManager.Default;
        Func<SKTypeface, SkiaSharp.HarfBuzz.SKShaper> shaperFactory = SkiaCardRenderer.GetShaper;
        var titleTypeface = SkiaCardRenderer.GetTypeface(ResolveFontFamily(), 600);
        var bodyTypeface = SkiaCardRenderer.GetTypeface(ResolveFontFamily(), 400);

        var innerWidth = width - _spec.PaddingDip * 2 * dpi;
        var verticalPadding = (_spec.HistoryLayout ? 14f : _spec.PaddingDip) * dpi;
        var y = verticalPadding;
        var historyChip = _spec.HistoryLayout ? _spec.Chip?.Invoke(viewModel) : null;
        var historyChipLayout = historyChip is { } status
            ? SkiaTextLayout.Layout(status.Text, 200f * dpi, SkiaCardRenderer.GetTypeface(ResolveFontFamily(), StatusChipMetrics.FontWeightValue),
                (float)StatusChipMetrics.FontSize * dpi, 1, fontManager, shaperFactory) : null;
        if (historyChipLayout is not null) innerWidth -= historyChipLayout.MaxIntrinsicWidth + (float)(StatusChipMetrics.HorizontalPadding * 2 + StatusChipMetrics.Gap) * dpi;
        innerWidth = Math.Max(1, innerWidth);

        var titleLayout = SkiaTextLayout.Layout(_spec.Title(viewModel), innerWidth, titleTypeface, _spec.TitleSize * dpi, 1, fontManager, shaperFactory);
        var titleHeight = _spec.HistoryLayout ? 22f * dpi : titleLayout.Height;

        var contentHeight = titleHeight;
        var lineLayouts = new List<SkiaTextLayout>();
        foreach (var line in _spec.Lines)
        {
            var layout = SkiaTextLayout.Layout(line.Text(viewModel), innerWidth, bodyTypeface, line.Size * dpi, _spec.HistoryLayout ? 1 : 2, fontManager, shaperFactory);
            lineLayouts.Add(layout);
            contentHeight += _spec.HistoryLayout ? 24f * dpi : 4f * dpi + layout.Height;
        }

        var questionLayout = _spec.Question is { } questionGetter
            ? SkiaTextLayout.Layout(questionGetter(viewModel), innerWidth, titleTypeface, 16f * dpi, 4, fontManager, shaperFactory)
            : null;
        if (questionLayout is not null)
            contentHeight += 12f * dpi + questionLayout.Height;

        var optionLayouts = new List<SkiaTextLayout>();
        if (_spec.Options is { } optionsGetter)
        {
            var options = optionsGetter(viewModel);
            var index = 0;
            foreach (var option in options)
            {
                if (index >= 6)
                    break;
                var label = ((char)('A' + index)) + ".  " + option;
                var layout = SkiaTextLayout.Layout(label, innerWidth, bodyTypeface, 14f * dpi, 2, fontManager, shaperFactory);
                optionLayouts.Add(layout);
                contentHeight += 8f * dpi + layout.Height;
                index++;
            }
        }

        var rowHeight = Math.Max(verticalPadding * 2 + titleHeight, verticalPadding * 2 + contentHeight);
        if (_spec.Checkbox is not null)
            rowHeight = Math.Max(rowHeight, 32f * dpi + _spec.PaddingDip * 2 * dpi);
        if (_spec.LeftGlyph is not null)
            rowHeight = Math.Max(rowHeight, 20f * dpi + _spec.PaddingDip * 2 * dpi);

        var bitmapWidth = (int)Math.Ceiling(width);
        var bitmapHeight = (int)Math.Ceiling(rowHeight + _spec.MarginBottomDip * dpi);
        heightDip = _spec.HistoryLayout ? (rowHeight + _spec.MarginBottomDip * dpi) / dpi : rowHeight + _spec.MarginBottomDip * dpi;

        var bitmap = new SKBitmap(bitmapWidth, bitmapHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        if (!visible)
        {
            return bitmap;
        }

        var cardRect = SKRect.Create(0, 0, width, rowHeight);
        var selectedSurface = _spec.SelectedSurface?.Invoke(viewModel) == true;
        using (var background = new SKPaint { Color = selectedSurface && _spec.TintSelectedSurface ? colors.ChipBackground : colors.CardBackground, IsAntialias = true })
        {
            var radius = _spec.RadiusDip * dpi;
            SurfaceMaterialRaster.Draw(canvas, cardRect, radius, radius, background, colors.Dark, dpi,
                selectedSurface && _spec.TintSelectedSurface ? SurfaceMaterialKind.Tinted :
                    _spec.CardBackgroundKey == "CardBackground" ? SurfaceMaterialKind.Raised : SurfaceMaterialKind.None,
                frameInset: (float)(selectedSurface ? _spec.SelectionThicknessDip : _spec.BorderThicknessDip) * dpi);
        }

        if (_spec.BorderThicknessDip > 0 || selectedSurface)
        {
            using var border = new SKPaint
            {
                Color = selectedSurface ? colors.SelectionEdge : colors.FrameEdge,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (selectedSurface ? _spec.SelectionThicknessDip : _spec.BorderThicknessDip) * dpi,
            };
            using var light = selectedSurface ? DashboardSelectionPaint.Shader(cardRect, colors.SelectionTop, colors.SelectionEdge, colors.SelectionBottom) : null;
            border.Shader = light;
            var rim = cardRect; rim.Inflate(-border.StrokeWidth / 2, -border.StrokeWidth / 2);
            var radius = Math.Max(0, _spec.RadiusDip * dpi - border.StrokeWidth / 2);
            canvas.DrawRoundRect(rim, radius, radius, border);
        }
        if (!selectedSurface && _spec.CardBackgroundKey == "CardBackground")
            SurfaceMaterialRaster.LightRaisedFrame(canvas, cardRect, _spec.RadiusDip, colors.FrameEdge, colors.Dark, dpi, (float)_spec.BorderThicknessDip);


        var x = _spec.PaddingDip * dpi;
        y = verticalPadding;

        if (_spec.Checkbox is { } checkboxGetter)
        {
            DrawCheckbox(canvas, x, y + (rowHeight - 20f * dpi) / 2, 18f * dpi, checkboxGetter(viewModel), colors.Text);
            x += 30f * dpi;
        }

        if (_spec.LeftGlyph is { } glyphGetter)
        {
            DrawGlyphMarker(canvas, x, y + (rowHeight - 18f * dpi) / 2, 16f * dpi, colors.TextSecondary);
            x += 26f * dpi;
        }

        var textWidth = width - _spec.PaddingDip * 2 * dpi;
        if (_spec.RightText is { } rightGetter)
        {
            var rightText = rightGetter(viewModel);
            var rightTypeface = _spec.RightTextSemiBold ? titleTypeface : bodyTypeface;
            var rightLayout = SkiaTextLayout.Layout(rightText, 90f * dpi, rightTypeface, 13f * dpi, 1, fontManager, shaperFactory);
            var chipWidth = rightLayout.MaxIntrinsicWidth + 16f * dpi;
            using var chipBg = new SKPaint { Color = colors.ChipBackground, IsAntialias = true };
            SurfaceMaterialRaster.Draw(canvas, SKRect.Create(width - _spec.PaddingDip * dpi - chipWidth, y + (rowHeight - 24f * dpi) / 2, chipWidth, 24f * dpi), 12f * dpi, 12f * dpi, chipBg, colors.Dark, dpi, SurfaceMaterialKind.Tinted);
            SkiaCardRenderer.PaintLayout(canvas, rightLayout, width - _spec.PaddingDip * dpi - chipWidth + 8f * dpi, y + (rowHeight - 24f * dpi) / 2, colors.Text, dpi);
            textWidth -= chipWidth + 8f * dpi;
        }

        if (_spec.ShowActionPills)
        {
            // Two placeholder pills where the restore/delete buttons sit (interactions are
            // live at rest; the tile only draws the row's resting shape).
            textWidth -= 92f * dpi;
            using var pill = new SKPaint { Color = colors.Border.WithAlpha(80), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f * dpi };
            var pillY = y + (rowHeight - 26f * dpi) / 2;
            canvas.DrawRoundRect(SKRect.Create(width - _spec.PaddingDip * dpi - 84f * dpi, pillY, 38f * dpi, 26f * dpi), 13f * dpi, 13f * dpi, pill);
            canvas.DrawRoundRect(SKRect.Create(width - _spec.PaddingDip * dpi - 40f * dpi, pillY, 38f * dpi, 26f * dpi), 13f * dpi, 13f * dpi, pill);
        }

        if (questionLayout is not null)
        {
            SkiaCardRenderer.PaintLayout(canvas, questionLayout, x, y, colors.Text, dpi);
            y += questionLayout.Height + 12f * dpi;
        }

        SkiaCardRenderer.PaintLayout(canvas, titleLayout, x, y, colors.Text, dpi);
        y += titleHeight;

        foreach (var layout in lineLayouts)
        {
            y += (_spec.HistoryLayout ? 6f : 4f) * dpi;
            SkiaCardRenderer.PaintLayout(canvas, layout, x, y, colors.TextSecondary, dpi);
            y += _spec.HistoryLayout ? 18f * dpi : layout.Height;
        }

        foreach (var layout in optionLayouts)
        {
            y += 8f * dpi;
            SkiaCardRenderer.PaintLayout(canvas, layout, x, y, colors.Text, dpi);
            y += layout.Height;
        }

        if (_spec.HistoryLayout && historyChip is { } historyStatus && historyChipLayout is not null)
        {
            var chipWidth = historyChipLayout.MaxIntrinsicWidth + (float)StatusChipMetrics.HorizontalPadding * 2 * dpi;
            var chipX = width - _spec.PaddingDip * dpi - chipWidth;
            var chipY = (rowHeight - (float)StatusChipMetrics.Height * dpi) / 2;
            using var chipPaint = new SKPaint { Color = ResolveResource(historyStatus.BgKey, System.Windows.Media.Colors.Gray), IsAntialias = true };
            SurfaceMaterialRaster.Draw(canvas, SKRect.Create(chipX, chipY, chipWidth, (float)StatusChipMetrics.Height * dpi), (float)StatusChipMetrics.Radius * dpi, (float)StatusChipMetrics.Radius * dpi,
                chipPaint, colors.Dark, dpi, SurfaceMaterialKind.Tinted);
            SkiaCardRenderer.PaintLayout(canvas, historyChipLayout, chipX + (float)StatusChipMetrics.HorizontalPadding * dpi,
                chipY + ((float)StatusChipMetrics.Height * dpi - historyChipLayout.Height) / 2, ResolveResource(historyStatus.FgKey, System.Windows.Media.Colors.Black), dpi);
        }
        else if (_spec.Chip is { } chipGetter)
        {
            var chip = chipGetter(viewModel);
            if (chip is not null)
            {
                var chipBg = ResolveResource(chip.Value.BgKey, System.Windows.Media.Colors.Gray);
                var chipFg = ResolveResource(chip.Value.FgKey, System.Windows.Media.Colors.Black);
                var chipLayout = SkiaTextLayout.Layout(chip.Value.Text, 200f * dpi, bodyTypeface, 11f * dpi, 1, fontManager, shaperFactory);
                var chipWidth = chipLayout.MaxIntrinsicWidth + 16f * dpi;
                using var chipPaint = new SKPaint { Color = chipBg, IsAntialias = true };
                SurfaceMaterialRaster.Draw(canvas, SKRect.Create(width - _spec.PaddingDip * dpi - chipWidth, _spec.PaddingDip * dpi, chipWidth, 22f * dpi), 11f * dpi, 11f * dpi, chipPaint, colors.Dark, dpi, SurfaceMaterialKind.Tinted);
                SkiaCardRenderer.PaintLayout(canvas, chipLayout, width - _spec.PaddingDip * dpi - chipWidth + 8f * dpi, _spec.PaddingDip * dpi + 2f * dpi, chipFg, dpi);
            }
        }

        if (_spec.ResultText is { } resultGetter)
        {
            var result = resultGetter(viewModel);
            if (result.Length > 0)
            {
                var resultLayout = SkiaTextLayout.Layout(result, 200f * dpi, titleTypeface, 11f * dpi, 1, fontManager, shaperFactory);
                var resultWidth = resultLayout.MaxIntrinsicWidth + 16f * dpi;
                using var chipPaint = new SKPaint { Color = colors.TextSecondary, IsAntialias = true };
                SurfaceMaterialRaster.Draw(canvas, SKRect.Create(width - _spec.PaddingDip * dpi - resultWidth, _spec.PaddingDip * dpi, resultWidth, 24f * dpi), 12f * dpi, 12f * dpi, chipPaint, colors.Dark, dpi, SurfaceMaterialKind.Tinted);
                SkiaCardRenderer.PaintLayout(canvas, resultLayout, width - _spec.PaddingDip * dpi - resultWidth + 8f * dpi, _spec.PaddingDip * dpi + 4f * dpi, colors.CardBackground, dpi);
            }
        }

        return bitmap;
    }

    public override SKBitmap RenderPlaceholder(double dpiScale)
    {
        var dpi = (float)dpiScale;
        var colors = ResolveColors();
        var bitmap = new SKBitmap(
            (int)Math.Ceiling(DefaultWidthDip * dpi),
            (int)Math.Ceiling(96f * dpi),
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = colors.CardBackground, IsAntialias = true };
        canvas.DrawRoundRect(SKRect.Create(0, 0, DefaultWidthDip * dpi, 96f * dpi), _spec.RadiusDip * dpi, _spec.RadiusDip * dpi, paint);
        return bitmap;
    }

    public override string BuildSignature(object viewModel)
    {
        var visible = _spec.IsVisible(viewModel);
        var title = _spec.Title(viewModel);
        var lines = string.Join("|", _spec.Lines.Select(l => l.Text(viewModel)));
        var chip = _spec.Chip?.Invoke(viewModel)?.Text ?? string.Empty;
        return $"{ThemeManager.CurrentTheme}|surface-v2|{visible}|{title}|{lines}|{chip}|{_spec.SelectedSurface?.Invoke(viewModel) == true}|{_spec.Checkbox?.Invoke(viewModel) == true}";
    }

    private static void DrawCheckbox(SKCanvas canvas, float x, float y, float size, bool isChecked, SKColor color)
    {
        using var outline = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
        canvas.DrawRoundRect(SKRect.Create(x, y, size, size), 4f, 4f, outline);
        if (isChecked)
        {
            using var check = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
            using var path = new SKPathBuilder();
            path.MoveTo(x + size * 0.22f, y + size * 0.52f);
            path.LineTo(x + size * 0.44f, y + size * 0.72f);
            path.LineTo(x + size * 0.8f, y + size * 0.3f);
            canvas.DrawPath(path.Detach(), check);
        }
    }

    private static void DrawGlyphMarker(SKCanvas canvas, float x, float y, float size, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
        var rect = SKRect.Create(x, y, size, size);
        canvas.DrawRoundRect(rect, 2f, 2f, paint);
        canvas.DrawLine(x + size * 0.2f, y + size * 0.55f, x + size * 0.8f, y + size * 0.55f, paint);
        canvas.DrawLine(x + size * 0.5f, y + size * 0.2f, x + size * 0.5f, y + size * 0.55f, paint);
    }

    private string ResolveFontFamily()
    {
        try
        {
            var app = System.Windows.Application.Current;
            var family = app?.Resources["FontFamily.UI"] as System.Windows.Media.FontFamily;
            var source = family?.Source ?? "Segoe UI Variable Text";
            return source.Split(',')[0].Trim();
        }
        catch
        {
            return "Segoe UI";
        }
    }

    private SKColor ResolveResource(string key, System.Windows.Media.Color fallback)
    {
        return ToSkia(DashboardSelectionPaint.ResolveColor(key, fallback));
    }

    private FluidRowColors ResolveColors()
    {
        var theme = (ThemeManager.CurrentTheme ?? string.Empty) + "|surface-v2|" + SurfaceMaterialRaster.ResourceDark;
        if (_colorsByTheme.TryGetValue(theme, out var cached))
            return cached;

        var colors = new FluidRowColors
        {
            Dark = SurfaceMaterialRaster.ResourceDark,
            CardBackground = ResolveResource(_spec.CardBackgroundKey, System.Windows.Media.Colors.White),
            Border = ResolveResource(_spec.CardBorderKey, System.Windows.Media.Colors.Transparent),
            FrameEdge = ResolveResource(_spec.BorderColorKey, System.Windows.Media.Colors.Transparent),
            Text = ResolveResource("TextColor", System.Windows.Media.Color.FromRgb(28, 28, 28)),
            TextSecondary = ResolveResource("TextColorSecondary", System.Windows.Media.Color.FromRgb(96, 100, 110)),
            ChipBackground = ResolveResource("SelectionBackground", System.Windows.Media.Color.FromRgb(240, 242, 246)),
            SelectionEdge = ResolveResource("AccentRimBody", System.Windows.Media.Color.FromRgb(67, 92, 236)),
            SelectionTop = ResolveResource("AccentRimTop", System.Windows.Media.Color.FromRgb(103, 134, 245)),
            SelectionBottom = ResolveResource("AccentRimBottom", System.Windows.Media.Color.FromRgb(52, 74, 194)),
        };

        _colorsByTheme[theme] = colors;
        return colors;
    }

    private static SKColor ToSkia(System.Windows.Media.Color c)
        => new(c.R, c.G, c.B, c.A);
}

internal sealed class FluidRowColors
{
    public bool Dark { get; init; }
    public required SKColor CardBackground;
    public required SKColor Border;
    public required SKColor FrameEdge;
    public required SKColor Text;
    public required SKColor TextSecondary;
    public required SKColor ChipBackground;
    public required SKColor SelectionEdge;
    public required SKColor SelectionTop;
    public required SKColor SelectionBottom;
}




