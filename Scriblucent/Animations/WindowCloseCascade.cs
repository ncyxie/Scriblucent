using System.Linq;
using System.Windows;

namespace Scriblucent.Animations;

internal static class WindowCloseCascade
{
    // Attempts to close every window currently owned by `owner`, giving each one's own Closing
    // handler a chance to prompt/cancel (a normal Window.Close() call, so all existing
    // unsaved-changes guards run exactly as they would for a direct user close). Returns false if
    // any refused to close (its own Closing set e.Cancel), so the caller can cancel its own close
    // in turn instead of tearing down the remaining tree via the OS destroy cascade.
    public static bool TryCloseOwnedWindows(Window owner)
    {
        foreach (var window in owner.OwnedWindows.Cast<Window>().ToArray())
        {
            window.Close();
            if (window.IsLoaded)
                return false;
        }

        return true;
    }
}
