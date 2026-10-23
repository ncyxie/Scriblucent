using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Controls;

namespace Scriblucent.Behaviors;

public enum CommandLightRole { Command, Navigation, Item, Value }

/// <summary>One pointer light and interruptible selection response for a connected command group.</summary>
public static class CommandLightBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(CommandLightBehavior), new PropertyMetadata(false, Enabled));
    public static readonly DependencyProperty IsScopeProperty = DependencyProperty.RegisterAttached("IsScope", typeof(bool), typeof(CommandLightBehavior), new PropertyMetadata(false));
    public static readonly DependencyProperty RoleProperty = DependencyProperty.RegisterAttached("Role", typeof(CommandLightRole), typeof(CommandLightBehavior),
        new FrameworkPropertyMetadata(CommandLightRole.Command, FrameworkPropertyMetadataOptions.Inherits));
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.RegisterAttached("IsSelected", typeof(bool), typeof(CommandLightBehavior), new PropertyMetadata(false, PaletteChanged));
    public static readonly DependencyProperty LightColorProperty = DependencyProperty.RegisterAttached("LightColor", typeof(Color), typeof(CommandLightBehavior),
        new FrameworkPropertyMetadata(Color.FromArgb(28, 67, 92, 236), FrameworkPropertyMetadataOptions.Inherits, PaletteChanged));
    private static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached("Target", typeof(Target), typeof(CommandLightBehavior));
    private static readonly DependencyProperty ScopeProperty = DependencyProperty.RegisterAttached("Scope", typeof(Scope), typeof(CommandLightBehavior));
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    public static void SetIsScope(DependencyObject target, bool value) => target.SetValue(IsScopeProperty, value);
    public static bool GetIsScope(DependencyObject target) => (bool)target.GetValue(IsScopeProperty);
    public static void SetRole(DependencyObject target, CommandLightRole value) => target.SetValue(RoleProperty, value);
    public static CommandLightRole GetRole(DependencyObject target) => (CommandLightRole)target.GetValue(RoleProperty);
    public static void SetIsSelected(DependencyObject target, bool value) => target.SetValue(IsSelectedProperty, value);
    public static bool GetIsSelected(DependencyObject target) => (bool)target.GetValue(IsSelectedProperty);
    public static void SetLightColor(DependencyObject target, Color value) => target.SetValue(LightColorProperty, value);
    public static Color GetLightColor(DependencyObject target) => (Color)target.GetValue(LightColorProperty);
    // Control families own eligibility. Explicit values/bindings still win, and a
    // current value follows subsequent style changes instead of replacing them.
    internal static void EnsureControlLight(FrameworkElement host, CommandLightRole role)
    {
        var source = DependencyPropertyHelper.GetValueSource(host, RoleProperty).BaseValueSource;
        if (source is BaseValueSource.Default or BaseValueSource.Inherited) host.SetCurrentValue(RoleProperty, role);
        if (DependencyPropertyHelper.GetValueSource(host, IsEnabledProperty).BaseValueSource == BaseValueSource.Default)
            host.SetCurrentValue(IsEnabledProperty, true);
    }
    internal static void ReleaseControlLight(FrameworkElement host)
    {
        var source = DependencyPropertyHelper.GetValueSource(host, IsEnabledProperty);
        if (source.BaseValueSource == BaseValueSource.Default && source.IsCurrent) host.ClearValue(IsEnabledProperty);
    }
    private static void Enabled(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not FrameworkElement face || face is not (Border or TabSelectionSurface or ButtonBase)) return;
        face.Loaded -= Loaded; face.Unloaded -= Unloaded;
        if (args.NewValue is true)
        { face.Loaded += Loaded; face.Unloaded += Unloaded; if (face.IsLoaded) Loaded(face, new()); }
        else Unloaded(face, new());
    }
    private static void Loaded(object sender, RoutedEventArgs args)
    {
        var host = (FrameworkElement)sender;
        if (host.GetValue(TargetProperty) is not null) return;
        var face = host;
        if (host is ButtonBase button)
        { button.ApplyTemplate(); if ((FindInteractionFace(button) ?? SurfacePaint.FindBorder(button)) is not { } border) return; face = border; }
        var root = host;
        for (var node = Parent(host); node is not null; node = Parent(node))
            if (node is FrameworkElement candidate && GetIsScope(candidate)) { root = candidate; break; }
        var scope = root.GetValue(ScopeProperty) as Scope;
        if (scope is null) { scope = new Scope(root); root.SetValue(ScopeProperty, scope); }
        var target = new Target(face, host, scope);
        host.SetValue(TargetProperty, target); face.SetValue(TargetProperty, target); scope.Add(target);
        if (face is InteractionSurface interactive) interactive.RefreshMaterial();
        else if (face is Border row && StateSurfaceMotionBehavior.GetIsEnabled(row)) StateSurfaceMotionBehavior.RefreshPaint(row);
    }
    private static void Unloaded(object sender, RoutedEventArgs args)
    {
        var face = (FrameworkElement)sender;
        var target = face.GetValue(TargetProperty) as Target;
        face.ClearValue(TargetProperty);
        target?.Dispose();
    }
    private static void PaletteChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    { if (owner.GetValue(TargetProperty) is Target target) target.Light.Invalidate(); }
    private static DependencyObject? Parent(DependencyObject value) => value is Visual or System.Windows.Media.Media3D.Visual3D
        ? VisualTreeHelper.GetParent(value) : LogicalTreeHelper.GetParent(value);
    private static InteractionSurface? FindInteractionFace(DependencyObject root)
    {
        if (root is InteractionSurface face) return face;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindInteractionFace(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
    private static T? Ancestor<T>(DependencyObject? value) where T : DependencyObject
    { for (; value is not null; value = Parent(value)) if (value is T result) return result; return null; }

    internal static CommandSurfaceLight? LightFor(FrameworkElement face) => (face.GetValue(TargetProperty) as Target)?.Light;
    internal static void MetricsChanged(FrameworkElement face)
    {
        // An explicit painted-width change must match the new contour immediately
        // (including collapsed navigation and Reduced motion). Layout/registration
        // notifications use the coalesced path instead.
        if (face.GetValue(TargetProperty) is Target target && !target.IsScrollSuspended) target.Scope.RefreshMetrics();
    }
    internal static void SuspendScrollTargets(DependencyObject root, bool suspended)
    {
        var scopes = new HashSet<Scope>();
        void Walk(DependencyObject node)
        {
            if (node.GetValue(TargetProperty) is Target target)
            {
                target.ScrollSuspended = suspended; target.Hit = false;
                target.Light.Strength = 0; scopes.Add(target.Scope);
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        foreach (var scope in scopes) scope.ReconcileScroll(suspended);
    }
    // The capture harness feeds the same production controller without moving the user's desktop pointer.
    internal static void PreviewPointer(FrameworkElement root, Point point, bool inside) => (root.GetValue(ScopeProperty) as Scope)?.Pointer(point, inside);
    internal static void PreviewPress(FrameworkElement root, FrameworkElement source, bool pressed) => (root.GetValue(ScopeProperty) as Scope)?.Press(source, pressed);
    internal static void SettleSelection(FrameworkElement root, Point from, Point to) => (root.GetValue(ScopeProperty) as Scope)?.Settle(from, to);

    private sealed class Layer : SurfacePaint
    {
        internal CommandSurfaceLight Light = null!;
        internal FrameworkElement Face = null!;
        protected override void DrawSurface(DrawingContext drawing)
        {
            if ((GetIsSelected(Face) && !EditorChromeContext.GetIsTabDragging(Face)) || !Face.IsEnabled) return;
            var filled = Face is InteractionSurface && SurfaceMaterial.GetKind(Face) == SurfaceMaterialKind.Filled;
            Light.Draw(drawing, Contour(), filled ? null : Rim(1, openTop: Face is Border border && SurfaceMaterial.GetOpenTop(border)));
        }
    }
    private sealed class Target
    {
        internal bool ScrollSuspended;
        internal bool IsScrollSuspended => ScrollSuspended || ScrollTileBehavior.GetIsScrolling(Host);
        internal readonly FrameworkElement Face;
        internal readonly FrameworkElement Host;
        internal readonly CommandSurfaceLight Light;
        internal bool Hit;
        private readonly Scope _scope;
        internal Scope Scope => _scope;
        private readonly Layer? _layer;
        internal Target(FrameworkElement face, FrameworkElement host, Scope scope)
        {
            ScrollSuspended = ScrollTileBehavior.GetIsScrolling(host);
            Face = face; Host = host; _scope = scope; face.SizeChanged += Resized; face.IsEnabledChanged += EnabledChanged;
            if (!ReferenceEquals(face, host)) face.Unloaded += TemplateUnloaded;
            if (face is Border)
            {
                _layer = new Layer { Face = face, IsHitTestVisible = false };
                Light = new(face, _layer); _layer.Light = Light; _layer.Attach(face, -1);
            }
            else
            { Light = new(face, face); ((TabSelectionSurface)face).CommandLight = Light; }
        }
        internal Point Local(Point point) => _scope.Root.TranslatePoint(point, Face);
        internal Rect Bounds => Face is TabSelectionSurface tab ? tab.Bounds : new Rect(0, 0,
            Face is InteractionSurface { PaintedWidth: var width } && double.IsFinite(width) ? Math.Clamp(width, 0, Face.ActualWidth) : Face.ActualWidth, Face.ActualHeight);
        internal Rect WorldBounds => new(Face.TranslatePoint(Bounds.TopLeft, _scope.Root), Bounds.Size);
        internal CommandLightRole Role => GetRole(Host);
        private void Resized(object sender, SizeChangedEventArgs args) => RefreshMetrics();
        private void EnabledChanged(object sender, DependencyPropertyChangedEventArgs args) => RefreshMetrics();
        private void TemplateUnloaded(object sender, RoutedEventArgs args)
        {
            Unloaded(Host, args);
            Host.Dispatcher.BeginInvoke(() =>
            { if (Host.IsLoaded && GetIsEnabled(Host)) Loaded(Host, new()); }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        internal void RefreshMetrics() { if (!IsScrollSuspended) _scope.QueueReconcile(); }
        internal bool Contains(Point point)
        {
            if (IsScrollSuspended) return false;
            if (!Face.IsVisible || !Face.IsEnabled || Face.ActualWidth <= 0 || Face.ActualHeight <= 0) return false;
            var bounds = Bounds;
            var local = Local(point);
            if (bounds.IsEmpty || !bounds.Contains(local)) return false;
            // A row's nested action owns feedback while that action is hovered.
            // Do not stack the full-row wash behind its button.
            if (Role != CommandLightRole.Item || Face.InputHitTest(local) is not DependencyObject hit) return true;
            var button = Ancestor<ButtonBase>(hit);
            return button is null || ReferenceEquals(button, Host);
        }
        internal void PointAt(Point point, bool spatial)
        {
            var bounds = Bounds;
            var local = spatial ? Local(point) : new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            Light.X = local.X; Light.Y = local.Y;
        }
        internal void Dispose()
        {
            Face.SizeChanged -= Resized;
            Face.IsEnabledChanged -= EnabledChanged; Face.Unloaded -= TemplateUnloaded;
            _scope.Remove(this); Light.Strength = 0;
            if (!ReferenceEquals(Face, Host)) Face.ClearValue(TargetProperty);
            if (_layer is not null && VisualTreeHelper.GetParent(_layer) is Panel host) host.Children.Remove(_layer);
            if (Face is TabSelectionSurface tab) { tab.CommandLight = null; tab.InvalidateVisual(); }
            if (Face is InteractionSurface interactive && Face.IsLoaded && !GetIsEnabled(Host)) interactive.RefreshMaterial();
            else if (Face is Border row && Face.IsLoaded && !GetIsEnabled(Host) && StateSurfaceMotionBehavior.GetIsEnabled(row))
                StateSurfaceMotionBehavior.RefreshPaint(row);
        }
    }

    private sealed class Scope : IDisposable
    {
        internal readonly FrameworkElement Root;
        private readonly List<Target> _targets = new();
        private readonly Window? _window;
        private Point _point;
        private bool _inside, _pointerWithinRoot, _pressed, _pulsing, _disposed;
        private bool _reconcileQueued;
        private bool _pointerEventsAttached;
        private bool _readPointerOnReconcile;
        private Border? _pressedHeader;
        private readonly HashSet<Border> _tactileHeaders = new();
        private Target? _footprintTarget;
        private Size _footprintSize;
        private Size _footprint = new(170, 120);
        internal Scope(FrameworkElement root)
        {
            Root = root;
            if (root.ReadLocalValue(LightColorProperty) == DependencyProperty.UnsetValue) root.SetResourceReference(LightColorProperty, "CommandSurfaceLightColor");
            UpdatePointerEvents();
            root.PreviewMouseLeftButtonDown += Down; root.PreviewMouseLeftButtonUp += Up;
            root.LostMouseCapture += CaptureLost;
            root.PreviewKeyDown += KeyDown; root.PreviewKeyUp += KeyUp;
            root.IsKeyboardFocusWithinChanged += FocusChanged;
            root.Unloaded += Unload; MotionPolicy.Changed += PolicyChanged; MotionPolicy.DecorationBudgetChanged += PolicyChanged;
            _window = Window.GetWindow(root);
            if (_window is not null) { _window.Deactivated += Deactivated; _window.Closed += Closed; }
        }
        internal void Add(Target target)
        {
            _targets.Add(target);
            if (!target.IsScrollSuspended) QueueReconcile();
        }
        internal void Remove(Target target)
        {
            if (!target.IsScrollSuspended)
            {
                SurfaceMotion.Stop(Root, MotionChannel.CommandLight); _pulsing = false;
                SurfaceMotion.Stop(Root, MotionChannel.CommandPress); ResetPress();
            }
            _targets.Remove(target);
            if (ReferenceEquals(target, _footprintTarget)) _footprintTarget = null;
            if (target.Face is Border header) _tactileHeaders.Remove(header);
            if (_targets.Count == 0) Dispose();
            else if (!target.IsScrollSuspended) QueueReconcile();
        }
        internal void QueueReconcile()
        {
            if (_disposed || _reconcileQueued || !MotionPolicy.ExpressiveExtras) return;
            _reconcileQueued = true;
            Root.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
            {
                _reconcileQueued = false;
                if (_disposed || !MotionPolicy.ExpressiveExtras) return;
                if (_readPointerOnReconcile)
                { _readPointerOnReconcile = false; _point = Mouse.GetPosition(Root); _pointerWithinRoot = Root.IsMouseOver; }
                RefreshMetrics(); Apply(false);
            }));
        }
        private void Moved(object sender, MouseEventArgs args)
        {
            if (_pressedHeader is not null && Mouse.Captured is TabItem) Press(null, false);
            if (MotionPolicy.ExpressiveExtras) Pointer(args.GetPosition(Root), true);
        }
        private void Entered(object sender, MouseEventArgs args) => Pointer(args.GetPosition(Root), true);
        private void Left(object sender, MouseEventArgs args) => Pointer(_point, false);
        private void Down(object sender, MouseButtonEventArgs args) => Press(args.OriginalSource as DependencyObject, true);
        private void Up(object sender, MouseButtonEventArgs args) => Press(null, false);
        private void CaptureLost(object sender, MouseEventArgs args) { if (!Root.IsMouseCaptureWithin) Press(null, false); }
        private void KeyDown(object sender, KeyEventArgs args)
        { if (!args.IsRepeat && args.Key is Key.Enter or Key.Space) Press(Keyboard.FocusedElement as DependencyObject, true); }
        private void KeyUp(object sender, KeyEventArgs args) { if (args.Key is Key.Enter or Key.Space) Press(null, false); }
        private void FocusChanged(object sender, DependencyPropertyChangedEventArgs args) { if (args.NewValue is false) Press(null, false); }
        private void Deactivated(object? sender, EventArgs args) { Press(null, false); Pointer(_point, false); }
        private void Closed(object? sender, EventArgs args) => Dispose();
        private void Unload(object sender, RoutedEventArgs args) => Dispose();
        private void UpdatePointerEvents()
        {
            // A held tab still needs capture handoff when dragging starts, even without light.
            var attach = !_disposed && (MotionPolicy.ExpressiveExtras || _pressedHeader is not null);
            if (_pointerEventsAttached == attach) return;
            _pointerEventsAttached = attach;
            if (attach) { Root.MouseEnter += Entered; Root.MouseMove += Moved; Root.MouseLeave += Left; }
            else { Root.MouseEnter -= Entered; Root.MouseMove -= Moved; Root.MouseLeave -= Left; }
        }
        private void PolicyChanged()
        {
            if (!Root.Dispatcher.CheckAccess() || _disposed) return;
            SurfaceMotion.Stop(Root, MotionChannel.CommandLight); SurfaceMotion.Stop(Root, MotionChannel.CommandPress);
            _pulsing = false; ResetPress();
            UpdatePointerEvents();
            if (!MotionPolicy.ExpressiveExtras)
            {
                _inside = _pointerWithinRoot = false;
                foreach (var target in _targets) target.Hit = false;
            }
            foreach (var target in _targets) target.PointAt(_point, MotionPolicy.ExpressiveExtras);
            Apply(false);
            if (MotionPolicy.ExpressiveExtras) { _readPointerOnReconcile = true; QueueReconcile(); }
        }
        internal void Pointer(Point point, bool inside)
        {
            if (_disposed || !MotionPolicy.ExpressiveExtras) return;
            _readPointerOnReconcile = false;
            using var timing = Scriblucent.Rendering.FlashcardScrollTrace.Measure(Scriblucent.Rendering.FlashcardScrollTrace.Kind.Lighting, Root);
            _pointerWithinRoot = inside;
            var active = inside && _targets.Any(target => !target.IsScrollSuspended && target.Contains(point));
            var changed = _inside != active;
            _point = point; _inside = active;
            if (_pulsing) { SurfaceMotion.Stop(Root, MotionChannel.CommandLight); _pulsing = false; changed = true; }
            foreach (var target in _targets)
            {
                if (target.IsScrollSuspended) continue;
                var hit = active && target.Contains(point);
                if ((!MotionPolicy.ExpressiveExtras || target.Role == CommandLightRole.Item) && hit != target.Hit) changed = true;
                target.Hit = hit; target.PointAt(point, MotionPolicy.ExpressiveExtras);
            }
            UpdateFootprint();
            if (changed) Apply(true);
        }
        internal void RefreshMetrics()
        {
            if (_disposed || !MotionPolicy.ExpressiveExtras) return;
            using var timing = Scriblucent.Rendering.FlashcardScrollTrace.Measure(Scriblucent.Rendering.FlashcardScrollTrace.Kind.Lighting, Root);
            _footprintTarget = null;
            var changed = false;
            foreach (var target in _targets)
            {
                if (target.IsScrollSuspended) continue;
                var hit = _pointerWithinRoot && target.Contains(_point);
                if (hit != target.Hit) changed = true;
                target.Hit = hit;
                if (!_pulsing) target.PointAt(_point, MotionPolicy.ExpressiveExtras);
            }
            _inside = _targets.Any(target => !target.IsScrollSuspended && target.Hit); UpdateFootprint();
            if (changed && !_pulsing) Apply(true);
        }
        private void UpdateFootprint()
        {
            var primary = _targets.Where(target => !target.IsScrollSuspended && target.Hit && !GetIsSelected(target.Face))
                .OrderByDescending(target => target.Face is InteractionSurface).FirstOrDefault();
            if (primary is null) return;
            var area = primary.Bounds.Size;
            if (primary.Role == CommandLightRole.Command && primary.Face is not InteractionSurface)
            {
                // Physically attached chrome shares one footprint across the tab/toolbar seam.
                var group = primary.WorldBounds;
                foreach (var peer in _targets)
                {
                    if (peer.IsScrollSuspended || !peer.Face.IsVisible || peer.Role != CommandLightRole.Command || peer.Face is InteractionSurface) continue;
                    var bounds = peer.WorldBounds; var contact = group; contact.Inflate(1, 1);
                    if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0 && contact.IntersectsWith(bounds)) group.Union(bounds);
                }
                area = group.Size;
            }
            if (ReferenceEquals(primary, _footprintTarget) && area == _footprintSize) return;
            _footprintTarget = primary; _footprintSize = area; _footprint = CommandSurfaceLight.Footprint(area);
            foreach (var target in _targets) { target.Light.RadiusX = _footprint.Width; target.Light.RadiusY = _footprint.Height; }
        }
        internal void ReconcileScroll(bool suspended)
        {
            SurfaceMotion.Stop(Root, MotionChannel.CommandLight); _pulsing = false;
            _readPointerOnReconcile |= !suspended;
            QueueReconcile();
        }
        private double Rest(Target target) => target.ScrollSuspended || ScrollTileBehavior.GetIsScrolling(target.Host)
            || !MotionPolicy.ExpressiveExtras || !target.Face.IsVisible || !target.Face.IsEnabled ? 0
            : _inside && (MotionPolicy.ExpressiveExtras && target.Role != CommandLightRole.Item || target.Hit)
                ? (MotionPolicy.LightweightRendering ? .55 : .7)
                    * (target.Role == CommandLightRole.Item ? .6 : 1) : 0;
        private void Apply(bool animate)
        {
            if (!MotionPolicy.ExpressiveExtras)
            {
                SurfaceMotion.Stop(Root, MotionChannel.CommandLight);
                foreach (var target in _targets) target.Light.Strength = 0;
                return;
            }
            var tracks = _targets.Select(target => new NativeMotionTrack(target.Light, CommandSurfaceLight.StrengthProperty, Rest(target) * (_pressed ? .55 : 1))).ToArray();
            if (animate) SurfaceMotion.AnimateTracks(Root, MotionChannel.CommandLight, tracks, _inside ? 140 : 180, UiMotion.OutSine);
            else { SurfaceMotion.Stop(Root, MotionChannel.CommandLight); foreach (var track in tracks) track.Target.SetCurrentValue(track.Property, track.To); }
        }
        internal void Press(DependencyObject? source, bool pressed)
        {
            if (_disposed) return;
            if (pressed)
            {
                var button = Ancestor<ButtonBase>(source); var tab = Ancestor<TabItem>(source);
                if (button?.IsEnabled != true && tab?.IsEnabled != true) return;
                if (button is null && tab?.Template.FindName("HeaderBorder", tab) is Border header)
                { _pressedHeader = header; _tactileHeaders.Add(header); }
            }
            if (_pressed == pressed) { UpdatePointerEvents(); return; }
            _pressed = pressed; _pulsing = false;
            var tracks = new List<NativeMotionTrack>();
            if (_pressedHeader is { } face)
            {
                face.SetCurrentValue(UIElement.RenderTransformOriginProperty, new Point(.5, 1));
                var scale = UiMotion.EnsureScale(face);
                tracks.Add(new(scale, ScaleTransform.ScaleXProperty, pressed && MotionPolicy.Spatial ? .994 : 1, true));
                tracks.Add(new(scale, ScaleTransform.ScaleYProperty, pressed && MotionPolicy.Spatial ? .985 : 1, true));
                foreach (var plate in _targets.Select(target => target.Face).OfType<TabSelectionSurface>())
                    tracks.Add(new(plate, TabSelectionSurface.PressProgressProperty, pressed && MotionPolicy.Spatial ? 1 : 0, true));
            }
            SurfaceMotion.AnimateTracks(Root, MotionChannel.CommandPress, tracks, pressed ? 70 : 180, UiMotion.OutSine);
            if (!pressed) _pressedHeader = null;
            UpdatePointerEvents();
            Apply(true);
        }
        internal void Settle(Point from, Point to)
        {
            if (_disposed || !MotionPolicy.ExpressiveExtras || _targets.Count == 0) return;
            SurfaceMotion.Stop(Root, MotionChannel.CommandLight);
            var tracks = new List<NativeMotionTrack>();
            foreach (var target in _targets)
            {
                if (!_inside) target.PointAt(from, true);
                var local = target.Local(to);
                tracks.Add(new(target.Light, CommandSurfaceLight.XProperty, local.X, true, Profile: new(UiMotion.OutCubic, 220)));
                tracks.Add(new(target.Light, CommandSurfaceLight.YProperty, local.Y, true, Profile: new(UiMotion.OutCubic, 220)));
                tracks.Add(new(target.Light, CommandSurfaceLight.StrengthProperty, Rest(target), Overshoot: .95, Correction: Rest(target)));
            }
            _pulsing = true;
            SurfaceMotion.AnimateTracks(Root, MotionChannel.CommandLight, tracks, 260, UiMotion.OutSine, () =>
            { _pulsing = false; foreach (var target in _targets) target.PointAt(_point, MotionPolicy.ExpressiveExtras); });
        }
        private void ResetPress()
        {
            _pressed = false; _pressedHeader = null;
            foreach (var header in _tactileHeaders)
            { var scale = UiMotion.EnsureScale(header); scale.ScaleX = 1; scale.ScaleY = 1; }
            foreach (var plate in _targets.Select(target => target.Face).OfType<TabSelectionSurface>()) plate.PressProgress = 0;
        }
        public void Dispose()
        {
            if (_disposed) return;
            Press(null, false); _disposed = true;
            SurfaceMotion.Stop(Root, MotionChannel.CommandLight); SurfaceMotion.Stop(Root, MotionChannel.CommandPress);
            ResetPress(); _tactileHeaders.Clear();
            UpdatePointerEvents();
            Root.PreviewMouseLeftButtonDown -= Down; Root.PreviewMouseLeftButtonUp -= Up; Root.LostMouseCapture -= CaptureLost;
            Root.PreviewKeyDown -= KeyDown; Root.PreviewKeyUp -= KeyUp; Root.IsKeyboardFocusWithinChanged -= FocusChanged; Root.Unloaded -= Unload;
            MotionPolicy.Changed -= PolicyChanged; MotionPolicy.DecorationBudgetChanged -= PolicyChanged;
            if (_window is not null) { _window.Deactivated -= Deactivated; _window.Closed -= Closed; }
            foreach (var target in _targets) target.Light.Strength = 0;
            Root.ClearValue(ScopeProperty);
        }
    }
}
