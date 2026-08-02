using Scriblucent.Animations;
using Scriblucent.Icons;
using Scriblucent.Localization;
using Scriblucent.Models;
using System;
using System.Linq;
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
        public string? NodeShape { get; set; } = "Rounded";
        public List<string> Icons { get; set; } = new();
        public string? IconBadgeColor { get; set; } = "#F59E0B"; // Default amber
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

        PopulateIconPicker();
    }

    private void PopulateIconPicker()
    {
        if (FindName("IconGrid") is not WrapPanel iconGrid)
            return;

        iconGrid.Children.Clear();
        foreach (var kind in MindMapNodeIconCatalog.PickerKinds)
        {
            var button = new Button
            {
                Style = TryFindResource("StyleNodeIconButtonStyle") as Style,
                Tag = MindMapNodeIconCatalog.ToStorageId(kind),
                ToolTip = kind.ToString(),
                Content = new AppIcon
                {
                    Kind = kind,
                    Width = 20,
                    Height = 20,
                    StrokeThickness = 1.75,
                    Foreground = TryFindResource("TextColor") as Brush ?? Brushes.Black
                }
            };
            button.Click += IconButton_Click;
            iconGrid.Children.Add(button);
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
            NodeShape = string.IsNullOrWhiteSpace(node.NodeShape) ? "Rounded" : node.NodeShape;
            Icons = MindMapNodeIconCatalog.NormalizeStoredIcons(node.Icons);
            IconBadgeColor = node.IconBadgeColor ?? "#F59E0B";
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

            // Update UI for border thickness
            if (FindName("BorderThicknessSlider") is Slider thicknessSlider)
            {
                thicknessSlider.Value = BorderThickness;
            }

            if (FindName("BorderThicknessText") is TextBlock thicknessText)
            {
                thicknessText.Text = BorderThickness.ToString();
            }

            // Update UI for shape - select by Tag match (SelectedValue can miss before layout).
            if (FindName("ShapeComboBox") is ComboBox shapeCombo)
            {
                foreach (var item in shapeCombo.Items.OfType<ComboBoxItem>())
                {
                    if (string.Equals(item.Tag as string, NodeShape, StringComparison.OrdinalIgnoreCase))
                    {
                        shapeCombo.SelectedItem = item;
                        break;
                    }
                }
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
            node.Icons = MindMapNodeIconCatalog.NormalizeStoredIcons(Icons);
            node.IconBadgeColor = IconBadgeColor;
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
            if (sender is not Button button || button.Tag is not string iconId)
                return;

            if (Icons.Contains(iconId))
            {
                Icons.Remove(iconId);
                ClearButtonHighlight(button);
            }
            else
            {
                Icons.Add(iconId);
                HighlightButtonAsSelected(button);
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
            var defaultBackground = FindResource("CardBackground") as Brush ?? Brushes.Transparent;
            var defaultBorder = FindResource("BorderColor") as Brush ?? Brushes.Gray;

            button.Background = defaultBackground;
            button.BorderBrush = defaultBorder;
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
            if (FindName("IconGrid") is not WrapPanel iconGrid)
                return;

            foreach (var child in iconGrid.Children)
            {
                if (child is Button button
                    && string.Equals(button.Tag as string, icon, StringComparison.Ordinal))
                {
                    HighlightButtonAsSelected(button);
                    break;
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
