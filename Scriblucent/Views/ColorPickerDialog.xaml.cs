using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Models;
using Scriblucent.Services;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Scriblucent.Views;

public partial class ColorPickerDialog : Window
{
    private const int MaxRecentColors = 8;
    private static readonly Regex HexRegex = new(
        @"^#?([0-9A-Fa-f]{6})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly OverlayDialogAnimator _overlayAnimator;
    private bool _spectrumDragging;
    private bool _updatingHex;
    private double _hue;
    private double _saturation = 1;
    private double _value = 0.1;

    public Color? SelectedColor { get; private set; }

    /// <summary>Optional starting color before the dialog opens.</summary>
    public Color? InitialColor { get; set; }

    public ColorPickerDialog()
    {
        InitializeComponent();
        _overlayAnimator = ((OverlayDialogShell)Content).BindAnimator(this);
        WindowThemeService.Register(this);
        Loaded += ColorPickerDialog_Loaded;
        SpectrumBorder.SizeChanged += (_, _) => UpdateSpectrumVisuals(updateThumb: true);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        OverlayDialogBoundsHelper.Apply(this);
    }

    private void ColorPickerDialog_Loaded(object sender, RoutedEventArgs e)
    {
        OverlayDialogBoundsHelper.Apply(this);
        UiMotion.ConfigurePopupsForWindow(this);
        BuildRecentColors();

        if (InitialColor is { } initial)
            SetFromRgb(initial, syncHex: true);
        else
            SetFromRgb(Color.FromRgb(0x1A, 0x1A, 0x1A), syncHex: true);

        UpdateSpectrumVisuals(updateThumb: true);
        _overlayAnimator.BeginOpen();
    }

    private void BuildRecentColors()
    {
        RecentColorsPanel.Children.Clear();
        var recents = LoadRecentColors();
        if (recents.Count == 0)
        {
            RecentColorsSection.Visibility = Visibility.Collapsed;
            return;
        }

        RecentColorsSection.Visibility = Visibility.Visible;
        foreach (var color in recents)
        {
            var captured = color;
            var button = new Button
            {
                Style = (Style)FindResource("RecentColorSwatchStyle"),
                Background = new SolidColorBrush(captured),
                ToolTip = ToHex(captured)
            };
            System.Windows.Automation.AutomationProperties.SetName(button, ToHex(captured));
            button.Click += (_, _) =>
            {
                SetFromRgb(captured, syncHex: true);
                UpdateSpectrumVisuals(updateThumb: true);
            };
            RecentColorsPanel.Children.Add(button);
        }
    }

    private void HueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
            return;

        _hue = HueSlider.Value;
        UpdateSpectrumVisuals(updateThumb: false);
    }

