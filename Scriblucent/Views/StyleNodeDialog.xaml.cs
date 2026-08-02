using Scriblucent.Animations;
using Scriblucent.Localization;
using Scriblucent.Models;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        public List<string> Icons { get; set; } = new();
        public string? IconBadgeColor { get; set; } = "#F59E0B"; // Default amber
        public bool IsBold { get; set; }
        public bool IsItalic { get; set; }
        public string? TextColor { get; set; }
        public string? Note { get; set; }
        public Guid? LinkedNoteId { get; set; }

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
            Icons = new List<string>(node.Icons);
            IconBadgeColor = node.IconBadgeColor ?? "#F59E0B";
            IsBold = node.IsBold;
            IsItalic = node.IsItalic;
            TextColor = node.TextColor;
            Note = node.Note;
            LinkedNoteId = node.LinkedNoteId;

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

            // Update UI for bold/italic
            if (FindName("BoldToggleButton") is ToggleButton boldToggle)
                boldToggle.IsChecked = IsBold;
            if (FindName("ItalicToggleButton") is ToggleButton italicToggle)
                italicToggle.IsChecked = IsItalic;

            // Update UI for text color
            if (!string.IsNullOrWhiteSpace(TextColor))
            {
                if (FindName("TextColorButton") is Button textColorButton)
                    textColorButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(TextColor));
            }
            else
            {
                if (FindName("TextColorButton") is Button textColorButton2)
                    textColorButton2.Background = Brushes.Transparent;
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

            // Highlight every currently-selected icon - FreeMind allows stacking any number of
            // icons on a node, so this is a multi-select grid, not single-select.
            ClearIconButtonHighlighting();
            foreach (var icon in Icons)
                HighlightSelectedIconButton(icon);

            if (FindName("NoteTextBox") is TextBox noteBox)
                noteBox.Text = Note ?? string.Empty;

            if (FindName("LinkedNoteComboBox") is ComboBox linkCombo)
                linkCombo.SelectedValue = LinkedNoteId;
        }

        // Populated from MindMapPreviewWindow.NoteLinkOptions (the same collection the document-
        // level linked-note picker already uses) - call before LoadFromNode so the combo's items
        // exist by the time LoadFromNode tries to select one.
        public void SetNoteLinkOptions(IEnumerable<MindMapPreviewWindow.MindMapNoteLinkOption> options)
        {
            if (FindName("LinkedNoteComboBox") is not ComboBox combo)
                return;

            combo.Items.Clear();
            combo.Items.Add(new ComboBoxItem
            {
                Content = LocalizationService.GetString("QuizLinkedNoteNone"),
                Tag = null
            });

            foreach (var option in options)
            {
                combo.Items.Add(new ComboBoxItem
                {
                    Content = option.Title,
                    Tag = option.Id
                });
            }
        }

        private void LinkedNoteComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FindName("LinkedNoteComboBox") is ComboBox combo)
                LinkedNoteId = combo.SelectedValue as Guid?;
        }

        public void ApplyToNode(MindMapNode node)
        {
            node.BackgroundColor = BackgroundColor;
            node.BorderColor = BorderColor;
            node.BorderThickness = BorderThickness;
            node.NodeShape = NodeShape;
            node.Icons = Icons;
            node.IconBadgeColor = IconBadgeColor;
            node.IsBold = IsBold;
            node.IsItalic = IsItalic;
            node.TextColor = TextColor;
            node.Note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim();
            node.LinkedNoteId = LinkedNoteId;
        }

        private void NoteTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox)
                Note = textBox.Text;
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

        private void BoldToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton toggle)
                IsBold = toggle.IsChecked == true;
        }

        private void ItalicToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton toggle)
                IsItalic = toggle.IsChecked == true;
        }

        private void TextColorButton_Click(object sender, RoutedEventArgs e)
        {
            var color = ShowColorPicker();
            if (color.HasValue)
            {
                TextColor = color.Value.ToString();
                if (FindName("TextColorButton") is Button button)
                    button.Background = new SolidColorBrush(color.Value);
            }
        }

        private void ClearTextColorButton_Click(object sender, RoutedEventArgs e)
        {
            TextColor = null;
            if (FindName("TextColorButton") is Button button)
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

            // Toggle membership rather than replace - FreeMind allows stacking any number of
            // icons on a node, so this grid is multi-select, not single-select.
            if (Icons.Contains(icon))
            {
                Icons.Remove(icon);
                ClearButtonHighlight(button);
            }
            else
            {
                Icons.Add(icon);
                HighlightButtonAsSelected(button);
            }
        }

        private void CustomIconButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SimpleInputDialog(
                LocalizationService.GetString("SelectIcon"),
                LocalizationService.GetString("EnterCustomIconEmoji"),
                string.Empty)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
            {
                var icon = dialog.InputText.Trim();
                if (!Icons.Contains(icon))
                {
                    Icons.Add(icon);
                    HighlightSelectedIconButton(icon);
                }
            }
        }

        private void ClearIconButton_Click(object sender, RoutedEventArgs e)
        {
            Icons.Clear();
            ClearIconButtonHighlighting();
        }

        private void HighlightButtonAsSelected(Button button)
        {
            button.Background = FindResource("SelectionBackground") as Brush
                ?? new SolidColorBrush(Color.FromRgb(232, 240, 254));
            button.BorderBrush = FindResource("AccentPrimary") as Brush
                ?? new SolidColorBrush(Color.FromRgb(74, 110, 224));
            button.BorderThickness = new Thickness(2);
        }

        private void ClearButtonHighlight(Button button)
        {
            var isCustomButton = button.ToolTip?.ToString() == LocalizationService.GetString("AddCustomIcon")
                || button.Content?.ToString() == "➕";

            var defaultBackground = FindResource("CardBackground") as Brush ?? Brushes.Transparent;
            var defaultBorder = FindResource("BorderColor") as Brush ?? Brushes.Gray;
            var customBackground = FindResource("SelectionBackground") as Brush ?? defaultBackground;

            button.Background = isCustomButton ? customBackground : defaultBackground;
            button.BorderBrush = isCustomButton
                ? FindResource("AccentPrimary") as Brush ?? defaultBorder
                : defaultBorder;
            button.BorderThickness = new Thickness(1);
        }

        private void ClearIconButtonHighlighting()
        {
            if (FindName("IconGrid") is not WrapPanel iconGrid)
                return;

            foreach (var child in iconGrid.Children)
            {
                if (child is Button button)
                    ClearButtonHighlight(button);
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
