using System.Diagnostics;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using Scriblucent.Animations;

namespace Scriblucent.Tests;

internal static class SidebarTestClock
{
    internal static Action<int> Pause(Border sidebar)
    {
        var table = typeof(SidebarResizeMotion).GetField("States", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        object?[] args = [sidebar, null]; table.GetType().GetMethod("TryGetValue")!.Invoke(table, args);
        var state = args[1]!;
        ((Stopwatch)state.GetType().GetField("_clock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!).Stop();
        ((DispatcherTimer)state.GetType().GetField("_frameWake", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!).Stop();
        var advance = state.GetType().GetMethod("Advance", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return ms => advance.Invoke(state, [(double)ms]);
    }
}
