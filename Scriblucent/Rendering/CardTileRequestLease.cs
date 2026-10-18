namespace Scriblucent.Rendering;

/// <summary>UI-dispatcher ownership of one preview-to-tile request chain.</summary>
internal sealed class CardTileRequestLease : IDisposable
{
    private Action? _cancel;
    internal bool IsDisposed { get; private set; }
    internal void Attach(Action cancel)
    {
        if (IsDisposed) cancel();
        else _cancel = cancel;
    }
    internal void Detach() => _cancel = null;
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        var cancel = _cancel; _cancel = null;
        cancel?.Invoke();
    }
}
