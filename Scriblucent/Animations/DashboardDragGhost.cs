using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent;
using Scriblucent.ViewModels;

namespace Scriblucent.Animations;

/// <summary>
/// Lifted bitmap ghost for dashboard card drag. Uses a topmost transparent window because
/// Popups and adorners do not paint reliably during WPF's modal drag loop.
/// </summary>
public static class DashboardDragGhost
{
    private const double NoteSourceOpacityDuringDrag = 0.22;
    private const double ListingSourceOpacityDuringDrag = 0.18;
    private const double GhostFloatYOffset = -5;
    private const double GhostLiftAboveGrabY = 14;
    private const double GhostShadowPad = 24;
    private const double CaptureMargin = 16;
    private const double NoteCardRestWidth = 220;
    private const double NoteCardRestMinHeight = 140;

    private static WeakReference<FrameworkElement>? _activeDragSource;
    private static object? _activeDragItem;
    private static DragGhostWindow? _activeGhostWindow;
    private static NoteCardViewModel? _activeDraggedNote;
    private static NoteCardControl? _activeNoteDragSource;
    private static object? _activeDraggedListing;
    private static FrameworkElement? _activeListingDragSource;
    private static readonly List<CardShadowLayer> HiddenShadowPlates = new();

    public static bool IsActiveDragSource(FrameworkElement element)
        => DragSourceDimming.IsSource(element)
            || _activeDragSource?.TryGetTarget(out var source) == true
                && ReferenceEquals(source, element) && ReferenceEquals(element.DataContext, _activeDragItem);

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
        Point grabOffsetRelativeToSource,
        FrameworkElement? preview = null)
    {
        DragGhostWindow? ghostWindow = null;
        DragSourceDimming? sourceDimming = null;
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
            _activeDragItem = dragSource.DataContext;
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

            var captureSurface = preview ?? ResolveCaptureSurface(dragSource);
            captureSurface.UpdateLayout();

            var (captureWidth, captureHeight) = preview is null ? GetRestCaptureSize(dragSource, captureSurface)
                : (captureSurface.ActualWidth, captureSurface.ActualHeight);
            if (captureWidth <= 1 || captureHeight <= 1)
            {
                result = DragDrop.DoDragDrop(dragSource, data, allowedEffects);
                return result;
            }

            var grabOffsetOnSurface = preview is not null ? new Point(Math.Min(32, captureWidth / 2), 18) : ResolveGrabOffsetOnSurface(
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

            var bakedGhost = BakeGhostVisual(snapshot, grabOffset, owner, captureSurface as Border);
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

            // Virtualized items can be reassigned or unloaded inside the native drag loop.
            // Their replacement is not the dragged item and must never inherit its dimming.
            void OnSourceReused(object sender, DependencyPropertyChangedEventArgs e) { if (sourceDimming is null) RestoreDragSource(dragSource); }
            void OnSourceUnloaded(object sender, RoutedEventArgs e) { if (sourceDimming is null) RestoreDragSource(dragSource); }

            dragSource.GiveFeedback += OnGiveFeedback;
            dragSource.QueryContinueDrag += OnQueryContinueDrag;
            dragSource.DataContextChanged += OnSourceReused;
            dragSource.Unloaded += OnSourceUnloaded;
            if (syncDropVisualsOnRender)
                RenderTransitionClock.AddFrameObserver(OnRendering);

            try
            {
                ghostWindow.AlignToCursor();
                ghostWindow.Show();
                ghostWindow.AlignToCursor();
                if (CollectionSelectionFrame.For(dragSource) is not null)
                    sourceDimming = new DragSourceDimming(owner, _activeDragItem,
                        dragSource is NoteCardControl ? NoteSourceOpacityDuringDrag : ListingSourceOpacityDuringDrag);
                else DimDragSource(dragSource);
                SyncDropVisualsNow();

                result = DragDrop.DoDragDrop(dragSource, data, allowedEffects);
                return result;
            }
            finally
            {
                dragSource.GiveFeedback -= OnGiveFeedback;
                dragSource.QueryContinueDrag -= OnQueryContinueDrag;
                dragSource.DataContextChanged -= OnSourceReused;
                dragSource.Unloaded -= OnSourceUnloaded;
                if (syncDropVisualsOnRender)
                    RenderTransitionClock.RemoveFrameObserver(OnRendering);
                if (sourceDimming is not null) sourceDimming.Dispose();
                else RestoreDragSource(dragSource);
            }
        }
        catch
        {
            if (sourceDimming is not null) sourceDimming.Dispose();
            else RestoreDragSource(dragSource);
            result = DragDrop.DoDragDrop(dragSource, data, allowedEffects);
            return result;
        }
        finally
        {
            sourceDimming?.Dispose();
            _activeDragSource = null;
            _activeDragItem = null;
            _activeDraggedNote = null;
            _activeNoteDragSource = null;
            _activeDraggedListing = null;
            _activeListingDragSource = null;
            RestoreShadowPlates();
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
        if (dragSource is CollectionSelectionFrame frame) frame.ResetHover();
        UiMotion.Stop(dragSource);

        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(dragSource);
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.X = 0;
        translate.Y = 0;

        // Listing motion/hover lives on the named face Border, not the outer drag host.
        if (TryResolveListingFace(dragSource, out var face) && !ReferenceEquals(face, dragSource))
        {
            UiMotion.Stop(face);
            var (faceScale, faceTranslate) = UiMotion.EnsureScaleTranslateGroup(face);
            faceScale.ScaleX = 1;
            faceScale.ScaleY = 1;
            faceTranslate.X = 0;
            faceTranslate.Y = 0;
            ResetShadowToRest(face);
        }
        else
        {
            ResetShadowToRest(ResolveShadowHost(dragSource));
        }

        dragSource.UpdateLayout();
    }

    private static void DimDragSource(FrameworkElement dragSource)
    {
        ResetShadowToRest(ResolveShadowHost(dragSource));
        HideShadowPlates(dragSource);

        if (dragSource is NoteCardControl noteCard)
            noteCard.SetDragDimmed(true, NoteSourceOpacityDuringDrag);
        else
            dragSource.Opacity = ListingSourceOpacityDuringDrag;
    }

    private static void RestoreDragSource(FrameworkElement dragSource)
    {
        RestoreShadowPlates();

        if (dragSource is NoteCardControl noteCard)
            noteCard.SetDragDimmed(false);
        else
            dragSource.ClearValue(UIElement.OpacityProperty);
    }

    private static void HideShadowPlates(FrameworkElement dragSource)
    {
        RestoreShadowPlates();

        foreach (var plate in EnumerateCardShadowLayers(dragSource))
        {
            if (plate.Visibility == Visibility.Collapsed)
                continue;

            HiddenShadowPlates.Add(plate);
            plate.Visibility = Visibility.Collapsed;
        }
    }

    private static void RestoreShadowPlates()
    {
        foreach (var plate in HiddenShadowPlates)
            plate.ClearValue(UIElement.VisibilityProperty);

        HiddenShadowPlates.Clear();
    }

    private static IEnumerable<CardShadowLayer> EnumerateCardShadowLayers(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is CardShadowLayer layer)
                yield return layer;

            foreach (var nested in EnumerateCardShadowLayers(child))
                yield return nested;
        }
    }

    private static FrameworkElement ResolveShadowHost(FrameworkElement dragSource)
    {
        if (dragSource.FindName("CardBorder") is FrameworkElement cardBorder)
            return cardBorder;

        if (TryResolveListingFace(dragSource, out var listingFace))
            return listingFace;

        return dragSource;
    }

    private static void ResetShadowToRest(FrameworkElement shadowHost)
    {
        // Listing/note tiles already use CardShadowLayer; do not reattach a live DropShadowEffect.
        UiMotion.Stop(shadowHost);
        if (shadowHost.Effect is DropShadowEffect)
            shadowHost.Effect = null;
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

    private static BakedGhost BakeGhostVisual(SnapshotCapture snapshot, Point grabOffset, Window owner, Border? cardFace = null)
    {
        var contentW = snapshot.Width;
        var contentH = snapshot.Height;
        var bakeW = contentW + GhostShadowPad * 2;
        var bakeH = contentH + GhostShadowPad * 2 + GhostLiftAboveGrabY;
        var originX = GhostShadowPad;
        var originY = GhostShadowPad + GhostLiftAboveGrabY + GhostFloatYOffset;

        var dpi = VisualTreeHelper.GetDpi(owner);
        var pixelW = Math.Max(1, (int)Math.Ceiling(bakeW * dpi.DpiScaleX));
        var pixelH = Math.Max(1, (int)Math.Ceiling(bakeH * dpi.DpiScaleY));

        // Skia composites the shared elevation shadow + the captured card instead of a
        // DrawingVisual + RTB pass.
        using var output = new SkiaSharp.SKBitmap(pixelW, pixelH, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
        using (var canvas = new SkiaSharp.SKCanvas(output))
        {
            canvas.Clear(SkiaSharp.SKColors.Transparent);

            // The shadow follows the visible card face only (exclude CaptureMargin pad).
            var face = new Rect(
                originX + snapshot.ContentOffsetX,
                originY + snapshot.ContentOffsetY,
                snapshot.Width - 2 * snapshot.ContentOffsetX,
                snapshot.Height - 2 * snapshot.ContentOffsetY);
            DashboardDragShadow.Draw(canvas, face, dpi, cardFace);

            // Capture and owner DPI can differ. Scale in DIP space so the bitmap face
            // covers the same rectangle used by its shadow on either monitor.
            using var snapshotBitmap = Scriblucent.Rendering.SkiaBitmapUtil.FromSource((BitmapSource)snapshot.Image);
            canvas.DrawBitmap(
                snapshotBitmap,
                new SkiaSharp.SKRect(
                    (float)(originX * dpi.DpiScaleX),
                    (float)(originY * dpi.DpiScaleY),
                    (float)((originX + snapshot.Width) * dpi.DpiScaleX),
                    (float)((originY + snapshot.Height) * dpi.DpiScaleY)),
                Scriblucent.Rendering.SkiaBitmapUtil.LinearSampling);
        }

        var baked = Scriblucent.Rendering.SkiaBitmapUtil.ToSource(output, dpi);

        var grabPx = ResolveTransformToDevice(owner).Transform(
            new Point(originX + grabOffset.X, originY + grabOffset.Y));

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
            // Capture every face in the same local frame. Directly rendering a listing
            // Border includes its layout offset, leaving part of the ghost shadow uncovered.
            if (TryPaddedVisualCapture(surface, width, height, dpi, out var padded))
                return padded;

            // If local-frame capture fails, Execute keeps native dragging available.
            // Direct RTB capture can include layout offsets and expose the shadow body.
        }
        finally
        {
            surface.CacheMode = savedCache;
        }

        return null;
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
            var faceBounds = new Rect(0, 0, width, height);
            if (VisualTreeHelper.GetParent(element) is Visual parent)
                faceBounds = element.TransformToAncestor(parent).TransformBounds(faceBounds);
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                var brush = new VisualBrush(element)
                {
                    // Normalize the transformed viewbox back to the rest-size face.
                    // Nested entrance transforms (including the template's .88 scale)
                    // must not leave a small card over a full-size shadow. Keep the
                    // live transform tree intact, including motion channels/bindings.
                    Stretch = Stretch.Fill,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                    Viewbox = faceBounds,
                    ViewboxUnits = BrushMappingMode.Absolute
                };
                // A VisualBrush referencing a live face cannot be frozen. Freeze only
                // the rendered bitmap; freezing here made every padded capture fail.
                if (element is Border face)
                {
                    var contour = SurfaceContour.Create(new Size(width, height), face.CornerRadius, SurfaceContour.EffectiveFrame(face)).Clone();
                    contour.Transform = new TranslateTransform(CaptureMargin, CaptureMargin);
                    contour.Freeze(); context.PushClip(contour);
                }
                context.DrawRectangle(
                    brush,
                    null,
                    new Rect(CaptureMargin, CaptureMargin, width, height));
                if (element is Border) context.Pop();
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
        if (dragSource is CollectionSelectionFrame frame)
        {
            var face = FindVisibleFace(frame, frame.FaceName);
            if (face is not null) return face;
        }
        if (dragSource.FindName("CardBorder") is FrameworkElement cardBorder)
            return cardBorder;

        // Listing hosts wrap CardShadowLayer + face; capture the face only (notes already do).
        if (TryResolveListingFace(dragSource, out var listingFace))
            return listingFace;

        var childCount = VisualTreeHelper.GetChildrenCount(dragSource);
        for (var i = 0; i < childCount; i++)
        {
            if (VisualTreeHelper.GetChild(dragSource, i) is Border innerBorder)
                return innerBorder;
        }

        return dragSource;
    }

    private static Border? FindVisibleFace(DependencyObject root, string name)
    {
        if (root is Border { IsVisible: true } face && face.Name == name) return face;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindVisibleFace(VisualTreeHelper.GetChild(root, i), name) is { } found) return found;
        return null;
    }

    private static bool TryResolveListingFace(FrameworkElement dragSource, out FrameworkElement face)
    {
        face = null!;
        if (!IsListingDragSource(dragSource))
            return false;

        var shadowName = CardSurfaceMotionBehavior.GetShadowElementName(dragSource);
        if (!string.IsNullOrWhiteSpace(shadowName)
            && dragSource.FindName(shadowName) is FrameworkElement namedFace)
        {
            face = namedFace;
            return true;
        }

        if (FindListingFaceBesideShadowPlate(dragSource) is { } siblingFace)
        {
            face = siblingFace;
            return true;
        }

        return false;
    }

    private static Border? FindListingFaceBesideShadowPlate(FrameworkElement dragSource)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(dragSource);
        for (var i = 0; i < childCount; i++)
        {
            if (VisualTreeHelper.GetChild(dragSource, i) is not Panel panel)
                continue;

            Border? face = null;
            var hasShadowPlate = false;
            var panelChildCount = VisualTreeHelper.GetChildrenCount(panel);
            for (var j = 0; j < panelChildCount; j++)
            {
                switch (VisualTreeHelper.GetChild(panel, j))
                {
                    case CardShadowLayer:
                        hasShadowPlate = true;
                        break;
                    case Border border when face is null:
                        face = border;
                        break;
                }
            }

            if (hasShadowPlate && face is not null)
                return face;
        }

        return null;
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
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);

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
