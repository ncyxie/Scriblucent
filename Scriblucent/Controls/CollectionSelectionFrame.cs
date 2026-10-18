using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Behaviors;
using Scriblucent.Animations;
using Scriblucent.Services;
using Scriblucent.Localization;
using System.Windows.Data;

namespace Scriblucent.Controls;

/// <summary>Shared live selection decoration above cached card paint.</summary>
public sealed class CollectionSelectionFrame : ContentControl
{
    static CollectionSelectionFrame() => DefaultStyleKeyProperty.OverrideMetadata(typeof(CollectionSelectionFrame), new FrameworkPropertyMetadata(typeof(CollectionSelectionFrame)));
    public CollectionSelectionFrame()
    {
        Loaded += (_, _) => { MotionPolicy.Changed += MotionChanged; DragSourceDimming.Changed += SyncDragDimming; Observe(); FindFace(); _feedback?.Select(IsItemSelected, false); SyncHover(false); SyncDragDimming(); };
        Unloaded += (_, _) => { MotionPolicy.Changed -= MotionChanged; DragSourceDimming.Changed -= SyncDragDimming; ClearDragDimming(); Unobserve(); SetFace(null); ResetHover(); };
        DataContextChanged += (_, _) => SyncDragDimming();
        SizeChanged += (_, _) => UpdateGeometry();
    }
    // Dashboard scope follows the collection through recycling; editors supply their own scope.
    public static readonly DependencyProperty SelectionProperty = DependencyProperty.RegisterAttached(nameof(Selection), typeof(ICollectionSelectionScope), typeof(CollectionSelectionFrame), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, Changed));
    public static ICollectionSelectionScope? GetSelection(DependencyObject element) => (ICollectionSelectionScope?)element.GetValue(SelectionProperty);
    public static void SetSelection(DependencyObject element, ICollectionSelectionScope? value) => element.SetValue(SelectionProperty, value);
    public ICollectionSelectionScope? Selection { get => (ICollectionSelectionScope?)GetValue(SelectionProperty); set => SetValue(SelectionProperty, value); }
    public static readonly DependencyProperty ItemIdProperty = DependencyProperty.Register(nameof(ItemId), typeof(Guid), typeof(CollectionSelectionFrame), new PropertyMetadata(Guid.Empty, Changed));
    public Guid ItemId { get => (Guid)GetValue(ItemIdProperty); set => SetValue(ItemIdProperty, value); }
    public static readonly DependencyProperty SelectionLabelProperty = DependencyProperty.Register(nameof(SelectionLabel), typeof(string), typeof(CollectionSelectionFrame), new PropertyMetadata(string.Empty));
    public string SelectionLabel { get => (string)GetValue(SelectionLabelProperty); set => SetValue(SelectionLabelProperty, value); }
    public static readonly DependencyProperty FaceNameProperty = DependencyProperty.Register(nameof(FaceName), typeof(string), typeof(CollectionSelectionFrame), new PropertyMetadata(string.Empty));
    public string FaceName { get => (string)GetValue(FaceNameProperty); set => SetValue(FaceNameProperty, value); }
    // Editable forms retain their own field/button feedback instead of washing the entire container.
    public static readonly DependencyProperty IsHoverTintEnabledProperty = DependencyProperty.Register(nameof(IsHoverTintEnabled), typeof(bool), typeof(CollectionSelectionFrame), new PropertyMetadata(true, HoverTintChanged));
    public bool IsHoverTintEnabled { get => (bool)GetValue(IsHoverTintEnabledProperty); set => SetValue(IsHoverTintEnabledProperty, value); }
    private static void HoverTintChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var frame = (CollectionSelectionFrame)d;
        frame.ResetHover();
        frame.SyncHover();
    }
    private static readonly DependencyPropertyKey IsSelectingKey = DependencyProperty.RegisterReadOnly(nameof(IsSelecting), typeof(bool), typeof(CollectionSelectionFrame), new PropertyMetadata(false));
    public static readonly DependencyProperty IsSelectingProperty = IsSelectingKey.DependencyProperty;
    public bool IsSelecting => (bool)GetValue(IsSelectingProperty);
    private static readonly DependencyPropertyKey IsItemSelectedKey = DependencyProperty.RegisterReadOnly(nameof(IsItemSelected), typeof(bool), typeof(CollectionSelectionFrame), new PropertyMetadata(false));
    public static readonly DependencyProperty IsItemSelectedProperty = IsItemSelectedKey.DependencyProperty;
    public bool IsItemSelected => (bool)GetValue(IsItemSelectedProperty);
    private static readonly DependencyPropertyKey FaceMarginKey = DependencyProperty.RegisterReadOnly(nameof(FaceMargin), typeof(Thickness), typeof(CollectionSelectionFrame), new PropertyMetadata(new Thickness()));
    public static readonly DependencyProperty FaceMarginProperty = FaceMarginKey.DependencyProperty;
    public Thickness FaceMargin => (Thickness)GetValue(FaceMarginProperty);
    public static readonly DependencyProperty SelectionInsetProperty = DependencyProperty.Register(nameof(SelectionInset), typeof(Thickness), typeof(CollectionSelectionFrame), new PropertyMetadata(new Thickness(0, 12, 12, 0)));
    public Thickness SelectionInset { get => (Thickness)GetValue(SelectionInsetProperty); set => SetValue(SelectionInsetProperty, value); }
    private ICollectionSelectionScope? _observed;
    private CollectionSelectionToggle? _toggle;
    private CollectionSelectionFeedback? _feedback;
    private Border? _face;
    private CardShadowLayer? _shadow;
    private bool _geometryQueued;
    private bool _dragDimmed;
    private void SyncDragDimming()
    {
        if (!Dispatcher.CheckAccess()) return;
        bool dimmed = IsLoaded && DragSourceDimming.IsSource(this);
        if (dimmed == _dragDimmed) return;
        if (!dimmed) { ClearDragDimming(); return; }
        // Keep temporary dimming outside the face's reusable scroll paint.
        _dragDimmed = true;
        Opacity = DragSourceDimming.Opacity;
    }
    private void ClearDragDimming()
    {
        if (!_dragDimmed) return;
        _dragDimmed = false;
        ClearValue(OpacityProperty);
    }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not CollectionSelectionFrame frame) return;
        if (frame.IsLoaded) frame.Observe(); else frame.SyncState(false);
    }
    private void Observe()
    {
        if (!ReferenceEquals(_observed, Selection)) { Unobserve(); _observed = Selection; if (_observed is not null) _observed.Changed += Sync; }
        SyncState(false);
    }
    private void Unobserve() { if (_observed is not null) _observed.Changed -= Sync; _observed = null; }
    private void Sync() => SyncState(true);
    private void SyncState(bool animate)
    {
        bool headerChanged = IsSelecting != (Selection?.IsActive == true);
        bool selected = Selection?.IsActive == true && Selection.Contains(ItemId);
        bool changed = IsItemSelected != selected;
        SetValue(IsSelectingKey, Selection?.IsActive == true);
        SetValue(IsItemSelectedKey, selected);
        if (_toggle is not null)
        {
            _toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, (bool?)selected);
            if (!animate) CheckBoxCheckMotionBehavior.SyncCheckBoxGlyph(_toggle);
        }
        if (_feedback is not null && (changed || !IsLoaded)) _feedback.Select(selected, animate && IsLoaded && changed);
        if (IsLoaded && headerChanged)
        {
            // Header space changes only when entering/leaving selection, never per selected item.
            foreach (var host in Descendants(this).OfType<DashboardTileHost>().Where(host => host.PreserveLiveInputInBitmapMode)) host.SyncSelectionHeader(IsSelecting);
            QueueGeometry();
        }
    }
    public override void OnApplyTemplate()
    {
        if (_toggle is not null) _toggle.ToggleRequested = null;
        base.OnApplyTemplate();
        _toggle = GetTemplateChild("PART_Toggle") as CollectionSelectionToggle;
        _feedback = GetTemplateChild("PART_Feedback") as CollectionSelectionFeedback;
        if (_toggle is not null)
        {
            _toggle.ToggleRequested = SelectFromPointer;
            _toggle.SetBinding(ToolTipProperty, new Binding("[SelectMultiple]") { Source = LocalizationProvider.Instance });
        }
        SyncState(false);
        _feedback?.Select(IsItemSelected, false);
        if (IsLoaded) FindFace();
    }
    private void SelectFromPointer() => Selection?.Toggle(ItemId, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (Selection is not { CanSelect: true } selection || EditorSelectionInput.IsInteractive(e.OriginalSource as DependencyObject, this)) return;
        bool modified = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (!selection.IsActive && !modified) return;
        SelectFromPointer(); Focus(); e.Handled = true;
    }
    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (IsSelecting && !EditorSelectionInput.IsInteractive(e.OriginalSource as DependencyObject, this)) e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Space && ReferenceEquals(e.OriginalSource, this) && Selection?.CanSelect == true)
        { SelectFromPointer(); e.Handled = true; }
    }
    internal void RefreshFace() => Dispatcher.BeginInvoke(() => { if (IsLoaded) FindFace(); }, DispatcherPriority.Loaded);
    private void FindFace()
    {
        if (string.IsNullOrEmpty(FaceName)) return;
        SetFace(Descendants(this).OfType<Border>().FirstOrDefault(face => face.Name == FaceName && face.IsVisible)
            ?? Descendants(this).OfType<Border>().FirstOrDefault(face => face.Name == FaceName));
        foreach (var host in Descendants(this).OfType<DashboardTileHost>()) host.SyncSelectionHeader(IsSelecting);
        QueueGeometry();
    }
    private void SetFace(Border? value)
    {
        if (_face is not null) _face.SizeChanged -= FaceSizeChanged;
        _face = value;
        _shadow = (_face?.Parent as Panel)?.Children.OfType<CardShadowLayer>().FirstOrDefault();
        if (_face is not null) _face.SizeChanged += FaceSizeChanged;
    }
    private void FaceSizeChanged(object sender, SizeChangedEventArgs e) => QueueGeometry();
    private void QueueGeometry()
    {
        if (_geometryQueued) return;
        _geometryQueued = true;
        Dispatcher.BeginInvoke(() => { _geometryQueued = false; if (IsLoaded) UpdateGeometry(); }, DispatcherPriority.Loaded);
    }
    private void UpdateGeometry()
    {
        if (_face is null || !_face.IsVisible || _face.ActualWidth <= 0 || _face.ActualHeight <= 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
        var bounds = _face.TransformToAncestor(this).TransformBounds(new Rect(_face.RenderSize));
        SetValue(FaceMarginKey, new Thickness(Math.Max(0, bounds.X), Math.Max(0, bounds.Y),
            Math.Max(0, ActualWidth - bounds.Right), Math.Max(0, ActualHeight - bounds.Bottom)));
        if (_feedback is not null)
        {
            double scale = Math.Min(bounds.Width / _face.ActualWidth, bounds.Height / _face.ActualHeight);
            var radius = _face.CornerRadius;
            _feedback.CornerRadius = new(radius.TopLeft * scale, radius.TopRight * scale, radius.BottomRight * scale, radius.BottomLeft * scale);
        }
    }
    // A single render transform owns the face, baked shadow and live selection chrome.
    // Nothing follows the card with per-frame geometry/layout calculations.
    internal static CollectionSelectionFrame? For(DependencyObject element)
    {
        for (var p = element; p is not null && p is not Window; p = EditorSelectionInput.Parent(p))
            if (p is CollectionSelectionFrame frame) return frame;
        return element is NoteCardControl card ? Descendants(card).OfType<CollectionSelectionFrame>().FirstOrDefault() : null;
    }
    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); SyncHover(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); SyncHover(); }
    private void MotionChanged() { if (Dispatcher.CheckAccess()) SyncHover(animate: MotionPolicy.Spatial); }
    internal void SyncHover(bool animate = true)
    {
        if (!IsLoaded || ScrollTileBehavior.GetIsScrolling(this)) return;
        PlayHover(IsMouseOver, animate);
    }
    private void PlayHover(bool hover, bool animate)
    {
        var translate = UiMotion.ChannelTranslate(this, MotionChannel.Interaction);
        double lift = hover && MotionPolicy.Spatial ? UiMotion.CardHoverLiftY : 0;
        if (!animate || !MotionPolicy.Spatial || !hover && translate.Y == 0 && !NativeSurfaceMotion.IsRunning(this, MotionChannel.Interaction))
        { ResetHover(); return; }
        var tracks = new List<NativeMotionTrack> { new(translate, TranslateTransform.YProperty, lift, true) };
        if (IsHoverTintEnabled && _face is { IsVisible: true }) SurfaceLight.AddTracks(_face, tracks, hover);
        if (_shadow is not null) tracks.Add(new(_shadow, OpacityProperty, hover ? 1 : .72));
        SurfaceMotion.AnimateTracks(this, MotionChannel.Interaction, tracks,
            hover ? UiMotion.HoverInMs : UiMotion.HoverOutMs, UiMotion.OutCubic,
            () =>
            {
                // The checkbox is outside the tile host. Warm neutral paint only after
                // the entire card has finished leaving hover, including that checkbox.
                if (!hover && IsLoaded)
                    foreach (var host in Descendants(this).OfType<DashboardTileHost>()) host.QueueWarm();
            });
    }
    internal void ResetHover()
    {
        SurfaceMotion.Stop(this, MotionChannel.Interaction);
        UiMotion.ChannelTranslate(this, MotionChannel.Interaction).Y = 0;
        if (_face is not null) SurfaceLight.Reset(_face);
        if (_shadow is not null) _shadow.Opacity = .72;
    }
    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}

