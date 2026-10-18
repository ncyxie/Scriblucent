namespace Scriblucent.Services;

internal sealed class ItemReorderAdapter(
    object owner,
    string kind,
    Func<object, IReadOnlyList<object>> draggedItems,
    Func<IReadOnlyList<object>, object, bool> canDrop,
    Func<IReadOnlyList<object>, object, bool, bool> move,
    Func<bool>? enabled = null)
{
    public object Owner { get; } = owner;
    public string Kind { get; } = kind;
    public bool IsEnabled => enabled?.Invoke() ?? true;
    public IReadOnlyList<object> DraggedItems(object item) => draggedItems(item);
    public bool CanDrop(ItemReorderAdapter source, IReadOnlyList<object> items, object anchor) => IsEnabled
        && source.IsEnabled && ReferenceEquals(Owner, source.Owner) && Kind == source.Kind && canDrop(items, anchor);
    public bool Move(IReadOnlyList<object> items, object anchor, bool after) => IsEnabled && move(items, anchor, after);
}
