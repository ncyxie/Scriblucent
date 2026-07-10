using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using NoteCards.Behaviors;
using NoteCards.Controls;
using NoteCards;
using NoteCards.ViewModels;

namespace NoteCards.Animations;

/// <summary>
/// Lifted bitmap ghost for dashboard card drag. Uses a topmost transparent window because
/// Popups and adorners do not paint reliably during WPF's modal drag loop.
/// </summary>
public static class DashboardDragGhost
{
    private const double NoteSourceOpacityDuringDrag = 0.22;
    private const double ListingSourceOpacityDuringDrag = 0.18;
    private const double GhostScale = 1.045;
    private const double GhostFloatYOffset = -5;
    private const double GhostLiftAboveGrabY = 14;
    private const double GhostShadowPad = 24;
    private const double CaptureMargin = 16;
    private const double NoteCardRestWidth = 220;
    private const double NoteCardRestMinHeight = 140;

    private static WeakReference<FrameworkElement>? _activeDragSource;
    private static DragGhostWindow? _activeGhostWindow;
    private static NoteCardViewModel? _activeDraggedNote;
    private static NoteCardControl? _activeNoteDragSource;
    private static object? _activeDraggedListing;
    private static FrameworkElement? _activeListingDragSource;

    public static bool IsActiveDragSource(FrameworkElement element)
        => _activeDragSource?.TryGetTarget(out var source) == true
            && ReferenceEquals(source, element);

    public static bool IsNoteDropGlowSyncActive => _activeDraggedNote is not null;

    public static bool IsListingDropVisualSyncActive => _activeDraggedListing is not null;

    public static NoteCardControl? ActiveNoteDragSource => _activeNoteDragSource;

    public static bool TryGetCursorScreenPoint(out Point screenPoint)
    {
        screenPoint = default;
        if (!GetCursorPos(out var cursor))
            return false;

        screenPoint = new Point(cursor.X, cursor.Y);
        return true;
    }

