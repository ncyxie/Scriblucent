using System.Windows;
using System.Windows.Media;

namespace NoteCards.Animations;

/// <summary>
/// Freezes the maximized main window into a bitmap while child windows play open/close motion.
/// </summary>
internal static class ChildWindowOwnerMotion
{
    private static int _depth;
    private static CacheMode? _savedMainContentCacheMode;

    public static void TryBegin(Window childWindow)
    {
        if (childWindow.Owner is not MainWindow main || main.WindowState != WindowState.Maximized)
            return;

        if (_depth == 0)
            main.BeginChildWindowOwnerMotion(ref _savedMainContentCacheMode);

        _depth++;
    }

    public static void TryEnd(Window childWindow)
    {
        if (childWindow.Owner is not MainWindow main || main.WindowState != WindowState.Maximized)
            return;

        if (_depth <= 0)
            return;

        _depth--;
        if (_depth > 0)
            return;

        main.EndChildWindowOwnerMotion(_savedMainContentCacheMode);
        _savedMainContentCacheMode = null;
    }
}