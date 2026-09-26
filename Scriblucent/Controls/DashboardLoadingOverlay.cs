using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Scriblucent.Controls;

/// <summary>A single retained drawing and animation for the visible dashboard skeleton.</summary>
public sealed class DashboardLoadingOverlay : FrameworkElement
{
    public const int WaveDurationMs = 1400;

    public enum DashboardKind { Notes, Flashcards, Quizzes, MindMaps }

    public readonly record struct Layout
    {
        public DashboardKind Kind { get; init; }
        public bool ShowRecent { get; init; }
        public bool ExpandRecent { get; init; }
        public bool ShowCalendar { get; init; }
        public bool ExpandCalendar { get; init; }
        public bool CalendarFirst { get; init; }
        public bool ShowContent { get; init; }
        public bool ExpandContent { get; init; }
        public bool ShowGroups { get; init; }
        public bool ExpandGroups { get; init; }
        public bool ShowUngrouped { get; init; }
        public bool ExpandUngrouped { get; init; }
        public bool GroupsFirst { get; init; }
        public double CardWidth { get; init; }
        public double CardHeight { get; init; }
    }

    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(DashboardLoadingOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SurfaceBrushProperty = DependencyProperty.Register(
        nameof(SurfaceBrush), typeof(Brush), typeof(DashboardLoadingOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DetailBrushProperty = DependencyProperty.Register(
        nameof(DetailBrush), typeof(Brush), typeof(DashboardLoadingOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly TranslateTransform _wavePosition = new();
    private readonly LinearGradientBrush _waveBrush;
    private Layout _layout = new()
    {
        Kind = DashboardKind.Notes,
        ShowRecent = true,
        ExpandRecent = true,
        ShowCalendar = true,
        ExpandCalendar = true,
        CalendarFirst = true,
        ShowContent = true,
        ExpandContent = true,
        ShowUngrouped = true,
        ExpandUngrouped = true,
        GroupsFirst = true,
        CardWidth = 244,
        CardHeight = 164
    };

    public Brush? Background
    {
        get => (Brush?)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public Brush? SurfaceBrush
    {
        get => (Brush?)GetValue(SurfaceBrushProperty);
        set => SetValue(SurfaceBrushProperty, value);
    }

    public Brush? DetailBrush
    {
        get => (Brush?)GetValue(DetailBrushProperty);
        set => SetValue(DetailBrushProperty, value);
    }

    public DashboardLoadingOverlay()
    {
        _waveBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            RelativeTransform = _wavePosition
        };
        _waveBrush.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        _waveBrush.GradientStops.Add(new GradientStop(Colors.Transparent, 0.35));
        _waveBrush.GradientStops.Add(new GradientStop(Color.FromArgb(80, 150, 170, 220), 0.5));
        _waveBrush.GradientStops.Add(new GradientStop(Colors.Transparent, 0.65));
        _waveBrush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
    }

    public void SetLayout(Layout layout)
    {
        if (_layout == layout)
            return;

        _layout = layout;
        InvalidateVisual();
    }

    public void StartWave(bool reducedMotion)
    {
        _wavePosition.BeginAnimation(TranslateTransform.XProperty, null);
        _wavePosition.X = -1;
        if (reducedMotion)
            return;

        _wavePosition.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1, 1,
            TimeSpan.FromMilliseconds(WaveDurationMs))
        {
            RepeatBehavior = RepeatBehavior.Forever
        });
    }

    public void StopWave() => _wavePosition.BeginAnimation(TranslateTransform.XProperty, null);

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsFinite(availableSize.Width) ? availableSize.Width : 0,
            double.IsFinite(availableSize.Height) ? availableSize.Height : 0);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        drawingContext.DrawRectangle(Background, null, new Rect(0, 0, width, height));
        var surfaces = new GeometryGroup();
        var insets = new GeometryGroup();
        var cards = new GeometryGroup();
        var details = new GeometryGroup();
        var shimmerShapes = new GeometryGroup();

        void Add(GeometryGroup group, double x, double y, double w, double h,
            double radius = 7, bool shimmer = true)
        {
            if (w <= 0 || h <= 0 || y >= height)
                return;
            var shape = new RectangleGeometry(new Rect(x, y, w, h), radius, radius);
            group.Children.Add(shape);
            if (shimmer)
                shimmerShapes.Children.Add(shape);
        }

        void DrawCardDetails(double x, double y, double w, double h, bool compact)
        {
            var inset = compact ? 11d : 15d;
            var titleY = y + (compact ? 14 : 18);
            Add(details, x + inset, titleY, Math.Min(w * 0.58, w - inset * 2), compact ? 9 : 11, 5);

            switch (_layout.Kind)
            {
                case DashboardKind.Notes:
                    Add(details, x + inset, y + (compact ? 35 : 47), w * 0.76, 8, 4);
                    if (!compact)
                    {
                        Add(details, x + inset, y + 67, w * 0.54, 8, 4);
                        Add(details, x + inset, y + h - 27, Math.Min(64, w * 0.32), 13, 6);
                    }
                    break;

                case DashboardKind.Flashcards:
                    Add(details, x + w - inset - 24, titleY - 3, 23, 17, 8);
                    Add(details, x + inset, y + (compact ? 36 : 57), w * 0.43, 8, 4);
                    if (!compact)
                    {
                        Add(details, x + inset, y + h - 42, w - inset * 2, 8, 4);
                        Add(details, x + inset, y + h - 24, w * 0.38, 8, 4);
                    }
                    break;

                case DashboardKind.Quizzes:
                    Add(details, x + inset, y + (compact ? 36 : 50), w * 0.66, 8, 4);
                    if (!compact)
                    {
                        Add(details, x + inset, y + 70, w * 0.57, 8, 4);
                        Add(details, x + inset, y + h - 30, Math.Min(84, w * 0.36), 14, 7);
                    }
                    break;

                case DashboardKind.MindMaps:
                    if (compact)
                    {
                        Add(details, x + inset, y + 36, w * 0.48, 9, 5);
                    }
                    else
                    {
                        var center = x + w * 0.5;
                        Add(details, center - 35, y + (h < 155 ? 48 : 61), 70, 19, 9);
                        Add(details, center - 92, y + (h < 155 ? 86 : 111), 57, 16, 8);
                        Add(details, center + 35, y + (h < 155 ? 86 : 111), 57, 16, 8);
                    }
                    break;
            }
        }

        var dashboardPadding = TryFindResource("Shell.DashboardPadding") is Thickness dp
            ? dp : new Thickness(20, 6, 20, 20);
        var recentPadding = TryFindResource("Shell.RecentPadding") is Thickness rp
            ? rp : new Thickness(20, 14, 20, 4);
        var left = dashboardPadding.Left;
        var usable = Math.Max(0, width - dashboardPadding.Left - dashboardPadding.Right);
        var contentTop = dashboardPadding.Top;

        if (_layout.ShowRecent)
        {
            var recentTop = recentPadding.Top;
            Add(details, recentPadding.Left + 2, recentTop + 6, 17, 17, 8);
            Add(details, recentPadding.Left + 30, recentTop + 7,
                Math.Min(138, usable * 0.4), 15, 7);
            contentTop = recentTop + 24 + recentPadding.Bottom + dashboardPadding.Top;
            if (_layout.ExpandRecent)
            {
                const double recentGap = 7;
                const double recentWidth = 168;
                var count = Math.Clamp((int)((usable + recentGap) / (recentWidth + recentGap)), 1, 5);
                for (var i = 0; i < count; i++)
                {
                    var x = recentPadding.Left + i * (recentWidth + recentGap);
                    var w = Math.Min(recentWidth, usable - i * (recentWidth + recentGap));
                    var cardY = recentTop + 40;
                    Add(cards, x, cardY, w, 62, 10);
                    DrawCardDetails(x, cardY, w, 62, compact: true);
                }
                contentTop += 78;
            }
        }

        void DrawCalendar(double y)
        {
            var panelHeight = _layout.ExpandCalendar ? 300d : 51d;
            Add(surfaces, left, y, usable, panelHeight, 16, shimmer: false);
            Add(details, left + 14, y + 17, 14, 14, 7);
            Add(details, left + 42, y + 17, Math.Min(133, usable * 0.4), 13, 6);
            if (!_layout.ExpandCalendar)
                return;

            var innerY = y + 43;
            var innerHeight = 245d;
            var calendarWidth = Math.Min(240, Math.Max(0, usable - 24));
            Add(insets, left + 12, innerY, calendarWidth, innerHeight, 13, shimmer: false);
            Add(details, left + 28, innerY + 16, calendarWidth * 0.42, 10, 5);
            var dateWidth = (calendarWidth - 32 - 6 * 6) / 7;
            for (var r = 0; r < 5; r++)
                for (var c = 0; c < 7; c++)
                    Add(details, left + 28 + c * (dateWidth + 6), innerY + 53 + r * 34,
                        Math.Max(5, dateWidth * 0.52), 7, 3);

            var scheduleX = left + 12 + calendarWidth + 12;
            var scheduleWidth = usable - (scheduleX - left) - 12;
            if (scheduleWidth < 70)
                return;

            Add(insets, scheduleX, innerY, scheduleWidth, innerHeight, 13, shimmer: false);
            Add(details, scheduleX + 16, innerY + 18, Math.Min(152, scheduleWidth * 0.54), 10, 5);
            for (var r = 0; r < 3; r++)
            {
                Add(details, scheduleX + 16, innerY + 54 + r * 57,
                    Math.Min(220, scheduleWidth - 32), 37, 8);
            }
        }

        void DrawCardRows(double y, bool grouped, double x, double available)
        {
            const double cardGap = 12;
            var cellWidth = Math.Min(grouped ? 240 : _layout.CardWidth, available);
            if (cellWidth <= 0)
                return;

            var cellHeight = grouped
                ? _layout.Kind switch
                {
                    DashboardKind.Flashcards or DashboardKind.Quizzes => 128d,
                    DashboardKind.MindMaps => 152d,
                    _ => 164d
                }
                : _layout.CardHeight;
            var columns = Math.Clamp((int)((available + cardGap) / (cellWidth + cardGap)), 1, 5);
            var row = y;
            do
            {
                for (var i = 0; i < columns; i++)
                {
                    var cardX = x + i * (cellWidth + cardGap);
                    Add(cards, cardX, row, cellWidth, cellHeight - 10, 13);
                    DrawCardDetails(cardX, row, cellWidth, cellHeight - 10, compact: false);
                }
                row += cellHeight;
            }
            while (!grouped && row < height);
        }

        double DrawSubsection(double y, bool grouped, bool expanded)
        {
            Add(details, left + 25, y + 8, grouped ? 104 : 142, 12, 6);
            if (!expanded)
                return y + 37;

            if (!grouped)
            {
                DrawCardRows(y + 37, grouped: false, x: left + 14, available: usable - 28);
                return height + 1;
            }

            var groupCellHeight = _layout.Kind switch
            {
                DashboardKind.Flashcards or DashboardKind.Quizzes => 128d,
                DashboardKind.MindMaps => 152d,
                _ => 164d
            };
            var groupY = y + 35;
            Add(insets, left + 14, groupY, usable - 28, groupCellHeight + 43, 13,
                shimmer: false);
            Add(details, left + 28, groupY + 14, Math.Min(155, usable * 0.38), 11, 5);
            DrawCardRows(groupY + 38, grouped: true, x: left + 26, available: usable - 52);
            return groupY + groupCellHeight + 57;
        }

        void DrawContent(double y)
        {
            // The listing is one surface containing independently ordered group sections.
            var hasBody = _layout.ExpandContent && (_layout.ShowGroups || _layout.ShowUngrouped);
            var panelHeight = hasBody ? Math.Max(52, height - y + 24) : 51;
            Add(surfaces, left, y, usable, panelHeight, 16, shimmer: false);
            Add(details, left + 14, y + 17, 14, 14, 7);
            Add(details, left + 42, y + 17, Math.Min(174, usable * 0.44), 14, 7);
            if (!hasBody)
                return;

            var sectionY = y + 44;
            if (_layout.GroupsFirst)
            {
                if (_layout.ShowGroups)
                    sectionY = DrawSubsection(sectionY, grouped: true, expanded: _layout.ExpandGroups);
                if (_layout.ShowUngrouped)
                    DrawSubsection(sectionY, grouped: false, expanded: _layout.ExpandUngrouped);
            }
            else
            {
                if (_layout.ShowUngrouped)
                    sectionY = DrawSubsection(sectionY, grouped: false, expanded: _layout.ExpandUngrouped);
                if (_layout.ShowGroups)
                    DrawSubsection(sectionY, grouped: true, expanded: _layout.ExpandGroups);
            }
        }

        if (_layout.CalendarFirst)
        {
            if (_layout.ShowCalendar)
            {
                DrawCalendar(contentTop);
                contentTop += (_layout.ExpandCalendar ? 300 : 51) + 14;
            }
            if (_layout.ShowContent)
                DrawContent(contentTop);
        }
        else
        {
            if (_layout.ShowContent)
            {
                DrawContent(contentTop);
                contentTop = _layout.ExpandContent
                    && (_layout.ShowGroups || _layout.ShowUngrouped)
                    ? height + 1 : contentTop + 66;
            }
            if (_layout.ShowCalendar)
                DrawCalendar(contentTop);
        }

        drawingContext.DrawGeometry(SurfaceBrush, null, surfaces);
        drawingContext.DrawGeometry(Background, null, insets);
        drawingContext.DrawGeometry(SurfaceBrush, new Pen(DetailBrush, 1), cards);
        drawingContext.DrawGeometry(DetailBrush, null, details);
        drawingContext.DrawGeometry(_waveBrush, null, shimmerShapes);
    }
}
