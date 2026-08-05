using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Scriblucent.Views;

namespace Scriblucent.Services;

/// <summary>
/// How custom caption chrome is applied.
/// </summary>
internal enum ShellChromeKind
{
    /// <summary>No custom caption.</summary>
    None = 0,
    /// <summary>
    /// Main shell: SingleBorderWindow + WM_NCCALCSIZE so DWM animations keep working.
    /// </summary>
    NativeAnimated = 1,
    /// <summary>
    /// Feature / modal windows: same themed caption + NC hook as the main shell.
    /// Uses SingleBorderWindow (not WindowChrome) so DWM open/min/max animations work;
    /// init avoids the style-fighting loop that froze nested ShowDialog.
    /// </summary>
    Client = 2
}

/// <summary>
/// App-colored caption chrome with native DWM open / close / maximize / minimize animations.
/// <para>
/// Contract: keep <see cref="WindowStyle.SingleBorderWindow"/> (WS_CAPTION | WS_THICKFRAME) for DWM
/// animations, then handle <c>WM_NCCALCSIZE</c> so the OS title bar is not drawn. Restored windows
/// inset L/R/B by the resize frame (top stays 0 for the custom caption). Maximized windows inset
/// all four sides so the client sits in the visible work area while the HWND keeps normal overscan.
/// </para>
/// </summary>
internal static class ShellWindowChrome
{
    public const double CaptionHeight = 36;
    private const double CaptionButtonWidthDip = 46;
    private const int CaptionButtonCount = 3;
    private const double InactiveCaptionOpacity = 0.55;

    private const int WmNcCalcSize = 0x0083;
    private const int WmNcHitTest = 0x0084;

    private const int HtClient = 1;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    private const int GwlStyle = -16;
    private const int WsCaption = 0x00C00000;
    private const int WsSysMenu = 0x00080000;
    private const int WsThickFrame = 0x00040000;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;
    private const int AnimationStyleBits = WsCaption | WsSysMenu | WsThickFrame | WsMinimizeBox | WsMaximizeBox;

    private const int SmCxSizeFrame = 32;
    private const int SmCxPaddedBorder = 92;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private static readonly SolidColorBrush CloseHoverBrush = CreateFrozenBrush(0xE8, 0x11, 0x23);
    private static readonly SolidColorBrush ClosePressedBrush = CreateFrozenBrush(0xF1, 0x70, 0x7A);

    private static readonly DependencyProperty IsAttachedProperty =
        DependencyProperty.RegisterAttached("IsAttached", typeof(bool), typeof(ShellWindowChrome), new PropertyMetadata(false));

    private static readonly DependencyProperty ChromeKindProperty =
        DependencyProperty.RegisterAttached("ChromeKind", typeof(ShellChromeKind), typeof(ShellWindowChrome), new PropertyMetadata(ShellChromeKind.None));

    private static readonly DependencyProperty CaptionHostProperty =
        DependencyProperty.RegisterAttached("CaptionHost", typeof(FrameworkElement), typeof(ShellWindowChrome), new PropertyMetadata(null));

    private static readonly DependencyProperty RootHostProperty =
        DependencyProperty.RegisterAttached("RootHost", typeof(FrameworkElement), typeof(ShellWindowChrome), new PropertyMetadata(null));

    private static readonly DependencyProperty TitleTextProperty =
        DependencyProperty.RegisterAttached("TitleText", typeof(TextBlock), typeof(ShellWindowChrome), new PropertyMetadata(null));

    private static readonly DependencyProperty CaptionButtonsProperty =
        DependencyProperty.RegisterAttached("CaptionButtons", typeof(UIElement), typeof(ShellWindowChrome), new PropertyMetadata(null));

    private static readonly DependencyProperty MaximizeGlyphProperty =
        DependencyProperty.RegisterAttached("MaximizeGlyph", typeof(TextBlock), typeof(ShellWindowChrome), new PropertyMetadata(null));

    private static readonly DependencyProperty IsFullscreenSuspendedProperty =
        DependencyProperty.RegisterAttached("IsFullscreenSuspended", typeof(bool), typeof(ShellWindowChrome), new PropertyMetadata(false));

