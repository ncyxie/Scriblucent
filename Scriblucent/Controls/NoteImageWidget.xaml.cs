using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;

namespace Scriblucent.Controls;

public partial class NoteImageWidget : UserControl
{
    public const double MinSideDip = NoteImageResizeMath.MinSideDip;
    public const double MaxSideDip = NoteImageResizeMath.MaxSideDip;

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

    private bool _isBodyDragging;
    private bool _bodyDragPending;
    private bool _visualStatesReady;
    private Point _bodyDragOrigin;
    private double _dragStartLeft;
    private double _dragStartTop;
    private const double BodyDragThresholdDip = 4.0;
    private ResizeHandle _activeHandle = ResizeHandle.None;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private double _resizeStartLeft;
    private double _resizeStartTop;
    private double _resizeAccumulatedX;
    private double _resizeAccumulatedY;
    private double _aspectRatio = 1.0;

    public event EventHandler? SelectionRequested;
    public event EventHandler? BoundsChanging;
    public event EventHandler? BoundsChanged;
    public event EventHandler? GestureStarted;
    public event EventHandler? GestureCompleted;
    public event EventHandler? DeleteRequested;
    public event EventHandler? LockAspectRatioChanged;
    public event EventHandler? WrapModeChanged;
    /// <summary>Raised when the context menu's Bring forward / Send backward is clicked.</summary>
    public event EventHandler<NoteImageArrangeDirection>? ArrangeRequested;
    public event EventHandler? CopyRequested;
    public event EventHandler? CutRequested;
    public event EventHandler? ChangePictureRequested;
    public event EventHandler? CaptionRequested;
    public bool IsBodyDragging => _isBodyDragging;

    public NoteImageWidget()
    {
        InitializeComponent();
        RenderOptions.SetBitmapScalingMode(DisplayImage, BitmapScalingMode.HighQuality);
        // Backup: re-sync the wrap-mode checkmarks whenever the menu actually opens, in case
        // the opening event's Source resolution ever differs.
        if (ContextMenu is not null)
            ContextMenu.Opened += (_, _) => SyncWrapMenuCheckStates();
        Loaded += (_, _) =>
        {
            _visualStatesReady = true;
            ApplyVisualStates(useTransitions: false);
            UpdateLockAspectChrome();
        };
    }

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(
            nameof(Source),
            typeof(ImageSource),
            typeof(NoteImageWidget),
            new PropertyMetadata(null, OnSourceChanged));

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.Register(
            nameof(IsSelected),
            typeof(bool),
            typeof(NoteImageWidget),
            new PropertyMetadata(false, OnIsSelectedChanged));

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public static readonly DependencyProperty ImageIdProperty =
        DependencyProperty.Register(
            nameof(ImageId),
            typeof(Guid),
            typeof(NoteImageWidget),
            new PropertyMetadata(Guid.Empty));

    public Guid ImageId
    {
        get => (Guid)GetValue(ImageIdProperty);
        set => SetValue(ImageIdProperty, value);
    }

    public static readonly DependencyProperty LockAspectRatioProperty =
        DependencyProperty.Register(
            nameof(LockAspectRatio),
            typeof(bool),
            typeof(NoteImageWidget),
            new PropertyMetadata(true, OnLockAspectRatioChanged));

    public bool LockAspectRatio
    {
        get => (bool)GetValue(LockAspectRatioProperty);
        set => SetValue(LockAspectRatioProperty, value);
    }

    public static readonly DependencyProperty WrapModeProperty =
        DependencyProperty.Register(
            nameof(WrapMode),
            typeof(NoteImageWrapMode),
            typeof(NoteImageWidget),
            new PropertyMetadata(NoteImageWrapMode.None));

    /// <summary>
    /// When the image is pressed against the editor's top edge, the action chips would clip
    /// off-canvas - the coordinator flips them below the image instead.
    /// </summary>
    public static readonly DependencyProperty ButtonsFlippedProperty = DependencyProperty.Register(
        nameof(ButtonsFlipped),
        typeof(bool),
        typeof(NoteImageWidget),
        new PropertyMetadata(false, (d, _) => ((NoteImageWidget)d).ApplyButtonPlacement()));

