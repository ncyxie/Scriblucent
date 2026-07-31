using System;
using Scriblucent.Localization;
using Scriblucent.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Scriblucent.Controls
{
    public partial class ResizableImage : UserControl
    {
        private const double CanvasPadding = 24;
        private const double FloatingCanvasAnchorHeight = 80;
        private const double EdgeScrollZone = 42;
        private const double MaxEdgeScrollStep = 24;
        private const double NormalImageOpacity = 1.0;
        private const double SelectedImageOpacity = 1.0;
        private const int FloatingImageZIndex = 1000;
        private const double MinimumAspectRatio = 0.01;
        private bool _isDragging;
        private bool _isInlineDragCandidate;
        private bool _isInlineDragging;
        private Point _clickPosition;
        private Point _inlineDragStartPosition;
        private double _lockedAspectRatio;
        private ResizeHandle _activeResizeHandle = ResizeHandle.None;
        private ResizeAxis _activeAspectResizeAxis = ResizeAxis.None;
        private double _resizeStartWidth;
        private double _resizeStartHeight;
        private double _resizeStartLeft;
        private double _resizeStartTop;
        private double _resizeAccumulatedWidthDelta;
        private double _resizeAccumulatedHeightDelta;
        private double _resizeGestureAspectRatio;

        private enum ResizeHandle
        {
            None,
            TopLeft,
            Top,
            TopRight,
            Right,
            BottomRight,
            Bottom,
            BottomLeft,
            Left
        }

        private enum ResizeAxis
        {
            None,
            Width,
            Height
        }

        public event EventHandler? ImageBoundsChanged;
        public event EventHandler<ImageLayoutChangeRequestedEventArgs>? LayoutChangeRequested;
        public event EventHandler<InlineImageMoveRequestedEventArgs>? InlineMoveRequested;
        public RichTextBox? EditorHost { get; set; }

        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register("IsSelected", typeof(bool), typeof(ResizableImage),
            new PropertyMetadata(false, OnIsSelectedChanged));

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ResizableImage control)
            {
                control.UpdateSelectionVisualState((bool)e.NewValue);
            }
        }

        public static readonly DependencyProperty ImageDataProperty =
            DependencyProperty.Register("ImageData", typeof(string), typeof(ResizableImage),
            new PropertyMetadata(null, OnImageDataChanged));

        public string ImageData
        {
            get => (string)GetValue(ImageDataProperty);
            set => SetValue(ImageDataProperty, value);
        }

        // Headroom multiplier above the control's current display width: the image can still be
        // drag-resized larger after insertion (which doesn't re-trigger a decode), so decoding at
        // the exact display size would go visibly soft the moment someone enlarges it. A larger
        // DecodePixelWidth than the source's native size is a safe no-op (WPF just decodes at
        // native resolution, never upscales), so this only trims genuinely oversized sources.
        private const double DecodeWidthHeadroomFactor = 3.0;

        private static void OnImageDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ResizableImage control && e.NewValue is string base64 && !string.IsNullOrEmpty(base64))
            {
                try
                {
                    var bytes = Convert.FromBase64String(base64);
                    using var ms = new System.IO.MemoryStream(bytes);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    var decodePixelWidth = ResolveDecodePixelWidth(control);
                    if (decodePixelWidth > 0)
                        bmp.DecodePixelWidth = decodePixelWidth;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    var displaySource = CreateOpaqueDisplaySource(bmp);
                    control.InnerImage.Source = displaySource;
                    control.SetCurrentValue(SourceProperty, displaySource);
                    control.RefreshVisualState();
                }
                catch (Exception ex)
                {
                    Scriblucent.Services.CrashLog.Append("ResizableImage.OnImageDataChanged", ex);
                }
            }
        }

        private static int ResolveDecodePixelWidth(ResizableImage control)
        {
            var width = !double.IsNaN(control.Width) && control.Width > 0 ? control.Width : control.ActualWidth;
            if (width <= 0)
                return 0; // Display size not known yet - fall back to a full decode.

            return (int)Math.Ceiling(width * DecodeWidthHeadroomFactor);
        }

        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.Register("Source", typeof(ImageSource), typeof(ResizableImage), 
            new PropertyMetadata(null, OnSourceChanged));

        public ImageSource Source
        {
            get => (ImageSource)GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        public bool ShouldSerializeSource() => false;
        public bool ShouldSerializeIsSelected() => false;
        public bool ShouldSerializeImageData() => !string.IsNullOrWhiteSpace(ImageData);
        public bool ShouldSerializePreserveAspectRatio() => PreserveAspectRatio;

        public static readonly DependencyProperty ImageIdProperty =
            DependencyProperty.Register("ImageId", typeof(Guid), typeof(ResizableImage),
            new PropertyMetadata(Guid.Empty));

        public Guid ImageId
        {
            get => (Guid)GetValue(ImageIdProperty);
            set => SetValue(ImageIdProperty, value);
        }

        public static readonly DependencyProperty LayoutModeProperty =
            DependencyProperty.Register("LayoutMode", typeof(string), typeof(ResizableImage),
            new PropertyMetadata(NoteImageLayout.Floating, OnLayoutModeChanged));

        public string LayoutMode
        {
            get => (string)GetValue(LayoutModeProperty);
            set => SetValue(LayoutModeProperty, value);
        }

        private static void OnLayoutModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ResizableImage control)
            {
                control.UpdateLayoutMenuChecks();
            }
        }

        public static readonly DependencyProperty PreserveAspectRatioProperty =
            DependencyProperty.Register(
                nameof(PreserveAspectRatio),
                typeof(bool),
                typeof(ResizableImage),
                new PropertyMetadata(false, OnPreserveAspectRatioChanged));

        public bool PreserveAspectRatio
        {
            get => (bool)GetValue(PreserveAspectRatioProperty);
            set => SetValue(PreserveAspectRatioProperty, value);
        }

        private static void OnPreserveAspectRatioChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ResizableImage control)
                return;

            if ((bool)e.NewValue)
                control._lockedAspectRatio = control.ResolveCurrentAspectRatio();

            control.UpdateAspectRatioToggleState();
            control.ImageBoundsChanged?.Invoke(control, EventArgs.Empty);
        }

        private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ResizableImage control)
            {
                var displaySource = CreateOpaqueDisplaySource(e.NewValue as ImageSource);
                control.InnerImage.Source = displaySource;
                if (!ReferenceEquals(displaySource, e.NewValue))
                    control.SetCurrentValue(SourceProperty, displaySource);
                control.RefreshVisualState();
            }
        }

        public ResizableImage()
        {
            InitializeComponent();

            this.Focusable = true;
            this.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            this.MouseLeftButtonDown += OnMouseLeftButtonDown;
            this.MouseMove += OnMouseMove;
            this.MouseLeftButtonUp += OnMouseLeftButtonUp;
            this.LostFocus += ResizableImage_LostFocus;

            AttachResizeThumb(TopLeftThumb, ResizeHandle.TopLeft, TopLeft_DragDelta);
            AttachResizeThumb(TopThumb, ResizeHandle.Top, Top_DragDelta);
            AttachResizeThumb(TopRightThumb, ResizeHandle.TopRight, TopRight_DragDelta);
            AttachResizeThumb(RightThumb, ResizeHandle.Right, Right_DragDelta);
            AttachResizeThumb(BottomRightThumb, ResizeHandle.BottomRight, BottomRight_DragDelta);
            AttachResizeThumb(BottomThumb, ResizeHandle.Bottom, Bottom_DragDelta);
            AttachResizeThumb(BottomLeftThumb, ResizeHandle.BottomLeft, BottomLeft_DragDelta);
            AttachResizeThumb(LeftThumb, ResizeHandle.Left, Left_DragDelta);

            SetLocalizedLayoutText();
            UpdateLayoutMenuChecks();
            UpdateAspectRatioToggleState();
            UpdateSelectionVisualState(IsSelected);

            // Make sure the UserControl itself can receive focus when clicked
            FocusManager.SetIsFocusScope(this, true);
        }

        private void AttachResizeThumb(Thumb thumb, ResizeHandle handle, DragDeltaEventHandler dragDeltaHandler)
        {
            thumb.DragStarted += (_, _) => BeginResizeGesture(handle);
            thumb.DragDelta += dragDeltaHandler;
            thumb.DragCompleted += (_, _) => EndResizeGesture();
        }

        private void UpdateSelectionVisualState(bool isSelected)
        {
            Opacity = NormalImageOpacity;
            RootGrid.Opacity = NormalImageOpacity;
            InnerImage.Opacity = isSelected ? SelectedImageOpacity : NormalImageOpacity;
            ResizeHandlesGrid.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
            Panel.SetZIndex(this, isSelected ? FloatingImageZIndex + 2 : FloatingImageZIndex + 1);
        }

        public void RefreshVisualState()
        {
            UpdateSelectionVisualState(IsSelected);
        }

        private static ImageSource? CreateOpaqueDisplaySource(ImageSource? source)
        {
            if (source is not BitmapSource bitmapSource || !HasAlpha(bitmapSource.Format))
                return source;

            try
            {
                var pixelWidth = Math.Max(1, bitmapSource.PixelWidth);
                var pixelHeight = Math.Max(1, bitmapSource.PixelHeight);
                var visual = new DrawingVisual();

                using (var context = visual.RenderOpen())
                {
                    var bounds = new Rect(0, 0, pixelWidth, pixelHeight);
                    context.DrawRectangle(ResolveOpaqueImageBackgroundBrush(), null, bounds);
                    context.DrawImage(bitmapSource, bounds);
                }

                var rendered = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
                rendered.Render(visual);

                var opaque = new FormatConvertedBitmap(rendered, PixelFormats.Bgr32, null, 0);
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

        private void SetLocalizedLayoutText()
        {
            LayoutButton.ToolTip = LocalizationService.GetString("ImageLayoutOptions");
            InlineLayoutMenuItem.Header = LocalizationService.GetString("ImageLayoutInline");
            FloatingLayoutMenuItem.Header = LocalizationService.GetString("ImageLayoutFloating");
            UpdateAspectRatioToggleState();
        }

        private void UpdateLayoutMenuChecks()
        {
            if (InlineLayoutMenuItem == null || FloatingLayoutMenuItem == null)
                return;

            InlineLayoutMenuItem.IsCheckable = true;
            FloatingLayoutMenuItem.IsCheckable = true;
            InlineLayoutMenuItem.IsChecked = string.Equals(LayoutMode, NoteImageLayout.Inline, StringComparison.OrdinalIgnoreCase);
            FloatingLayoutMenuItem.IsChecked = string.Equals(LayoutMode, NoteImageLayout.Floating, StringComparison.OrdinalIgnoreCase);
            LayoutButton.Content = InlineLayoutMenuItem.IsChecked
                ? LocalizationService.GetString("ImageLayoutInlineShort")
                : LocalizationService.GetString("ImageLayoutFloatingShort");
        }

        private void UpdateAspectRatioToggleState()
        {
            if (AspectRatioToggleButton == null)
                return;

            AspectRatioToggleButton.IsChecked = PreserveAspectRatio;
            AspectRatioToggleButton.ToolTip = LocalizationService.GetString(PreserveAspectRatio
                ? "ImageAspectRatioUnlockTooltip"
                : "ImageAspectRatioLockTooltip");
        }

        private void ResizableImage_LostFocus(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsKeyboardFocusWithin && LayoutButton.ContextMenu?.IsOpen != true)
                    IsSelected = false;
            }));
        }

        private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            this.Focus();
            IsSelected = true;
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsLayoutChrome(e.OriginalSource as DependencyObject))
                return;

            if (IsInlineLayout())
            {
                _isInlineDragCandidate = true;
                _inlineDragStartPosition = e.GetPosition(this);
                CaptureMouse();
                e.Handled = true;
                return;
            }

            if (Parent is not Canvas)
                return;

            // If dragging started from a Thumb, this bubbling event wouldn't fire because Thumb marks it Handled.
            // Ergo, this is the main image body being clicked.
            _isDragging = true;
            _clickPosition = e.GetPosition(this.Parent as UIElement);
            this.CaptureMouse();
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_isInlineDragCandidate && e.LeftButton == MouseButtonState.Pressed)
            {
                var currentPosition = e.GetPosition(this);
                if (Math.Abs(currentPosition.X - _inlineDragStartPosition.X) >= SystemParameters.MinimumHorizontalDragDistance
                    || Math.Abs(currentPosition.Y - _inlineDragStartPosition.Y) >= SystemParameters.MinimumVerticalDragDistance)
                {
                    _isInlineDragCandidate = false;
                    _isInlineDragging = true;
                    CaptureMouse();
                    e.Handled = true;
                }

                return;
            }

            if (_isInlineDragging && e.LeftButton == MouseButtonState.Pressed)
            {
                e.Handled = true;
                return;
            }

            if (_isDragging && this.Parent is UIElement parent && e.LeftButton == MouseButtonState.Pressed)
            {
                var currentPosition = e.GetPosition(parent);
                var delta = currentPosition - _clickPosition;

                var left = Canvas.GetLeft(this);
                if (double.IsNaN(left)) left = 0;

                var top = Canvas.GetTop(this);
                if (double.IsNaN(top)) top = 0;

                var newLeft = left + delta.X;
                var newTop = top + delta.Y;
                var clampedPosition = ClampPositionToParentCanvas(newLeft, newTop);

                Canvas.SetLeft(this, clampedPosition.X);
                Canvas.SetTop(this, clampedPosition.Y);

                EnsureParentCanvasContainsImage();
                AutoScrollEditor(e);
                ImageBoundsChanged?.Invoke(this, EventArgs.Empty);

                _clickPosition = currentPosition;
                e.Handled = true;
            }
        }

        private bool IsInlineLayout()
        {
            return string.Equals(LayoutMode, NoteImageLayout.Inline, StringComparison.OrdinalIgnoreCase);
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isInlineDragging)
            {
                _isInlineDragging = false;
                _isInlineDragCandidate = false;
                if (IsMouseCaptured)
                    ReleaseMouseCapture();

                var editor = EditorHost ?? FindVisualAncestor<RichTextBox>(this);
                if (editor != null)
                    InlineMoveRequested?.Invoke(this, new InlineImageMoveRequestedEventArgs(e.GetPosition(editor)));

                e.Handled = true;
                return;
            }

            if (_isInlineDragCandidate)
            {
                _isInlineDragCandidate = false;
                if (IsMouseCaptured)
                    ReleaseMouseCapture();

                e.Handled = true;
                return;
            }

            if (_isDragging)
            {
                _isDragging = false;
                this.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private static bool IsLayoutChrome(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is ButtonBase or System.Windows.Controls.ContextMenu or MenuItem or Thumb)
                    return true;

                DependencyObject? parent = null;
                if (source is Visual or System.Windows.Media.Media3D.Visual3D)
                {
                    parent = VisualTreeHelper.GetParent(source);
                }

                parent ??= source switch
                {
                    FrameworkElement element => element.Parent,
                    FrameworkContentElement contentElement => contentElement.Parent,
                    _ => null
                };

                source = parent;
            }

            return false;
        }

        private void BeginResizeGesture(ResizeHandle handle)
        {
            _activeResizeHandle = handle;
            _activeAspectResizeAxis = ResizeAxis.None;
            _resizeStartWidth = ResolveElementWidth(this);
            _resizeStartHeight = ResolveElementHeight(this);
            _resizeStartLeft = Canvas.GetLeft(this);
            _resizeStartTop = Canvas.GetTop(this);
            if (double.IsNaN(_resizeStartLeft))
                _resizeStartLeft = 0;
            if (double.IsNaN(_resizeStartTop))
                _resizeStartTop = 0;

            _resizeAccumulatedWidthDelta = 0;
            _resizeAccumulatedHeightDelta = 0;
            _resizeGestureAspectRatio = ResolveLockedAspectRatio(_resizeStartWidth, _resizeStartHeight);
        }

        private void EndResizeGesture()
        {
            _activeResizeHandle = ResizeHandle.None;
            _activeAspectResizeAxis = ResizeAxis.None;
            _resizeAccumulatedWidthDelta = 0;
            _resizeAccumulatedHeightDelta = 0;
            _resizeGestureAspectRatio = 0;
        }

        private void Resize(double dX, double dY, bool adjustLeft, bool adjustTop)
        {
            if (_activeResizeHandle == ResizeHandle.None)
                BeginResizeGesture(ResizeHandle.None);

            _resizeAccumulatedWidthDelta += dX;
            _resizeAccumulatedHeightDelta += dY;

            var baseWidth = _resizeStartWidth > 0 ? _resizeStartWidth : ResolveElementWidth(this);
            var baseHeight = _resizeStartHeight > 0 ? _resizeStartHeight : ResolveElementHeight(this);
            var newWidth = baseWidth + _resizeAccumulatedWidthDelta;
            var newHeight = baseHeight + _resizeAccumulatedHeightDelta;

            if (PreserveAspectRatio)
            {
                var lockedSize = ResolveAspectLockedSize(baseWidth, baseHeight, _resizeAccumulatedWidthDelta, _resizeAccumulatedHeightDelta);
                newWidth = lockedSize.Width;
                newHeight = lockedSize.Height;
            }
            else
            {
                newWidth = Math.Max(MinWidth, newWidth);
                newHeight = Math.Max(MinHeight, newHeight);
            }

            var widthDelta = newWidth - baseWidth;
            var heightDelta = newHeight - baseHeight;
            var isHorizontalResize = Math.Abs(_resizeAccumulatedWidthDelta) > 0.001;
            var isVerticalResize = Math.Abs(_resizeAccumulatedHeightDelta) > 0.001;

            if (Parent is Canvas)
            {
                var left = _resizeStartLeft;
                var top = _resizeStartTop;

                if (adjustLeft)
                    left -= widthDelta;
                else if (PreserveAspectRatio && isVerticalResize && !isHorizontalResize)
                    left -= widthDelta / 2;

                if (adjustTop)
                    top -= heightDelta;
                else if (PreserveAspectRatio && isHorizontalResize && !isVerticalResize)
                    top -= heightDelta / 2;

                Canvas.SetLeft(this, left);
                Canvas.SetTop(this, top);
            }

            Width = newWidth;
            Height = newHeight;

            ClampBoundsToParentCanvas();
            EnsureParentCanvasContainsImage();
            ImageBoundsChanged?.Invoke(this, EventArgs.Empty);
        }

        private Size ResolveAspectLockedSize(double currentWidth, double currentHeight, double dX, double dY)
        {
            var ratio = _resizeGestureAspectRatio >= MinimumAspectRatio && !double.IsInfinity(_resizeGestureAspectRatio)
                ? _resizeGestureAspectRatio
                : ResolveLockedAspectRatio(currentWidth, currentHeight);
            var isHorizontalResize = Math.Abs(dX) > 0.001;
            var isVerticalResize = Math.Abs(dY) > 0.001;

            double targetWidth;
            double targetHeight;

            var resizeAxis = ResolveAspectResizeAxis(ratio, dX, dY, isHorizontalResize, isVerticalResize);
            if (resizeAxis == ResizeAxis.Width)
            {
                targetWidth = currentWidth + dX;
                targetHeight = targetWidth / ratio;
            }
            else
            {
                targetHeight = currentHeight + dY;
                targetWidth = targetHeight * ratio;
            }

            if (targetWidth < MinWidth)
            {
                targetWidth = MinWidth;
                targetHeight = targetWidth / ratio;
            }

            if (targetHeight < MinHeight)
            {
                targetHeight = MinHeight;
                targetWidth = targetHeight * ratio;
            }

            var maximumWidth = ResolveMaximumAspectLockedWidth();
            if (maximumWidth > 0 && targetWidth > maximumWidth)
            {
                targetWidth = maximumWidth;
                targetHeight = targetWidth / ratio;

                if (targetHeight < MinHeight)
                {
                    targetHeight = MinHeight;
                    targetWidth = targetHeight * ratio;
                }
            }

            return new Size(Math.Max(MinWidth, targetWidth), Math.Max(MinHeight, targetHeight));
        }

        private ResizeAxis ResolveAspectResizeAxis(
            double ratio,
            double dX,
            double dY,
            bool isHorizontalResize,
            bool isVerticalResize)
        {
            if (isHorizontalResize && !isVerticalResize)
                return ResizeAxis.Width;

            if (isVerticalResize && !isHorizontalResize)
                return ResizeAxis.Height;

            if (_activeAspectResizeAxis != ResizeAxis.None)
                return _activeAspectResizeAxis;

            var horizontalMagnitude = Math.Abs(dX);
            var verticalMagnitudeAsWidth = Math.Abs(dY * ratio);
            var selectedAxis = horizontalMagnitude >= verticalMagnitudeAsWidth
                ? ResizeAxis.Width
                : ResizeAxis.Height;

            if (Math.Max(horizontalMagnitude, verticalMagnitudeAsWidth) >= 2)
                _activeAspectResizeAxis = selectedAxis;

            return selectedAxis;
        }

        private double ResolveLockedAspectRatio(double currentWidth, double currentHeight)
        {
            if (_lockedAspectRatio >= MinimumAspectRatio && !double.IsInfinity(_lockedAspectRatio))
                return _lockedAspectRatio;

            _lockedAspectRatio = ResolveCurrentAspectRatio(currentWidth, currentHeight);
            return _lockedAspectRatio;
        }

        private double ResolveCurrentAspectRatio()
        {
            return ResolveCurrentAspectRatio(ResolveElementWidth(this), ResolveElementHeight(this));
        }

        private static double ResolveCurrentAspectRatio(double width, double height)
        {
            if (width >= MinimumAspectRatio && height >= MinimumAspectRatio)
                return Math.Max(MinimumAspectRatio, width / height);

            return 1d;
        }

        private double ResolveMaximumAspectLockedWidth()
        {
            if (Parent is not Canvas canvas)
                return 0;

            var canvasWidth = ResolveCanvasWidth(canvas);
            if (canvasWidth <= 0)
                return 0;

            return Math.Max(MinWidth, canvasWidth);
        }

        private void LayoutButton_Click(object sender, RoutedEventArgs e)
        {
            if (LayoutButton.ContextMenu == null)
                return;

            LayoutButton.ContextMenu.PlacementTarget = LayoutButton;
            LayoutButton.ContextMenu.Placement = PlacementMode.Bottom;
            LayoutButton.ContextMenu.IsOpen = true;
            e.Handled = true;
        }

        private void AspectRatioToggleButton_Checked(object sender, RoutedEventArgs e)
        {
            if (!PreserveAspectRatio)
                PreserveAspectRatio = true;

            e.Handled = true;
        }

        private void AspectRatioToggleButton_Unchecked(object sender, RoutedEventArgs e)
        {
            if (PreserveAspectRatio)
                PreserveAspectRatio = false;

            e.Handled = true;
        }

        private void InlineLayoutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            RequestLayoutChange(NoteImageLayout.Inline);
        }

        private void FloatingLayoutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            RequestLayoutChange(NoteImageLayout.Floating);
        }

        private void RequestLayoutChange(string layoutMode)
        {
            if (string.Equals(LayoutMode, layoutMode, StringComparison.OrdinalIgnoreCase))
                return;

            if (LayoutButton.ContextMenu != null)
                LayoutButton.ContextMenu.IsOpen = false;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                LayoutChangeRequested?.Invoke(this, new ImageLayoutChangeRequestedEventArgs(layoutMode));
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private Point ClampPositionToParentCanvas(double left, double top)
        {
            left = Math.Max(0, left);

            if (Parent is not Canvas canvas)
                return new Point(left, Math.Max(0, top));

            var imageHeight = ResolveElementHeight(this);
            var minimumTop = 0d;
            var maximumTop = ResolveMaximumTop(canvas, imageHeight);
            top = maximumTop >= minimumTop
                ? Math.Min(Math.Max(minimumTop, top), maximumTop)
                : minimumTop;

            var canvasWidth = ResolveCanvasWidth(canvas);
            var imageWidth = ResolveElementWidth(this);
            if (canvasWidth > 0 && imageWidth > 0)
            {
                left = Math.Min(left, Math.Max(0, canvasWidth - imageWidth));
            }

            return new Point(left, top);
        }

        private void ClampBoundsToParentCanvas()
        {
            if (Parent is not Canvas canvas)
                return;

            var canvasWidth = ResolveCanvasWidth(canvas);
            if (canvasWidth > 0)
            {
                Width = Math.Min(Width, Math.Max(MinWidth, canvasWidth));
            }

            var left = Canvas.GetLeft(this);
            if (double.IsNaN(left))
                left = 0;

            var top = Canvas.GetTop(this);
            if (double.IsNaN(top))
                top = 0;

            var clampedPosition = ClampPositionToParentCanvas(left, top);
            Canvas.SetLeft(this, clampedPosition.X);
            Canvas.SetTop(this, clampedPosition.Y);
        }

        private double ResolveCanvasWidth(Canvas canvas)
        {
            if (!double.IsNaN(canvas.Width) && canvas.Width > 0)
                return canvas.Width;

            if (canvas.ActualWidth > 0)
                return canvas.ActualWidth;

            var editor = EditorHost ?? FindVisualAncestor<RichTextBox>(canvas);
            if (editor?.ActualWidth > 0)
                return Math.Max(0, editor.ActualWidth - editor.Padding.Left - editor.Padding.Right);

            return 0;
        }

        private double ResolveMaximumTop(Canvas canvas, double imageHeight)
        {
            if (canvas.Parent is not BlockUIContainer && canvas.ActualHeight > 0 && imageHeight > 0)
                return Math.Max(0, canvas.ActualHeight - imageHeight);

            var editor = EditorHost ?? FindVisualAncestor<RichTextBox>(canvas);
            if (editor == null || editor.ActualHeight <= 0 || imageHeight <= 0)
                return double.PositiveInfinity;

            try
            {
                var canvasOrigin = canvas.TranslatePoint(new Point(0, 0), editor);
                var editorBottom = Math.Max(0, editor.ActualHeight - editor.Padding.Bottom);
                return editorBottom - imageHeight - canvasOrigin.Y;
            }
            catch
            {
                return double.PositiveInfinity;
            }
        }

        private static double ResolveElementWidth(FrameworkElement element)
        {
            if (!double.IsNaN(element.Width) && element.Width > 0)
                return element.Width;

            if (element.ActualWidth > 0)
                return element.ActualWidth;

            return Math.Max(0, element.MinWidth);
        }

        private static double ResolveElementHeight(FrameworkElement element)
        {
            if (!double.IsNaN(element.Height) && element.Height > 0)
                return element.Height;

            if (element.ActualHeight > 0)
                return element.ActualHeight;

            return Math.Max(0, element.MinHeight);
        }

        private void EnsureParentCanvasContainsImage()
        {
            if (Parent is not Canvas canvas)
                return;

            if (string.Equals(LayoutMode, NoteImageLayout.Floating, StringComparison.OrdinalIgnoreCase))
            {
                if (canvas.Parent is BlockUIContainer)
                {
                    canvas.Height = FloatingCanvasAnchorHeight;
                    canvas.MinHeight = FloatingCanvasAnchorHeight;
                }

                canvas.InvalidateMeasure();
                return;
            }

            var top = Canvas.GetTop(this);
            if (double.IsNaN(top) || top < 0)
            {
                top = 0;
                Canvas.SetTop(this, top);
            }

            var imageHeight = double.IsNaN(Height) || Height <= 0 ? ActualHeight : Height;
            if (imageHeight <= 0)
                imageHeight = MinHeight;

            var requiredHeight = Math.Max(FloatingCanvasAnchorHeight, top + imageHeight + CanvasPadding);
            if (Math.Abs(canvas.Height - requiredHeight) > 0.5 || double.IsNaN(canvas.Height))
            {
                canvas.Height = requiredHeight;
            }

            canvas.MinHeight = requiredHeight;
            canvas.InvalidateMeasure();
        }

        private void AutoScrollEditor(MouseEventArgs e)
        {
            var editor = EditorHost ?? FindVisualAncestor<RichTextBox>(this);
            if (editor == null || editor.ActualHeight <= 0)
                return;

            var pointer = e.GetPosition(editor);
            if (pointer.Y > editor.ActualHeight - EdgeScrollZone)
            {
                var distance = pointer.Y - (editor.ActualHeight - EdgeScrollZone);
                var step = Math.Min(MaxEdgeScrollStep, Math.Max(4, distance * 0.35));
                editor.ScrollToVerticalOffset(editor.VerticalOffset + step);
            }
            else if (pointer.Y < EdgeScrollZone)
            {
                var distance = EdgeScrollZone - pointer.Y;
                var step = Math.Min(MaxEdgeScrollStep, Math.Max(4, distance * 0.35));
                editor.ScrollToVerticalOffset(Math.Max(0, editor.VerticalOffset - step));
            }
        }

        private static T? FindVisualAncestor<T>(DependencyObject current)
            where T : DependencyObject
        {
            DependencyObject? cursor = current;

            while (cursor != null)
            {
                if (cursor is T match)
                    return match;

                cursor = VisualTreeHelper.GetParent(cursor);
            }

            return null;
        }

        private void TopLeft_DragDelta(object sender, DragDeltaEventArgs e) => Resize(-e.HorizontalChange, -e.VerticalChange, true, true);
        private void Top_DragDelta(object sender, DragDeltaEventArgs e) => Resize(0, -e.VerticalChange, false, true);
        private void TopRight_DragDelta(object sender, DragDeltaEventArgs e) => Resize(e.HorizontalChange, -e.VerticalChange, false, true);
        private void Right_DragDelta(object sender, DragDeltaEventArgs e) => Resize(e.HorizontalChange, 0, false, false);
        private void BottomRight_DragDelta(object sender, DragDeltaEventArgs e) => Resize(e.HorizontalChange, e.VerticalChange, false, false);
        private void Bottom_DragDelta(object sender, DragDeltaEventArgs e) => Resize(0, e.VerticalChange, false, false);
        private void BottomLeft_DragDelta(object sender, DragDeltaEventArgs e) => Resize(-e.HorizontalChange, e.VerticalChange, true, false);
        private void Left_DragDelta(object sender, DragDeltaEventArgs e) => Resize(-e.HorizontalChange, 0, true, false);
    }

    public sealed class ImageLayoutChangeRequestedEventArgs : EventArgs
    {
        public ImageLayoutChangeRequestedEventArgs(string layoutMode)
        {
            LayoutMode = layoutMode;
        }

        public string LayoutMode { get; }
    }

    public sealed class InlineImageMoveRequestedEventArgs : EventArgs
    {
        public InlineImageMoveRequestedEventArgs(Point editorPoint)
        {
            EditorPoint = editorPoint;
        }

        public Point EditorPoint { get; }
    }
}
