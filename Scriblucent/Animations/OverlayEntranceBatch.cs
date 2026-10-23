namespace Scriblucent.Animations;

/// <summary>Starts a modal's window backdrops on the same tick, after every snapshot is ready.</summary>
internal sealed class OverlayEntranceBatch
{
    private readonly List<Action> _starts = new();
    private int _pending;
    private bool _sealed;

    internal Action Register(Action start)
    {
        if (_sealed) throw new InvalidOperationException("The modal entrance is already sealed.");
        _starts.Add(start);
        _pending++;
        bool ready = false;
        return () =>
        {
            if (ready) return;
            ready = true;
            _pending--;
            TryStart();
        };
    }

    internal void Seal()
    {
        _sealed = true;
        TryStart();
    }

    private void TryStart()
    {
        if (!_sealed || _pending != 0) return;
        var starts = _starts.ToArray();
        _starts.Clear();
        foreach (var start in starts) start();
    }
}
