using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;

namespace Scriblucent.Controls;

/// <summary>
/// An "in line with text" image element hosted inside a paragraph's text flow
/// (LibreOffice "as character" / ONLYOFFICE "in line"): it occupies a character position,
/// moves with the text, can be dragged to a new anchor, resized via corner handles, and
/// deleted like a character. The coordinator owns the session state; this control only
/// raises events.
/// </summary>
public partial class InlineNoteImageElement : UserControl
{
    private const double BodyDragThresholdDip = 4.0;

    /// <summary>Carries a mouse position relative to the inline element while it is being
    /// dragged for re-anchoring (the coordinator resolves it against the editor).</summary>
    public sealed class InlineReanchorEventArgs : EventArgs
    {
        public required Point ElementPoint { get; init; }
    }

    public event EventHandler? SelectionRequested;
    public event EventHandler? BoundsChanging;
    public event EventHandler? BoundsChanged;
    public event EventHandler? GestureStarted;
    public event EventHandler? GestureCompleted;
    public event EventHandler? DeleteRequested;
    public event EventHandler? LockAspectRatioChanged;
    public event EventHandler? WrapModeChanged;
    public event EventHandler? CopyRequested;
    public event EventHandler? CutRequested;
    public event EventHandler? ChangePictureRequested;
    public event EventHandler? CaptionRequested;
    /// <summary>Raised while a re-anchor drag moves over the text; the position under the mouse.</summary>
    public event EventHandler<InlineReanchorEventArgs>? ReanchorPreview;
    /// <summary>Raised when a re-anchor drag is released.</summary>
    public event EventHandler<InlineReanchorEventArgs>? ReanchorRequested;