    private static readonly DependencyProperty HookInstalledProperty =
        DependencyProperty.RegisterAttached("HookInstalled", typeof(bool), typeof(ShellWindowChrome), new PropertyMetadata(false));

    private static readonly DependencyProperty SavedResizeModeProperty =
        DependencyProperty.RegisterAttached("SavedResizeMode", typeof(ResizeMode), typeof(ShellWindowChrome), new PropertyMetadata(ResizeMode.CanResize));

    public static bool IsFullscreenSuspended(Window window)
        => (bool)window.GetValue(IsFullscreenSuspendedProperty);

    public static bool IsAttached(Window window)
        => window is not null && (bool)window.GetValue(IsAttachedProperty);

    public static ShellChromeKind GetChromeKind(Window window)
        => window is null ? ShellChromeKind.None : (ShellChromeKind)window.GetValue(ChromeKindProperty);

    /// <summary>
    /// Caption/root brush: Main shell uses Sidebar; feature Client chrome matches FeatureFrameBackground.
    /// </summary>
    public static string GetShellFrameBackgroundKey(Window window)
        => GetChromeKind(window) == ShellChromeKind.Client
            ? "FeatureFrameBackground"
            : "SidebarBackground";


    /// <summary>
    /// Removes the caption host (if any) and returns the window body for reparenting.
    /// Used when a <see cref="Window"/> is constructed only to host content inside another window
    /// (e.g. note editor tabs) and must not keep a second caption bar.
    /// </summary>
    public static UIElement? DetachContent(Window window)
    {
        if (window is null)
            return null;

        WindowChrome.SetWindowChrome(window, null);

        if (window.GetValue(RootHostProperty) is not Grid root
            || window.GetValue(CaptionHostProperty) is not FrameworkElement caption)
        {
            var plain = window.Content as UIElement;
            window.Content = null;
            window.SetValue(IsAttachedProperty, false);
            window.SetValue(ChromeKindProperty, ShellChromeKind.None);
            return plain;
        }

        UIElement? body = null;
        for (var i = root.Children.Count - 1; i >= 0; i--)
        {
            var child = root.Children[i];
            if (ReferenceEquals(child, caption))
                continue;
            if (child is FrameworkElement fe && IsShellOverlay(fe))
                continue;

            body = child;
            root.Children.RemoveAt(i);
            break;
        }

        window.Content = null;
        window.ClearValue(CaptionHostProperty);
        window.ClearValue(RootHostProperty);
        window.ClearValue(TitleTextProperty);
        window.ClearValue(CaptionButtonsProperty);
        window.ClearValue(MaximizeGlyphProperty);
        window.SetValue(IsAttachedProperty, false);
        window.SetValue(ChromeKindProperty, ShellChromeKind.None);
        return body;
    }

    public static void TryAttach(Window window, ShellChromeKind kind = ShellChromeKind.NativeAnimated)
    {
        if (window is null || window.AllowsTransparency || kind == ShellChromeKind.None)
            return;

        if ((bool)window.GetValue(IsAttachedProperty))
        {
            if (!IsFullscreenSuspended(window))
                ApplyShellFrame(window);
            return;
        }

        window.SetValue(ChromeKindProperty, kind);

        if (window.Content is UIElement)
            InstallCaptionHost(window);
        ApplyShellFrame(window);

        window.StateChanged -= OnWindowStateChanged;
        window.StateChanged += OnWindowStateChanged;
        window.Activated -= OnWindowActivationChanged;
        window.Activated += OnWindowActivationChanged;
        window.Deactivated -= OnWindowActivationChanged;
        window.Deactivated += OnWindowActivationChanged;

        if (kind == ShellChromeKind.NativeAnimated || kind == ShellChromeKind.Client)
        {
            window.SourceInitialized -= OnWindowSourceInitialized;
            window.SourceInitialized += OnWindowSourceInitialized;

            if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
                OnWindowSourceInitialized(window, EventArgs.Empty);
        }

        window.SetValue(IsAttachedProperty, true);
    }

