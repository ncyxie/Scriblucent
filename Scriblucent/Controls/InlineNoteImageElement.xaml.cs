using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Scriblucent.Models;
using Scriblucent.Services;

namespace Scriblucent.Controls;

/// <summary>
/// An "in line with text" image element hosted inside a paragraph's text flow
/// (LibreOffice "as character" / ONLYOFFICE "in line"): it occupies a character position,
/// moves with the text, and can be dragged to a new anchor. The selection chrome (resize
/// handles + chips) lives on the overlay canvas in <see cref="InlineImageChrome"/>, so it
/// always meets the photo's corners - the element itself only renders the photo, the caption
/// row and the selection wash. The coordinator owns the session state and drives ALL real
/// mouse interaction at the editor (the flow element is never hit directly - the surrounding
/// text visuals cover the line); this control only raises events and answers property writes.
/// </summary>
public partial class InlineNoteImageElement : UserControl
{
    public event EventHandler? SelectionRequested;
    public event EventHandler? DeleteRequested;
    public event EventHandler? LockAspectRatioChanged;
    public event EventHandler? WrapModeChanged;
    public event EventHandler? CopyRequested;
    public event EventHandler? CutRequested;
    public event EventHandler? ChangePictureRequested;
    public event EventHandler? CaptionRequested;
    public event EventHandler? PaintRequested;

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

    public InlineNoteImageElement()
    {
        InitializeComponent();

        // A stable text cursor: the photo is part of the text flow, so it consistently shows
        // the I-beam (never the RichTextBox's hit-test-dependent flicker between I-beam and
        // arrow). The selection chrome lives on the overlay and has its own cursors.
        Cursor = Cursors.IBeam;

        // Crisp at rest - a resize gesture drops this to Linear for smooth frames (see
        // SetResizeScaling).
        RenderOptions.SetBitmapScalingMode(DisplayImage, BitmapScalingMode.HighQuality);

        // The routed-event route from flow-hosted children does NOT pass through the parent
        // Border (the flow's island host takes over), so the click-to-select handler must
        // live ON the picture itself. Real OS clicks never reach this element (the coordinator
        // intercepts them at the editor), but programmatic/test input routes here.
        DisplayImage.AddHandler(
            UIElement.MouseLeftButtonDownEvent,
            new MouseButtonEventHandler(Body_MouseLeftButtonDown),
            handledEventsToo: false);

        // The paragraph's LineHeight/LineStackingStrategy inherit into the context menu's
        // content through the logical tree (the element is part of the flow), stretching every
        // menu row. Block the inheritance at the menu root.
        if (ContextMenu is not null)
        {
            ContextMenu.SetValue(Block.LineHeightProperty, double.NaN);
            ContextMenu.SetValue(Block.LineStackingStrategyProperty, LineStackingStrategy.MaxHeight);
            // The coordinator opens this menu directly (the element never receives the
            // ContextMenuOpening event - it lives in the flow), so keep the checkmarks fresh
            // whenever the menu actually opens.
            ContextMenu.Opened += (_, _) =>
            {
                SyncWrapMenuCheckStates();
                LockAspectItem.IsChecked = LockAspectRatio;
            };
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

    /// <summary>Resizes the image box (the coordinator applies the LibreOffice-style resize
    /// math from the overlay chrome and drives this). The caption is re-applied every frame so
    /// the image row and the element box (image + caption) stay consistent.</summary>
    internal void SetImageBox(double width, double height)
    {
        _imageWidth = width;
        _imageHeight = height;
        ApplyCaptionLayout();
    }

    /// <summary>The image re-scales on every resize frame - the expensive Fant filter is the
    /// jank behind sluggish resizing. The chrome switches the element to the cheap Linear
    /// filter for the duration of a resize gesture and back to full quality at the end.</summary>
    internal void SetResizeScaling(bool resizing)
        => RenderOptions.SetBitmapScalingMode(DisplayImage, resizing ? BitmapScalingMode.Linear : BitmapScalingMode.HighQuality);

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
        // A selected photo is draggable (re-anchor) - it shows the move cursor; unselected it
        // is just part of the text and keeps the stable text cursor. The selection wash marks
        // the selection (the handles live in the overlay chrome).
        Cursor = IsSelected ? Cursors.SizeAll : Cursors.IBeam;
        SelectionWash.Visibility = IsSelected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLockAspectRatioChanged() => LockAspectRatioChanged?.Invoke(this, EventArgs.Empty);

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

    private void ContextMenuPaint_Click(object sender, RoutedEventArgs e)
    {
        SelectionRequested?.Invoke(this, EventArgs.Empty);
        PaintRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ContextMenuLockAspect_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SelectionRequested?.Invoke(this, EventArgs.Empty);
        LockAspectRatio = !LockAspectRatio; // the DP callback raises LockAspectRatioChanged
        LockAspectItem.IsChecked = LockAspectRatio;
    }

    /// <summary>Selects the photo when it is clicked. Real OS clicks never reach this handler
    /// (the coordinator intercepts them at the editor - the element is not hit-testable from
    /// the surface), but programmatic/test input still routes here.</summary>
    private void Body_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        SelectionRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