    public static DragDropEffects Execute(
        FrameworkElement dragSource,
        DataObject data,
        DragDropEffects allowedEffects,
        Point grabOffsetRelativeToSource)
    {
        DragGhostWindow? ghostWindow = null;
        DragDropEffects result;

        try
        {
            if (UiMotion.PrefersReducedMotion)
            {
                result = DragDrop.DoDragDrop(dragSource, data, allowedEffects);
                return result;
            }

            var owner = Window.GetWindow(dragSource);
            if (owner is null)
            {
                result = DragDrop.DoDragDrop(dragSource, data, allowedEffects);
                return result;
            }
            _activeDragSource = new WeakReference<FrameworkElement>(dragSource);
            if (dragSource is NoteCardControl { DataContext: NoteCardViewModel draggedNote } noteDragSource)
            {
                _activeDraggedNote = draggedNote;
                _activeNoteDragSource = noteDragSource;
            }
            else if (ResolveListingDragPayload(dragSource) is { } draggedListing)
            {
                _activeDraggedListing = draggedListing;
                _activeListingDragSource = dragSource;
            }

            dragSource.UpdateLayout();
            ResetRestVisualState(dragSource);

            var captureSurface = ResolveCaptureSurface(dragSource);
            captureSurface.UpdateLayout();

            var (captureWidth, captureHeight) = GetRestCaptureSize(dragSource, captureSurface);
            if (captureWidth <= 1 || captureHeight <= 1)
            {
                result = DragDrop.DoDragDrop(dragSource, data, allowedEffects);
                return result;
            }

            var grabOffsetOnSurface = ResolveGrabOffsetOnSurface(
                dragSource,
                captureSurface,
                grabOffsetRelativeToSource);

            var capture = CaptureSnapshot(dragSource, captureSurface, captureWidth, captureHeight);
            if (capture is not { } snapshot)
            {
                result = DragDrop.DoDragDrop(dragSource, data, allowedEffects);
                return result;
            }

            var grabOffset = new Point(
                snapshot.ContentOffsetX + grabOffsetOnSurface.X,
                snapshot.ContentOffsetY + grabOffsetOnSurface.Y);

            var bakedGhost = BakeGhostVisual(snapshot, grabOffset, owner);
            ghostWindow = new DragGhostWindow(bakedGhost);
            _activeGhostWindow = ghostWindow;

            var cursorApplied = false;
            var syncDropVisualsOnRender = _activeDraggedNote is not null || _activeDraggedListing is not null;

            void SyncDropVisualsNow()
            {
                if (owner is not MainWindow mainWindow)
                    return;

                if (_activeDraggedNote is not null)
                    mainWindow.SyncNoteDropGlowDuringDrag(_activeDraggedNote, _activeNoteDragSource);

                if (_activeDraggedListing is not null)
                    mainWindow.SyncDashboardListingDropVisualDuringDrag(_activeDraggedListing, _activeListingDragSource);
            }

            void OnRendering(object? sender, EventArgs e)
                => SyncDropVisualsNow();

            void OnGiveFeedback(object sender, GiveFeedbackEventArgs e)
            {
                e.UseDefaultCursors = false;
                if (!cursorApplied)
                {
                    Mouse.SetCursor(Cursors.Hand);
                    cursorApplied = true;
                }

                e.Handled = true;
                ghostWindow?.AlignToCursor();
            }

            void OnQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
                => ghostWindow?.AlignToCursor();

            dragSource.GiveFeedback += OnGiveFeedback;
            dragSource.QueryContinueDrag += OnQueryContinueDrag;
            if (syncDropVisualsOnRender)
                CompositionTarget.Rendering += OnRendering;

            try
            {
                ghostWindow.AlignToCursor();
                ghostWindow.Show();
                ghostWindow.AlignToCursor();
                DimDragSource(dragSource);
                SyncDropVisualsNow();

                result = DragDrop.DoDragDrop(dragSource, data, allowedEffects);
                return result;
            }
            finally
            {
                dragSource.GiveFeedback -= OnGiveFeedback;
                dragSource.QueryContinueDrag -= OnQueryContinueDrag;
                if (syncDropVisualsOnRender)
                    CompositionTarget.Rendering -= OnRendering;
                RestoreDragSource(dragSource);
            }
        }
        catch
        {
            RestoreDragSource(dragSource);
            result = DragDrop.DoDragDrop(dragSource, data, allowedEffects);
            return result;
        }
        finally
        {
            _activeDragSource = null;
            _activeDraggedNote = null;
            _activeNoteDragSource = null;
            _activeDraggedListing = null;
            _activeListingDragSource = null;
            ForceCloseActiveGhost(ghostWindow);

            if (Window.GetWindow(dragSource) is MainWindow mainWindow)
            {
                mainWindow.ClearAllDragDropVisuals();
                mainWindow.EnsureActiveListingDashboardInteractive();
            }
        }
    }

    private static object? ResolveListingDragPayload(FrameworkElement element)
    {
        if (element.Tag is FlashcardSetViewModel or MindMapViewModel or QuizViewModel)
            return element.Tag;

        return element.DataContext switch
        {
            FlashcardSetViewModel flashcardSet => flashcardSet,
            MindMapViewModel mindMap => mindMap,
            QuizViewModel quiz => quiz,
            _ => null
        };
    }

    private static void ForceCloseActiveGhost(DragGhostWindow? ghostWindow = null)
    {
        var target = ghostWindow ?? _activeGhostWindow;
        if (target is null)
            return;

        try
        {
            target.Close();
        }
        catch
        {
            // Best-effort cleanup if the HWND is already gone.
        }

        if (ReferenceEquals(_activeGhostWindow, target))
            _activeGhostWindow = null;
    }

    private static void ResetRestVisualState(FrameworkElement dragSource)
    {
        UiMotion.Stop(dragSource);

        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(dragSource);
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.X = 0;
        translate.Y = 0;

        ResetShadowToRest(ResolveShadowHost(dragSource));
        dragSource.UpdateLayout();
    }

    private static void DimDragSource(FrameworkElement dragSource)
    {
        ResetShadowToRest(ResolveShadowHost(dragSource));

        if (dragSource is NoteCardControl noteCard)
            noteCard.SetDragDimmed(true, NoteSourceOpacityDuringDrag);
        else
            dragSource.Opacity = ListingSourceOpacityDuringDrag;
    }