    public static void SuspendForFullscreen(Window window)
    {
        if (window is null)
            return;

        window.SetValue(IsFullscreenSuspendedProperty, true);
        window.SetValue(SavedResizeModeProperty, window.ResizeMode);
        if (window.GetValue(CaptionHostProperty) is FrameworkElement caption)
            caption.Visibility = Visibility.Collapsed;
        if (window.GetValue(RootHostProperty) is FrameworkElement root)
            root.Margin = new Thickness(0);

        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        window.WindowState = WindowState.Maximized;
    }

    public static void ResumeFromFullscreen(Window window)
    {
        if (window is null)
            return;

        window.SetValue(IsFullscreenSuspendedProperty, false);
        window.ResizeMode = (ResizeMode)window.GetValue(SavedResizeModeProperty);
        window.WindowState = WindowState.Normal;

        if (window.GetValue(CaptionHostProperty) is FrameworkElement caption)
            caption.Visibility = Visibility.Visible;

        if (!IsAttached(window))
        {
            window.WindowStyle = WindowStyle.SingleBorderWindow;
            return;
        }

        ApplyShellFrame(window);
        EnsureAnimationStyles(window);
        ScheduleForceFrameChanged(window);
        UpdateMaximizeGlyph(window);
        UpdateCaptionActiveState(window);
    }

