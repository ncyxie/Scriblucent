using Scriblucent.Animations;
using Scriblucent.Services;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Scriblucent.Views;

/// <summary>The photograph moves from its thumbnail independently of the backdrop and close chrome.</summary>
public partial class FlashcardImageViewerWindow : Window
{
    private readonly WeakReference<FrameworkElement>? _thumbnail;
    private readonly object? _sourceItem;
    private IDisposable? _backdrop;
    private bool _isClosing, _allowClose, _closed, _entered;
    private Rect _restBounds;
    private Action? _pendingEnter;
    private TaskCompletionSource? _entrySettled;

    public FlashcardImageViewerWindow(BitmapImage image, FrameworkElement? thumbnail = null)
    {
        InitializeComponent(); WindowThemeService.Register(this); ViewerImage.Source = image;
        if (thumbnail is not null) { _thumbnail = new(thumbnail); _sourceItem = thumbnail.DataContext; }
        BackdropLayer.Opacity = 0; ViewerImage.Opacity = 0; CloseButton.Opacity = 0;
        // Complete the first layout before applying the thumbnail transform. WPF can deliver
        // initial SizeChanged after Loaded; that event must not settle the new entrance.
        Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (_closed || _isClosing || _entered) return;
            BeginEnterAnimation(); _entered = true;
        }, System.Windows.Threading.DispatcherPriority.Loaded);
        SizeChanged += (_, e) => { if (_entered && !_isClosing && e.PreviousSize.Width > 0) SettleAfterResize(); };
        DpiChanged += (_, _) => { if (_entered && !_isClosing) SettleAfterResize(); };
        MotionPolicy.Changed += PolicyChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            MotionPolicy.Changed -= PolicyChanged;
            _pendingEnter = null;
            _backdrop?.Dispose(); _backdrop = null;
            _entrySettled?.TrySetResult();
            UiMotion.Cleanup(BackdropLayer); UiMotion.Cleanup(BackdropImage); UiMotion.Cleanup(ViewerImage);
            UiMotion.Cleanup(CloseButton); BackdropImage.Source = null;
        };
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e); OverlayDialogBoundsHelper.Apply(this);
    }
    private static Rect ImageBounds(FrameworkElement element, Visual relative)
    {
        var size = element.RenderSize;
        if (element is Image { Source: { } source } image && image.Stretch == Stretch.Uniform && source.Width > 0 && source.Height > 0)
        {
            var factor = Math.Min(size.Width / source.Width, size.Height / source.Height);
            var drawn = new Size(source.Width * factor, source.Height * factor);
            return element.TransformToVisual(relative).TransformBounds(new Rect(
                new Point((size.Width - drawn.Width) / 2, (size.Height - drawn.Height) / 2), drawn));
        }
        return element.TransformToVisual(relative).TransformBounds(new Rect(size));
    }
    private Rect? ThumbnailBounds()
    {
        if (_thumbnail is null || !_thumbnail.TryGetTarget(out var source) || !source.IsVisible
            || !ReferenceEquals(_sourceItem, source.DataContext) || PresentationSource.FromVisual(source) is null) return null;
        try
        {
            var sourceRoot = PresentationSource.FromVisual(source)!.RootVisual;
            var visible = ImageBounds(source, sourceRoot);
            var screenA = ((FrameworkElement)sourceRoot).PointToScreen(visible.TopLeft);
            var screenB = ((FrameworkElement)sourceRoot).PointToScreen(visible.BottomRight);
            var rect = new Rect(ContentRoot.PointFromScreen(screenA), ContentRoot.PointFromScreen(screenB));
            if (rect.Width < 1 || rect.Height < 1 || !new Rect(ContentRoot.RenderSize).IntersectsWith(rect)) return null;
            // A virtualized or scrolled-out thumbnail must not become the dismissal destination.
            for (DependencyObject? parent = source; parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is ScrollViewer scroll)
                {
                    var viewport = scroll.TransformToVisual(sourceRoot).TransformBounds(new Rect(scroll.RenderSize));
                    if (!viewport.Contains(visible)) return null;
                }
            return rect;
        }
        catch (InvalidOperationException) { return null; }
    }
    private void ConfigureShadow()
    {
        _restBounds = ImageBounds(ViewerImage, ContentRoot);
        var elevation = BakedElevationAsset.Dialog;
        ImageShadow.Width = Math.Max(1, _restBounds.Width) + elevation.BleedTop * 2;
        ImageShadow.Height = Math.Max(1, _restBounds.Height) + elevation.BleedTop + elevation.BleedBottom;
        ImageShadow.RenderTransform = ViewerImage.RenderTransform;
        ImageShadow.RenderTransformOrigin = new Point(.5, (elevation.BleedTop + _restBounds.Height / 2) / ImageShadow.Height);
    }
    private void BeginEnterAnimation()
    {
        var (scale, move) = UiMotion.EnsureScaleTranslateGroup(ViewerImage);
        ViewerImage.RenderTransformOrigin = new Point(.5, .5);
        ConfigureShadow();
        var origin = MotionPolicy.Spatial ? ThumbnailBounds() : null;
        var bounds = _restBounds;
        scale.ScaleX = origin is { } rect ? rect.Width / bounds.Width : MotionPolicy.Spatial ? .92 : 1;
        scale.ScaleY = origin is { } rectY ? rectY.Height / bounds.Height : MotionPolicy.Spatial ? .92 : 1;
        move.X = origin is { } offset ? offset.X + offset.Width / 2 - (bounds.X + bounds.Width / 2) : 0;
        move.Y = origin is { } offsetY ? offsetY.Y + offsetY.Height / 2 - (bounds.Y + bounds.Height / 2) : 0;
        RenderOptions.SetBitmapScalingMode(ViewerImage, BitmapScalingMode.Linear);
        _entrySettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BackdropLayer.Opacity = MotionPolicy.Spatial ? .12 : 1;
        void Start()
        {
            if (_closed || _isClosing || _pendingEnter is null) return;
            _pendingEnter = null;
            SurfaceMotion.Animate(BackdropLayer, 1, 240, UiMotion.OutSine, false);
            SurfaceMotion.Animate(CloseButton, 1, 180, UiMotion.OutSine, false, startDelayMs: 80);
            SurfaceMotion.AnimateTracks(ViewerImage, MotionChannel.Presence,
                [new(ViewerImage, OpacityProperty, 1, Profile: MotionProfiles.Readable),
                 new(ImageShadow, OpacityProperty, .30, Profile: MotionProfiles.Readable),
                 new(scale, ScaleTransform.ScaleXProperty, 1, true, Profile: MotionProfiles.Arrival),
                 new(scale, ScaleTransform.ScaleYProperty, 1, true, Profile: MotionProfiles.Arrival),
                 new(move, TranslateTransform.XProperty, 0, true, Profile: MotionProfiles.Arrival),
                 new(move, TranslateTransform.YProperty, 0, true, Profile: MotionProfiles.Arrival)],
                320, UiMotion.InOutSine, () =>
                {
                    RenderOptions.SetBitmapScalingMode(ViewerImage, BitmapScalingMode.HighQuality);
                    _entrySettled?.TrySetResult();
                });
        }
        _pendingEnter = Start;
        if (!MotionPolicy.Spatial) Start();
        _backdrop = OverlayBackdropCapture.RequestPrepared(Owner, image =>
        {
            if (_isClosing) return;
            var first = BackdropImage.Source is null;
            BackdropImage.Source = image;
            if (first && image is not null && MotionPolicy.Spatial)
            { BackdropImage.Opacity = 0; SurfaceMotion.Animate(BackdropImage, 1, 160, UiMotion.OutSine, false); }
            else BackdropImage.Opacity = 1;
        }, () => _pendingEnter?.Invoke(), WaitForEntrance);
    }
    private async Task WaitForEntrance()
    { if (_entrySettled is { } pending) await pending.Task; }
    private void PolicyChanged()
    { if (!MotionPolicy.Spatial) _pendingEnter?.Invoke(); }
    private void SettleAfterResize()
    {
        _pendingEnter = null; _entrySettled?.TrySetResult();
        SurfaceMotion.Stop(BackdropLayer); SurfaceMotion.Stop(CloseButton);
        BackdropLayer.Opacity = CloseButton.Opacity = 1;
        SurfaceMotion.Stop(ViewerImage); ViewerImage.Opacity = 1; ImageShadow.Opacity = .30;
        var (scale, move) = UiMotion.EnsureScaleTranslateGroup(ViewerImage);
        scale.ScaleX = scale.ScaleY = 1; move.X = move.Y = 0;
        ConfigureShadow(); RenderOptions.SetBitmapScalingMode(ViewerImage, BitmapScalingMode.HighQuality);
    }
    private void RequestClose()
    {
        if (_isClosing) return;
        _isClosing = true; _backdrop?.Dispose(); _backdrop = null;
        _pendingEnter = null; _entrySettled?.TrySetResult();
        SurfaceMotion.Stop(BackdropImage); SurfaceMotion.Stop(ViewerImage);
        var (scale, move) = UiMotion.EnsureScaleTranslateGroup(ViewerImage);
        // Rest bounds were computed before transforms: interrupted opening retains its visible pose.
        var destination = MotionPolicy.Spatial ? ThumbnailBounds() : null;
        var bounds = _restBounds;
        var sx = destination is { } rect ? rect.Width / bounds.Width : .96;
        var sy = destination is { } rectY ? rectY.Height / bounds.Height : .96;
        var x = destination is { } offset ? offset.X + offset.Width / 2 - (bounds.X + bounds.Width / 2) : 0;
        var y = destination is { } offsetY ? offsetY.Y + offsetY.Height / 2 - (bounds.Y + bounds.Height / 2) : 0;
        RenderOptions.SetBitmapScalingMode(ViewerImage, BitmapScalingMode.Linear);
        SurfaceMotion.Animate(BackdropLayer, 0, 140, UiMotion.InQuad, false);
        SurfaceMotion.Animate(CloseButton, 0, 100, UiMotion.InQuad, false);
        SurfaceMotion.AnimateTracks(ViewerImage, MotionChannel.Presence,
            [new(ViewerImage, OpacityProperty, 0), new(ImageShadow, OpacityProperty, 0),
             new(scale, ScaleTransform.ScaleXProperty, sx, true), new(scale, ScaleTransform.ScaleYProperty, sy, true),
             new(move, TranslateTransform.XProperty, x, true), new(move, TranslateTransform.YProperty, y, true)],
            140, UiMotion.InQuad, () =>
            {
                _allowClose = true;
                // Off completes inline, potentially inside WPF's Closing event. Re-enter later.
                _ = Dispatcher.BeginInvoke(() => { if (!_closed) Close(); });
            });
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose && !Dispatcher.HasShutdownStarted) { e.Cancel = true; RequestClose(); }
        base.OnClosing(e);
    }
    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => RequestClose();
    private void CloseButton_Click(object sender, RoutedEventArgs e) { e.Handled = true; RequestClose(); }
    protected override void OnKeyDown(KeyEventArgs e)
    { base.OnKeyDown(e); if (e.Key == Key.Escape) { e.Handled = true; RequestClose(); } }
}