    private void Spectrum_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _spectrumDragging = true;
        SpectrumBorder.CaptureMouse();
        SampleSpectrum(e.GetPosition(SpectrumBorder));
        e.Handled = true;
    }

    private void Spectrum_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_spectrumDragging || e.LeftButton != MouseButtonState.Pressed)
            return;

        SampleSpectrum(e.GetPosition(SpectrumBorder));
        e.Handled = true;
    }

    private void Spectrum_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_spectrumDragging)
            return;

        _spectrumDragging = false;
        if (SpectrumBorder.IsMouseCaptured)
            SpectrumBorder.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void SampleSpectrum(Point position)
    {
        var width = Math.Max(1, SpectrumBorder.ActualWidth);
        var height = Math.Max(1, SpectrumBorder.ActualHeight);
        _saturation = Math.Clamp(position.X / width, 0, 1);
        _value = Math.Clamp(1.0 - (position.Y / height), 0, 1);
        UpdateSpectrumVisuals(updateThumb: true);
    }

    private void UpdateSpectrumVisuals(bool updateThumb)
    {
        var hueColor = ColorFromHsv(_hue, 1, 1);
        SpectrumHueStop.Color = hueColor;

        var color = ColorFromHsv(_hue, _saturation, _value);
        PreviewSwatch.Background = new SolidColorBrush(color);
        SelectedColor = color;
        WriteHexBox(color);

        if (HueSlider is not null && Math.Abs(HueSlider.Value - _hue) > 0.01)
            HueSlider.Value = _hue;

        if (updateThumb && SpectrumThumb is not null)
        {
            var width = Math.Max(1, SpectrumBorder.ActualWidth);
            var height = Math.Max(1, SpectrumBorder.ActualHeight);
            Canvas.SetLeft(SpectrumThumb, (_saturation * width) - (SpectrumThumb.Width / 2));
            Canvas.SetTop(SpectrumThumb, ((1.0 - _value) * height) - (SpectrumThumb.Height / 2));
        }
    }

    private void WriteHexBox(Color color)
    {
        if (HexTextBox is null)
            return;

        var hex = ToHex(color);
        if (string.Equals(HexTextBox.Text, hex, StringComparison.OrdinalIgnoreCase))
            return;

        _updatingHex = true;
        HexTextBox.Text = hex;
        _updatingHex = false;
    }

    private void HexTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingHex || !IsLoaded)
            return;

        if (!TryParseHex(HexTextBox.Text, out var color))
            return;

        SetFromRgb(color, syncHex: false);
        UpdateSpectrumVisuals(updateThumb: true);
    }

    private void HexTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (TryParseHex(HexTextBox.Text, out var color))
        {
            SetFromRgb(color, syncHex: true);
            UpdateSpectrumVisuals(updateThumb: true);
            return;
        }

        // Snap invalid input back to the current color.
        WriteHexBox(ColorFromHsv(_hue, _saturation, _value));
    }

    private void HexTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        if (TryParseHex(HexTextBox.Text, out var color))
        {
            SetFromRgb(color, syncHex: true);
            UpdateSpectrumVisuals(updateThumb: true);
            Confirm_Click(sender, e);
        }
        else
        {
            WriteHexBox(ColorFromHsv(_hue, _saturation, _value));
        }

        e.Handled = true;
    }

    private void SetFromRgb(Color color, bool syncHex)
    {
        RgbToHsv(color.R, color.G, color.B, out _hue, out _saturation, out _value);
        if (HueSlider is not null)
            HueSlider.Value = _hue;
        if (syncHex)
            WriteHexBox(color);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        // Prefer a valid typed hex if the box was edited last.
        if (TryParseHex(HexTextBox.Text, out var typed))
            SelectedColor = typed;
        else
            SelectedColor = ColorFromHsv(_hue, _saturation, _value);

        RememberRecent(SelectedColor.Value);
        BeginCloseAnimation(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        SelectedColor = null;
        BeginCloseAnimation(false);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Cancel_Click(this, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !ReferenceEquals(e.OriginalSource, HexTextBox))
        {
            Confirm_Click(this, e);
            e.Handled = true;
        }
    }

    private void BeginCloseAnimation(bool accepted)
    {
        _overlayAnimator.BeginClose(() =>
        {
            DialogResult = accepted;
            Close();
        });
    }

    private static List<Color> LoadRecentColors()
    {
        var settings = AppSettingsService.Load();
        var result = new List<Color>(MaxRecentColors);
        foreach (var hex in settings.RecentColors)
        {
            if (!TryParseHex(hex, out var color))
                continue;
            if (result.Any(existing => ColorsEqual(existing, color)))
                continue;
            result.Add(color);
            if (result.Count >= MaxRecentColors)
                break;
        }

        return result;
    }

    private static void RememberRecent(Color color)
    {
        try
        {
            var settings = AppSettingsService.Load();
            var hex = ToHex(color);
            settings.RecentColors ??= new List<string>();
            settings.RecentColors.RemoveAll(entry =>
                string.Equals(NormalizeHex(entry), NormalizeHex(hex), StringComparison.OrdinalIgnoreCase));
            settings.RecentColors.Insert(0, hex);
            if (settings.RecentColors.Count > MaxRecentColors)
                settings.RecentColors.RemoveRange(MaxRecentColors, settings.RecentColors.Count - MaxRecentColors);
            AppSettingsService.Save(settings);
        }
        catch
        {
            // Color selection still succeeds if recent persistence fails.
        }
    }

    private static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var match = HexRegex.Match(text.Trim());
        if (!match.Success)
            return false;

        var value = match.Groups[1].Value;
        if (!byte.TryParse(value.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            || !byte.TryParse(value.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            || !byte.TryParse(value.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return false;
        }

        color = Color.FromRgb(r, g, b);
        return true;
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string NormalizeHex(string? hex)
        => TryParseHex(hex, out var color) ? ToHex(color) : (hex ?? string.Empty).Trim().ToUpperInvariant();

    private static bool ColorsEqual(Color a, Color b)
        => a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;

    private static Color ColorFromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);

        var c = value * saturation;
        var x = c * (1 - Math.Abs((hue / 60) % 2 - 1));
        var m = value - c;

        double r1, g1, b1;
        if (hue < 60) { r1 = c; g1 = x; b1 = 0; }
        else if (hue < 120) { r1 = x; g1 = c; b1 = 0; }
        else if (hue < 180) { r1 = 0; g1 = c; b1 = x; }
        else if (hue < 240) { r1 = 0; g1 = x; b1 = c; }
        else if (hue < 300) { r1 = x; g1 = 0; b1 = c; }
        else { r1 = c; g1 = 0; b1 = x; }

        return Color.FromRgb(
            (byte)Math.Round((r1 + m) * 255),
            (byte)Math.Round((g1 + m) * 255),
            (byte)Math.Round((b1 + m) * 255));
    }

    private static void RgbToHsv(byte r, byte g, byte b, out double hue, out double saturation, out double value)
    {
        var rd = r / 255.0;
        var gd = g / 255.0;
        var bd = b / 255.0;
        var max = Math.Max(rd, Math.Max(gd, bd));
        var min = Math.Min(rd, Math.Min(gd, bd));
        var delta = max - min;

        value = max;
        saturation = max <= 0 ? 0 : delta / max;

        if (delta <= 0.00001)
        {
            hue = 0;
            return;
        }

        if (Math.Abs(max - rd) < 0.00001)
            hue = 60 * (((gd - bd) / delta) % 6);
        else if (Math.Abs(max - gd) < 0.00001)
            hue = 60 * (((bd - rd) / delta) + 2);
        else
            hue = 60 * (((rd - gd) / delta) + 4);

        if (hue < 0)
            hue += 360;
    }
}
