using Scriblucent.Animations;
using Scriblucent.Localization;
using Scriblucent.Models;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using Scriblucent.Controls;

namespace Scriblucent.Views
{
    public partial class StyleNodeDialog : Window
    {
        public string? BackgroundColor { get; set; }
        public string? BorderColor { get; set; }
        public new double BorderThickness { get; set; } = 1.0;
        public string? NodeShape { get; set; } = "Rectangle";
        public new string? Icon { get; set; }
        public string? IconBadgeColor { get; set; } = "#F59E0B"; // Default amber

        private Button? _selectedIconButton;

    private readonly OverlayDialogAnimator _overlayAnimator;
    private bool _pendingDialogResult;

    public StyleNodeDialog()
    {
        InitializeComponent();
        _overlayAnimator = ((OverlayDialogShell)Content).BindAnimator(this);
        Scriblucent.Services.WindowThemeService.Register(this);
        Loaded += StyleNodeDialog_Loaded;

        if (FindName("BorderThicknessSlider") is Slider slider)
        {
            slider.ValueChanged += BorderThicknessSlider_ValueChanged;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyOwnerBounds();
    }

    private void ApplyOwnerBounds()
    {
        OverlayDialogBoundsHelper.Apply(this);
    }

    private void StyleNodeDialog_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyOwnerBounds();
        UiMotion.ConfigurePopupsForWindow(this);
        _overlayAnimator.BeginOpen();
    }

        public void LoadFromNode(MindMapNode node)
        {
            BackgroundColor = node.BackgroundColor;
            BorderColor = node.BorderColor;
            BorderThickness = node.BorderThickness;
            NodeShape = node.NodeShape ?? "Rectangle";
            Icon = node.Icon;
            IconBadgeColor = node.IconBadgeColor ?? "#F59E0B";

            // Update UI for background color
            if (!string.IsNullOrWhiteSpace(BackgroundColor))
            {
                if (FindName("BackgroundColorButton") is Button bgButton)
                    bgButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(BackgroundColor));
            }
            else
            {
                if (FindName("BackgroundColorButton") is Button bgButton2)
                    bgButton2.Background = Brushes.Transparent;
            }

            // Update UI for border color
            if (!string.IsNullOrWhiteSpace(BorderColor))
            {
                if (FindName("BorderColorButton") is Button borderButton)
                    borderButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(BorderColor));
            }
            else
            {
                if (FindName("BorderColorButton") is Button borderButton2)
                    borderButton2.Background = Brushes.Transparent;
            }

