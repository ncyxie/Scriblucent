using Scriblucent.Animations;
using Scriblucent.Localization;
using System.Media;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

using Scriblucent.Controls;

namespace Scriblucent.Views;

public partial class ModernMessageDialog : Window
{
    private readonly OverlayDialogAnimator _overlayAnimator;
    private MessageBoxResult _pendingResult;
    private ModernDialogResult _pendingChoice;

    public string TitleText { get; }
    public string MessageText { get; }
    public string IconGlyph { get; }
    public Brush IconBackground { get; }
    public Brush IconBorder { get; }
    public Brush IconForeground { get; }
    public string PrimaryText { get; }
    public string NoText { get; }
    public string CancelText { get; }
    public Visibility PrimaryVisibility { get; }
    public Visibility NoVisibility { get; }
    public Visibility CancelVisibility { get; }
    public MessageBoxResult PrimaryResult { get; }
    public MessageBoxResult EscapeResult { get; }
    public MessageBoxResult SelectedResult { get; private set; }
    public ModernDialogResult SelectedChoice { get; private set; }

    private MessageBoxImage DialogImage { get; }
    private ModernDialogTone DialogTone { get; }
    private ModernDialogResult PrimaryChoice { get; }
    private ModernDialogResult SecondaryChoice { get; }
    private ModernDialogResult CancelChoice { get; }
    private ModernDialogResult EscapeChoice { get; }

    public ModernMessageDialog(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage image,
        MessageBoxResult defaultResult)
    {
        TitleText = string.IsNullOrWhiteSpace(caption)
            ? ResolveDefaultTitle(image)
            : caption;
        MessageText = messageBoxText ?? string.Empty;
        DialogImage = image;
        DialogTone = ResolveTone(image);

        (IconGlyph, IconBackground, IconBorder, IconForeground) = ResolveIcon(DialogTone);
        (PrimaryText, PrimaryResult, PrimaryVisibility, NoText, NoVisibility, CancelText, CancelVisibility, EscapeResult) =
            ResolveButtons(button);

        SelectedResult = ResolveInitialResult(button, defaultResult);
        SelectedChoice = ResultToChoice(SelectedResult);
        _pendingResult = SelectedResult;
        _pendingChoice = SelectedChoice;

        PrimaryChoice = ModernDialogResult.Primary;
        SecondaryChoice = ModernDialogResult.Secondary;
        CancelChoice = ModernDialogResult.Cancel;
        EscapeChoice = ResultToChoice(EscapeResult);

        InitializeComponent();
        _overlayAnimator = ((OverlayDialogShell)Content).BindAnimator(this);
        ApplyPrimaryButtonStyle(DialogTone);
        ConfigureButtonLayout();
        Scriblucent.Services.WindowThemeService.Register(this);
        DataContext = this;
        Loaded += ModernMessageDialog_Loaded;
    }

    public ModernMessageDialog(
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
        TitleText = title ?? string.Empty;
        MessageText = message ?? string.Empty;
        DialogTone = tone;
        DialogImage = ToneToImage(tone);

        (IconGlyph, IconBackground, IconBorder, IconForeground) = ResolveIcon(tone);

        PrimaryText = primaryText;
        PrimaryResult = MessageBoxResult.OK;
        PrimaryVisibility = Visibility.Visible;
        NoText = secondaryText ?? string.Empty;
        NoVisibility = string.IsNullOrWhiteSpace(secondaryText) ? Visibility.Collapsed : Visibility.Visible;
        CancelText = cancelText ?? string.Empty;
        CancelVisibility = string.IsNullOrWhiteSpace(cancelText) ? Visibility.Collapsed : Visibility.Visible;
        EscapeResult = CancelVisibility == Visibility.Visible
            ? MessageBoxResult.Cancel
            : NoVisibility == Visibility.Visible
                ? MessageBoxResult.No
                : MessageBoxResult.OK;

        var fallbackChoice = CancelVisibility == Visibility.Visible
            ? ModernDialogResult.Cancel
            : NoVisibility == Visibility.Visible
                ? ModernDialogResult.Secondary
                : ModernDialogResult.Primary;
        SelectedChoice = ResolveDefaultChoice(
            defaultChoice,
            NoVisibility == Visibility.Visible,
            CancelVisibility == Visibility.Visible,
            fallbackChoice);
        SelectedResult = ChoiceToResult(SelectedChoice);
        _pendingResult = SelectedResult;
        _pendingChoice = SelectedChoice;

        PrimaryChoice = ModernDialogResult.Primary;
        SecondaryChoice = ModernDialogResult.Secondary;
        CancelChoice = ModernDialogResult.Cancel;
        EscapeChoice = ResultToChoice(EscapeResult);

        InitializeComponent();
        _overlayAnimator = ((OverlayDialogShell)Content).BindAnimator(this);
        ApplyButtonStyles(
            primaryStyle ?? DefaultPrimaryButtonStyleForTone(tone),
            secondaryStyle,
            cancelStyle);
        ConfigureButtonLayout();
        Scriblucent.Services.WindowThemeService.Register(this);
        DataContext = this;
        Loaded += ModernMessageDialog_Loaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyOwnerBounds();
    }

