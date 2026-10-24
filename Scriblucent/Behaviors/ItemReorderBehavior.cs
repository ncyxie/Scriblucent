using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Services;
using Scriblucent.ViewModels;
using Scriblucent.Views;

namespace Scriblucent.Behaviors;

public enum ItemReorderKind { None, DashboardItems, DashboardGroups, Flashcards, QuizQuestions, QuizOptions, AiTools }

/// <summary>One bounded drag preview for virtualized grids and editable lists. No data writes on hover.</summary>
public static class ItemReorderBehavior
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached("Kind", typeof(ItemReorderKind), typeof(ItemReorderBehavior), new PropertyMetadata(ItemReorderKind.None, Changed));
    public static ItemReorderKind GetKind(DependencyObject element) => (ItemReorderKind)element.GetValue(KindProperty);
    public static void SetKind(DependencyObject element, ItemReorderKind value) => element.SetValue(KindProperty, value);
    public static readonly DependencyProperty MenuActionsProperty = DependencyProperty.RegisterAttached("MenuActions", typeof(bool), typeof(ItemReorderBehavior), new PropertyMetadata(false));
    public static bool GetMenuActions(DependencyObject element) => (bool)element.GetValue(MenuActionsProperty);
    public static void SetMenuActions(DependencyObject element, bool value) => element.SetValue(MenuActionsProperty, value);
    public static readonly DependencyProperty IsHandleProperty = DependencyProperty.RegisterAttached("IsHandle", typeof(bool), typeof(ItemReorderBehavior), new PropertyMetadata(false));
    public static bool GetIsHandle(DependencyObject element) => (bool)element.GetValue(IsHandleProperty);
    public static void SetIsHandle(DependencyObject element, bool value) => element.SetValue(IsHandleProperty, value);
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(State), typeof(ItemReorderBehavior));
    private static readonly object MenuTag = new();
    private const string DataFormat = "Scriblucent.ItemReorder";
    private static bool _dragging;
    internal static bool IsDragging => _dragging;
    internal static Window? DragOwner { get; private set; }
    internal static event Action? DragStateChanged;
    private static State? _previewState;
    internal static bool IsInsertionActive => _previewState?.HasDestination == true;
    private static readonly DependencyProperty RoutingProperty = DependencyProperty.RegisterAttached("Routing", typeof(bool), typeof(ItemReorderBehavior));

    private static void RouteWindow(Window? window)
    {
        if (window is null || (bool)window.GetValue(RoutingProperty)) return;
        window.SetValue(RoutingProperty, true);
        window.PreviewDragOver += (_, args) =>
        {
            if (args.Data.GetData(DataFormat) is not Payload) return;
            var list = FindDragList(args.OriginalSource as DependencyObject, (Payload)args.Data.GetData(DataFormat));
            if (list?.GetValue(StateProperty) is State state) state.Over(list, args);
            else { _previewState?.Clear(); }
        };
        window.PreviewDrop += (_, args) =>
        {
            if (args.Data.GetData(DataFormat) is not Payload) return;
            var list = FindDragList(args.OriginalSource as DependencyObject, (Payload)args.Data.GetData(DataFormat));
            if (list?.GetValue(StateProperty) is State state) state.Drop(list, args);
        };
    }

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ItemsControl list) return;
        (list.GetValue(StateProperty) as State)?.Dispose();
        list.SetValue(StateProperty, GetKind(list) == ItemReorderKind.None ? null : new State(list));
    }
    private sealed record Payload(ItemReorderAdapter Adapter, IReadOnlyList<object> Items)
    {
        public IReadOnlySet<object> ItemSet { get; } = Items.ToHashSet();
    }
    private sealed record Destination(FrameworkElement Container, object Item, bool After, Rect Bounds, bool Vertical, double? MarkerX = null);

    private sealed class State : IDisposable
    {
        private readonly ItemsControl _list;
        private ItemReorderAdapter? _adapter;
        private Point _start;
        private FrameworkElement? _source;
        private object? _item;
        private InsertionAdorner? _adorner;
        private AdornerLayer? _layer;
        private Destination? _destination;
        internal bool HasDestination => _destination is not null;
        internal bool Accepts(Payload payload)
        {
            if (_adapter is null) LoadAdapter();
            return _adapter is not null && ReferenceEquals(_adapter.Owner, payload.Adapter.Owner) && _adapter.Kind == payload.Adapter.Kind;
        }
        private ScrollContentPresenter? _flashcardViewport;
        private bool _groupingAllowed;
        private Guid? _selectionPress;
        private bool _suppressUp;
        public State(ItemsControl list)
        {
            _list = list;
            list.Loaded += Loaded; list.Unloaded += Unloaded;
            list.DataContextChanged += ContextChanged;
            list.PreviewMouseLeftButtonDown += Down;
            list.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(Move), true);
            list.PreviewMouseLeftButtonUp += Up;
            list.PreviewDragOver += Over; list.PreviewDragLeave += Leave; list.PreviewDrop += Drop;
            list.ContextMenuOpening += MenuOpening;
            list.AllowDrop = true;
            if (list.IsLoaded) LoadAdapter();
        }
        private void Loaded(object sender, RoutedEventArgs args) => LoadAdapter();
        private void ContextChanged(object sender, DependencyPropertyChangedEventArgs args) { Clear(); if (_list.IsLoaded) LoadAdapter(); }
        private void LoadAdapter()
        {
            RouteWindow(Window.GetWindow(_list));
            var controller = EditorChromeContext.GetController(_list);
            _adapter = GetKind(_list) switch
            {
                ItemReorderKind.DashboardItems or ItemReorderKind.DashboardGroups when Window.GetWindow(_list)?.DataContext is MainViewModel model
                    => model.CreateDashboardReorderAdapter(GetKind(_list) == ItemReorderKind.DashboardGroups),
                ItemReorderKind.Flashcards when controller is FlashcardsPreviewWindow editor => editor.CreateCardReorderAdapter(),
                ItemReorderKind.QuizQuestions when controller is QuizPreviewWindow editor => editor.CreateQuestionReorderAdapter(),
                ItemReorderKind.QuizOptions when controller is QuizPreviewWindow editor => editor.CreateOptionReorderAdapter(_list.DataContext),
                ItemReorderKind.AiTools when FindSettings(_list) is { } settings => settings.CreateAiToolReorderAdapter(),
                _ => null
            };
        }
        private bool HandlesOnly => GetKind(_list) != ItemReorderKind.DashboardItems;
        private void Down(object sender, MouseButtonEventArgs args)
        {
            _suppressUp = false;
            if (_dragging || !ReferenceEquals(FindList(args.OriginalSource as DependencyObject), _list)) return;
            _source = null; _item = null;
            if (_adapter is null) LoadAdapter();
            if (_adapter?.IsEnabled != true) return;
            var source = args.OriginalSource as DependencyObject;
            if (HandlesOnly ? !HasHandle(source, _list) : IsInteractive(source, _list)) return;
            if (ItemsControl.ContainerFromElement(_list, source) is not FrameworkElement container) return;
            var item = _list.ItemContainerGenerator.ItemFromContainer(container);
            if (item == DependencyProperty.UnsetValue) return;
            // Defer body-selection toggling only for an already selected dashboard item.
            var items = _adapter.DraggedItems(item);
            if (items.Count == 0)
            {
                if (!HandlesOnly && Window.GetWindow(_list)?.DataContext is MainViewModel { IsMassSelectMode: true }) args.Handled = true;
                return;
            }
            _source = container; _item = item; _start = args.GetPosition(container);
            if (!HandlesOnly && Window.GetWindow(_list)?.DataContext is MainViewModel { IsMassSelectMode: true } model
                && model.DashboardSelection.Contains(MainViewModel.DashboardOrderItem.From(item).Id))
            { _selectionPress = MainViewModel.DashboardOrderItem.From(item).Id; args.Handled = true; }
        }
        private void Up(object sender, MouseButtonEventArgs args)
        {
            if (_dragging || _suppressUp) { _suppressUp = false; args.Handled = true; return; }
            if (_selectionPress is Guid id && _source is not null && Window.GetWindow(_list)?.DataContext is MainViewModel model)
            { model.DashboardSelection.Toggle(id, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Control)); args.Handled = true; }
            _selectionPress = null; _source = null; _item = null;
        }
        private void Move(object sender, MouseEventArgs args)
        {
            if (_dragging || _source is null || _item is null || args.LeftButton != MouseButtonState.Pressed || _adapter?.IsEnabled != true) return;
            var delta = args.GetPosition(_source) - _start;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var items = _adapter.DraggedItems(_item);
            if (items.Count == 0) { _source = null; _item = null; return; }
            var payload = new Payload(_adapter, items);
            var data = new DataObject(); data.SetData(DataFormat, payload);
            // Legacy centre grouping carries one item. A selected block must never
            // fall through to it and silently move only the card under the pointer.
            if (GetKind(_list) == ItemReorderKind.DashboardItems && items.Count == 1) data.SetData(_item.GetType(), _item);
            var source = _source;
            var preview = items.Count > 1 || GetKind(_list) is ItemReorderKind.QuizQuestions or ItemReorderKind.DashboardGroups
                ? ItemReorderPreview.Create(source, _item, items.Count) : null;
            // Group ghosts capture only the header, never a group's potentially enormous contents.
            if (GetKind(_list) == ItemReorderKind.DashboardGroups)
                source = VisualDescendants(source).OfType<DashboardGroupHeader>().FirstOrDefault() ?? source;
            else if (GetKind(_list) == ItemReorderKind.DashboardItems)
                source = _item is NoteCardViewModel
                    ? VisualDescendants(source).OfType<NoteCardControl>().FirstOrDefault() ?? source
                    : VisualDescendants(source).OfType<FrameworkElement>().FirstOrDefault(element => ReferenceEquals(element.Tag, _item)) ?? source;
            else if (GetKind(_list) == ItemReorderKind.Flashcards)
                // Dim the complete item outside its cached paint, including selection chrome.
                // A face-level opacity would be captured into its reusable scroll tile.
                source = VisualDescendants(source).OfType<CollectionSelectionFrame>().FirstOrDefault() ?? source;
            else if (GetKind(_list) == ItemReorderKind.QuizQuestions)
                source = VisualDescendants(source).OfType<Border>().FirstOrDefault(element => element.Name == "QuizQuestionFace") ?? source;
            var grab = _source.TranslatePoint(_start, source);
            _source = null; _item = null; _selectionPress = null;
            var owner = Window.GetWindow(_list); bool cancelled = false;
            void Deactivated(object? sender, EventArgs e) => cancelled = true;
            void Continue(object sender, QueryContinueDragEventArgs e)
            {
                if (cancelled || !_list.IsLoaded) { e.Action = DragAction.Cancel; e.Handled = true; }
            }
            if (owner is not null) owner.Deactivated += Deactivated;
            source.QueryContinueDrag += Continue;
            // A grip is a Button and captures the pointer on press. Hand capture over
            // to OLE explicitly; the native drag must not compete with button capture.
            if (Mouse.Captured is DependencyObject captured && HasHandle(captured, _list)) Mouse.Capture(null);
            _dragging = true;
            DragOwner = owner;
            if (source is NoteCardControl) MainWindow.SetNoteDragInProgress(true);
            ItemDragScroll? scroll = null;
            try
            {
                if (FindScroll(_list) is { } viewer) scroll = new ItemDragScroll(viewer, point => ItemReorderBehavior.RefreshPreview(owner, payload, point));
                DragStateChanged?.Invoke();
                DashboardDragGhost.Execute(source, data, DragDropEffects.Move, grab, preview); args.Handled = true;
            }
            finally
            {
                scroll?.Dispose();
                _dragging = false; _suppressUp = true; _previewState?.Clear(); Clear();
                DragOwner = null;
                if (source is NoteCardControl) MainWindow.SetNoteDragInProgress(false);
                source.QueryContinueDrag -= Continue;
                if (owner is not null) owner.Deactivated -= Deactivated;
                DragStateChanged?.Invoke();
            }
        }
        internal void Over(object sender, DragEventArgs args)
        {
            if (args.Data.GetData(DataFormat) is not Payload payload) return;
            if (_adapter is null) LoadAdapter();
            if (_adapter is null || !ReferenceEquals(_adapter.Owner, payload.Adapter.Owner) || _adapter.Kind != payload.Adapter.Kind) return;
            if (_previewState is not null && !ReferenceEquals(_previewState, this)) _previewState.Clear();
            _previewState = this;
            _destination = Resolve(args.GetPosition(_list), payload);
            if (_destination is null)
            {
                ClearPreview();
                if (!_groupingAllowed) { args.Handled = true; args.Effects = DragDropEffects.None; }
                return;
            }
            if (Window.GetWindow(_list) is MainWindow main) main.ClearAllDragDropVisuals();
            ShowPreview();
            args.Effects = DragDropEffects.Move; args.Handled = true;
        }
        private Destination? Resolve(Point point, Payload payload)
        {
            _groupingAllowed = false;
            bool flashcards = GetKind(_list) == ItemReorderKind.Flashcards;
            var viewport = flashcards ? FlashcardViewport() : Rect.Empty;
            if (flashcards && !viewport.Contains(point)) return null;
            List<Rect>? faces = flashcards ? [] : null;
            var vertical = GetKind(_list) is ItemReorderKind.DashboardGroups or ItemReorderKind.QuizQuestions or ItemReorderKind.QuizOptions or ItemReorderKind.AiTools
                || _list is DashboardVirtualizedCardGrid { Layout: DashboardCardGridLayout.VerticalList };
            Destination? best = null; double distance = double.MaxValue;
            foreach (var container in Realized(_list))
            {
                var item = _list.ItemContainerGenerator.ItemFromContainer(container);
                var rect = flashcards ? FlashcardBounds(container)
                    : container.TransformToAncestor(_list).TransformBounds(new Rect(container.RenderSize));
                if (rect.Width <= 0 || rect.Height <= 0) continue;
                faces?.Add(rect);
                if (flashcards)
                {
                    var visible = Rect.Intersect(rect, viewport);
                    if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) continue;
                }
                if (payload.ItemSet.Contains(item)) { if (rect.Contains(point)) return null; continue; }
                if (payload.Items.Count == 1 && GetKind(_list) == ItemReorderKind.DashboardItems && rect.Contains(point))
                {
                    var relative = vertical ? point.Y - rect.Top : point.X - rect.Left;
                    var extent = vertical ? rect.Height : rect.Width;
                    if (relative > 24 && relative < extent - 24) { _groupingAllowed = true; return null; }
                }
                bool after = vertical ? point.Y >= rect.Top + rect.Height / 2 : point.X >= rect.Left + rect.Width / 2;
                var dx = vertical ? Math.Max(rect.Left - point.X, Math.Max(0, point.X - rect.Right)) : Math.Abs(point.X - (after ? rect.Right : rect.Left));
                var dy = vertical ? Math.Abs(point.Y - (after ? rect.Bottom : rect.Top)) : Math.Max(rect.Top - point.Y, Math.Max(0, point.Y - rect.Bottom));
                var score = dx * dx + dy * dy;
                if (score >= distance) continue;
                distance = score; best = new(container, item, after, rect, vertical);
            }
            if (best is null || !_adapter!.CanDrop(payload.Adapter, payload.Items, best.Item)) return null;
            return faces is null ? best : best with { MarkerX = FlashcardGapCentre(best.Bounds, best.After, faces) };
        }
        private Rect FlashcardViewport()
        {
            if (_flashcardViewport is null || !_list.IsAncestorOf(_flashcardViewport))
                _flashcardViewport = VisualDescendants(_list).OfType<ScrollContentPresenter>().FirstOrDefault();
            return _flashcardViewport is { } viewport
                ? viewport.TransformToAncestor(_list).TransformBounds(new Rect(viewport.RenderSize))
                : new Rect(_list.RenderSize);
        }
        private Rect FlashcardBounds(FrameworkElement container)
        {
            var frame = VisualDescendants(container).OfType<CollectionSelectionFrame>().FirstOrDefault();
            if (frame is { ActualWidth: > 0, ActualHeight: > 0 } && VisualTreeHelper.GetParent(frame) is Visual parent)
            {
                // Use the cached face insets and layout origin, excluding the frame's
                // hover lift. Padding and the unused half of a flip never define a slot.
                var offset = VisualTreeHelper.GetOffset(frame); var inset = frame.FaceMargin;
                var face = new Rect(offset.X + inset.Left, offset.Y + inset.Top,
                    Math.Max(0, frame.ActualWidth - inset.Left - inset.Right), Math.Max(0, frame.ActualHeight - inset.Top - inset.Bottom));
                return parent.TransformToAncestor(_list).TransformBounds(face);
            }
            return container.TransformToAncestor(_list).TransformBounds(new Rect(container.RenderSize));
        }
        private static double FlashcardGapCentre(Rect face, bool after, List<Rect> faces)
        {
            double? left = null, right = null;
            foreach (var other in faces)
            {
                if (Math.Min(face.Bottom, other.Bottom) - Math.Max(face.Top, other.Top) < Math.Min(face.Height, other.Height) / 2) continue;
                if (other.Right <= face.Left + .5) left = Math.Max(left ?? other.Right, other.Right);
                if (other.Left >= face.Right - .5) right = Math.Min(right ?? other.Left, other.Left);
            }
            // At a row's outside edge, mirror its measured spacing; a single-column
            // row retains the flashcard grid's 16 DIP inter-card breathing room.
            double? beforeGap = face.Left - left, afterGap = right - face.Right;
            double gap = after ? afterGap ?? beforeGap ?? 16 : beforeGap ?? afterGap ?? 16;
            return after ? face.Right + gap / 2 : face.Left - gap / 2;
        }
        internal void Drop(object sender, DragEventArgs args)
        {
            if (args.Data.GetData(DataFormat) is not Payload payload || _adapter is null) return;
            if (!ReferenceEquals(_adapter.Owner, payload.Adapter.Owner) || _adapter.Kind != payload.Adapter.Kind) return;
            var destination = Resolve(args.GetPosition(_list), payload);
            var grouping = _groupingAllowed;
            Clear();
            if (destination is null) { if (!grouping) { args.Handled = true; args.Effects = DragDropEffects.None; } return; }
            args.Handled = true;
            args.Effects = ReorderMotion.Commit(_list, () => _adapter.CanDrop(payload.Adapter, payload.Items, destination.Item)
                && _adapter.Move(payload.Items, destination.Item, destination.After), payload.ItemSet) ? DragDropEffects.Move : DragDropEffects.None;
        }
        private void Leave(object sender, DragEventArgs args)
        {
            var point = args.GetPosition(_list);
            if (point.X < 0 || point.Y < 0 || point.X > _list.ActualWidth || point.Y > _list.ActualHeight) Clear();
        }
        private void ShowPreview()
        {
            if (_destination is null) { ClearPreview(); return; }
            _layer ??= AdornerLayer.GetAdornerLayer(_list);
            if (_layer is null) return;
            if (_adorner is null) { _adorner = new InsertionAdorner(_list); _layer.Add(_adorner); }
            // The adorner lives above the ScrollViewer, so its descendants' clipping
            // does not apply automatically. Match the actual content viewport.
            if (GetKind(_list) == ItemReorderKind.Flashcards)
                _adorner.SetViewport(FlashcardViewport());
            _adorner.Show(_destination.Bounds, _destination.After, _destination.Vertical, _destination.MarkerX);
        }
        internal void RefreshPreview(Point point, Payload payload)
        {
            _destination = Resolve(point, payload);
            ShowPreview();
        }
        private void MenuOpening(object sender, ContextMenuEventArgs args)
        {
            if (_adapter?.IsEnabled != true || !ReferenceEquals(FindList(args.OriginalSource as DependencyObject), _list)) return;
            var source = args.OriginalSource as DependencyObject;
            if (source is null) return;
            ContextMenu? menu = null;
            for (var current = source; current is not null && !ReferenceEquals(current, _list); current = Parent(current))
                if (current is FrameworkElement { ContextMenu: { } found }) { menu = found; break; }
            if (menu is null) return;
            PopulateMenu(menu, source);
        }
        internal void PopulateMenu(ContextMenu menu, DependencyObject source)
        {
            ClearMenuActions(menu);
            if (!GetMenuActions(menu) || _adapter?.IsEnabled != true || ItemsControl.ContainerFromElement(_list, source) is not FrameworkElement container) return;
            var item = _list.ItemContainerGenerator.ItemFromContainer(container);
            var moving = _adapter.DraggedItems(item);
            var selected = moving.ToHashSet();
            var visible = _list.Items.Cast<object>().ToList();
            var indices = visible.Select((value, index) => (value, index)).Where(pair => selected.Contains(pair.value)).Select(pair => pair.index).ToArray();
            if (indices.Length == 0) return;
            if (menu.Items.Count > 0) menu.Items.Add(new Separator { Tag = MenuTag });
            foreach (var after in new[] { false, true })
            {
                int index = after ? indices.Max() + 1 : indices.Min() - 1;
                var anchor = index >= 0 && index < visible.Count ? visible[index] : null;
                var action = new MenuItem { Header = LocalizationService.GetString(after ? "ReorderLater" : "ReorderEarlier"), Tag = MenuTag,
                    IsEnabled = anchor is not null && _adapter.CanDrop(_adapter, moving, anchor) };
                action.Click += (_, e) => { if (anchor is not null) ReorderMotion.Commit(_list, () => _adapter.Move(moving, anchor, after)); e.Handled = true; };
                menu.Items.Add(action);
            }
        }
        internal bool MoveStep(DependencyObject handle, bool after)
        {
            var (moving, anchor) = StepTarget(handle, after);
            return anchor is not null && ReorderMotion.Commit(_list, () => _adapter!.Move(moving, anchor, after));
        }
        internal bool CanStep(DependencyObject handle, bool after) => StepTarget(handle, after).Anchor is not null;
        private (IReadOnlyList<object> Moving, object? Anchor) StepTarget(DependencyObject handle, bool after)
        {
            if (_adapter?.IsEnabled != true || ItemsControl.ContainerFromElement(_list, handle) is not FrameworkElement container) return ([], null);
            var item = _list.ItemContainerGenerator.ItemFromContainer(container);
            var moving = _adapter.DraggedItems(item);
            int first = int.MaxValue, last = -1;
            if (moving.Count == 1) first = last = _list.Items.IndexOf(moving[0]);
            else
            {
                var set = moving.ToHashSet();
                for (int i = 0; i < _list.Items.Count; i++)
                    if (set.Contains(_list.Items[i])) { first = Math.Min(first, i); last = i; }
            }
            if (first < 0 || last < 0) return ([], null);
            int index = after ? last + 1 : first - 1;
            return (moving, index >= 0 && index < _list.Items.Count && _adapter.CanDrop(_adapter, moving, _list.Items[index]) ? _list.Items[index] : null);
        }
        private void ClearPreview() { if (_adorner is not null) _layer?.Remove(_adorner); _adorner = null; }
        internal void Clear() { ClearPreview(); _destination = null; if (ReferenceEquals(_previewState, this)) _previewState = null; }
        private void Unloaded(object sender, RoutedEventArgs args) { Clear(); _adapter = null; _source = null; _item = null; _flashcardViewport = null; _layer = null; }
        public void Dispose()
        {
            Clear(); _list.Loaded -= Loaded; _list.Unloaded -= Unloaded;
            _list.DataContextChanged -= ContextChanged;
            _list.PreviewMouseLeftButtonDown -= Down; _list.RemoveHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(Move));
            _list.PreviewMouseLeftButtonUp -= Up; _list.PreviewDragOver -= Over; _list.PreviewDragLeave -= Leave; _list.PreviewDrop -= Drop; _list.ContextMenuOpening -= MenuOpening;
        }
    }
    private static void RefreshPreview(Window? owner, Payload payload, Point screenPoint)
    {
        if (!_dragging || owner is not { IsLoaded: true }) return;
        var point = owner.PointFromScreen(screenPoint);
        var hit = owner.InputHitTest(point) as DependencyObject;
        var list = FindDragList(hit, payload);
        var state = list?.GetValue(StateProperty) as State;
        if (!ReferenceEquals(_previewState, state)) _previewState?.Clear();
        if (state is null || list is null) return;
        _previewState = state;
        state.RefreshPreview(owner.TranslatePoint(point, list), payload);
    }
    internal static IEnumerable<FrameworkElement> Realized(ItemsControl list)
    {
        var stack = new Stack<DependencyObject>(); stack.Push(list);
        while (stack.TryPop(out var node))
        {
            if (!ReferenceEquals(node, list) && node is FrameworkElement element && ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(element), list))
            { yield return element; continue; }
            if (node is not Visual) continue;
            for (int i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(node, i));
        }
    }
    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject element)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i); yield return child;
            foreach (var nested in VisualDescendants(child)) yield return nested;
        }
    }
    private static DependencyObject? Parent(DependencyObject element) => element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
    private static ItemsControl? FindList(DependencyObject? source)
    {
        for (var current = source; current is not null; current = Parent(current))
            if (current is ItemsControl list && GetKind(list) != ItemReorderKind.None) return list;
        return null;
    }
    internal static bool MoveFromHandle(DependencyObject handle, bool after)
        => FindList(handle)?.GetValue(StateProperty) is State state && state.MoveStep(handle, after);
    internal static bool CanMoveFromHandle(DependencyObject handle, bool after)
        => FindList(handle)?.GetValue(StateProperty) is State state && state.CanStep(handle, after);
    internal static ItemsControl? FindReorderList(DependencyObject element) => FindList(element);
    internal static void PrepareContextMenu(ContextMenu menu, DependencyObject placementTarget)
    {
        if (FindList(placementTarget)?.GetValue(StateProperty) is State state) state.PopulateMenu(menu, placementTarget);
        else ClearMenuActions(menu);
    }
    private static void ClearMenuActions(ContextMenu menu)
    {
        // The note menu is shared with Recents; never retain commands captured
        // from a previous dashboard owner or a now-disabled editor mode.
        foreach (var old in menu.Items.OfType<FrameworkElement>().Where(item => ReferenceEquals(item.Tag, MenuTag)).ToArray()) menu.Items.Remove(old);
    }
    private static ItemsControl? FindDragList(DependencyObject? source, Payload payload)
    {
        for (var current = source; current is not null; current = Parent(current))
            if (current is ItemsControl list && list.GetValue(StateProperty) is State state && state.Accepts(payload)) return list;
        return null;
    }
    private static bool HasHandle(DependencyObject? source, DependencyObject stop)
    {
        for (var current = source; current is not null && !ReferenceEquals(current, stop); current = Parent(current)) if (GetIsHandle(current)) return true;
        return false;
    }
    private static bool IsInteractive(DependencyObject? source, DependencyObject stop)
    {
        for (var current = source; current is not null && !ReferenceEquals(current, stop); current = Parent(current))
            if (current is ButtonBase or TextBoxBase or ComboBox or Thumb or CollectionSelectionToggle) return true;
        return false;
    }
    private static ScrollViewer? FindScroll(DependencyObject element)
    {
        var own = DescendantScroll(element); if (own?.ScrollableHeight > 0) return own;
        ScrollViewer? parent = null;
        for (var current = Parent(element); current is not null; current = Parent(current))
            if (current is ScrollViewer scroll) { parent ??= scroll; if (scroll.ScrollableHeight > 0) return scroll; }
        return own ?? parent;
    }
    private static SettingsPanel? FindSettings(DependencyObject element)
    {
        for (var current = Parent(element); current is not null; current = Parent(current))
            if (current is SettingsPanel settings) return settings;
        return null;
    }
    private static ScrollViewer? DescendantScroll(DependencyObject element)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is ScrollViewer scroll) return scroll;
            if (child is ItemsPresenter or ItemsControl) continue;
            if (DescendantScroll(child) is { } nested) return nested;
        }
        return null;
    }
    private sealed class InsertionAdorner(UIElement owner) : Adorner(owner)
    {
        private Rect _bounds; private bool _after, _vertical;
        private double? _markerX;
        private RectangleGeometry? _viewportClip;
        public void SetViewport(Rect bounds)
        {
            if (_viewportClip?.Rect == bounds) return;
            _viewportClip = new RectangleGeometry(bounds); _viewportClip.Freeze();
        }
        public void Show(Rect bounds, bool after, bool vertical, double? markerX) { IsHitTestVisible = false; _bounds = bounds; _after = after; _vertical = vertical; _markerX = markerX; InvalidateVisual(); }
        protected override void OnRender(DrawingContext drawing)
        {
            var owner = AdornedElement as FrameworkElement;
            var brush = owner?.TryFindResource(_vertical ? "AccentGuideHorizontalBrush" : "AccentRimBrush") as Brush ?? Brushes.DodgerBlue;
            var contrast = owner?.TryFindResource("EditHandleContrast") as Brush;
            // AdornerLayer manages UIElement.Clip during layout. Keep our viewport
            // boundary on the drawing itself so later layout cannot remove it.
            if (_viewportClip is not null) drawing.PushClip(_viewportClip);
            Rect marker;
            if (_vertical)
            {
                double y = _after ? _bounds.Bottom - 4 : _bounds.Top + 2;
                marker = new Rect(_bounds.Left + 7, y - 1, Math.Max(0, _bounds.Width - 14), 2);
            }
            else
            {
                double x = _markerX ?? (_after ? _bounds.Right - 4 : _bounds.Left + 2);
                marker = new Rect(x - 1, _bounds.Top + 7, 2, Math.Max(0, _bounds.Height - 14));
            }
            // A retained contrast edge keeps the lit accent readable over cards and photos.
            // Filled geometry gives a thin guide a stable brush mapping in either orientation.
            if (contrast is not null)
            {
                var outline = marker; outline.Inflate(.75, .75);
                drawing.DrawRoundedRectangle(contrast, null, outline, 1.75, 1.75);
            }
            drawing.DrawRoundedRectangle(brush, null, marker, 1, 1);
            if (_viewportClip is not null) drawing.Pop();
        }
    }
}