            // Update UI for icon badge color
            if (!string.IsNullOrWhiteSpace(IconBadgeColor))
            {
                if (FindName("IconBadgeColorButton") is Button iconBadgeButton)
                    iconBadgeButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(IconBadgeColor));
            }
            else
            {
                if (FindName("IconBadgeColorButton") is Button iconBadgeButton2)
                    iconBadgeButton2.Background = Brushes.Transparent;
            }

            // Update UI for border thickness
            if (FindName("BorderThicknessSlider") is Slider thicknessSlider)
            {
                thicknessSlider.Value = BorderThickness;
            }

            if (FindName("BorderThicknessText") is TextBlock thicknessText)
            {
                thicknessText.Text = BorderThickness.ToString();
            }

            // Update UI for shape
            if (FindName("ShapeComboBox") is ComboBox shapeCombo)
            {
                shapeCombo.SelectedValue = NodeShape;
            }

            // Highlight the current icon button if node has an icon
            if (!string.IsNullOrWhiteSpace(Icon))
            {
                HighlightSelectedIconButton(Icon);
            }
        }

        public void ApplyToNode(MindMapNode node)
        {
            node.BackgroundColor = BackgroundColor;
            node.BorderColor = BorderColor;
            node.BorderThickness = BorderThickness;
            node.NodeShape = NodeShape;
            node.Icon = Icon;
            node.IconBadgeColor = IconBadgeColor;
        }

        private void BackgroundColorButton_Click(object sender, RoutedEventArgs e)
        {
            var color = ShowColorPicker();
            if (color.HasValue)
            {
                BackgroundColor = color.Value.ToString();
                if (FindName("BackgroundColorButton") is Button button)
                    button.Background = new SolidColorBrush(color.Value);
            }
        }

        private void ClearBackgroundColorButton_Click(object sender, RoutedEventArgs e)
        {
            BackgroundColor = null;
            if (FindName("BackgroundColorButton") is Button button)
                button.Background = Brushes.Transparent;
        }

        private void BorderColorButton_Click(object sender, RoutedEventArgs e)
        {
            var color = ShowColorPicker();
            if (color.HasValue)
            {
                BorderColor = color.Value.ToString();
                if (FindName("BorderColorButton") is Button button)
                    button.Background = new SolidColorBrush(color.Value);
            }
        }

        private void ClearBorderColorButton_Click(object sender, RoutedEventArgs e)
        {
            BorderColor = null;
            if (FindName("BorderColorButton") is Button button)
                button.Background = Brushes.Transparent;
        }

        private void IconBadgeColorButton_Click(object sender, RoutedEventArgs e)
        {
            var color = ShowColorPicker();
            if (color.HasValue)
            {
                IconBadgeColor = color.Value.ToString();
                if (FindName("IconBadgeColorButton") is Button button)
                    button.Background = new SolidColorBrush(color.Value);
            }
        }

        private void ClearIconBadgeColorButton_Click(object sender, RoutedEventArgs e)
        {
            IconBadgeColor = null;
            if (FindName("IconBadgeColorButton") is Button button)
                button.Background = Brushes.Transparent;
        }

        private void BorderThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            BorderThickness = e.NewValue;
            if (FindName("BorderThicknessText") is TextBlock text)
                text.Text = BorderThickness.ToString();
        }

        private void ShapeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FindName("ShapeComboBox") is ComboBox combo && combo.SelectedValue is string shape)
            {
                NodeShape = shape;
            }
        }

        private void IconButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Content is not string icon)
                return;

            Icon = icon;
            ClearIconButtonHighlighting();
            HighlightButtonAsSelected(button);
            _selectedIconButton = button;
        }

        private void CustomIconButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SimpleInputDialog(
                LocalizationService.GetString("SelectIcon"),
                LocalizationService.GetString("EnterCustomIconEmoji"),
                Icon ?? string.Empty)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
            {
                Icon = dialog.InputText.Trim();
                ClearIconButtonHighlighting();
                _selectedIconButton = null;
            }
        }

        private void ClearIconButton_Click(object sender, RoutedEventArgs e)
        {
            Icon = null;
            ClearIconButtonHighlighting();
            _selectedIconButton = null;
        }

        private void HighlightButtonAsSelected(Button button)
        {
            button.Background = FindResource("SelectionBackground") as Brush
                ?? new SolidColorBrush(Color.FromRgb(232, 240, 254));
            button.BorderBrush = FindResource("AccentPrimary") as Brush
                ?? new SolidColorBrush(Color.FromRgb(74, 110, 224));
            button.BorderThickness = new Thickness(2);
        }

        private void ClearIconButtonHighlighting()
        {
            if (FindName("IconGrid") is not WrapPanel iconGrid)
                return;

            var defaultBackground = FindResource("CardBackground") as Brush ?? Brushes.Transparent;
            var defaultBorder = FindResource("BorderColor") as Brush ?? Brushes.Gray;
            var customBackground = FindResource("SelectionBackground") as Brush ?? defaultBackground;

            foreach (var child in iconGrid.Children)
            {
                if (child is not Button button)
                    continue;

                var isCustomButton = button.ToolTip?.ToString() == LocalizationService.GetString("AddCustomIcon")
                    || button.Content?.ToString() == "➕";

                button.Background = isCustomButton ? customBackground : defaultBackground;
                button.BorderBrush = isCustomButton
                    ? FindResource("AccentPrimary") as Brush ?? defaultBorder
                    : defaultBorder;
                button.BorderThickness = new Thickness(1);
            }
        }

        private void HighlightSelectedIconButton(string icon)
        {
            if (FindName("IconGrid") is WrapPanel iconGrid)
            {
                foreach (var child in iconGrid.Children)
                {
                    if (child is Button button && button.Content?.ToString() == icon)
                    {
                        HighlightButtonAsSelected(button);
                        _selectedIconButton = button;
                        break;
                    }
                }
            }
        }

        private Color? ShowColorPicker()
        {
            var dialog = new ColorPickerDialog
            {
                Owner = this
            };

            return dialog.ShowDialog() == true
                ? dialog.SelectedColor
                : null;
        }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (FindName("BorderThicknessSlider") is Slider slider)
            BorderThickness = slider.Value;

        BeginCloseAnimation(true);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        BeginCloseAnimation(false);
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            BeginCloseAnimation(false);
            e.Handled = true;
        }
    }

    private void BeginCloseAnimation(bool dialogResult)
    {
        _pendingDialogResult = dialogResult;
        _overlayAnimator.BeginClose(() =>
        {
            DialogResult = _pendingDialogResult;
            Close();
        });
    }
    }
}