    private void ModernMessageDialog_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyOwnerBounds();
        UiMotion.ConfigurePopupsForWindow(this);
        PlayDialogSound();
        _overlayAnimator.BeginOpen(() =>
        {
            FocusDefaultButton();
            ConfigureNativeDialogButtons();
        });
    }

    private void ApplyOwnerBounds()
    {
        OverlayDialogBoundsHelper.Apply(this);
    }

    private void FocusDefaultButton()
    {
        var target = SelectedChoice switch
        {
            ModernDialogResult.Cancel => CancelButton,
            ModernDialogResult.Secondary => NoButton,
            _ => PrimaryButton
        };

        target.Focus();
    }

    private void ConfigureNativeDialogButtons()
    {
        PrimaryButton.IsDefault = SelectedChoice == ModernDialogResult.Primary;
        NoButton.IsDefault = SelectedChoice == ModernDialogResult.Secondary;
        CancelButton.IsDefault = SelectedChoice == ModernDialogResult.Cancel;
    }

    private void PlayDialogSound()
    {
        if (DialogTone is ModernDialogTone.Danger or ModernDialogTone.Error)
        {
            SystemSounds.Hand.Play();
        }
        else if (DialogTone == ModernDialogTone.Warning)
        {
            SystemSounds.Exclamation.Play();
        }
        else if (DialogTone == ModernDialogTone.Question)
        {
            SystemSounds.Question.Play();
        }
        else if (DialogTone is ModernDialogTone.Info or ModernDialogTone.Success)
        {
            SystemSounds.Asterisk.Play();
        }
    }

    private static string ResolveDefaultTitle(MessageBoxImage image)
    {
        return ResolveTone(image) switch
        {
            ModernDialogTone.Error => LocalizationService.GetString("Error"),
            ModernDialogTone.Warning => "Warning",
            ModernDialogTone.Question => "Question",
            _ => "Scriblucent"
        };
    }

    private static ModernDialogTone ResolveTone(MessageBoxImage image)
    {
        if (image is MessageBoxImage.Error or MessageBoxImage.Hand or MessageBoxImage.Stop)
            return ModernDialogTone.Error;

        if (image is MessageBoxImage.Warning or MessageBoxImage.Exclamation)
            return ModernDialogTone.Warning;

        if (image == MessageBoxImage.Question)
            return ModernDialogTone.Question;

        return ModernDialogTone.Info;
    }

    private static MessageBoxImage ToneToImage(ModernDialogTone tone)
    {
        return tone switch
        {
            ModernDialogTone.Danger or ModernDialogTone.Error => MessageBoxImage.Error,
            ModernDialogTone.Warning => MessageBoxImage.Warning,
            ModernDialogTone.Question => MessageBoxImage.Question,
            ModernDialogTone.Info or ModernDialogTone.Success => MessageBoxImage.Information,
            _ => MessageBoxImage.None
        };
    }

    private static (string Glyph, Brush Background, Brush Border, Brush Foreground) ResolveIcon(ModernDialogTone tone)
    {
        return tone switch
        {
            ModernDialogTone.Success => (
                "✓",
                ResolveResourceBrush("AccentSuccessSubtle", "#E8F8F0"),
                ResolveResourceBrush("AccentSuccessSubtleBorder", "#B7E6CC"),
                ResolveResourceBrush("AccentSuccessSubtleForeground", "#096B3F")),
            ModernDialogTone.Danger or ModernDialogTone.Error => (
                "!",
                ResolveResourceBrush("AccentDangerSubtle", "#F6DEDE"),
                ResolveResourceBrush("AccentDangerSubtleBorder", "#D7A6A6"),
                ResolveResourceBrush("AccentDangerSubtleForeground", "#9F2D2D")),
            ModernDialogTone.Warning => (
                "!",
                ResolveResourceBrush("AccentWarningSubtle", "#FFF9E6"),
                ResolveResourceBrush("AccentWarningSubtleBorder", "#F59E0B"),
                ResolveResourceBrush("AccentWarningSubtleForeground", "#92400E")),
            ModernDialogTone.Question => (
                "?",
                ResolveResourceBrush("AccentPrimarySubtle", "#EEF0FE"),
                ResolveResourceBrush("AccentPrimary", "#435CEC"),
                ResolveResourceBrush("AccentPrimary", "#435CEC")),
            _ => (
                "i",
                ResolveResourceBrush("AccentPrimarySubtle", "#EEF0FE"),
                ResolveResourceBrush("AccentPrimary", "#435CEC"),
                ResolveResourceBrush("AccentPrimary", "#435CEC"))
        };
    }

    private static Brush ResolveResourceBrush(string key, string fallback)
    {
        return Application.Current?.TryFindResource(key) as Brush
            ?? CreateBrush(fallback);
    }

    private static Brush CreateBrush(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }

    private static (
        string PrimaryText,
        MessageBoxResult PrimaryResult,
        Visibility PrimaryVisibility,
        string NoText,
        Visibility NoVisibility,
        string CancelText,
        Visibility CancelVisibility,
        MessageBoxResult EscapeResult) ResolveButtons(MessageBoxButton button)
    {
        var ok = LocalizationService.GetString("Ok");
        var cancel = LocalizationService.GetString("Cancel");
        var yes = LocalizationService.GetString("InfoYes");
        var no = LocalizationService.GetString("InfoNo");

        return button switch
        {
            MessageBoxButton.OKCancel => (ok, MessageBoxResult.OK, Visibility.Visible, string.Empty, Visibility.Collapsed, cancel, Visibility.Visible, MessageBoxResult.Cancel),
            MessageBoxButton.YesNo => (yes, MessageBoxResult.Yes, Visibility.Visible, no, Visibility.Visible, string.Empty, Visibility.Collapsed, MessageBoxResult.No),
            MessageBoxButton.YesNoCancel => (yes, MessageBoxResult.Yes, Visibility.Visible, no, Visibility.Visible, cancel, Visibility.Visible, MessageBoxResult.Cancel),
            _ => (ok, MessageBoxResult.OK, Visibility.Visible, string.Empty, Visibility.Collapsed, string.Empty, Visibility.Collapsed, MessageBoxResult.OK)
        };
    }

    private static MessageBoxResult ResolveInitialResult(MessageBoxButton button, MessageBoxResult defaultResult)
    {
        if (defaultResult != MessageBoxResult.None)
            return defaultResult;

        return button switch
        {
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.OK
        };
    }

    private static ModernDialogResult ResultToChoice(MessageBoxResult result)
    {
        return result switch
        {
            MessageBoxResult.No => ModernDialogResult.Secondary,
            MessageBoxResult.Cancel => ModernDialogResult.Cancel,
            _ => ModernDialogResult.Primary
        };
    }

    private static MessageBoxResult ChoiceToResult(ModernDialogResult choice)
    {
        return choice switch
        {
            ModernDialogResult.Secondary => MessageBoxResult.No,
            ModernDialogResult.Cancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.OK
        };
    }

    private static ModernDialogResult ResolveDefaultChoice(
        ModernDialogResult? requestedChoice,
        bool hasSecondary,
        bool hasCancel,
        ModernDialogResult fallbackChoice)
    {
        if (!requestedChoice.HasValue)
            return fallbackChoice;

        if (requestedChoice == ModernDialogResult.Cancel && !hasCancel)
            return fallbackChoice;

        if (requestedChoice == ModernDialogResult.Secondary && !hasSecondary)
            return fallbackChoice;

        return requestedChoice.Value;
    }

    private static ModernDialogButtonStyle DefaultPrimaryButtonStyleForTone(ModernDialogTone tone)
    {
        return tone switch
        {
            ModernDialogTone.Danger or ModernDialogTone.Error => ModernDialogButtonStyle.Danger,
            ModernDialogTone.Warning => ModernDialogButtonStyle.Warning,
            _ => ModernDialogButtonStyle.Primary
        };
    }

    private void ApplyPrimaryButtonStyle(ModernDialogTone tone)
    {
        ApplyButtonStyles(DefaultPrimaryButtonStyleForTone(tone));
    }

    private void ApplyButtonStyles(
        ModernDialogButtonStyle primaryStyle,
        ModernDialogButtonStyle secondaryStyle = ModernDialogButtonStyle.Secondary,
        ModernDialogButtonStyle cancelStyle = ModernDialogButtonStyle.Secondary)
    {
        ApplyButtonStyle(PrimaryButton, primaryStyle);
        ApplyButtonStyle(NoButton, secondaryStyle);
        ApplyButtonStyle(CancelButton, cancelStyle);
    }

    private void ApplyButtonStyle(System.Windows.Controls.Button button, ModernDialogButtonStyle buttonStyle)
    {
        var styleKey = buttonStyle switch
        {
            ModernDialogButtonStyle.Danger => "DialogDangerButtonStyle",
            ModernDialogButtonStyle.Warning => "DialogWarningButtonStyle",
            ModernDialogButtonStyle.Primary => "DialogPrimaryButtonStyle",
            _ => "DialogSecondaryButtonStyle"
        };

        if (TryFindResource(styleKey) is Style style)
            button.Style = style;
    }

    private void ConfigureButtonLayout()
    {
        var visibleButtonCount =
            (CancelVisibility == Visibility.Visible ? 1 : 0) +
            (NoVisibility == Visibility.Visible ? 1 : 0) +
            (PrimaryVisibility == Visibility.Visible ? 1 : 0);
        var labelLength = CancelText.Length + NoText.Length + PrimaryText.Length;

        if (visibleButtonCount < 3 || labelLength <= 32)
            return;

        ButtonPanel.Orientation = System.Windows.Controls.Orientation.Vertical;
        ButtonPanel.HorizontalAlignment = HorizontalAlignment.Stretch;

        ConfigureStackedButton(
            CancelButton,
            CancelVisibility == Visibility.Visible && (NoVisibility == Visibility.Visible || PrimaryVisibility == Visibility.Visible));
        ConfigureStackedButton(
            NoButton,
            NoVisibility == Visibility.Visible && PrimaryVisibility == Visibility.Visible);
        ConfigureStackedButton(PrimaryButton, false);
    }

    private static void ConfigureStackedButton(System.Windows.Controls.Button button, bool addBottomMargin)
    {
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.MinWidth = 0;
        button.Margin = addBottomMargin
            ? new Thickness(0, 0, 0, 10)
            : new Thickness(0);
    }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        BeginCloseAnimation(PrimaryResult, PrimaryChoice);
    }

    private void NoButton_Click(object sender, RoutedEventArgs e)
    {
        BeginCloseAnimation(MessageBoxResult.No, SecondaryChoice);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        BeginCloseAnimation(MessageBoxResult.Cancel, CancelChoice);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape)
        {
            BeginCloseAnimation(EscapeResult, EscapeChoice);
            e.Handled = true;
        }
    }

    private void BeginCloseAnimation(MessageBoxResult result, ModernDialogResult choice)
    {
        _pendingResult = result;
        _pendingChoice = choice;

        _overlayAnimator.BeginClose(() =>
        {
            SelectedResult = _pendingResult;
            SelectedChoice = _pendingChoice;
            Close();
        });
    }
}