    public bool ButtonsFlipped
    {
        get => (bool)GetValue(ButtonsFlippedProperty);
        set => SetValue(ButtonsFlippedProperty, value);
    }

    /// <summary>Text wrap mode; the coordinator mirrors it onto the image session.</summary>
    public NoteImageWrapMode WrapMode
    {
        get => (NoteImageWrapMode)GetValue(WrapModeProperty);
        set => SetValue(WrapModeProperty, value);
    }

    public static readonly DependencyProperty DocumentLeftProperty =
        DependencyProperty.Register(
            nameof(DocumentLeft),
            typeof(double),
            typeof(NoteImageWidget),
            new PropertyMetadata(0.0));

    /// <summary>
    /// View mirror of session document X. Written by <see cref="FloatingImageCoordinator"/>;
    /// resize gestures may stage values that the coordinator commits back to the session.
    /// </summary>
    public double DocumentLeft
    {
        get => (double)GetValue(DocumentLeftProperty);
        set => SetValue(DocumentLeftProperty, value);
    }

    public static readonly DependencyProperty DocumentTopProperty =
        DependencyProperty.Register(
            nameof(DocumentTop),
            typeof(double),
            typeof(NoteImageWidget),
            new PropertyMetadata(0.0));

    /// <summary>
    /// View mirror of session document Y. Written by <see cref="FloatingImageCoordinator"/>;
    /// resize gestures may stage values that the coordinator commits back to the session.
    /// </summary>
    public double DocumentTop
    {
        get => (double)GetValue(DocumentTopProperty);
        set => SetValue(DocumentTopProperty, value);
    }

    public bool IsInteracting => _isBodyDragging || _activeHandle != ResizeHandle.None;

    public double AspectRatio => _aspectRatio;

    public void SetDisplaySize(double width, double height)
    {
        var clamped = LockAspectRatio
            ? NoteImageResizeMath.ClampDisplaySize(width, height)
            : NoteImageResizeMath.ClampFreeformSize(width, height);
        Width = clamped.Width;
        Height = clamped.Height;
        if (clamped.Height > 0)
            _aspectRatio = clamped.Width / clamped.Height;
    }

