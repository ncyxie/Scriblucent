using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Scriblucent.Animations;

namespace Scriblucent.Controls;

public enum InteractionRole { Compact, Standard, Prominent }

/// <summary>A button face with layered state paint and a short press. Its button remains the stationary hit root.</summary>
public sealed class InteractionSurface : Border
{
    public static readonly DependencyProperty PaintedWidthProperty = DependencyProperty.Register(
        nameof(PaintedWidth), typeof(double), typeof(InteractionSurface), new PropertyMetadata(double.NaN));
    public double PaintedWidth { get => (double)GetValue(PaintedWidthProperty); set => SetValue(PaintedWidthProperty, value); }
    public static readonly DependencyProperty RoleProperty = DependencyProperty.RegisterAttached(
        "Role", typeof(InteractionRole), typeof(InteractionSurface), new PropertyMetadata(InteractionRole.Standard, PaletteChanged));
    public static InteractionRole GetRole(DependencyObject target) => (InteractionRole)target.GetValue(RoleProperty);
    public static void SetRole(DependencyObject target, InteractionRole value) => target.SetValue(RoleProperty, value);
    public InteractionRole Role { get => GetRole(this); set => SetRole(this, value); }
    public static readonly DependencyProperty HighlightWashProperty = DependencyProperty.RegisterAttached(
        "HighlightWash", typeof(Brush), typeof(InteractionSurface), new PropertyMetadata(null, PaletteChanged));
    public static readonly DependencyProperty HighlightEdgeProperty = DependencyProperty.RegisterAttached(
        "HighlightEdge", typeof(Brush), typeof(InteractionSurface), new PropertyMetadata(null, PaletteChanged));
    public static Brush? GetHighlightWash(DependencyObject target) => (Brush?)target.GetValue(HighlightWashProperty);
    public static void SetHighlightWash(DependencyObject target, Brush? value) => target.SetValue(HighlightWashProperty, value);
    public static Brush? GetHighlightEdge(DependencyObject target) => (Brush?)target.GetValue(HighlightEdgeProperty);
    public static void SetHighlightEdge(DependencyObject target, Brush? value) => target.SetValue(HighlightEdgeProperty, value);
    public Brush? HighlightWash { get => GetHighlightWash(this); set => SetHighlightWash(this, value); }
    public Brush? HighlightEdge { get => GetHighlightEdge(this); set => SetHighlightEdge(this, value); }
    public static readonly DependencyProperty HoverBackgroundProperty = DependencyProperty.RegisterAttached(
        nameof(HoverBackground), typeof(Brush), typeof(InteractionSurface), new PropertyMetadata(null, PaletteChanged));
    public static readonly DependencyProperty PressedBackgroundProperty = DependencyProperty.RegisterAttached(
        nameof(PressedBackground), typeof(Brush), typeof(InteractionSurface), new PropertyMetadata(null, PaletteChanged));
    public Brush? HoverBackground { get => (Brush?)GetValue(HoverBackgroundProperty); set => SetValue(HoverBackgroundProperty, value); }
    public Brush? PressedBackground { get => (Brush?)GetValue(PressedBackgroundProperty); set => SetValue(PressedBackgroundProperty, value); }
    public static Brush? GetHoverBackground(DependencyObject target) => (Brush?)target.GetValue(HoverBackgroundProperty);
    public static void SetHoverBackground(DependencyObject target, Brush? value) => target.SetValue(HoverBackgroundProperty, value);
    public static Brush? GetPressedBackground(DependencyObject target) => (Brush?)target.GetValue(PressedBackgroundProperty);
    public static void SetPressedBackground(DependencyObject target, Brush? value) => target.SetValue(PressedBackgroundProperty, value);
    private ButtonBase? _button;
    private DependencyPropertyDescriptor? _pressDescriptor;
    private DependencyPropertyDescriptor? _styleDescriptor;
    private bool _enterHeld;
    private bool _lastPressed;
    private bool _hovered;

