using Scriblucent.Views;
using System.Windows;

namespace Scriblucent;

public static class ModernMessageBox
{
    public static MessageBoxResult Show(string messageBoxText)
    {
        return Show(messageBoxText, string.Empty, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
    }

    public static MessageBoxResult Show(string messageBoxText, string caption)
    {
        return Show(messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
    }

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button)
    {
        return Show(messageBoxText, caption, button, MessageBoxImage.None, MessageBoxResult.None);
    }

    public static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon)
    {
        return Show(messageBoxText, caption, button, icon, MessageBoxResult.None);
    }

    public static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult)
    {
        if (Application.Current is null)
            return MessageBox.Show(messageBoxText, caption, button, icon, defaultResult);

        var dialog = new ModernMessageDialog(messageBoxText, caption, button, icon, defaultResult);
        var owner = ResolveOwner();
        if (owner is not null)
            dialog.Owner = owner;

        dialog.ShowDialog();
        return dialog.SelectedResult;
    }

    private static Window? ResolveOwner()
    {
        if (Application.Current?.Windows is not { } windows)
            return null;

        foreach (Window window in windows)
        {
            if (window.IsActive && window.IsVisible)
                return window;
        }

        return Application.Current.MainWindow?.IsVisible == true
            ? Application.Current.MainWindow
            : null;
    }
}
