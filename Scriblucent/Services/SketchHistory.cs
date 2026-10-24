using System.Windows.Ink;
using System.Windows.Media;
using Scriblucent.Models;

namespace Scriblucent.Services;

/// <summary>Gesture history retains vector references, not a bitmap or a full ink clone per pointer move.</summary>
internal sealed class SketchHistory
{
    private sealed record State(Stroke[] Strokes, Color? Background, long Revision);
    private readonly SketchDocument _document;
    private readonly List<State> _undo = new();
    private readonly Stack<State> _redo = new();
    private State? _pending;
    private long _revision, _nextRevision;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsChanged => _revision != 0;
    public SketchHistory(SketchDocument document) => _document = document;
    private State Capture() => new(_document.Strokes.ToArray(), _document.Background, _revision);
    public void Begin() => _pending ??= Capture();
    public bool Commit()
    {
        if (_pending is not { } before) return false;
        _pending = null;
        if (before.Background == _document.Background && before.Strokes.SequenceEqual(_document.Strokes)) return false;
        _undo.Add(before); if (_undo.Count > 200) _undo.RemoveAt(0);
        _redo.Clear(); _revision = ++_nextRevision; return true;
    }
    public void Undo()
    {
        Commit(); if (!CanUndo) return;
        _redo.Push(Capture()); var target = _undo[^1]; _undo.RemoveAt(_undo.Count - 1); Restore(target);
    }
    public void Redo()
    {
        Commit(); if (!CanRedo) return;
        _undo.Add(Capture()); Restore(_redo.Pop());
    }
    private void Restore(State state)
    {
        _document.Strokes.Clear(); _document.Strokes.Add(new StrokeCollection(state.Strokes));
        _document.Background = state.Background; _revision = state.Revision;
    }
}