internal static class EditorSelectionInput
{
    internal static DependencyObject? Parent(DependencyObject source) => source is Visual or System.Windows.Media.Media3D.Visual3D
        ? VisualTreeHelper.GetParent(source) : source is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(source);
    internal static bool IsInteractive(DependencyObject? source, DependencyObject? stop = null)
    {
        for (var p = source; p is not null && p != stop; p = Parent(p))
            if (p is TextBoxBase or PasswordBox or Selector or ButtonBase or ScrollBar or Hyperlink or Image) return true;
        return false;
    }
    internal static bool HandleKey(KeyEventArgs e, ICollectionSelectionScope selection, Action delete)
    {
        if (!selection.IsActive) return false;
        if (e.Key == Key.Escape)
        {
            for (var p = e.OriginalSource as DependencyObject; p is not null; p = Parent(p))
            {
                if (p is ComboBox { IsDropDownOpen: true } combo) { combo.IsDropDownOpen = false; e.Handled = true; return true; }
                if (p is ContextMenu { IsOpen: true } menu) { menu.IsOpen = false; e.Handled = true; return true; }
            }
            selection.End(); e.Handled = true; return true;
        }
        for (var p = e.OriginalSource as DependencyObject; p is not null; p = Parent(p))
            if (p is TextBoxBase or PasswordBox or ComboBox) return false;
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) selection.SelectAll();
        else if (e.Key == Key.Delete && selection.Count > 0) delete();
        else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && e.OriginalSource is CollectionSelectionFrame frame) selection.Toggle(frame.ItemId);
        else return false;
        e.Handled = true; return true;
    }
}