    public static readonly DependencyProperty ImageIdProperty = DependencyProperty.Register(
        nameof(ImageId), typeof(Guid), typeof(InlineNoteImageElement));

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(InlineNoteImageElement),
        new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(InlineNoteImageElement),
        new PropertyMetadata(false, (d, _) => ((InlineNoteImageElement)d).OnIsSelectedChanged()));

    public static readonly DependencyProperty LockAspectRatioProperty = DependencyProperty.Register(
        nameof(LockAspectRatio), typeof(bool), typeof(InlineNoteImageElement),
        new PropertyMetadata(true, (d, _) => ((InlineNoteImageElement)d).OnLockAspectRatioChanged()));

    public static readonly DependencyProperty WrapModeProperty = DependencyProperty.Register(
        nameof(WrapMode), typeof(NoteImageWrapMode), typeof(InlineNoteImageElement),
        new PropertyMetadata(NoteImageWrapMode.None));

    /// <summary>
    /// When the image is pressed against the editor's top edge, the action chips would clip
    /// off-canvas - the coordinator flips them below the image instead.
    /// </summary>
    public static readonly DependencyProperty ButtonsFlippedProperty = DependencyProperty.Register(
        nameof(ButtonsFlipped), typeof(bool), typeof(InlineNoteImageElement),
        new PropertyMetadata(false, (d, _) => ((InlineNoteImageElement)d).ApplyButtonPlacement()));

    public bool ButtonsFlipped
    {
        get => (bool)GetValue(ButtonsFlippedProperty);
        set => SetValue(ButtonsFlippedProperty, value);
    }

    // NOTE: this control deliberately holds NO reference to the editor. Any public property
    // pointing back at the RichTextBox creates a reference cycle (element → editor →
    // document → element) that WPF's undo units and MarkupWriter follow indefinitely when
    // the element is removed from the flow (stack overflow). Drag positions are reported in
    // this element's own coordinate space; the coordinator converts them.

    public Guid ImageId
    {
        get => (Guid)GetValue(ImageIdProperty);
        set => SetValue(ImageIdProperty, value);
    }

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public bool LockAspectRatio
    {
        get => (bool)GetValue(LockAspectRatioProperty);
        set => SetValue(LockAspectRatioProperty, value);
    }

    public NoteImageWrapMode WrapMode
    {
        get => (NoteImageWrapMode)GetValue(WrapModeProperty);
        set => SetValue(WrapModeProperty, value);
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((InlineNoteImageElement)d).DisplayImage.Source = e.NewValue as ImageSource;

    private bool _bodyDragPending;
    private bool _isBodyDragging;
    private Point _bodyDragOrigin;
    private bool _isResizing;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private double _resizeAccumulatedX;
    private double _resizeAccumulatedY;
    private string _activeHandle = string.Empty;

    public InlineNoteImageElement()
    {
        InitializeComponent();

        // A stable text cursor: the photo is part of the text flow, so it consistently shows
        // the I-beam (never the RichTextBox's hit-test-dependent flicker between I-beam and
        // arrow). The resize thumbs and action chips keep their own cursors.
        Cursor = Cursors.IBeam;
        UpdateLockAspectChrome();

        // The element is hosted inside an InlineUIContainer in a RichTextBox flow. WPF's
        // routed-event route from such flow-hosted children does not reliably pass through
        // the parent Border (both for programmatic raises and real input in some setups), so
        // the image itself carries a handler copy - clicks on the picture always select it.
        DisplayImage.AddHandler(
            UIElement.MouseLeftButtonDownEvent,
            new MouseButtonEventHandler(Body_MouseLeftButtonDown),
            handledEventsToo: false);

        // The action chips (aspect lock / delete) are ordinary Buttons inside the flow, and
        // the RichTextBox's editing engine owns mouse input over flow content - a click can
        // be swallowed for caret placement before the button ever sees it. Intercept the
        // tunneling phase (even when the editor marked it handled) and act on the chip
        // directly, so the chips always work with real input.
        AddHandler(
            UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnChipPreviewMouseLeftButtonDown),
            handledEventsToo: true);

        // The paragraph's LineHeight/LineStackingStrategy inherit into the context menu's
        // content through the logical tree (the element is part of the flow), stretching every
        // menu row. Block the inheritance at the menu root.
        if (ContextMenu is not null)
        {
            ContextMenu.SetValue(Block.LineHeightProperty, double.NaN);
            ContextMenu.SetValue(Block.LineStackingStrategyProperty, LineStackingStrategy.MaxHeight);
            // Backup: re-sync the wrap-mode checkmarks whenever the menu actually opens.
            ContextMenu.Opened += (_, _) => SyncWrapMenuCheckStates();
        }
    }

    private void OnChipPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        for (DependencyObject? hit = source;
             hit is not null && hit is not InlineNoteImageElement;
             hit = hit is Visual visual
                 ? VisualTreeHelper.GetParent(visual)
                 : LogicalTreeHelper.GetParent(hit))
        {
            if (ReferenceEquals(hit, LockAspectButton))
            {
                e.Handled = true;
                SelectionRequested?.Invoke(this, EventArgs.Empty);
                LockAspectRatio = !LockAspectRatio;
                return;
            }

            if (ReferenceEquals(hit, DeleteButton))
            {
                e.Handled = true;
                DeleteRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
        }
    }

    private double _imageWidth;
    private double _imageHeight;

    public void SetDisplaySize(double width, double height)
    {
        _imageWidth = width;
        _imageHeight = height;
        ApplyCaptionLayout();
    }

    /// <summary>Shows or hides the caption row; the element's box grows to fit the caption.</summary>
    public void SetCaption(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            CaptionTextBlock.Text = string.Empty;
            CaptionTextBlock.Visibility = Visibility.Collapsed;
        }
        else
        {
            CaptionTextBlock.Text = text;
            CaptionTextBlock.Visibility = Visibility.Visible;
        }

        ApplyCaptionLayout();
    }

    /// <summary>Height reserved by the caption row (used to keep the session's image height
    /// consistent when the element box includes the caption).</summary>
    public double CaptionHeight => CaptionTextBlock.Visibility == Visibility.Visible
        ? CaptionTextBlock.DesiredSize.Height + 6
        : 0;

    private void ApplyCaptionLayout()
    {
        ImageRow.Height = new GridLength(_imageHeight);
        if (CaptionTextBlock.Visibility == Visibility.Visible)
            CaptionTextBlock.Measure(new Size(Math.Max(40, _imageWidth), double.PositiveInfinity));

        Width = _imageWidth;
        Height = _imageHeight + CaptionHeight;
    }

    private void OnIsSelectedChanged()
    {
        // Matches the floating widget's selection chrome: accent wash + corner handles.
        if (IsSelected)
        {
            SelectionWash.Visibility = Visibility.Visible;
            HandleLayer.Visibility = Visibility.Visible;
        }
        else
        {
            SelectionWash.Visibility = Visibility.Collapsed;
            HandleLayer.Visibility = Visibility.Collapsed;
        }
    }

    private void OnLockAspectRatioChanged()
    {
        // The chip must show its state - otherwise toggling the lock looks like it does
        // nothing (the "the button is not working" confusion).
        UpdateLockAspectChrome();
        LockAspectRatioChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Mirrors the floating widget's lock chip: active style while locked, neutral
    /// otherwise, and the tooltip names the action the next click takes.</summary>
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

    private void SyncWrapMenuCheckStates()
    {
        WrapNoneItem.IsChecked = WrapMode == NoteImageWrapMode.None;
        WrapSquareItem.IsChecked = WrapMode == NoteImageWrapMode.Square;
        WrapTopBottomItem.IsChecked = WrapMode == NoteImageWrapMode.TopAndBottom;
        WrapInlineItem.IsChecked = WrapMode == NoteImageWrapMode.Inline;
    }

    private void ContextMenuDelete_Click(object sender, RoutedEventArgs e)
        => DeleteRequested?.Invoke(this, EventArgs.Empty);

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

    private void ContextMenuLockAspect_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SelectionRequested?.Invoke(this, EventArgs.Empty);
        LockAspectRatio = !LockAspectRatio; // the DP callback raises LockAspectRatioChanged
        LockAspectItem.IsChecked = LockAspectRatio;
    }

    private void Body_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        SelectionRequested?.Invoke(this, EventArgs.Empty);
        _bodyDragPending = true;
        _isBodyDragging = false;
        _bodyDragOrigin = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if ((!_bodyDragPending && !_isBodyDragging) || e.LeftButton != MouseButtonState.Pressed)
            return;

        if (!_isBodyDragging)
        {
            var delta = e.GetPosition(this) - _bodyDragOrigin;
            if (Math.Abs(delta.X) < BodyDragThresholdDip && Math.Abs(delta.Y) < BodyDragThresholdDip)
                return;

            _isBodyDragging = true;
            GestureStarted?.Invoke(this, EventArgs.Empty);
        }

        RaiseReanchorPreview();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        var wasDragging = _isBodyDragging;
        if (wasDragging)
            ReanchorRequested?.Invoke(this, new InlineReanchorEventArgs { ElementPoint = e.GetPosition(this) });

        _bodyDragPending = false;
        _isBodyDragging = false;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        if (wasDragging)
            GestureCompleted?.Invoke(this, EventArgs.Empty);

        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        if (_isBodyDragging)
        {
            _isBodyDragging = false;
            GestureCompleted?.Invoke(this, EventArgs.Empty);
        }

        _bodyDragPending = false;
    }

    private void RaiseReanchorPreview()
        => ReanchorPreview?.Invoke(this, new InlineReanchorEventArgs { ElementPoint = Mouse.GetPosition(this) });

    private void ResizeThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string tag || tag.Length == 0)
            return;

        _activeHandle = tag;
        SelectionRequested?.Invoke(this, EventArgs.Empty);
        _resizeStartWidth = Width;
        _resizeStartHeight = Height;
        _resizeAccumulatedX = 0;
        _resizeAccumulatedY = 0;
        _isResizing = true;
        GestureStarted?.Invoke(this, EventArgs.Empty);
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_isResizing)
            return;

        _resizeAccumulatedX += e.HorizontalChange;
        _resizeAccumulatedY += e.VerticalChange;
        ApplyResize(_resizeAccumulatedX, _resizeAccumulatedY);
        BoundsChanging?.Invoke(this, EventArgs.Empty);
    }

    private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_isResizing)
            return;

        _isResizing = false;
        var resized = !Width.Equals(_resizeStartWidth) || !Height.Equals(_resizeStartHeight);
        if (resized)
            BoundsChanged?.Invoke(this, EventArgs.Empty);
        GestureCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Scales the inline image from the drag deltas; the text anchor stays in place,
    /// so growth is right/down anchored and left/top drags just mirror the scale.</summary>
    private void ApplyResize(double accumulatedX, double accumulatedY)
    {
        var horizontal = _activeHandle is "TL" or "TR" ? -accumulatedX : accumulatedX;
        var vertical = _activeHandle is "TL" or "BL" ? -accumulatedY : accumulatedY;

        double newWidth;
        double newHeight;
        if (LockAspectRatio)
        {
            var projected = NoteImageResizeMath.ProjectCornerScale(
                _resizeStartWidth,
                _resizeStartHeight,
                _resizeStartWidth + horizontal,
                _resizeStartHeight + vertical);
            newWidth = projected.Width;
            newHeight = projected.Height;
        }
        else
        {
            newWidth = _resizeStartWidth + horizontal;
            newHeight = _resizeStartHeight + vertical;
        }

        var clamped = LockAspectRatio
            ? NoteImageResizeMath.ClampDisplaySize(newWidth, newHeight)
            : NoteImageResizeMath.ClampFreeformSize(newWidth, newHeight);

        Width = clamped.Width;
        Height = clamped.Height;
        // Keep the image size in sync with the box (the caption row is not resized), so a
        // later caption layout pass cannot reset the element to its pre-resize size.
        _imageWidth = clamped.Width;
        _imageHeight = clamped.Height - CaptionHeight;
    }
}
