using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Scriblucent.Views;

internal static class OverlayDialogBoundsHelper
{
    public static void Apply(Window dialog)
    {
        var owner = dialog.Owner;
        if (owner is null)
            return;

        // Overlay dialogs must cover the owner; SizeToContent fights that and skews the scrim.
        dialog.SizeToContent = SizeToContent.Manual;

        owner.UpdateLayout();

        var ownerHandle = new WindowInteropHelper(owner).Handle;
        if (ownerHandle != IntPtr.Zero && TryGetOwnerClientScreenBounds(ownerHandle, out var clientRect))
        {
            var transformFromDevice = ResolveTransformFromDevice(ownerHandle, owner);
            var topLeft = transformFromDevice.Transform(new Point(clientRect.Left, clientRect.Top));
            var bottomRight = transformFromDevice.Transform(new Point(clientRect.Right, clientRect.Bottom));

            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Left = topLeft.X;
            dialog.Top = topLeft.Y;
            dialog.Width = Math.Max(0, bottomRight.X - topLeft.X);
            dialog.Height = Math.Max(0, bottomRight.Y - topLeft.Y);
            return;
        }

        ApplyFromOwnerClientDipBounds(dialog);
    }

    private static bool TryGetOwnerClientScreenBounds(IntPtr ownerHandle, out RECT screenRect)
    {
        screenRect = default;
        if (!GetClientRect(ownerHandle, out var clientRect))
            return false;

        var topLeft = new POINT { X = clientRect.Left, Y = clientRect.Top };
        var bottomRight = new POINT { X = clientRect.Right, Y = clientRect.Bottom };
        if (!ClientToScreen(ownerHandle, ref topLeft) || !ClientToScreen(ownerHandle, ref bottomRight))
            return false;

        screenRect = new RECT
        {
            Left = topLeft.X,
            Top = topLeft.Y,
            Right = bottomRight.X,
            Bottom = bottomRight.Y
        };
        return true;
    }

    private static void ApplyFromOwnerClientDipBounds(Window dialog)
    {
        var owner = dialog.Owner;
        if (owner is null)
            return;

        var width = owner.ActualWidth > 0 ? owner.ActualWidth : owner.Width;
        var height = owner.ActualHeight > 0 ? owner.ActualHeight : owner.Height;
        if (double.IsNaN(width) || width <= 0 || double.IsNaN(height) || height <= 0)
            return;

        var transformFromDevice = ResolveTransformFromDevice(IntPtr.Zero, owner);
        var topLeft = transformFromDevice.Transform(owner.PointToScreen(new Point(0, 0)));
        var bottomRight = transformFromDevice.Transform(owner.PointToScreen(new Point(width, height)));

        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = topLeft.X;
        dialog.Top = topLeft.Y;
        dialog.Width = Math.Max(0, bottomRight.X - topLeft.X);
        dialog.Height = Math.Max(0, bottomRight.Y - topLeft.Y);
    }

    private static Matrix ResolveTransformFromDevice(IntPtr ownerHandle, Window owner)
    {
        if (ownerHandle != IntPtr.Zero
            && HwndSource.FromHwnd(ownerHandle)?.CompositionTarget is { } hwndTarget)
        {
            return hwndTarget.TransformFromDevice;
        }

        if (PresentationSource.FromVisual(owner)?.CompositionTarget is { } visualTarget)
            return visualTarget.TransformFromDevice;

        return Matrix.Identity;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
