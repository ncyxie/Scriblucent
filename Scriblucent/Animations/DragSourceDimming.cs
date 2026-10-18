using System.Windows;

namespace Scriblucent.Animations;

/// <summary>Gesture state belongs to the item, not to its recyclable visual container.</summary>
internal sealed class DragSourceDimming : IDisposable
{
    private static DragSourceDimming? _active;
    internal static event Action? Changed;
    private readonly Window _owner;
    private readonly object? _item;
    private readonly double _opacity;

    internal DragSourceDimming(Window owner, object? item, double opacity)
    {
        if (_active is not null) throw new InvalidOperationException("A drag dimming scope is already active.");
        _owner = owner; _item = item; _opacity = opacity;
        _active = this;
        Changed?.Invoke();
    }

    internal static bool IsSource(FrameworkElement element) => _active is { _item: not null } active
        && ReferenceEquals(element.DataContext, active._item)
        && ReferenceEquals(Window.GetWindow(element), active._owner);

    internal static double Opacity => _active?._opacity ?? 1;

    public void Dispose()
    {
        if (!ReferenceEquals(_active, this)) return;
        _active = null;
        Changed?.Invoke();
    }
}