    private static void RestoreDragSource(FrameworkElement dragSource)
    {
        if (dragSource is NoteCardControl noteCard)
            noteCard.SetDragDimmed(false);
        else
            dragSource.ClearValue(UIElement.OpacityProperty);
    }

    private static FrameworkElement ResolveShadowHost(FrameworkElement dragSource)
    {
        if (dragSource.FindName("CardBorder") is FrameworkElement cardBorder)
            return cardBorder;

        if (CardSurfaceMotionBehavior.GetIsEnabled(dragSource)
            && CardSurfaceMotionBehavior.GetProfile(dragSource) == CardSurfaceMotionProfile.Listing)
        {
            var childCount = VisualTreeHelper.GetChildrenCount(dragSource);
            for (var i = 0; i < childCount; i++)
            {
                if (VisualTreeHelper.GetChild(dragSource, i) is Border innerBorder)
                    return innerBorder;
            }
        }

        return dragSource;
    }

    private static void ResetShadowToRest(FrameworkElement shadowHost)
    {
        UiMotion.Stop(shadowHost);
        var shadow = UiMotion.EnsureShadow(shadowHost);
        shadow.Opacity = UiMotion.ShadowOpacityRest;
        shadow.BlurRadius = UiMotion.ShadowBlurRest;
        shadow.ShadowDepth = 1;
    }

    private static (double Width, double Height) GetRestCaptureSize(
        FrameworkElement dragSource,
        FrameworkElement captureSurface)
    {
        if (dragSource is NoteCardControl)
        {
            return (
                NoteCardRestWidth,
                Math.Max(NoteCardRestMinHeight, captureSurface.ActualHeight));
        }

        if (IsListingDragSource(dragSource))
        {
            dragSource.UpdateLayout();
            return (
                Math.Max(1, dragSource.ActualWidth),
                Math.Max(1, dragSource.ActualHeight));
        }

        captureSurface.UpdateLayout();
        return (
            Math.Max(1, captureSurface.ActualWidth),
            Math.Max(1, captureSurface.ActualHeight));
    }

    private static bool IsListingDragSource(FrameworkElement dragSource)
        => CardSurfaceMotionBehavior.GetIsEnabled(dragSource)
            && CardSurfaceMotionBehavior.GetProfile(dragSource) == CardSurfaceMotionProfile.Listing;

    private static Point ResolveGrabOffsetOnSurface(
        FrameworkElement dragSource,
        FrameworkElement captureSurface,
        Point grabOffsetRelativeToSource)
    {
        if (ReferenceEquals(dragSource, captureSurface))
            return grabOffsetRelativeToSource;

        try
        {
            return dragSource.TransformToVisual(captureSurface).Transform(grabOffsetRelativeToSource);
        }
        catch
        {
            return grabOffsetRelativeToSource;
        }
    }