    private static void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window)
            return;

        window.SourceInitialized -= OnWindowSourceInitialized;
        if (GetChromeKind(window) is not (ShellChromeKind.NativeAnimated or ShellChromeKind.Client))
            return;

        InstallNcHook(window);
        // Caption style bits must be present before the first show, or DWM skips the
        // built-in open animation (WindowStyle.None / late SetWindowLong looks like a pop-in).
        EnsureAnimationStyles(window);
        // MSDN: SWP_FRAMECHANGED after replacing the standard frame so the first paint
        // uses our WM_NCCALCSIZE (not a remmaximize workaround).
        ScheduleForceFrameChanged(window);
    }

    private static void ScheduleForceFrameChanged(Window window)
    {
        var priority = GetChromeKind(window) == ShellChromeKind.NativeAnimated
            ? System.Windows.Threading.DispatcherPriority.Loaded
            : System.Windows.Threading.DispatcherPriority.Input;

        window.Dispatcher.BeginInvoke(
            () =>
            {
                if (!IsAttached(window) || IsFullscreenSuspended(window))
                    return;

                EnsureAnimationStyles(window);
                ForceFrameChanged(window);
                UpdateMaximizeGlyph(window);
            },
            priority);
    }

    private static void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (sender is not Window window || IsFullscreenSuspended(window))
            return;

        UpdateMaximizeGlyph(window);
        // WPF may clear caption style bits on max/restore — put them back once, do not
        // fight continuous WM_STYLECHANGING (that loop froze secondary windows).
        EnsureAnimationStyles(window);
        if (window.WindowState == WindowState.Maximized)
            ForceFrameChanged(window);

        var isDarkMode = string.Equals(ThemeManager.CurrentTheme, "Dark", StringComparison.OrdinalIgnoreCase);
        ApplyCaptionFrameBorder(window, isDarkMode);
    }

    private static void OnWindowActivationChanged(object? sender, EventArgs e)
    {
        if (sender is Window window)
            UpdateCaptionActiveState(window);
    }

    /// <summary>
    /// Keeps the caption top hairline in sync with theme / OS frame.
    /// Maximized: match the title-bar fill (no visible OS frame edge).
    /// Win10 light restored: system window-frame color.
    /// Dark / Win11 restored: theme BorderColor.
    /// </summary>
    internal static void ApplyCaptionFrameBorder(Window window, bool isDarkMode)
    {
        if (window.GetValue(CaptionHostProperty) is Border caption)
            ApplyCaptionFrameBorder(window, caption, isDarkMode);
    }

    private static void ApplyCaptionFrameBorder(Window window, Border caption, bool isDarkMode)
    {
        if (window.WindowState == WindowState.Maximized)
        {
            caption.SetResourceReference(Border.BorderBrushProperty, GetShellFrameBackgroundKey(window));
            return;
        }

        var useThemeBorder = isDarkMode || Environment.OSVersion.Version.Build >= 22000;
        if (useThemeBorder)
        {
            caption.SetResourceReference(Border.BorderBrushProperty, "BorderColor");
            return;
        }

        // Win10 light: match the untinted OS frame; keep WindowFrame when unfocused so the
        // hairline does not wash out to InactiveBorderColor.
        var brush = new SolidColorBrush(SystemColors.WindowFrameColor);
        if (brush.CanFreeze)
            brush.Freeze();
        caption.BorderBrush = brush;
    }

    /// <summary>
    /// Sharp 1px top line for full-bleed overlays so the frame edge stays synced while the
    /// caption (and its hairline) are covered by the blurred scrim.
    /// </summary>
    /// <param name="frameActive">
    /// Whether the window whose OS L/R/B are visible is active. Overlay dialogs cover an inactive
    /// owner — pass the owner's IsActive so Win10 light matches InactiveBorderColor.
    /// </param>
    /// <param name="frameWindow">Window whose chrome the hairline should match (usually the owner).</param>
    internal static void ApplyOverlayFrameHairline(
        Border hairline,
        bool isDarkMode,
        bool frameActive = true,
        Window? frameWindow = null)
    {
        hairline.Height = 1;
        hairline.BorderThickness = new Thickness(0);
        hairline.SnapsToDevicePixels = true;
        hairline.IsHitTestVisible = false;
        hairline.VerticalAlignment = VerticalAlignment.Top;
        hairline.HorizontalAlignment = HorizontalAlignment.Stretch;

        if (frameWindow?.WindowState == WindowState.Maximized)
        {
            hairline.SetResourceReference(
                Border.BackgroundProperty,
                GetShellFrameBackgroundKey(frameWindow));
            return;
        }

        var useThemeBorder = isDarkMode || Environment.OSVersion.Version.Build >= 22000;
        if (useThemeBorder)
        {
            hairline.SetResourceReference(Border.BackgroundProperty, "BorderColor");
            return;
        }

        // Win10 light: match the live OS frame (active vs inactive).
        var color = frameActive
            ? SystemColors.WindowFrameColor
            : SystemColors.InactiveBorderColor;
        var brush = new SolidColorBrush(color);
        if (brush.CanFreeze)
            brush.Freeze();
        hairline.Background = brush;
    }

    private static void ApplyShellFrame(Window window)
    {
        if (IsFullscreenSuspended(window))
            return;

        // Never use WPF WindowChrome — it strips the Win32 caption bits DWM needs.
        WindowChrome.SetWindowChrome(window, null);
        // SingleBorder from the start so WS_CAPTION exists before first paint (open animation).
        window.WindowStyle = WindowStyle.SingleBorderWindow;
        if (window.ResizeMode == ResizeMode.NoResize)
            window.ResizeMode = ResizeMode.CanResize;
    }

    private static void EnsureAnimationStyles(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || IsFullscreenSuspended(window))
            return;

        var style = GetWindowLong(handle, GwlStyle);
        var next = style | AnimationStyleBits;
        if (next != style)
            SetWindowLong(handle, GwlStyle, next);
    }

    private static void ForceFrameChanged(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        SetWindowPos(
            handle,
            IntPtr.Zero,
            0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private static void InstallCaptionHost(Window window)
    {
        if (window.GetValue(CaptionHostProperty) is not null)
            return;
        if (window.Content is not UIElement content)
            return;

        window.Content = null;

        var root = new Grid();
        root.SetResourceReference(Panel.BackgroundProperty, GetShellFrameBackgroundKey(window));
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CaptionHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var caption = CreateCaptionBar(window);
        Grid.SetRow(caption, 0);
        root.Children.Add(caption);

        if (content is FrameworkElement frameworkContent)
            Grid.SetRow(frameworkContent, 1);
        root.Children.Add(content);
        PromoteShellOverlays(root, content);

        window.Content = root;
        window.SetValue(CaptionHostProperty, caption);
        window.SetValue(RootHostProperty, root);
        UpdateMaximizeGlyph(window);
        UpdateCaptionActiveState(window);
    }

    private static void PromoteShellOverlays(Grid root, UIElement content)
    {
        if (content is not Panel panel)
            return;

        for (var i = panel.Children.Count - 1; i >= 0; i--)
        {
            if (panel.Children[i] is not FrameworkElement overlay || !IsShellOverlay(overlay))
                continue;

            panel.Children.RemoveAt(i);
            Grid.SetRow(overlay, 0);
            Grid.SetRowSpan(overlay, 2);
            Panel.SetZIndex(overlay, 2000 + root.Children.Count);
            root.Children.Add(overlay);
        }
    }

    private static bool IsShellOverlay(FrameworkElement element)
        => element is SettingsPanel or AboutPanel or NoteSchedulePanel or TrashPanel;

    private static Border CreateCaptionBar(Window window)
    {
        var caption = new Border
        {
            Height = CaptionHeight,
            SnapsToDevicePixels = true,
            Cursor = Cursors.Arrow,
            // Top hairline only — color applied by ApplyCaptionFrameBorder (theme / OS frame).
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        caption.SetResourceReference(Border.BackgroundProperty, GetShellFrameBackgroundKey(window));
        ApplyCaptionFrameBorder(
            window,
            caption,
            string.Equals(ThemeManager.CurrentTheme, "Dark", StringComparison.OrdinalIgnoreCase));
        caption.MouseLeftButtonDown += (_, e) => OnCaptionMouseLeftButtonDown(window, e);
        caption.MouseRightButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is not null)
                return;
            SystemCommands.ShowSystemMenu(window, window.PointToScreen(e.GetPosition(window)));
            e.Handled = true;
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Margin = new Thickness(14, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = TypeScale.Px(TypeScale.Caption, 12),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextColor");
        title.SetResourceReference(TextBlock.FontFamilyProperty, "FontFamily.UI");
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(Window.Title)) { Source = window });
        Grid.SetColumn(title, 0);
        grid.Children.Add(title);
        window.SetValue(TitleTextProperty, title);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(buttons, 1);
        window.SetValue(CaptionButtonsProperty, buttons);

        buttons.Children.Add(CreateCaptionButton(window, "\uE921", "Minimize",
            (_, _) => SystemCommands.MinimizeWindow(window)));

        var maximizeGlyph = new TextBlock();
        buttons.Children.Add(CreateCaptionButton(window, maximizeGlyph, "Maximize", (_, _) =>
        {
            if (window.WindowState == WindowState.Maximized)
                SystemCommands.RestoreWindow(window);
            else
                SystemCommands.MaximizeWindow(window);
        }));
        window.SetValue(MaximizeGlyphProperty, maximizeGlyph);

        buttons.Children.Add(CreateCaptionButton(window, "\uE8BB", "Close",
            (_, _) => SystemCommands.CloseWindow(window), isClose: true));

        grid.Children.Add(buttons);
        caption.Child = grid;
        return caption;
    }

    private static void OnCaptionMouseLeftButtonDown(Window window, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is not null)
            return;

        if (e.ClickCount == 2)
        {
            if (window.WindowState == WindowState.Maximized)
                SystemCommands.RestoreWindow(window);
            else
                SystemCommands.MaximizeWindow(window);
            e.Handled = true;
            return;
        }

        if (window.WindowState == WindowState.Maximized)
            return;

        try
        {
            window.DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove throws if the mouse button is no longer down.
        }
    }

    private static Button CreateCaptionButton(Window window, string glyph, string name, RoutedEventHandler click, bool isClose = false)
        => CreateCaptionButton(window, new TextBlock { Text = glyph }, name, click, isClose);

    private static Button CreateCaptionButton(Window window, TextBlock glyphBlock, string name, RoutedEventHandler click, bool isClose = false)
    {
        glyphBlock.FontFamily = new FontFamily("Segoe MDL2 Assets");
        glyphBlock.FontSize = TypeScale.Px(TypeScale.Chip, 10);
        glyphBlock.HorizontalAlignment = HorizontalAlignment.Center;
        glyphBlock.VerticalAlignment = VerticalAlignment.Center;
        glyphBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextColor");

        var chrome = new Border { Background = Brushes.Transparent, Child = glyphBlock, SnapsToDevicePixels = true };
        var button = new Button
        {
            Width = CaptionButtonWidthDip,
            Height = CaptionHeight,
            Content = chrome,
            Focusable = false,
            ToolTip = name,
            Cursor = Cursors.Arrow,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0)
        };

        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        template.VisualTree = presenter;
        button.Template = template;
        button.Click += click;
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);

        button.MouseEnter += (_, _) =>
        {
            glyphBlock.Opacity = 1;
            if (isClose)
            {
                chrome.Background = CloseHoverBrush;
                glyphBlock.Foreground = Brushes.White;
            }
            else if (window.TryFindResource("HoverBackground") is Brush hover)
                chrome.Background = hover;
        };
        button.MouseLeave += (_, _) =>
        {
            chrome.Background = Brushes.Transparent;
            glyphBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextColor");
            glyphBlock.Opacity = window.IsActive ? 1 : InactiveCaptionOpacity;
        };
        button.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (isClose)
            {
                chrome.Background = ClosePressedBrush;
                glyphBlock.Foreground = Brushes.White;
            }
            else if (window.TryFindResource("SelectionBackground") is Brush pressed)
                chrome.Background = pressed;
        };

        return button;
    }

    private static void UpdateMaximizeGlyph(Window window)
    {
        if (window.GetValue(MaximizeGlyphProperty) is not TextBlock glyph)
            return;

        var maximized = window.WindowState == WindowState.Maximized;
        glyph.Text = maximized ? "\uE923" : "\uE922";
        if (glyph.Parent is Border { Parent: Button button })
            button.ToolTip = maximized ? "Restore" : "Maximize";
    }

    private static void UpdateCaptionActiveState(Window window)
    {
        var opacity = window.IsActive ? 1.0 : InactiveCaptionOpacity;
        if (window.GetValue(TitleTextProperty) is TextBlock title)
            title.Opacity = opacity;

        if (window.GetValue(CaptionButtonsProperty) is not Panel buttons)
            return;

        foreach (UIElement child in buttons.Children)
        {
            if (child is Button { IsMouseOver: false, Content: Border { Child: TextBlock glyph } })
                glyph.Opacity = opacity;
        }
    }

    private static void InstallNcHook(Window window)
    {
        if ((bool)window.GetValue(HookInstalledProperty))
            return;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        window.SetValue(HookInstalledProperty, true);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        Window? host = null;
        // Only resolve the WPF window when the message actually needs it. FromHwnd + RootVisual
        // during high-frequency NC traffic while a ShowDialog tree is loading is unsafe.
        if (msg is WmNcHitTest or WmNcCalcSize)
        {
            host = HwndSource.FromHwnd(hwnd)?.RootVisual as Window;
            if (host is not null && IsFullscreenSuspended(host))
                return IntPtr.Zero;
        }

        switch (msg)
        {
            case WmNcCalcSize when wParam != IntPtr.Zero:
                handled = true;
                ApplyCustomNcCalcSize(hwnd, lParam);
                return IntPtr.Zero;

            case WmNcHitTest:
                handled = true;
                return new IntPtr(HitTest(hwnd, host, lParam));

            default:
                return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Win10 custom-caption contract: keep thick-frame HWND, shrink the client so the OS
    /// caption is not drawn. Maximized HWNDs overscan past the monitor — inset all sides
    /// so the client (and Close) sit on the visible work-area edge.
    /// </summary>
    private static void ApplyCustomNcCalcSize(IntPtr hwnd, IntPtr lParam)
    {
        var frame = GetResizeFrameBorderPx(hwnd);
        if (frame <= 0)
            return;

        var ncParams = Marshal.PtrToStructure<NcCalcSizeParams>(lParam);
        var maximized = IsZoomed(hwnd);

        ncParams.Rgrc0.Left += frame;
        ncParams.Rgrc0.Right -= frame;
        ncParams.Rgrc0.Bottom -= frame;
        // Restored: top stays aligned with the window so our caption owns the top edge.
        // Maximized: HWND overscans past the top — pull the client down by the frame.
        // Do not leave a restored top NC strip — that resurfaces the OS title bar.
        if (maximized)
            ncParams.Rgrc0.Top += frame;

        Marshal.StructureToPtr(ncParams, lParam, fDeleteOld: false);
    }

    private static int HitTest(IntPtr hwnd, Window? window, IntPtr lParam)
    {
        GetWindowRect(hwnd, out var windowRect);
        var x = (short)(lParam.ToInt32() & 0xFFFF);
        var y = (short)((lParam.ToInt32() >> 16) & 0xFFFF);

        // Win32 DPI only — never VisualTreeHelper / PointFromScreen / TransformToAncestor here.
        var dpiScale = GetWindowDpiScale(hwnd);
        var frame = GetResizeFrameBorderPx(hwnd);
        var maximized = IsZoomed(hwnd);
        var captionTop = maximized ? frame : 0;
        var captionBottom = captionTop + (int)Math.Ceiling(CaptionHeight * dpiScale);
        var buttonsWidth = (int)Math.Ceiling(CaptionButtonWidthDip * CaptionButtonCount * dpiScale);

        var left = x - windowRect.Left;
        var top = y - windowRect.Top;
        var right = windowRect.Right - x;
        var bottom = windowRect.Bottom - y;

        // Caption buttons live in the client, inset from the HWND right by the frame border.
        if (top >= captionTop && top < captionBottom)
        {
            if (right > frame && right <= frame + buttonsWidth)
                return HtClient;
        }

        // Resize grips live in the true non-client frame only (NCCALCSIZE insets).
        // Use strict < frame: bottom/right/left == frame is the first client pixel
        // (footer Save sits there — <= stole those clicks as HtBottom).
        var canResize = window is null || window.ResizeMode != ResizeMode.NoResize;
        if (canResize && !maximized)
        {
            if (top < frame && left < frame) return HtTopLeft;
            if (top < frame && right < frame) return HtTopRight;
            if (bottom < frame && left < frame) return HtBottomLeft;
            if (bottom < frame && right < frame) return HtBottomRight;
            if (left < frame) return HtLeft;
            if (right < frame) return HtRight;
            // Top has no NC inset when restored — keep a thin in-client resize strip.
            if (top < frame) return HtTop;
            if (bottom < frame) return HtBottom;
        }

        if (top >= captionTop && top < captionBottom)
            return HtCaption;

        return HtClient;
    }

    private static int GetResizeFrameBorderPx(IntPtr hwnd)
    {
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            if (dpi == 0)
                dpi = 96;
            return GetSystemMetricsForDpi(SmCxSizeFrame, dpi) + GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);
        }
        catch (EntryPointNotFoundException)
        {
            return GetSystemMetrics(SmCxSizeFrame) + GetSystemMetrics(SmCxPaddedBorder);
        }
    }

    private static double GetWindowDpiScale(IntPtr hwnd)
    {
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            if (dpi > 0)
                return dpi / 96.0;
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-Win10 fallback — should not happen on our target.
        }

        return 1.0;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static int GetWindowLong(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex).ToInt32() : GetWindowLong32(hWnd, nIndex);

    private static void SetWindowLong(IntPtr hWnd, int nIndex, int value)
    {
        if (IntPtr.Size == 8)
            SetWindowLongPtr64(hWnd, nIndex, new IntPtr(value));
        else
            SetWindowLong32(hWnd, nIndex, value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NcCalcSizeParams
    {
        public NativeRect Rgrc0;
        public NativeRect Rgrc1;
        public NativeRect Rgrc2;
        public IntPtr Lppos;
    }
}
