using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shapes;
using Scriblucent.Behaviors;
using Scriblucent.Services;

namespace Scriblucent.Animations;

/// <summary>Extends a modal dialog's existing backdrop to the other app windows it disables.</summary>
internal static class BlockedWindowBackdrops
{
    private static readonly Dictionary<Window, int> CoveredOwners = new();
    private static readonly Dictionary<Window, Backdrop> Backdrops = new();

    internal static IDisposable Begin(Window dialog, OverlayEntranceBatch entrance)
    {
        dialog.Dispatcher.VerifyAccess();
        return new Scope(dialog, entrance);
    }

    private sealed class Scope : IDisposable
    {
        private readonly Window? _owner;
        private readonly List<Backdrop> _backdrops = new();
        private bool _disposed;

        internal Scope(Window dialog, OverlayEntranceBatch entrance)
        {
            _owner = dialog.Owner;
            // The dialog itself already paints this owner's backdrop. A nested dialog
            // must not add another copy underneath an existing owner-sized scrim.
            if (_owner is not null)
                CoveredOwners[_owner] = CoveredOwners.GetValueOrDefault(_owner) + 1;

            var windows = Application.Current is { } app
                ? app.Windows.Cast<Window>().ToArray() : OwnerChain(dialog).ToArray();
            foreach (var window in windows)
            {
                if (ReferenceEquals(window, dialog) || CoveredOwners.ContainsKey(window)
                    || !window.IsVisible || window.WindowState == WindowState.Minimized) continue;
                var handle = new WindowInteropHelper(window).Handle;
                // ShowDialog disables native HWNDs; FrameworkElement.IsEnabled remains true.
                if (handle == IntPtr.Zero || IsWindowEnabled(handle)) continue;
                if (!Backdrops.TryGetValue(window, out var backdrop))
                {
                    // Scriblucent's shared caption chrome provides this full-client grid.
                    if (window.Content is not Grid root) continue;
                    Backdrops[window] = backdrop = new Backdrop(window, root);
                }
                backdrop.Acquire(entrance);
                _backdrops.Add(backdrop);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var backdrop in _backdrops) backdrop.Release();
            _backdrops.Clear();
            if (_owner is not null && CoveredOwners.TryGetValue(_owner, out var count))
            {
                if (count == 1) CoveredOwners.Remove(_owner);
                else CoveredOwners[_owner] = count - 1;
            }
        }
    }

    private static IEnumerable<Window> OwnerChain(Window dialog)
    {
        for (var owner = dialog.Owner; owner is not null; owner = owner.Owner)
            yield return owner;
    }

    private sealed class Backdrop
    {
        private readonly Window _window;
        private readonly Grid _root;
        private readonly Grid _host;
        private readonly OverlayPanelAnimator _animator;
        private int _users;
        private bool _removed;

        internal Backdrop(Window window, Grid root)
        {
            _window = window; _root = root;
            _host = new Grid { Visibility = Visibility.Collapsed, Focusable = false };
            Panel.SetZIndex(_host, int.MaxValue);
            Grid.SetRowSpan(_host, Math.Max(1, root.RowDefinitions.Count));
            Grid.SetColumnSpan(_host, Math.Max(1, root.ColumnDefinitions.Count));
            var scrim = new Grid();
            RoundedClipBehavior.SetIsEnabled(scrim, true);
            RoundedClipBehavior.SetTopInset(scrim, 1);
            RoundedClipBehavior.SetCornerRadius(scrim, OverlayScrimRounding.WindowMatchRadius);
            var image = new Image { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            var dim = new Rectangle();
            dim.SetResourceReference(Shape.FillProperty, "OverlayScrim");
            scrim.Children.Add(image); scrim.Children.Add(dim);
            _host.Children.Add(scrim);
            // Reuse the exact dialog scrim and transition implementation; this window
            // has no foreground card, since the actual dialog lives above its owner.
            var surface = new Border { Width = 0, Height = 0, IsHitTestVisible = false };
            _host.Children.Add(surface);
            _animator = OverlayPanelAnimator.Bind(_host, dim, surface, image, scrim);
            root.Children.Add(_host);
            window.Closed += Closed;
        }

        internal void Acquire(OverlayEntranceBatch entrance)
        {
            if (_removed) return;
            if (++_users != 1) return;
            _animator.Show(entrance, backdropOwner: _window);
        }

        internal void Release()
        {
            if (_removed || _users == 0) return;
            if (--_users == 0) _animator.Hide(Remove);
        }

        private void Closed(object? sender, EventArgs e) => Remove();

        private void Remove()
        {
            if (_removed || _users > 0 && _window.IsVisible) return;
            _removed = true;
            _window.Closed -= Closed;
            _root.Children.Remove(_host);
            Backdrops.Remove(_window);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);
}
