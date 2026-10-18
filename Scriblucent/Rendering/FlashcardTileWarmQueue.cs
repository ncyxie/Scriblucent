using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Scriblucent.Controls;

namespace Scriblucent.Rendering;

// One idle pump per dispatcher rather than one capture callback per card. A
// gesture cancels its hosts; settlement schedules them after the quiet period.
internal static class FlashcardTileWarmQueue
{
    private static readonly ConditionalWeakTable<Dispatcher, Pump> Pumps = new();
    internal static void Enqueue(DashboardTileHost host) => Pumps.GetValue(host.Dispatcher, d => new Pump(d)).Enqueue(host);
    internal static void Cancel(DashboardTileHost host)
    { if (Pumps.TryGetValue(host.Dispatcher, out var pump)) pump.Cancel(host); }

    private sealed class Pump
    {
        private readonly LinkedList<DashboardTileHost> _hosts = new();
        private readonly Dictionary<DashboardTileHost, LinkedListNode<DashboardTileHost>> _nodes = new();
        private readonly DispatcherTimer _timer;
        internal Pump(Dispatcher dispatcher)
        {
            _timer = new DispatcherTimer(DispatcherPriority.ContextIdle, dispatcher);
            _timer.Tick += Tick;
        }
        internal void Enqueue(DashboardTileHost host)
        {
            if (_nodes.ContainsKey(host)) return;
            _nodes.Add(host, _hosts.AddLast(host));
            _timer.Stop(); _timer.Interval = TimeSpan.FromMilliseconds(150); _timer.Start();
        }
        internal void Cancel(DashboardTileHost host)
        {
            if (_nodes.Remove(host, out var node)) _hosts.Remove(node);
            if (_hosts.Count == 0) _timer.Stop();
        }
        private void Tick(object? sender, EventArgs args)
        {
            if (_hosts.First is not { } node) { _timer.Stop(); return; }
            var host = node.Value; _hosts.RemoveFirst(); _nodes.Remove(host);
            if (host.WarmScrollTileSlice()) _nodes.Add(host, _hosts.AddLast(host));
            if (_hosts.Count == 0) _timer.Stop();
            else _timer.Interval = TimeSpan.FromMilliseconds(1);
        }
    }
}
