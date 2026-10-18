using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Scriblucent.Views;

/// <summary>
/// Insert / edit / remove an image caption, mirroring the LibreOffice and OnlyOffice caption
/// dialogs: label (category) with custom labels, caption text, numbering, separator, position
/// (above/below), and "exclude label from caption".
/// </summary>
public partial class InsertCaptionDialog : Window
{
    private static readonly string[] NumberingOptions =
    {
        "1, 2, 3, ...",
        "I, II, III, ...",
        "i, ii, iii, ...",
        "A, B, C, ...",
        "a, b, c, ...",
        LocalizationService.GetString("CaptionNumberingNone")
    };

    private readonly OverlayDialogAnimator _overlayAnimator;
    private readonly Func<string, int> _nextNumberForLabel;
    private readonly IReadOnlyList<string> _defaultLabels;
    private List<string> _currentLabels = new();
    private bool _labelValidationVisible;

    /// <summary>The final label set the dialog settled on: the list plus any label the user
    /// typed into the editable combo (a typed label is not part of the ItemsSource until used).</summary>
    public IReadOnlyList<string> Labels
    {
        get
        {
            var current = ResultLabel;
            if (current.Length == 0 || _currentLabels.Contains(current))
                return _currentLabels;

            return _currentLabels.Append(current).ToList();
        }
    }

    public string ResultLabel => (LabelComboBox.Text ?? string.Empty).Trim();
    public string ResultText => CaptionTextBox.Text ?? string.Empty;
    public CaptionNumbering ResultNumbering => (CaptionNumbering)NumberingComboBox.SelectedIndex;
    // No Trim: the separator's spaces are significant (e.g. " - "), and trimming would drop
    // a separator the user typed as just a space.
    public string ResultSeparator => SeparatorTextBox.Text ?? string.Empty;
    public CaptionPosition ResultPosition =>
        AboveRadioButton.IsChecked == true ? CaptionPosition.Above : CaptionPosition.Below;
    public bool ResultExcludeLabel => ExcludeLabelCheckBox.IsChecked == true;
    public bool ResultRemoved { get; private set; }

    public InsertCaptionDialog(
        string? label,
        string captionText,
        CaptionNumbering numbering,
        string separator,
        CaptionPosition position,
        bool excludeLabel,
        IReadOnlyList<string> labels,
        IReadOnlyList<string> defaultLabels,
        Func<string, int> nextNumberForLabel,
        bool hasExistingCaption)
    {
        InitializeComponent();
        _overlayAnimator = ((OverlayDialogShell)Content).BindAnimator(this);
        WindowThemeService.Register(this);
        Loaded += InsertCaptionDialog_Loaded;

        _currentLabels = labels.ToList();
        _defaultLabels = defaultLabels;
        _nextNumberForLabel = nextNumberForLabel;
        LabelComboBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(Label_TextChanged));

        LabelComboBox.ItemsSource = _currentLabels;
        LabelComboBox.Text = label ?? (_currentLabels.Count > 0 ? _currentLabels[0] : string.Empty);
        CaptionTextBox.Text = captionText;
        NumberingComboBox.ItemsSource = NumberingOptions;
        NumberingComboBox.SelectedIndex = ((int)numbering + NumberingOptions.Length) % NumberingOptions.Length;
        SeparatorTextBox.Text = separator;
        BelowRadioButton.IsChecked = position != CaptionPosition.Above;
        AboveRadioButton.IsChecked = position == CaptionPosition.Above;
        ExcludeLabelCheckBox.IsChecked = excludeLabel;
        RemoveButton.Visibility = hasExistingCaption ? Visibility.Visible : Visibility.Collapsed;

        Preview_Changed(null, EventArgs.Empty);
    }

    private void InsertCaptionDialog_Loaded(object sender, RoutedEventArgs e)
    {
        OverlayDialogBoundsHelper.Apply(this);
        UiMotion.ConfigurePopupsForWindow(this);
        // Let the dialog arrive without an input ring competing with its entrance.
        // Tab/click still enters the native fields; validation focuses the invalid label.
        CaptionDialogShell.Focus();
        _overlayAnimator.BeginOpen();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        OverlayDialogBoundsHelper.Apply(this);
    }

    private void Preview_Changed(object? sender, EventArgs e)
    {
        // Fires during InitializeComponent, before the constructor assigns the fields.
        if (PreviewTextBlock is null || LabelComboBox is null || _nextNumberForLabel is null)
            return;

        // Editable ComboBox raises SelectionChanged before it copies the chosen item
        // into Text, so a label pick must use the item that was just added.
        var label = LabelForPreview(sender, e);
        if (_labelValidationVisible && label.Length > 0) SetLabelValidation(false);

        // OnlyOffice rule: only labels the user created can be deleted.
        DeleteLabelButton.IsEnabled = label.Length > 0 && !_defaultLabels.Contains(label);

        var number = FloatingImageCoordinator.FormatCaptionNumber(_nextNumberForLabel(label), ResultNumbering);
        var prefix = ResultExcludeLabel ? number : $"{label} {number}".Trim();
        var separator = ResultText.Length > 0 ? ResultSeparator : string.Empty;
        PreviewTextBlock.Text = $"{prefix}{separator}{ResultText}".Trim();
    }

    private string LabelForPreview(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, LabelComboBox)
            && e is SelectionChangedEventArgs selection
            && selection.AddedItems.Count > 0
            && selection.AddedItems[0] is string picked)
            return picked.Trim();
        if (ReferenceEquals(sender, LabelComboBox) && e is TextChangedEventArgs { OriginalSource: TextBox input })
            return input.Text.Trim();

        return ResultLabel;
    }

    private void Preview_KeyUp(object sender, KeyEventArgs e) => Preview_Changed(sender, e);

    private void DeleteLabel_Click(object sender, RoutedEventArgs e)
    {
        var current = ResultLabel;
        if (current.Length == 0 || _defaultLabels.Contains(current))
            return;

        var remaining = _currentLabels.Where(label => !string.Equals(label, current, StringComparison.Ordinal)).ToList();
        _currentLabels = remaining;
        LabelComboBox.ItemsSource = remaining;
        LabelComboBox.Text = remaining.Count > 0 ? remaining[0] : string.Empty;
        Preview_Changed(null, EventArgs.Empty);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        ResultRemoved = true;
        BeginCloseAnimation(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => BeginCloseAnimation(false);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ResultLabel.Length == 0)
        {
            SetLabelValidation(true);
            LabelComboBox.ApplyTemplate();
            if (LabelComboBox.Template.FindName("PART_EditableTextBox", LabelComboBox) is TextBox input)
                input.Focus();
            else LabelComboBox.Focus();
            return;
        }

        BeginCloseAnimation(true);
    }

    private void BeginCloseAnimation(bool accepted)
    {
        _overlayAnimator.BeginClose(() =>
        {
            DialogResult = accepted;
            Close();
        });
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            BeginCloseAnimation(false);
            e.Handled = true;
        }
    }

    private void Label_TextChanged(object sender, TextChangedEventArgs e) => Preview_Changed(sender, e);

    private void SetLabelValidation(bool visible)
    {
        _labelValidationVisible = visible;
        LabelValidationMessage.IsError = visible;
    }
}