    private static BakedGhost BakeGhostVisual(SnapshotCapture snapshot, Point grabOffset, Window owner)
    {
        var contentW = snapshot.Width;
        var contentH = snapshot.Height;
        var scaledW = contentW * GhostScale;
        var scaledH = contentH * GhostScale;
        var bakeW = scaledW + GhostShadowPad * 2;
        var bakeH = scaledH + GhostShadowPad * 2 + GhostLiftAboveGrabY;

        var centerX = contentW * 0.5;
        var centerY = contentH * 0.5;
        var originX = GhostShadowPad + centerX * (1 - GhostScale);
        var originY = GhostShadowPad + GhostLiftAboveGrabY + GhostFloatYOffset + centerY * (1 - GhostScale);

        var dpi = VisualTreeHelper.GetDpi(owner);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var shadowRect = new Rect(originX + 5, originY + 9, scaledW, scaledH);
            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(52, 0, 0, 0)),
                null,
                shadowRect,
                12 * GhostScale,
                12 * GhostScale);

            dc.PushTransform(new TransformGroup
            {
                Children =
                {
                    new TranslateTransform(originX, originY),
                    new ScaleTransform(GhostScale, GhostScale)
                }
            });
            dc.DrawImage(snapshot.Image, new Rect(0, 0, contentW, contentH));
            dc.Pop();
        }

        var pixelW = Math.Max(1, (int)Math.Ceiling(bakeW * dpi.DpiScaleX));
        var pixelH = Math.Max(1, (int)Math.Ceiling(bakeH * dpi.DpiScaleY));
        var baked = new RenderTargetBitmap(
            pixelW,
            pixelH,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        baked.Render(visual);
        baked.Freeze();

        var grabAnchorXDip = originX + grabOffset.X * GhostScale;
        var grabAnchorYDip = originY + grabOffset.Y * GhostScale;
        var grabPx = ResolveTransformToDevice(owner).Transform(new Point(grabAnchorXDip, grabAnchorYDip));

        return new BakedGhost(
            baked,
            bakeW,
            bakeH,
            (int)Math.Round(grabPx.X),
            (int)Math.Round(grabPx.Y));
    }

    private static SnapshotCapture? CaptureSnapshot(
        FrameworkElement dragSource,
        FrameworkElement surface,
        double width,
        double height)
    {
        var dpi = VisualTreeHelper.GetDpi(surface);
        var savedCache = surface.CacheMode;
        surface.CacheMode = null;

        try
        {
            var preferDirectCapture = dragSource is not NoteCardControl;

            if (preferDirectCapture && TryDirectCapture(surface, width, height, dpi, out var direct))
                return new SnapshotCapture(direct, width, height, 0, 0);

            if (TryPaddedVisualCapture(surface, width, height, dpi, out var padded))
                return padded;

            if (!preferDirectCapture && TryDirectCapture(surface, width, height, dpi, out direct))
                return new SnapshotCapture(direct, width, height, 0, 0);
        }
        finally
        {
            surface.CacheMode = savedCache;
        }

        return null;
    }

    private static bool TryDirectCapture(
        FrameworkElement element,
        double width,
        double height,
        DpiScale dpi,
        out ImageSource image)
    {
        image = null!;
        var savedEffect = element.Effect;
        element.Effect = null;

        try
        {
            var pixelWidth = Math.Max(1, (int)Math.Ceiling(width * dpi.DpiScaleX));
            var pixelHeight = Math.Max(1, (int)Math.Ceiling(height * dpi.DpiScaleY));
            var rtb = new RenderTargetBitmap(
                pixelWidth,
                pixelHeight,
                dpi.PixelsPerInchX,
                dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);
            rtb.Render(element);
            rtb.Freeze();
            image = rtb;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            element.Effect = savedEffect;
        }
    }

    private static bool TryPaddedVisualCapture(
        FrameworkElement element,
        double width,
        double height,
        DpiScale dpi,
        out SnapshotCapture capture)
    {
        capture = default;
        var paddedWidth = width + CaptureMargin * 2;
        var paddedHeight = height + CaptureMargin * 2;
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(paddedWidth * dpi.DpiScaleX));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(paddedHeight * dpi.DpiScaleY));

        var savedEffect = element.Effect;
        element.Effect = null;
        try
        {
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                var brush = new VisualBrush(element)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                    Viewbox = new Rect(0, 0, width, height),
                    ViewboxUnits = BrushMappingMode.Absolute
                };
                brush.Freeze();
                context.DrawRectangle(
                    brush,
                    null,
                    new Rect(CaptureMargin, CaptureMargin, width, height));
            }

            var image = RenderDrawingToBitmap(drawing, pixelWidth, pixelHeight, dpi);
            capture = new SnapshotCapture(image, paddedWidth, paddedHeight, CaptureMargin, CaptureMargin);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            element.Effect = savedEffect;
        }
    }

    private static FrameworkElement ResolveCaptureSurface(FrameworkElement dragSource)
    {
        if (dragSource.FindName("CardBorder") is FrameworkElement cardBorder)
            return cardBorder;

        if (IsListingDragSource(dragSource))
            return dragSource;

        var childCount = VisualTreeHelper.GetChildrenCount(dragSource);
        for (var i = 0; i < childCount; i++)
        {
            if (VisualTreeHelper.GetChild(dragSource, i) is Border innerBorder)
                return innerBorder;
        }

        return dragSource;
    }

    private static ImageSource RenderDrawingToBitmap(
        DrawingVisual drawing,
        int pixelWidth,
        int pixelHeight,
        DpiScale dpi)
    {
        var rtb = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        rtb.Render(drawing);
        rtb.Freeze();
        return rtb;
    }

    private static Matrix ResolveTransformFromDevice(Window owner)
    {
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        if (ownerHandle != IntPtr.Zero
            && HwndSource.FromHwnd(ownerHandle)?.CompositionTarget is { } hwndTarget)
        {
            return hwndTarget.TransformFromDevice;
        }

        if (PresentationSource.FromVisual(owner)?.CompositionTarget is { } visualTarget)
            return visualTarget.TransformFromDevice;

        return Matrix.Identity;
    }

    private static Matrix ResolveTransformToDevice(Window owner)
    {
        var matrix = ResolveTransformFromDevice(owner);
        if (!matrix.HasInverse)
            return Matrix.Identity;

        matrix.Invert();
        return matrix;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private readonly struct BakedGhost(
        ImageSource image,
        double widthDip,
        double heightDip,
        int grabAnchorXPx,
        int grabAnchorYPx)
    {
        public ImageSource Image { get; } = image;
        public double WidthDip { get; } = widthDip;
        public double HeightDip { get; } = heightDip;
        public int GrabAnchorXPx { get; } = grabAnchorXPx;
        public int GrabAnchorYPx { get; } = grabAnchorYPx;
    }

    private readonly struct SnapshotCapture(
        ImageSource image,
        double width,
        double height,
        double contentOffsetX,
        double contentOffsetY)
    {
        public ImageSource Image { get; } = image;
        public double Width { get; } = width;
        public double Height { get; } = height;
        public double ContentOffsetX { get; } = contentOffsetX;
        public double ContentOffsetY { get; } = contentOffsetY;
    }

    private sealed class DragGhostWindow : Window
    {
        private const int WmNcHitTest = 0x0084;
        private const int HtTransparent = -1;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoSendChanging = 0x0400;

        private static readonly IntPtr HwndTopMost = new(-1);

        private readonly int _grabAnchorXPx;
        private readonly int _grabAnchorYPx;
        private IntPtr _hwnd;
        private int _lastX = int.MinValue;
        private int _lastY = int.MinValue;

        public DragGhostWindow(BakedGhost bakedGhost)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            IsHitTestVisible = false;
            ShowActivated = false;
            Focusable = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Left = 0;
            Top = 0;

            _grabAnchorXPx = bakedGhost.GrabAnchorXPx;
            _grabAnchorYPx = bakedGhost.GrabAnchorYPx;

            var image = new Image
            {
                Source = bakedGhost.Image,
                Width = bakedGhost.WidthDip,
                Height = bakedGhost.HeightDip,
                Opacity = 1,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.LowQuality);
            image.CacheMode = new BitmapCache
            {
                EnableClearType = false,
                SnapsToDevicePixels = true
            };

            Content = image;
            Width = bakedGhost.WidthDip;
            Height = bakedGhost.HeightDip;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _hwnd = new WindowInteropHelper(this).Handle;
            if (PresentationSource.FromVisual(this) is HwndSource source)
                source.AddHook(ClickThroughHook);
        }

        public void AlignToCursor()
        {
            if (_hwnd == IntPtr.Zero || !GetCursorPos(out var cursor))
                return;

            var x = cursor.X - _grabAnchorXPx;
            var y = cursor.Y - _grabAnchorYPx;

            if (x == _lastX && y == _lastY)
                return;

            _lastX = x;
            _lastY = y;

            SetWindowPos(
                _hwnd,
                HwndTopMost,
                x,
                y,
                0,
                0,
                SwpNoSize | SwpNoActivate | SwpNoSendChanging);
        }

        private static IntPtr ClickThroughHook(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (msg == WmNcHitTest)
            {
                handled = true;
                return new IntPtr(HtTransparent);
            }

            return IntPtr.Zero;
        }
    }
}