    /// <summary>Applies a size that was already saved. Live resize still goes through
    /// <see cref="SetDisplaySize"/>.</summary>
    public void SetExactDisplaySize(double width, double height)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0
            || double.IsNaN(height) || double.IsInfinity(height) || height <= 0)
        {
            SetDisplaySize(width, height);
            return;
        }

        Width = width;
        Height = height;
        _aspectRatio = width / height;
    }

    public Rect GetDocumentRect() =>
        new(DocumentLeft, DocumentTop, Width, Height);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NoteImageWidget widget)
            widget.DisplayImage.Source = e.NewValue as ImageSource;
    }

    private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NoteImageWidget widget)
        {
            widget.Cursor = widget.IsSelected ? Cursors.SizeAll : Cursors.Arrow;
            widget.ApplyVisualStates(useTransitions: false);
        }
    }

    private static void OnLockAspectRatioChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NoteImageWidget widget)
            widget.UpdateLockAspectChrome();
    }

    private void ApplyButtonPlacement()
    {
        if (LockAspectButton is null || DeleteButton is null)
            return;

        LockAspectButton.VerticalAlignment = ButtonsFlipped ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        LockAspectButton.Margin = ButtonsFlipped
            ? new Thickness(0, 0, 44, -34)
            : new Thickness(0, -34, 44, 0);
        DeleteButton.VerticalAlignment = ButtonsFlipped ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        DeleteButton.Margin = ButtonsFlipped
            ? new Thickness(0, 0, 12, -34)
            : new Thickness(0, -34, 12, 0);
    }

    private void UpdateLockAspectChrome()
    {
        if (LockAspectButton is null)
            return;

        LockAspectButton.ToolTip = LocalizationService.GetString(
            LockAspectRatio ? "UnlockAspectRatio" : "LockAspectRatio");

        if (LockAspectRatio)
        {
            LockAspectButton.SetResourceReference(Control.BackgroundProperty, "EditorToolActiveBackground");
            LockAspectButton.SetResourceReference(Control.BorderBrushProperty, "EditorToolActiveBorder");
            LockAspectButton.SetResourceReference(Control.ForegroundProperty, "EditorToolActiveForeground");
            LockAspectButton.BorderThickness = new Thickness(2);
        }
        else
        {
            LockAspectButton.SetResourceReference(Control.BackgroundProperty, "IconButtonBackground");
            LockAspectButton.BorderBrush = Brushes.Transparent;
            LockAspectButton.SetResourceReference(Control.ForegroundProperty, "IconButtonForeground");
            LockAspectButton.BorderThickness = new Thickness(0);
        }
    }

    private void ApplyVisualStates(bool useTransitions)
    {
        if (!_visualStatesReady)
            return;

        VisualStateManager.GoToState(this, IsSelected ? "Selected" : "Unselected", useTransitions);

        // Flat at rest / when idle; Popup elevation only while dragging or resizing.
        // Effect lives on ElevationHost (unclipped) so ImageClipHost can keep ClipToBounds
        // without cutting the shadow, and bitmap alpha drives the silhouette (no card plate).
        if (IsInteracting
            && TryFindResource("Elevation.Popup") is System.Windows.Media.Effects.Effect effect)
        {
            var clone = effect.CloneCurrentValue();
            if (clone.CanFreeze)
                clone.Freeze();
            ElevationHost.Effect = clone;
        }
        else
        {
            ElevationHost.Effect = null;
        }
    }

    private void BeginGesture()
    {
        ApplyVisualStates(useTransitions: false);
        GestureStarted?.Invoke(this, EventArgs.Empty);
    }

    private void EndGesture()
    {
        ApplyVisualStates(useTransitions: false);
        GestureCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        DeleteRequested?.Invoke(this, EventArgs.Empty);
    }

    private void LockAspectButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SelectionRequested?.Invoke(this, EventArgs.Empty);
        LockAspectRatio = !LockAspectRatio;
        if (LockAspectRatio && Height > 0)
            _aspectRatio = Width / Height;
        LockAspectRatioChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ContextMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        SelectionRequested?.Invoke(this, EventArgs.Empty);
        // ContextMenuOpening is raised on THIS control, so the sender is the UserControl -
        // the opening menu comes from the event args' Source (fall back to the sender or the
        // control's own menu to stay null-tolerant).
        var menu = e?.Source as ContextMenu ?? sender as ContextMenu ?? ContextMenu;
        if (menu is not null)
        {
            menu.PlacementTarget = this;
            SyncWrapMenuCheckStates();
            LockAspectItem.IsChecked = LockAspectRatio;
        }
    }

    private void SyncWrapMenuCheckStates()
    {
        WrapNoneItem.IsChecked = WrapMode == NoteImageWrapMode.None;
        WrapSquareItem.IsChecked = WrapMode == NoteImageWrapMode.Square;
        WrapTopBottomItem.IsChecked = WrapMode == NoteImageWrapMode.TopAndBottom;
        WrapInlineItem.IsChecked = WrapMode == NoteImageWrapMode.Inline;
    }

    private void ContextMenuWrapMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag })
            return;

        WrapMode = tag switch
        {
            "Square" => NoteImageWrapMode.Square,
            "TopAndBottom" => NoteImageWrapMode.TopAndBottom,
            "Inline" => NoteImageWrapMode.Inline,
            _ => NoteImageWrapMode.None
        };

        // Radio semantics: clicking the already-active item keeps it selected.
        SyncWrapMenuCheckStates();
        WrapModeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ContextMenuDelete_Click(object sender, RoutedEventArgs e)
    {
        DeleteRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ContextMenuCopy_Click(object sender, RoutedEventArgs e)
    {
        SelectionRequested?.Invoke(this, EventArgs.Empty);
        CopyRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ContextMenuCut_Click(object sender, RoutedEventArgs e)
    {
        SelectionRequested?.Invoke(this, EventArgs.Empty);
        CutRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ContextMenuChangePicture_Click(object sender, RoutedEventArgs e)
    {
        SelectionRequested?.Invoke(this, EventArgs.Empty);
        ChangePictureRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ContextMenuCaption_Click(object sender, RoutedEventArgs e)
    {
        SelectionRequested?.Invoke(this, EventArgs.Empty);
        CaptionRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ContextMenuArrange_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag })
            return;

        e.Handled = true;
        ArrangeRequested?.Invoke(
            this,
            tag == "Forward"
                ? NoteImageArrangeDirection.BringForward
                : NoteImageArrangeDirection.SendBackward);
    }

    private void ContextMenuLockAspect_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SelectionRequested?.Invoke(this, EventArgs.Empty);
        LockAspectRatio = !LockAspectRatio;
        if (LockAspectRatio && Height > 0)
            _aspectRatio = Width / Height;
        LockAspectItem.IsChecked = LockAspectRatio;
        LockAspectRatioChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Body_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        SelectionRequested?.Invoke(this, EventArgs.Empty);

        _bodyDragPending = true;
        _isBodyDragging = false;
        _bodyDragOrigin = e.GetPosition(Parent as IInputElement ?? this);
        _dragStartLeft = DocumentLeft;
        _dragStartTop = DocumentTop;
        // Capture for reliable mouse-up; BeginGesture only after move threshold.
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if ((!_bodyDragPending && !_isBodyDragging) || e.LeftButton != MouseButtonState.Pressed)
            return;

        var parent = Parent as IInputElement;
        if (parent is null)
            return;

        var current = e.GetPosition(parent);

        if (_bodyDragPending && !_isBodyDragging)
        {
            var dx = current.X - _bodyDragOrigin.X;
            var dy = current.Y - _bodyDragOrigin.Y;
            if ((dx * dx) + (dy * dy) < BodyDragThresholdDip * BodyDragThresholdDip)
                return;

            _bodyDragPending = false;
            _isBodyDragging = true;
            BeginGesture();
        }

        if (!_isBodyDragging)
            return;

        // Coordinator places via absolute pointer + scroll (FollowPointer) so pace matches scrollbar.
        BoundsChanging?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (!_bodyDragPending && !_isBodyDragging)
            return;

        var wasDragging = _isBodyDragging;
        _bodyDragPending = false;
        _isBodyDragging = false;
        if (IsMouseCaptured)
            ReleaseMouseCapture();

        if (wasDragging)
        {
            var moved = !DocumentLeft.Equals(_dragStartLeft) || !DocumentTop.Equals(_dragStartTop);
            if (moved)
                BoundsChanged?.Invoke(this, EventArgs.Empty);

            EndGesture();
        }

        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        if (!_bodyDragPending && !_isBodyDragging)
            return;

        // FlowDocument extent-pad updates can steal capture mid-drag; keep going while LMB is down.
        if (Mouse.LeftButton == MouseButtonState.Pressed && CaptureMouse())
            return;

        var wasDragging = _isBodyDragging;
        _bodyDragPending = false;
        _isBodyDragging = false;

        if (wasDragging)
        {
            var moved = !DocumentLeft.Equals(_dragStartLeft) || !DocumentTop.Equals(_dragStartTop);
            if (moved)
                BoundsChanged?.Invoke(this, EventArgs.Empty);

            EndGesture();
        }
    }

    private void ResizeThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string tag)
            return;

        _activeHandle = ParseHandle(tag);
        if (_activeHandle == ResizeHandle.None)
            return;

        SelectionRequested?.Invoke(this, EventArgs.Empty);
        _resizeStartWidth = Width;
        _resizeStartHeight = Height;
        _resizeStartLeft = DocumentLeft;
        _resizeStartTop = DocumentTop;
        _resizeAccumulatedX = 0;
        _resizeAccumulatedY = 0;
        if (_resizeStartHeight > 0)
            _aspectRatio = _resizeStartWidth / _resizeStartHeight;

        // The image re-scales on every drag frame - the expensive Fant filter (HighQuality)
        // is the jank behind sluggish resizing; the cheap Linear filter keeps the drag smooth
        // and the final frame re-renders sharp when the gesture ends.
        RenderOptions.SetBitmapScalingMode(DisplayImage, BitmapScalingMode.Linear);

        BeginGesture();
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_activeHandle == ResizeHandle.None)
            return;

        // DragDelta values are incremental since the last event — accumulate from gesture start.
        _resizeAccumulatedX += e.HorizontalChange;
        _resizeAccumulatedY += e.VerticalChange;
        if (LockAspectRatio)
            ApplyAspectResize(_resizeAccumulatedX, _resizeAccumulatedY);
        else
            ApplyFreeformResize(_resizeAccumulatedX, _resizeAccumulatedY);
        BoundsChanging?.Invoke(this, EventArgs.Empty);
    }

    private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_activeHandle == ResizeHandle.None)
            return;

        _activeHandle = ResizeHandle.None;

        // The gesture is over - render the settled size at full quality again.
        RenderOptions.SetBitmapScalingMode(DisplayImage, BitmapScalingMode.HighQuality);

        var resized = !Width.Equals(_resizeStartWidth)
            || !Height.Equals(_resizeStartHeight)
            || !DocumentLeft.Equals(_resizeStartLeft)
            || !DocumentTop.Equals(_resizeStartTop);
        if (resized)
            BoundsChanged?.Invoke(this, EventArgs.Empty);

        EndGesture();
    }

    private void ApplyAspectResize(double accumulatedX, double accumulatedY)
    {
        var ratio = _aspectRatio > 0.0001 ? _aspectRatio : 1.0;
        double newWidth = _resizeStartWidth;
        double newHeight = _resizeStartHeight;
        double newLeft = _resizeStartLeft;
        double newTop = _resizeStartTop;

        switch (_activeHandle)
        {
            case ResizeHandle.BottomRight:
            {
                var projected = NoteImageResizeMath.ProjectCornerScale(
                    _resizeStartWidth,
                    _resizeStartHeight,
                    _resizeStartWidth + accumulatedX,
                    _resizeStartHeight + accumulatedY);
                newWidth = projected.Width;
                newHeight = projected.Height;
                break;
            }

            case ResizeHandle.BottomLeft:
            {
                var projected = NoteImageResizeMath.ProjectCornerScale(
                    _resizeStartWidth,
                    _resizeStartHeight,
                    _resizeStartWidth - accumulatedX,
                    _resizeStartHeight + accumulatedY);
                newWidth = projected.Width;
                newHeight = projected.Height;
                newLeft = _resizeStartLeft + (_resizeStartWidth - newWidth);
                break;
            }

            case ResizeHandle.TopRight:
            {
                var projected = NoteImageResizeMath.ProjectCornerScale(
                    _resizeStartWidth,
                    _resizeStartHeight,
                    _resizeStartWidth + accumulatedX,
                    _resizeStartHeight - accumulatedY);
                newWidth = projected.Width;
                newHeight = projected.Height;
                newTop = _resizeStartTop + (_resizeStartHeight - newHeight);
                break;
            }

            case ResizeHandle.TopLeft:
            {
                var projected = NoteImageResizeMath.ProjectCornerScale(
                    _resizeStartWidth,
                    _resizeStartHeight,
                    _resizeStartWidth - accumulatedX,
                    _resizeStartHeight - accumulatedY);
                newWidth = projected.Width;
                newHeight = projected.Height;
                newLeft = _resizeStartLeft + (_resizeStartWidth - newWidth);
                newTop = _resizeStartTop + (_resizeStartHeight - newHeight);
                break;
            }

            case ResizeHandle.Right:
                newWidth = _resizeStartWidth + accumulatedX;
                newHeight = newWidth / ratio;
                newTop = _resizeStartTop + (_resizeStartHeight - newHeight) / 2;
                break;

            case ResizeHandle.Left:
                newWidth = _resizeStartWidth - accumulatedX;
                newHeight = newWidth / ratio;
                newLeft = _resizeStartLeft + (_resizeStartWidth - newWidth);
                newTop = _resizeStartTop + (_resizeStartHeight - newHeight) / 2;
                break;

            case ResizeHandle.Bottom:
                newHeight = _resizeStartHeight + accumulatedY;
                newWidth = newHeight * ratio;
                newLeft = _resizeStartLeft + (_resizeStartWidth - newWidth) / 2;
                break;

            case ResizeHandle.Top:
                newHeight = _resizeStartHeight - accumulatedY;
                newWidth = newHeight * ratio;
                newLeft = _resizeStartLeft + (_resizeStartWidth - newWidth) / 2;
                newTop = _resizeStartTop + (_resizeStartHeight - newHeight);
                break;
        }

        if (newWidth <= 0 || newHeight <= 0)
        {
            var min = NoteImageResizeMath.MinimumSizeForAspect(_aspectRatio);
            newWidth = min.Width;
            newHeight = min.Height;
        }

        var clamped = NoteImageResizeMath.ClampDisplaySize(newWidth, newHeight);

        // Re-anchor opposite edge/corner after clamp so aspect lock never drifts.
        if (_activeHandle is ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft)
            newLeft = _resizeStartLeft + (_resizeStartWidth - clamped.Width);
        if (_activeHandle is ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight)
            newTop = _resizeStartTop + (_resizeStartHeight - clamped.Height);
        if (_activeHandle is ResizeHandle.Top or ResizeHandle.Bottom)
            newLeft = _resizeStartLeft + (_resizeStartWidth - clamped.Width) / 2;
        if (_activeHandle is ResizeHandle.Left or ResizeHandle.Right)
            newTop = _resizeStartTop + (_resizeStartHeight - clamped.Height) / 2;

        Width = clamped.Width;
        Height = clamped.Height;
        DocumentLeft = newLeft;
        DocumentTop = newTop;
    }

    private void ApplyFreeformResize(double accumulatedX, double accumulatedY)
    {
        double newWidth = _resizeStartWidth;
        double newHeight = _resizeStartHeight;
        double newLeft = _resizeStartLeft;
        double newTop = _resizeStartTop;

        switch (_activeHandle)
        {
            case ResizeHandle.BottomRight:
                newWidth = _resizeStartWidth + accumulatedX;
                newHeight = _resizeStartHeight + accumulatedY;
                break;

            case ResizeHandle.BottomLeft:
                newWidth = _resizeStartWidth - accumulatedX;
                newHeight = _resizeStartHeight + accumulatedY;
                newLeft = _resizeStartLeft + (_resizeStartWidth - newWidth);
                break;

            case ResizeHandle.TopRight:
                newWidth = _resizeStartWidth + accumulatedX;
                newHeight = _resizeStartHeight - accumulatedY;
                newTop = _resizeStartTop + (_resizeStartHeight - newHeight);
                break;

            case ResizeHandle.TopLeft:
                newWidth = _resizeStartWidth - accumulatedX;
                newHeight = _resizeStartHeight - accumulatedY;
                newLeft = _resizeStartLeft + (_resizeStartWidth - newWidth);
                newTop = _resizeStartTop + (_resizeStartHeight - newHeight);
                break;

            case ResizeHandle.Right:
                newWidth = _resizeStartWidth + accumulatedX;
                break;

            case ResizeHandle.Left:
                newWidth = _resizeStartWidth - accumulatedX;
                newLeft = _resizeStartLeft + (_resizeStartWidth - newWidth);
                break;

            case ResizeHandle.Bottom:
                newHeight = _resizeStartHeight + accumulatedY;
                break;

            case ResizeHandle.Top:
                newHeight = _resizeStartHeight - accumulatedY;
                newTop = _resizeStartTop + (_resizeStartHeight - newHeight);
                break;
        }

        var clamped = NoteImageResizeMath.ClampFreeformSize(newWidth, newHeight);

        // Re-anchor opposite edge/corner after clamp.
        if (_activeHandle is ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft)
            newLeft = _resizeStartLeft + (_resizeStartWidth - clamped.Width);
        if (_activeHandle is ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight)
            newTop = _resizeStartTop + (_resizeStartHeight - clamped.Height);

        Width = clamped.Width;
        Height = clamped.Height;
        DocumentLeft = newLeft;
        DocumentTop = newTop;
        if (clamped.Height > 0)
            _aspectRatio = clamped.Width / clamped.Height;
    }

    private static ResizeHandle ParseHandle(string tag) => tag switch
    {
        "TL" => ResizeHandle.TopLeft,
        "T" => ResizeHandle.Top,
        "TR" => ResizeHandle.TopRight,
        "R" => ResizeHandle.Right,
        "BR" => ResizeHandle.BottomRight,
        "B" => ResizeHandle.Bottom,
        "BL" => ResizeHandle.BottomLeft,
        "L" => ResizeHandle.Left,
        _ => ResizeHandle.None
    };
}
