using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Scriblucent.Animations;
using Scriblucent.Rendering;
using Scriblucent.Icons;
using Scriblucent.Localization;

namespace Scriblucent.Controls;

/// <summary>A single retained drawing and animation for the visible dashboard skeleton.</summary>
public sealed class DashboardLoadingOverlay : FrameworkElement
{
    public const int WaveDurationMs = 1600;

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
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, PaintChanged));

    public static readonly DependencyProperty SurfaceBrushProperty = DependencyProperty.Register(
        nameof(SurfaceBrush), typeof(Brush), typeof(DashboardLoadingOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, PaintChanged));

    public static readonly DependencyProperty DetailBrushProperty = DependencyProperty.Register(
        nameof(DetailBrush), typeof(Brush), typeof(DashboardLoadingOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, PaintChanged));

    public static readonly DependencyProperty InsetBrushProperty = DependencyProperty.Register(
        nameof(InsetBrush), typeof(Brush), typeof(DashboardLoadingOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, PaintChanged));
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(DashboardLoadingOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, PaintChanged));
    public static readonly DependencyProperty ControlBrushProperty = DependencyProperty.Register(
        nameof(ControlBrush), typeof(Brush), typeof(DashboardLoadingOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, PaintChanged));
    private DrawingGroup? _drawing;
    private static void PaintChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((DashboardLoadingOverlay)d).InvalidateDrawing();
    private void InvalidateDrawing() { _drawing = null; InvalidateVisual(); }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == SurfaceMaterial.DarkProperty) InvalidateDrawing();
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo info) { base.OnRenderSizeChanged(info); InvalidateDrawing(); }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) { base.OnDpiChanged(oldDpi, newDpi); InvalidateDrawing(); }
    public Brush? InsetBrush { get => (Brush?)GetValue(InsetBrushProperty); set => SetValue(InsetBrushProperty, value); }
    public Brush? AccentBrush { get => (Brush?)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public Brush? ControlBrush { get => (Brush?)GetValue(ControlBrushProperty); set => SetValue(ControlBrushProperty, value); }
    internal bool IsWaveRunning => _waveRunning;
    internal int DrawingBuildCount { get; private set; }
    private readonly Dictionary<string, Rect> _chromeBounds = new();
    internal IReadOnlyDictionary<string, Rect> ChromeBounds => _chromeBounds;

    private readonly TranslateTransform _wavePosition = new();
    private readonly LinearGradientBrush _waveBrush;
    private bool _waveRequested, _waveRunning;
    private Window? _owner;
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
        CardWidth = DashboardVirtualizedNoteCardGrid.GridCellWidth,
        CardHeight = DashboardVirtualizedNoteCardGrid.GridCellHeight
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
        _waveBrush.GradientStops.Add(new GradientStop(Colors.Transparent, 0.30));
        _waveBrush.GradientStops.Add(new GradientStop(Colors.Transparent, 0.5));
        _waveBrush.GradientStops.Add(new GradientStop(Colors.Transparent, 0.70));
        _waveBrush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        SetResourceReference(SurfaceMaterial.DarkProperty, "SurfaceMaterialIsDark");
        Loaded += (_, _) =>
        {
            _owner = Window.GetWindow(this);
            if (_owner is not null)
            {
                _owner.Activated += OwnerActivityChanged;
                _owner.Deactivated += OwnerActivityChanged;
                _owner.StateChanged += OwnerActivityChanged;
            }
            MotionPolicy.Changed += RefreshWave;
            LocalizationService.CultureChanged += CultureChanged;
            RefreshWave();
        };
        Unloaded += (_, _) =>
        {
            if (_owner is not null)
            {
                _owner.Activated -= OwnerActivityChanged;
                _owner.Deactivated -= OwnerActivityChanged;
                _owner.StateChanged -= OwnerActivityChanged;
            }
            _owner = null;
            MotionPolicy.Changed -= RefreshWave;
            LocalizationService.CultureChanged -= CultureChanged;
            PauseWave();
        };
        IsVisibleChanged += (_, _) => RefreshWave();
    }

    public void SetLayout(Layout layout)
    {
        if (_layout == layout)
            return;

        _layout = layout;
        InvalidateDrawing();
    }

    public void StartWave()
    {
        _waveRequested = true;
        RefreshWave();
    }

    private void OwnerActivityChanged(object? sender, EventArgs e) => RefreshWave();
    private void CultureChanged(object? sender, EventArgs e) => InvalidateDrawing();

    private void RefreshWave()
    {
        var run = _waveRequested && IsVisible && IsLoaded && MotionPolicy.Spatial
            && _owner is { IsActive: true } && _owner.WindowState != WindowState.Minimized;
        if (run == _waveRunning) return;
        PauseWave();
        if (!run) return;
        _waveRunning = true;
        RenderTransitionClock.NativeStarted();

        _wavePosition.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1, 1,
            TimeSpan.FromMilliseconds(WaveDurationMs))
        {
            RepeatBehavior = RepeatBehavior.Forever
        });
    }

    private void PauseWave()
    {
        _wavePosition.BeginAnimation(TranslateTransform.XProperty, null);
        _wavePosition.X = -1;
        if (_waveRunning) RenderTransitionClock.NativeStopped();
        _waveRunning = false;
    }

    public void StopWave() { _waveRequested = false; PauseWave(); }

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsFinite(availableSize.Width) ? availableSize.Width : 0,
            double.IsFinite(availableSize.Height) ? availableSize.Height : 0);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        _drawing ??= BuildDrawing();
        drawingContext.DrawDrawing(_drawing);
    }

    private DrawingGroup BuildDrawing()
    {
        DrawingBuildCount++;
        _chromeBounds.Clear();
        var width = ActualWidth; var height = ActualHeight;
        var viewport = new Rect(0, 0, width, height);
        var drawing = new DrawingGroup();
        using var dc = drawing.Open();
        dc.PushClip(new RectangleGeometry(viewport));
        dc.DrawRectangle(Background, null, viewport);
        var dark = SurfaceMaterial.IsDark(this);
        var accent = (AccentBrush as SolidColorBrush)?.Color ?? Color.FromRgb(67, 92, 236);
        _waveBrush.GradientStops[2].Color = Color.FromArgb((byte)Math.Round(255 * (dark ? .20 : .14)), accent.R, accent.G, accent.B);
        var surfaces = new GeometryGroup(); var insets = new GeometryGroup();
        var cards = new GeometryGroup(); var controls = new GeometryGroup();
        var titles = new GeometryGroup(); var details = new GeometryGroup();
        var shimmer = new GeometryGroup(); var dividers = new GeometryGroup();
        var glyphs = new List<(AppIconKind Kind, Rect Frame)>();
        double Metric(string key, double fallback) => DashboardChromeMetrics.Number(this, key, fallback);
        T Style<T>(string key, DependencyProperty property, T fallback) => DashboardChromeMetrics.StyleValue(this, key, property, fallback);
        var cardRadius = TryFindResource("Radius.Card") is CornerRadius cr ? cr.TopLeft : 14;
        var controlRadius = TryFindResource("Radius.Control") is CornerRadius rr ? rr.TopLeft : 10;
        var trayPadding = Style("ReorderControlTrayStyle", System.Windows.Controls.Border.PaddingProperty, new Thickness(2));
        var sectionSize = Metric("Control.Size.Section", 28);
        var groupSize = Metric("Control.Size.Reorder", 24);
        var dividerWidth = Style("ActionStripDividerStyle", WidthProperty, 1d);
        var dividerHeight = Style("ActionStripDividerStyle", HeightProperty, 14d);
        var dividerMargin = Style("ActionStripDividerStyle", MarginProperty, new Thickness(6, 0, 6, 0));
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double Round(double value) => Math.Round(value * dpi) / dpi;
        var dividerSpace = Round(dividerWidth + dividerMargin.Left + dividerMargin.Right);
        var sectionStep = Round(sectionSize + 2);
        var groupStep = Round(groupSize + 2);
        var sectionStripWidth = trayPadding.Left + sectionSize * 2 + sectionStep + dividerSpace + trayPadding.Right;
        // ItemReorderControls' native Border retains its tray inset in compiled group headers.
        // Match that measured slot without painting an extra nested surface.
        var groupStripWidth = trayPadding.Left + groupSize * 2 + groupStep * 4 + dividerSpace + trayPadding.Right + trayPadding.Left + trayPadding.Right;
        var sectionStripHeight = sectionSize + trayPadding.Top + trayPadding.Bottom;
        var groupStripHeight = groupSize + (trayPadding.Top + trayPadding.Bottom) * 2;
        Rect Track(string key, double x, double y, double w, double h)
        {
            var frame = new Rect(x, y, Math.Max(0, w), Math.Max(0, h));
            if (frame.IntersectsWith(viewport)) _chromeBounds[key] = frame;
            return frame;
        }
        void Add(GeometryGroup group, double x, double y, double w, double h, double radius = 4)
        {
            if (w <= 0 || h <= 0 || !new Rect(x, y, w, h).IntersectsWith(viewport)) return;
            var shape = new RectangleGeometry(new Rect(x, y, w, h), radius, radius);
            shape.Freeze(); group.Children.Add(shape);
            if (ReferenceEquals(group, details) || ReferenceEquals(group, titles)) shimmer.Children.Add(shape);
        }
        void Glyph(AppIconKind kind, Rect button, double size = 14)
            => glyphs.Add((kind, new Rect(button.X + (button.Width - size) / 2, button.Y + (button.Height - size) / 2, size, size)));
        void Strip(string key, double x, double y, bool group, bool expanded)
        {
            double size = group ? groupSize : sectionSize;
            var frame = Track(key, x, y, group ? groupStripWidth : sectionStripWidth, group ? groupStripHeight : sectionStripHeight);
            Add(controls, frame.X, frame.Y, frame.Width, frame.Height, controlRadius);
            var slotX = x + trayPadding.Left + (group ? trayPadding.Left : 0);
            var slotY = y + (frame.Height - size) / 2;
            if (group) { Glyph(AppIconKind.GripVertical, new Rect(slotX, slotY, size, size)); slotX += groupStep; }
            Glyph(AppIconKind.ArrowUp, new Rect(slotX, slotY, size, size)); slotX += group ? groupStep : sectionStep;
            Glyph(AppIconKind.ArrowDown, new Rect(slotX, slotY, size, size)); slotX += size;
            if (group) slotX += trayPadding.Right;
            Add(dividers, slotX + dividerMargin.Left, y + (frame.Height - dividerHeight) / 2, dividerWidth, dividerHeight, .5);
            slotX += dividerSpace;
            Glyph(expanded ? AppIconKind.ChevronDown : AppIconKind.ChevronRight, new Rect(slotX, slotY, size, size)); slotX += group ? groupStep : size;
            if (group)
            {
                Glyph(AppIconKind.Color, new Rect(slotX, slotY, size, size)); slotX += groupStep;
                Glyph(AppIconKind.MoreVertical, new Rect(slotX, slotY, size, size));
            }
        }
        void Header(string key, double x, double y, double available, double titleWidth, bool expanded)
        {
            Strip(key, x, y, false, expanded);
            var titleX = x + sectionStripWidth + 8;
            Add(titles, titleX, y + (sectionStripHeight - 11) / 2, Math.Min(titleWidth, Math.Max(0, available - (titleX - x))), 11, 5);
        }
        void CardDetails(double x, double y, double w, double h, bool compact)
        {
            var inset = compact ? 11d : 15d;
            Add(titles, x + inset, y + (compact ? 13 : 18), Math.Min(w * .58, w - inset * 2), compact ? 9 : 11, 5);
            if (compact)
            {
                Add(details, x + inset, y + 33, w * .72, 7);
                if (_layout.Kind != DashboardKind.Notes) Add(details, x + inset, y + 51, w * .45, 7);
                return;
            }
            switch (_layout.Kind)
            {
                case DashboardKind.MindMaps:
                    var center = x + w / 2;
                    Add(details, center - 33, y + 54, 66, 18, 7);
                    Add(details, center - 89, y + 91, 55, 15, 7);
                    Add(details, center + 34, y + 91, 55, 15, 7);
                    break;
                case DashboardKind.Flashcards:
                    Add(details, x + w - inset - 23, y + 15, 23, 17, 8);
                    Add(details, x + inset, y + 55, w * .43, 8);
                    Add(details, x + inset, y + h - 37, w - inset * 2, 7);
                    Add(details, x + inset, y + h - 21, w * .38, 7);
                    break;
                default:
                    Add(details, x + inset, y + 47, w * .72, 8);
                    Add(details, x + inset, y + 67, w * .51, 8);
                    Add(details, x + inset, y + h - 26, Math.Min(84, w * .36), 12, 6);
                    break;
            }
        }

        var padding = TryFindResource("Shell.DashboardPadding") is Thickness p ? p : new Thickness(20, 6, 20, 20);
        var recentPadding = TryFindResource("Shell.RecentPadding") is Thickness rp ? rp : new Thickness(20, 14, 20, 4);
        var stripInsets = TryFindResource("Shell.RecentStripInsets") is Thickness si ? si : new Thickness(17, 14, 17, 17);
        var left = padding.Left;
        var fullUsable = Math.Max(0, width - padding.Left - padding.Right);
        var top = padding.Top;
        if (_layout.ShowRecent)
        {
            var recentHeight = sectionStripHeight;
            var recentFrame = Track("Recent.Strip", recentPadding.Left, recentPadding.Top, sectionSize + trayPadding.Left + trayPadding.Right, sectionStripHeight);
            Add(controls, recentFrame.X, recentFrame.Y, recentFrame.Width, recentFrame.Height, controlRadius);
            Glyph(_layout.ExpandRecent ? AppIconKind.ChevronDown : AppIconKind.ChevronRight, recentFrame);
            Add(titles, recentFrame.Right + 8, recentFrame.Y + 10, Math.Min(138, fullUsable * .4), 11, 5);
            if (_layout.ExpandRecent)
            {
                var notes = _layout.Kind == DashboardKind.Notes;
                var metaSize = Metric("Type.Meta", 13);
                var titleHeight = DashboardChromeMetrics.Text(this, "Ag", notes ? Metric("Type.CardTitle", 14.5) : metaSize, FontWeights.SemiBold).Height;
                var lineHeight = DashboardChromeMetrics.Text(this, "Ag", metaSize, FontWeights.Normal).Height;
                var cardHeight = Math.Ceiling(16 + titleHeight + lineHeight * (notes ? 1 : 2) + (notes ? 3 : 5));
                var recentY = recentPadding.Top + sectionStripHeight + 6 + stripInsets.Top + 5;
                var recentX = recentPadding.Left + stripInsets.Left;
                var recentWidth = Math.Max(0, fullUsable - stripInsets.Left - stripInsets.Right);
                for (var i = 0; i < Math.Ceiling(recentWidth / 167); i++)
                {
                    var x = recentX + i * 167; var w = Math.Min(160, recentX + recentWidth - x);
                    Add(cards, x, recentY, w, cardHeight, controlRadius);
                    CardDetails(x, recentY, w, cardHeight, true);
                }
                recentHeight += 6 + stripInsets.Top + 5 + cardHeight + 5 + stripInsets.Bottom + Metric("GlobalHorizontalScrollBarHeight", 8);
            }
            top += recentPadding.Top + recentHeight + recentPadding.Bottom;
        }

        // The scrollbar consumes viewport width; do not draw content beneath its real track.
        var hasListingBody = _layout.ShowContent && _layout.ExpandContent && (_layout.ShowGroups || _layout.ShowUngrouped);
        var reserveScroll = hasListingBody || (_layout.ShowCalendar && _layout.ExpandCalendar && top + 388 + 112 > height);
        var usable = Math.Max(0, fullUsable - (reserveScroll ? Metric("GlobalVerticalScrollBarWidth", 8) : 0));
        var plannerWidth = Math.Max(0, usable - 48);
        var stacked = plannerWidth < DashboardCalendarPanel.StackBelowWidth;
        var agendaWidth = Math.Max(0, stacked ? plannerWidth : plannerWidth - DashboardCalendarPanel.Rail - DashboardCalendarPanel.Gap);
        var filterWidth = CalendarPlannerHeaderPanel.FilterWidth(this);
        var agendaExtra = CalendarPlannerHeaderPanel.NeedsWrap(agendaWidth, filterWidth) ? CalendarPlannerHeaderPanel.WrapExtraHeight : 0;
        var actionWidths = DashboardChromeMetrics.CalendarActions(this);
        var calendarTitle = DashboardChromeMetrics.Text(this, LocalizationService.GetString("CalendarSection"), Metric("Type.SectionTitle", 15), FontWeights.SemiBold).Width;
        var sectionExtra = CalendarSectionHeaderPanel.NeedsWrap(usable - 28, sectionStripWidth + 8 + calendarTitle,
            actionWidths.Today + 8 + actionWidths.Add) ? CalendarSectionHeaderPanel.WrapExtraHeight : 0;
        var calendarHeight = (_layout.ExpandCalendar ? (stacked ? DashboardCalendarPanel.StackedHeight : DashboardCalendarPanel.WideHeight) + agendaExtra + 48 : 24)
            + CalendarSectionHeaderPanel.RowHeight + 8 + sectionExtra;

        void Calendar(double y)
        {
            Track("Calendar.Panel", left, y, usable, calendarHeight);
            Add(surfaces, left, y, usable, calendarHeight, cardRadius);
            Header("Calendar.Strip", left + 14, y + 14, usable - 28, 90, _layout.ExpandCalendar);
            var add = Track("Calendar.Add", left + usable - 14 - actionWidths.Add, y + 12 + sectionExtra, actionWidths.Add, 36);
            var today = Track("Calendar.Today", add.X - 8 - actionWidths.Today, add.Y, actionWidths.Today, 36);
            foreach (var action in new[] { today, add })
            {
                Add(controls, action.X, action.Y, action.Width, action.Height, controlRadius);
                Add(titles, action.X + 12, action.Y + 14, Math.Max(0, action.Width - 24), 8);
            }
            if (!_layout.ExpandCalendar) return;
            var innerY = y + 56 + sectionExtra;
            var bodyHeight = (stacked ? DashboardCalendarPanel.StackedHeight : DashboardCalendarPanel.WideHeight) + agendaExtra;
            Add(insets, left + 12, innerY, usable - 24, bodyHeight + 24, cardRadius);
            var bodyX = left + 24; var bodyY = innerY + 12;
            var calendarWidth = 256d; var calendarControlHeight = Metric("Dashboard.CalendarHeight", 288);
            var calendarX = bodyX + ((stacked ? plannerWidth : DashboardCalendarPanel.Rail) - calendarWidth) / 2;
            var calendarMargin = Style("CalendarItemStyle", MarginProperty, new Thickness(0, 3, 0, 3));
            var calendarY = bodyY + (stacked ? 0 : (bodyHeight - calendarControlHeight) / 2) + calendarMargin.Top;
            var previous = Track("Calendar.Previous", calendarX, calendarY, 36, 36);
            var next = Track("Calendar.Next", calendarX + calendarWidth - 36, calendarY, 36, 36);
            foreach (var button in new[] { previous, next }) Add(controls, button.X, button.Y, button.Width, button.Height, controlRadius);
            Glyph(AppIconKind.ChevronLeft, previous, 16); Glyph(AppIconKind.ChevronRight, next, 16);
            Add(titles, calendarX + 57, calendarY + 13, 142, 10, 5);
            var gridY = calendarY + 44;
            for (var c = 0; c < 7; c++)
            {
                var cx = calendarX + calendarWidth * (c + .5) / 7;
                Add(details, cx - 5, gridY + 9, 10, 5, 2.5);
                for (var r = 0; r < 6; r++)
                {
                    var markWidth = (c + r) % 3 == 0 ? 8 : 11;
                    Add(details, cx - markWidth / 2d, gridY + 24 + r * 36 + 14, markWidth, 8);
                }
            }
            var agendaX = stacked ? bodyX : bodyX + DashboardCalendarPanel.Rail + DashboardCalendarPanel.Gap;
            var agendaY = stacked ? bodyY + DashboardCalendarPanel.AgendaTop : bodyY;
            var modes = Track("Calendar.Modes", agendaX, agendaY, Math.Min(CalendarPlannerHeaderPanel.ModeWidth, agendaWidth), 32);
            Add(controls, modes.X, modes.Y, modes.Width, modes.Height, controlRadius);
            for (var i = 0; i < 3; i++) Add(titles, modes.X + i * modes.Width / 3 + 17, modes.Y + 13, Math.Max(0, modes.Width / 3 - 34), 6, 3);
            var filter = Track("Calendar.Filter", agendaX + (agendaExtra > 0 ? 0 : CalendarPlannerHeaderPanel.ModeWidth + 12), agendaY + agendaExtra,
                Math.Min(filterWidth, agendaExtra > 0 ? agendaWidth : Math.Max(0, agendaWidth - CalendarPlannerHeaderPanel.ModeWidth - 12)), 32);
            Add(controls, filter.X, filter.Y, filter.Width, filter.Height, controlRadius);
            Add(titles, filter.X + 10, filter.Y + 12, Math.Max(0, filter.Width - 48), 8);
            Glyph(AppIconKind.ChevronDown, new Rect(filter.Right - 32, filter.Y, 32, 32), 12);
            Add(titles, agendaX, agendaY + 46 + agendaExtra, Math.Min(176, agendaWidth * .7), 11, 5);
            for (var r = 0; r < 2; r++)
            {
                var rowY = agendaY + 76 + agendaExtra + r * 68;
                Add(cards, agendaX, rowY, agendaWidth, 60, cardRadius);
                Add(titles, agendaX + 14, rowY + 15, 35, 10, 5);
                Add(titles, agendaX + 76, rowY + 14, Math.Max(0, Math.Min(156, agendaWidth - 120)), 10, 5);
                Add(details, agendaX + 76, rowY + 34, Math.Max(0, Math.Min(104, agendaWidth - 120)), 7);
            }
        }
        double CellHeight(bool grouped) => grouped ? _layout.Kind switch
        {
            DashboardKind.Flashcards or DashboardKind.Quizzes => Metric("Dashboard.GroupedCardHeight", 128),
            DashboardKind.MindMaps => 152, _ => DashboardVirtualizedNoteCardGrid.GridCellHeight
        } : Math.Max(40, _layout.CardHeight);
        void CardRows(double y, bool grouped, double x, double available)
        {
            if (available <= 0 || y >= height) return;
            var notes = _layout.Kind == DashboardKind.Notes; var gap = notes ? 0 : 12;
            var cellWidth = Math.Min(notes ? DashboardVirtualizedNoteCardGrid.GridCellWidth : grouped ? Metric("Dashboard.GroupedCardWidth", 240) : Math.Max(80, _layout.CardWidth), available);
            var cellHeight = CellHeight(grouped);
            var columns = Math.Max(1, (int)((available + gap) / (cellWidth + gap)));
            for (var row = y; row < height; row += cellHeight)
            {
                for (var i = 0; i < columns; i++)
                {
                    var cx = x + i * (cellWidth + gap) + (notes ? 12 : 0);
                    var cy = row + (notes ? 12 : 0);
                    var cw = Math.Max(0, cellWidth - (notes ? 24 : 0)); var ch = cellHeight - (notes ? 24 : 10);
                    Add(cards, cx, cy, cw, ch, cardRadius); CardDetails(cx, cy, cw, ch, false);
                }
            }
        }
        double Subsection(double y, bool grouped, bool expanded)
        {
            Header(grouped ? "Groups.Strip" : "Ungrouped.Strip", left + 24, y + 2, usable - 48, grouped ? 66 : 96, expanded);
            if (!expanded) return y + 42;
            if (!grouped) { CardRows(y + 42, false, left + 14, usable - 28); return height + 1; }
            var groupY = y + 42; var groupWidth = Math.Max(0, usable - 28);
            var headerX = left + 26; var headerWidth = Math.Max(0, groupWidth - 24);
            var compact = headerWidth < DashboardGroupHeaderPanel.CompactWidth;
            var headerHeight = DashboardGroupHeaderPanel.RowHeight * (compact ? 2 : 1) + (compact ? DashboardGroupHeaderPanel.RowGap : 0);
            // An unknown expanded collection owns the remaining placeholder viewport.
            // Do not invent the next section's position from an assumed one-row group.
            var groupHeight = Math.Max(24 + headerHeight + 8 + CellHeight(true), height - groupY + 24);
            Track("Group.Panel", left + 14, groupY, groupWidth, groupHeight);
            Add(insets, left + 14, groupY, groupWidth, groupHeight, cardRadius);
            var headerY = groupY + 12;
            Strip("Group.Strip", headerX, headerY + (compact ? DashboardGroupHeaderPanel.RowHeight + DashboardGroupHeaderPanel.RowGap : 0)
                + (DashboardGroupHeaderPanel.RowHeight - groupStripHeight) / 2, true, true);
            var nameX = headerX + (compact ? 0 : groupStripWidth + DashboardGroupHeaderPanel.Gap);
            Add(titles, nameX + 8, headerY + 9, 4, 18, 2);
            Add(titles, nameX + 20, headerY + 12, Math.Min(106, Math.Max(0, headerWidth - (nameX - headerX) - 48)), 11, 5);
            CardRows(groupY + 12 + headerHeight + 8, true, headerX, headerWidth);
            return height + 1;
        }
        double Content(double y)
        {
            var body = _layout.ExpandContent && (_layout.ShowGroups || _layout.ShowUngrouped);
            Add(surfaces, left, y, usable, body ? Math.Max(70, height - y + 24) : 70, cardRadius);
            Track("Content.Panel", left, y, usable, body ? Math.Max(70, height - y) : 70);
            Header("Content.Strip", left + 16, y + 14, usable - 32, 114, _layout.ExpandContent);
            if (!body) return y + 84;
            var sectionY = y + 56;
            if (_layout.GroupsFirst)
            {
                if (_layout.ShowGroups) sectionY = Subsection(sectionY, true, _layout.ExpandGroups);
                if (_layout.ShowUngrouped) Subsection(sectionY, false, _layout.ExpandUngrouped);
            }
            else
            {
                if (_layout.ShowUngrouped) sectionY = Subsection(sectionY, false, _layout.ExpandUngrouped);
                if (_layout.ShowGroups) Subsection(sectionY, true, _layout.ExpandGroups);
            }
            return height + 1;
        }
        if (_layout.CalendarFirst)
        {
            if (_layout.ShowCalendar) { Calendar(top); top += calendarHeight + 14; }
            if (_layout.ShowContent) Content(top);
        }
        else
        {
            if (_layout.ShowContent) top = Content(top);
            if (_layout.ShowCalendar) Calendar(top);
        }
        var creationSize = Metric("Control.Size.Creation", 52);
        var creation = Track("Creation", width - 40 - creationSize, height - 36 - creationSize, creationSize, creationSize);
        Add(controls, creation.X, creation.Y, creation.Width, creation.Height, cardRadius);
        Glyph(AppIconKind.Plus, creation, 22);

        void Faces(GeometryGroup shapes, Brush? brush, SurfaceMaterialKind kind = SurfaceMaterialKind.Raised)
        {
            var paint = SurfaceMaterial.Paint(brush, kind, dark);
            foreach (RectangleGeometry shape in shapes.Children)
            {
                dc.DrawGeometry(paint?.Fill ?? brush, null, shape);
                if (paint is null) continue;
                var innerBounds = shape.Rect; innerBounds.Inflate(-1, -1);
                if (innerBounds.Width <= 0 || innerBounds.Height <= 0) continue;
                var inner = new RectangleGeometry(innerBounds, Math.Max(0, shape.RadiusX - 1), Math.Max(0, shape.RadiusY - 1));
                var edge = new CombinedGeometry(GeometryCombineMode.Exclude, shape, inner); edge.Freeze();
                dc.DrawGeometry(paint.Edge, null, edge);
            }
        }
        Faces(surfaces, SurfaceBrush); Faces(insets, InsetBrush); Faces(cards, SurfaceBrush);
        Faces(controls, ControlBrush ?? InsetBrush, SurfaceMaterialKind.Neutral);
        var surface = (SurfaceBrush as SolidColorBrush)?.Color ?? (dark ? Colors.Black : Colors.White);
        var foreground = (DetailBrush as SolidColorBrush)?.Color ?? (dark ? Colors.White : Colors.Black);
        Brush Ink(double amount) { var brush = new SolidColorBrush(SurfaceMaterialPalette.Mix(surface, foreground, amount)); brush.Freeze(); return brush; }
        titles.Freeze(); details.Freeze(); shimmer.Freeze(); dividers.Freeze();
        dc.DrawGeometry(Ink(dark ? .32 : .27), null, titles);
        dc.DrawGeometry(Ink(dark ? .21 : .16), null, details);
        dc.PushOpacity(.55); dc.DrawGeometry(TryFindResource("ActionDividerBrush") as Brush ?? DetailBrush, null, dividers); dc.Pop();
        dc.PushOpacity(.42);
        var glyphInk = TryFindResource("IconButtonForeground") as Brush ?? DetailBrush;
        foreach (var glyph in glyphs) AppIcon.DrawGlyph(dc, AppIconKindMap.ToLucide(glyph.Kind), glyph.Frame, glyphInk);
        dc.Pop();
        dc.DrawGeometry(_waveBrush, null, shimmer);
        dc.Pop();
        return drawing;
    }
}
