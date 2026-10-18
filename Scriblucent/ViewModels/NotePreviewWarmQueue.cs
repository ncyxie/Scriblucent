using System.Diagnostics;
using System.Windows.Threading;
using Scriblucent.Rendering;

namespace Scriblucent.ViewModels;

/// <summary>
/// Decodes note previews on the UI thread in short idle slices. <see cref="NoteCardViewModel.Content"/>
/// builds a WPF FlowDocument, so the work cannot move to the tile workers, and it must not run
/// inside a scroll or search frame.
/// </summary>
internal static class NotePreviewWarmQueue
{
    private const int SliceBudgetMs = 4;

    private static readonly LinkedList<NoteCardViewModel> Pending = new();
    private static readonly Dictionary<NoteCardViewModel, List<Waiter>> Callbacks = new();
    private static readonly Dictionary<NoteCardViewModel, LinkedListNode<NoteCardViewModel>> Nodes = new();
    private static bool _pumpScheduled;
    private static int _generation;

    private sealed class Waiter
    {
        public int? Generation;
        public required Action Callback;
        public CardTileRequestLease? Lease;
    }

    /// <summary>Drops search warms queued under the previous generation. Tile warms pass
    /// <c>null</c> and are not affected.</summary>
    // Typing must not synchronously scan a large previous search. Its stale waiters
    // are pruned by the existing idle budget without decoding their documents.
    public static int BumpGeneration() => ++_generation;

    public static void Enqueue(NoteCardViewModel note, Action? onReady = null, int? generation = null,
        CardTileRequestLease? lease = null)
    {
        if (lease?.IsDisposed == true || !IsLive(generation) || onReady is null) return;
        if (note.IsContentPreviewCached)
        {
            if (onReady is not null && IsLive(generation))
                onReady();
            return;
        }

        if (!Callbacks.TryGetValue(note, out var callbacks))
        {
            callbacks = new List<Waiter>();
            Callbacks[note] = callbacks;
            Nodes[note] = Pending.AddLast(note);
        }

        var waiter = new Waiter { Generation = generation, Callback = onReady, Lease = lease };
        callbacks.Add(waiter);
        lease?.Attach(() => Remove(note, waiter));

        Schedule();
    }

    internal static int PendingCount => Pending.Count;

    private static void Remove(NoteCardViewModel note, Waiter waiter)
    {
        waiter.Lease?.Detach();
        if (!Callbacks.TryGetValue(note, out var waiters)) return;
        waiters.Remove(waiter);
        if (waiters.Count != 0) return;
        Callbacks.Remove(note);
        if (Nodes.Remove(note, out var node)) Pending.Remove(node);
    }

    private static void Schedule()
    {
        if (_pumpScheduled)
            return;

        var dispatcher = Dispatcher.CurrentDispatcher;

        _pumpScheduled = true;
        dispatcher.BeginInvoke(Pump, DispatcherPriority.ApplicationIdle);
    }

    private static void Pump()
    {
        _pumpScheduled = false;
        var budget = Stopwatch.StartNew();
        while (Pending.Count > 0 && budget.ElapsedMilliseconds < SliceBudgetMs)
        {
            var note = Pending.First!.Value;
            Pending.RemoveFirst(); Nodes.Remove(note);
            if (!Callbacks.Remove(note, out var callbacks))
                continue;

            var live = new List<Waiter>(callbacks.Count);
            foreach (var waiter in callbacks)
            {
                waiter.Lease?.Detach();
                if (IsLive(waiter.Generation) && waiter.Lease?.IsDisposed != true)
                    live.Add(waiter);
            }

            if (live.Count == 0)
                continue;

            note.WarmContentPreview();
            foreach (var waiter in live)
            {
                if (IsLive(waiter.Generation) && waiter.Lease?.IsDisposed != true) waiter.Callback();
            }
        }

        if (Pending.Count > 0)
            Schedule();
    }

    private static bool IsLive(int? generation)
        => generation is null || generation == _generation;
}
