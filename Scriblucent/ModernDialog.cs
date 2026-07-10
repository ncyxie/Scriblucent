using Scriblucent.Localization;
using Scriblucent.Views;
using System.Windows;

namespace Scriblucent;

public enum ModernDialogTone
{
    Info,
    Success,
    Warning,
    Danger,
    Question,
    Error
}

public enum ModernDialogResult
{
    None,
    Primary,
    Secondary,
    Cancel
}

public enum ModernDialogButtonStyle
{
    Primary,
    Secondary,
    Warning,
    Danger
}

public static class ModernDialog
{
    public static ModernDialogResult Show(
        Window? owner,
        string title,
        string message,
        ModernDialogTone tone,
        string primaryText,
        string? cancelText = null,
        string? secondaryText = null,
        ModernDialogButtonStyle? primaryStyle = null,
        ModernDialogButtonStyle secondaryStyle = ModernDialogButtonStyle.Secondary,
        ModernDialogButtonStyle cancelStyle = ModernDialogButtonStyle.Secondary,
        ModernDialogResult? defaultChoice = null)
    {
        var dialog = new ModernMessageDialog(
            title,
            message,
            tone,
            primaryText,
            cancelText,
            secondaryText,
            primaryStyle,
            secondaryStyle,
            cancelStyle,
            defaultChoice);
        var resolvedOwner = owner ?? ResolveOwner();
        if (resolvedOwner is not null)
            dialog.Owner = resolvedOwner;

        dialog.ShowDialog();
        ActivateOwner(resolvedOwner);
        return dialog.SelectedChoice;
    }

    private static void ActivateOwner(Window? owner)
    {
        var target = owner is { IsVisible: true } ? owner : Application.Current?.MainWindow;
        if (target is null)
            return;

        try
        {
            target.Activate();
        }
        catch
        {
            // Owner may be closing; ignore.
        }
    }

    public static void ShowInfo(Window? owner, string title, string message)
    {
        Show(owner, title, message, ModernDialogTone.Info, LocalizationService.GetString("Ok"));
    }

    public static void ShowSuccess(Window? owner, string title, string message)
    {
        Show(owner, title, message, ModernDialogTone.Success, LocalizationService.GetString("Ok"));
    }

    public static void ShowError(Window? owner, string title, string message)
    {
        Show(owner, title, message, ModernDialogTone.Error, LocalizationService.GetString("Ok"));
    }

    public static bool ConfirmDanger(
        Window? owner,
        string title,
        string message,
        string primaryText,
        string? cancelText = null)
    {
        return Show(
            owner,
            title,
            message,
            ModernDialogTone.Danger,
            primaryText,
            cancelText ?? LocalizationService.GetString("Cancel")) == ModernDialogResult.Primary;
    }

    public static bool ConfirmWarning(
        Window? owner,
        string title,
        string message,
        string primaryText,
        string? cancelText = null)
    {
        return Show(
            owner,
            title,
            message,
            ModernDialogTone.Warning,
            primaryText,
            cancelText ?? LocalizationService.GetString("Cancel")) == ModernDialogResult.Primary;
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
