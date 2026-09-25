using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace Scriblucent.ViewModels;

/// <summary>
/// Decodes note previews on the UI thread in short idle slices. <see cref="NoteCardViewModel.Content"/>
/// builds a WPF FlowDocument, so the work cannot move to the tile workers, and it must not run
/// inside a scroll or search frame.
/// </summary>
internal static class NotePreviewWarmQueue
{
    private const int SliceBudgetMs = 4;

    private static readonly Queue<NoteCardViewModel> Pending = new();
    private static readonly Dictionary<NoteCardViewModel, List<Waiter>> Callbacks = new();
    private static bool _pumpScheduled;
    private static int _generation;

    private sealed class Waiter
    {
        public int? Generation;
        public required Action Callback;
    }

    /// <summary>Drops search warms queued under the previous generation. Tile warms pass
    /// <c>null</c> and are not affected.</summary>
    public static int BumpGeneration() => ++_generation;

    public static void Enqueue(NoteCardViewModel note, Action? onReady = null, int? generation = null)
    {
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
            Pending.Enqueue(note);
        }

        if (onReady is not null)
            callbacks.Add(new Waiter { Generation = generation, Callback = onReady });

        Schedule();
    }

    private static void Schedule()
    {
        if (_pumpScheduled)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        _pumpScheduled = true;
        dispatcher.BeginInvoke(Pump, DispatcherPriority.ApplicationIdle);
    }

    private static void Pump()
    {
        _pumpScheduled = false;
        var budget = Stopwatch.StartNew();
        while (Pending.Count > 0 && budget.ElapsedMilliseconds < SliceBudgetMs)
        {
            var note = Pending.Dequeue();
            if (!Callbacks.Remove(note, out var callbacks))
                continue;

            var live = new List<Waiter>(callbacks.Count);
            foreach (var waiter in callbacks)
            {
                if (IsLive(waiter.Generation))
                    live.Add(waiter);
            }

            if (live.Count == 0)
                continue;

            note.WarmContentPreview();
            foreach (var waiter in live)
                waiter.Callback();
        }

        if (Pending.Count > 0)
            Schedule();
    }

    private static bool IsLive(int? generation)
        => generation is null || generation == _generation;
}