    public InteractionSurface()
    {
        SetResourceReference(SurfaceMaterial.DarkProperty, "SurfaceMaterialIsDark");
        Loaded += Attach;
        Unloaded += Detach;
    }
    private (Size Size, CornerRadius Radius, Thickness Frame, double Width)? _contourKey;
    private Geometry? _contour;
    private Geometry? _restRim;
    private Geometry? _materialRim;
    private void UpdateContour(Size size)
    {
        var frame = SurfaceContour.EffectiveFrame(this);
        var key = (size, CornerRadius, frame, PaintedWidth);
        if (_contourKey is { } cached && cached.Equals(key)) return;
        _contourKey = key;
        var width = double.IsNaN(PaintedWidth) ? size.Width : Math.Clamp(PaintedWidth, 0, size.Width);
        size = new(width, size.Height);
        _contour = SurfaceContour.Create(size, CornerRadius, frame);
        var innerSize = new Size(Math.Max(0, size.Width - frame.Left - frame.Right), Math.Max(0, size.Height - frame.Top - frame.Bottom));
        var innerRadius = new CornerRadius(Math.Max(0, CornerRadius.TopLeft - Math.Max(frame.Left, frame.Top) / 2),
            Math.Max(0, CornerRadius.TopRight - Math.Max(frame.Right, frame.Top) / 2),
            Math.Max(0, CornerRadius.BottomRight - Math.Max(frame.Right, frame.Bottom) / 2),
            Math.Max(0, CornerRadius.BottomLeft - Math.Max(frame.Left, frame.Bottom) / 2));
        var inner = SurfaceContour.Create(innerSize, innerRadius, new Thickness()).Clone();
        inner.Transform = new TranslateTransform(frame.Left, frame.Top); inner.Freeze();
        _restRim = SurfaceContour.Ring(_contour, inner);
        _materialRim = SurfaceContour.Ring(_contour, SurfaceContour.Create(size, CornerRadius, frame, 1));
        // The background, tint and content share a single antialiased outer silhouette.
        SetCurrentValue(ClipProperty, _contour);
    }
    protected override Size ArrangeOverride(Size finalSize)
    { var size = base.ArrangeOverride(finalSize); UpdateContour(size); return size; }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == Behaviors.ScrollTileBehavior.IsScrollingProperty && IsLoaded)
        {
            _hovered = _button?.IsMouseOver == true;
            Update(false);
        }
        if (e.Property == CornerRadiusProperty || e.Property == BorderThicknessProperty || e.Property == PaintedWidthProperty || e.Property == UseLayoutRoundingProperty)
        {
            _contourKey = null; if (ActualWidth > 0 && ActualHeight > 0) UpdateContour(RenderSize); InvalidateVisual();
            if (e.Property == PaintedWidthProperty) Behaviors.CommandLightBehavior.MetricsChanged(this);
        }
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    { base.OnDpiChanged(oldDpi, newDpi); _contourKey = null; UpdateContour(RenderSize); InvalidateVisual(); }
    protected override void OnRender(DrawingContext drawing)
    {
        UpdateContour(RenderSize);
        var material = SurfaceMaterial.Paint(Background, SurfaceMaterial.GetKind(this), SurfaceMaterial.IsDark(this),
            SurfaceMaterial.GetForeground(this) ?? (TemplatedParent as Control)?.Foreground);
        drawing.DrawRectangle(material?.Fill ?? Background, null, new Rect(RenderSize));
        if (material is not null) drawing.DrawGeometry(material.Edge, null, _materialRim);
        if (_restRim is not null) drawing.DrawGeometry(BorderBrush, null, _restRim);
    }
    internal void RefreshMaterial() { InvalidateVisual(); Update(false); }
    private static void PaletteChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    { if (target is InteractionSurface surface) surface.Update(false); }
    private void Attach(object sender, RoutedEventArgs e)
    {
        if (_button is not null || TemplatedParent is not ButtonBase button) return;
        _button = button;
        Behaviors.CommandLightBehavior.EnsureControlLight(button, Behaviors.CommandLightRole.Command);
        _hovered = button.IsMouseOver;
        button.MouseEnter += HoverChanged; button.MouseLeave += HoverChanged;
        button.IsEnabledChanged += EnabledChanged;
        button.PreviewKeyDown += InputKeyDown; button.PreviewKeyUp += InputKeyUp;
        button.LostKeyboardFocus += InputFocusLost;
        _pressDescriptor = DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(ButtonBase));
        _pressDescriptor?.AddValueChanged(button, PressChanged);
        _styleDescriptor = DependencyPropertyDescriptor.FromProperty(StyleProperty, typeof(ButtonBase));
        _styleDescriptor?.AddValueChanged(button, StyleChanged);
        MotionPolicy.Changed += PolicyChanged;
        Update(false);
    }
    private void Detach(object sender, RoutedEventArgs e)
    {
        if (_button is { } button)
        {
            button.MouseEnter -= HoverChanged; button.MouseLeave -= HoverChanged;
            button.IsEnabledChanged -= EnabledChanged;
            button.PreviewKeyDown -= InputKeyDown; button.PreviewKeyUp -= InputKeyUp;
            button.LostKeyboardFocus -= InputFocusLost;
            _pressDescriptor?.RemoveValueChanged(button, PressChanged);
            _styleDescriptor?.RemoveValueChanged(button, StyleChanged);
        }
        MotionPolicy.Changed -= PolicyChanged;
        _button = null; _pressDescriptor = _styleDescriptor = null; _enterHeld = _lastPressed = false;
        SurfaceMotion.Stop(this, MotionChannel.Interaction);
        SurfaceMotion.Stop(this, MotionChannel.InteractionPaint);
        UiMotion.ChannelTranslate(this, MotionChannel.Interaction).Y = 0;
        var scale = UiMotion.EnsureScale(this); scale.ScaleX = scale.ScaleY = 1;
        SurfaceLight.Reset(this);
        foreach (var role in new[] { SurfaceFill.Role.Hover, SurfaceFill.Role.Pressed })
            if (SurfaceFill.Get(this, role, false) is { } layer) layer.Opacity = 0;
    }
    private void HoverChanged(object sender, MouseEventArgs e)
    {
        if (Behaviors.ScrollTileBehavior.GetIsScrolling(this)) return;
        _hovered = e.RoutedEvent == Mouse.MouseEnterEvent; Update(true);
    }
    private void PressChanged(object? sender, EventArgs e) => Update(true);
    private void StyleChanged(object? sender, EventArgs e)
    { if (_button is { } button) Behaviors.CommandLightBehavior.EnsureControlLight(button, Behaviors.CommandLightRole.Command); }
    private void EnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    { if (e.NewValue is false) _enterHeld = false; Update(false); }
    private void InputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.IsRepeat || _button?.IsEnabled != true) return;
        _enterHeld = true; Update(true);
    }
    private void InputKeyUp(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter && _enterHeld) { _enterHeld = false; Update(true); } }
    private void InputFocusLost(object sender, KeyboardFocusChangedEventArgs e)
    { _enterHeld = false; Update(true); }
    private void PolicyChanged() => Update(false);

    private void Update(bool animate)
    {
        if (_button is not { } button) return;
        var scrolling = Behaviors.ScrollTileBehavior.GetIsScrolling(this);
        var hovered = button.IsEnabled && _hovered && !scrolling;
        var pressed = button.IsEnabled && (button.IsPressed || _enterHeld);
        AnimateState(hovered, pressed, animate && !scrolling);
    }

    // The audit harness exercises the actual face controller without sending input into the user's desktop.
    internal void AnimateState(bool hovered, bool pressed, bool animate = true)
    {
        var pressChanged = pressed != _lastPressed; _lastPressed = pressed;
        var paintMs = pressChanged ? pressed ? 80 : 140 : hovered ? 160 : 180;
        var tracks = new List<NativeMotionTrack>();
        void Fill(SurfaceFill.Role role, Brush? brush, bool visible)
        {
            if (SurfaceFill.Get(this, role, brush is not null && visible) is not { } layer) return;
            layer.SetCurrentValue(SurfaceFill.Layer.BrushProperty, brush);
            tracks.Add(new(layer, OpacityProperty, visible && brush is not null ? 1 : 0));
        }
        Fill(SurfaceFill.Role.Hover, HoverBackground, hovered);
        Fill(SurfaceFill.Role.Pressed, PressedBackground, pressed);
        var move = UiMotion.ChannelTranslate(this, MotionChannel.Interaction);
        var lift = !MotionPolicy.ExpressiveExtras ? 0d : Role == InteractionRole.Prominent ? -2d : Role == InteractionRole.Standard ? -1d : 0d;
        var targetY = MotionPolicy.Spatial ? pressed ? 1 : hovered ? lift : 0 : 0;
        var ownsMovement = _button is null || !Behaviors.ChromeHoverMotionBehavior.GetIsEnabled(_button);
        // Chrome owns lift on this same face. Its movement and our fill must not
        // cancel each other's clocks when both respond to the same mouse event.
        var channel = ownsMovement ? MotionChannel.Interaction : MotionChannel.InteractionPaint;
        if (ownsMovement && (MotionPolicy.ExpressiveExtras || pressed || move.Y != 0))
            tracks.Add(new(move, TranslateTransform.YProperty, targetY, true, Profile: new(UiMotion.OutCubic, paintMs)));
        RenderTransformOrigin = new Point(.5, .5);
        var scale = UiMotion.EnsureScale(this);
        var targetScale = pressed && MotionPolicy.Spatial ? .985 : 1;
        var settle = pressChanged && !pressed && hovered && Role == InteractionRole.Prominent && MotionPolicy.ExpressiveExtras ? 1.005 : (double?)null;
        if (ownsMovement && (MotionPolicy.ExpressiveExtras || pressed || scale.ScaleX != 1 || scale.ScaleY != 1))
        {
            tracks.Add(new(scale, ScaleTransform.ScaleXProperty, targetScale, true, settle, settle is null ? null : 1));
            tracks.Add(new(scale, ScaleTransform.ScaleYProperty, targetScale, true, settle, settle is null ? null : 1));
        }
        var chromeOwnsCardTint = !ownsMovement && _button is not null
            && Behaviors.ChromeHoverMotionBehavior.GetProfile(_button) == Behaviors.ChromeHoverMotionProfile.RecentCard;
        if (!chromeOwnsCardTint)
        {
            if (Behaviors.CommandLightBehavior.LightFor(this) is not null) SurfaceLight.Reset(this);
            else SurfaceLight.AddTracks(this, tracks, hovered, pressed ? .35 : 1, neutral: true,
                wash: HighlightWash, edge: HighlightEdge);
        }
        if (_button?.Template.FindName("ButtonShadow", _button) is BakedElevationLayer shadow)
        {
            if (MotionPolicy.ExpressiveExtras)
            {
                tracks.Add(new(shadow, OpacityProperty, hovered && !pressed ? .32 : 0));
                tracks.Add(new(UiMotion.ChannelTranslate(shadow, MotionChannel.Interaction), TranslateTransform.YProperty, targetY, true));
            }
            else { shadow.Opacity = 0; UiMotion.ChannelTranslate(shadow, MotionChannel.Interaction).Y = 0; }
        }
        if (!animate || !MotionPolicy.Spatial)
        {
            SurfaceMotion.Stop(this, channel);
            foreach (var track in tracks) track.Target.SetCurrentValue(track.Property, track.To);
            if (!MotionPolicy.Spatial) SurfaceLight.Reset(this);
            return;
        }
        SurfaceMotion.AnimateTracks(this, channel, tracks, paintMs, UiMotion.OutSine);
    }
}
