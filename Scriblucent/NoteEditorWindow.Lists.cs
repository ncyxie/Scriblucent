using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using Scriblucent.Localization;
using Scriblucent.Services;
using Scriblucent.Views;

namespace Scriblucent;

public partial class NoteEditorWindow
{
    private EditorLists? _lists;
    private TextMarkerStyle _lastBulletStyle = TextMarkerStyle.Disc;
    private TextMarkerStyle _lastNumberStyle = TextMarkerStyle.Decimal;
    private EditorLists ListEditor => _lists ??= new EditorLists(ContentTextBox, () => _indentSize);

    private void InitializeListEditing()
    {
        FontPanel.Loaded += (_, _) =>
        {
            UpdateInsertCaptionAvailability();
            UpdateFormattingToolbarState();
        };
        NumberedListButton.ContextMenu.Opened += (_, _) =>
        {
            MaterializeSelectionForAction();
            UpdateNumberingMenuState();
        };
        ListEditor.BeforeChange = () => WpfNonUndoChange.Run(ContentTextBox,
            () => { SuspendEditorEdgeSpacing(); _wrapLayout?.Clear(); });
        ListEditor.AfterChange = () => WpfNonUndoChange.Run(ContentTextBox, () =>
        {
            _floatingImages.ReconcileInlineElementsAfterTableEdit();
            ApplyEditorEdgeSpacing(); _wrapLayout?.Reapply();
        });
        foreach (var (command, action) in new (RoutedCommand, Action)[]
        {
            (EditingCommands.ToggleBullets, () => ListEditor.ApplyStyle(_lastBulletStyle, true, false)),
            (EditingCommands.ToggleNumbering, () => ListEditor.ApplyStyle(_lastNumberStyle, true, false)),
            (EditingCommands.IncreaseIndentation, () => ListEditor.Indent(false)),
            (EditingCommands.DecreaseIndentation, () => ListEditor.Indent(true))
        })
        {
            // Tab within text is handled by the editor's spacing code before routed commands.
            ContentTextBox.CommandBindings.Add(new CommandBinding(command,
                (_, e) => { RunListEdit(action); e.Handled = true; },
                (_, e) =>
                {
                    e.CanExecute = _floatingImages?.SelectedImageId is null
                        && (command == EditingCommands.ToggleBullets || command == EditingCommands.ToggleNumbering
                            || (command == EditingCommands.IncreaseIndentation ? ListEditor.CanIndent : ListEditor.HasList));
                    e.Handled = true;
                }));
        }
        if (_defaultContentTextBoxContextMenu is { } menu)
        {
            var separator = new Separator();
            var toggle = ListMenu("ListToggleCheckbox", () => { ListEditor.ToggleCheckbox(); });
            var restart = ListMenu("ListRestartNumbering", () => ListEditor.SetNumbering(1));
            var continuation = ListMenu("ListContinueNumbering", () => ListEditor.SetNumbering(null));
            var start = new MenuItem { Header = LocalizationService.GetString("ListSetStartingValue") };
            start.Click += (_, _) => ShowListStartingValue();
            var remove = ListMenu("ListRemove", () => ListEditor.Remove());
            menu.Items.Add(separator); menu.Items.Add(toggle); menu.Items.Add(restart);
            menu.Items.Add(continuation); menu.Items.Add(start); menu.Items.Add(remove);
            menu.Opened += (_, _) =>
            {
                MaterializeSelectionForAction();
                bool hasList = ListEditor.HasList;
                bool numbered = ListEditor.Targets().Any(p => EditorLists.Ancestor<System.Windows.Documents.List>(p)
                    is { } list && EditorLists.Numbered(list.MarkerStyle));
                separator.Visibility = remove.Visibility = hasList ? Visibility.Visible : Visibility.Collapsed;
                toggle.Visibility = ListEditor.SelectionStyle == TextMarkerStyle.None ? Visibility.Visible : Visibility.Collapsed;
                restart.Visibility = continuation.Visibility = start.Visibility = numbered ? Visibility.Visible : Visibility.Collapsed;
                bool textSelected = _floatingImages?.SelectedImageId is null;
                restart.IsEnabled = textSelected && ListEditor.CanRestartNumbering;
                continuation.IsEnabled = textSelected && ListEditor.CanContinue;
                start.IsEnabled = textSelected && ListEditor.CanSetNumbering;
            };
        }
    }

    private void UpdateNumberingMenuState()
    {
        bool textSelected = _floatingImages?.SelectedImageId is null;
        ListRestartMenuItem.IsEnabled = textSelected && ListEditor.CanRestartNumbering;
        ListContinueMenuItem.IsEnabled = textSelected && ListEditor.CanContinue;
        ListStartingValueMenuItem.IsEnabled = textSelected && ListEditor.CanSetNumbering;
        var style = textSelected ? ListEditor.SelectionStyle : null;
        ListNumberDecimalMenuItem.IsChecked = style == TextMarkerStyle.Decimal;
        ListNumberLowerLatinMenuItem.IsChecked = style == TextMarkerStyle.LowerLatin;
        ListNumberUpperRomanMenuItem.IsChecked = style == TextMarkerStyle.UpperRoman;
    }

    private MenuItem ListMenu(string key, Action action)
    {
        var item = new MenuItem { Header = LocalizationService.GetString(key) };
        item.Click += (_, _) => RunListEdit(action);
        return item;
    }

    private void RunListEdit(Action action)
    {
        MaterializeSelectionForAction();
        ContentTextBox.Focus();
        var view = _floatingImages.CaptureViewState();
        try { action(); }
        finally
        {
            // Preserve scrolling without restoring the old caret after a structural action.
            _floatingImages.RestoreViewState((view.ScrollOffset, null));
        }
        MarkEditorContentChanged(); UpdateEditedIndicator(); UpdateFormattingToolbarState();
    }

    private void BulletStylesButton_Click(object sender, RoutedEventArgs e) => OpenButtonContextMenu(BulletListButton);
    private void NumberStylesButton_Click(object sender, RoutedEventArgs e) => OpenButtonContextMenu(NumberedListButton);
    private void ListIncreaseIndentButton_Click(object sender, RoutedEventArgs e) => RunListEdit(() => ListEditor.Indent(false));
    private void ListDecreaseIndentButton_Click(object sender, RoutedEventArgs e) => RunListEdit(() => ListEditor.Indent(true));
    private void ListRestartMenuItem_Click(object sender, RoutedEventArgs e) => RunListEdit(() => ListEditor.SetNumbering(1));
    private void ListContinueMenuItem_Click(object sender, RoutedEventArgs e) => RunListEdit(() => ListEditor.SetNumbering(null));
    private void ListStartingValueMenuItem_Click(object sender, RoutedEventArgs e) => ShowListStartingValue();

    private string BuildListParagraphClipboardText(Paragraph paragraph)
    {
        var text = EditorLists.InlineText(paragraph);
        var item = EditorLists.Ancestor<ListItem>(paragraph);
        if (item?.Parent is not System.Windows.Documents.List list || !ReferenceEquals(item.Blocks.FirstBlock, paragraph))
            return text;
        int index = list.ListItems.Cast<ListItem>().TakeWhile(candidate => !ReferenceEquals(candidate, item)).Count();
        return EditorLists.Marker(list.MarkerStyle, (int)Math.Min(int.MaxValue, (long)list.StartIndex + index)) + text;
    }

    private void ShowListStartingValue()
    {
        MaterializeSelectionForAction();
        if (_floatingImages?.SelectedImageId is not null || ListEditor.CurrentNumber is not { } currentValue) return;
        var dialog = new SimpleInputDialog(
            LocalizationService.GetString("ListStartingValueTitle"),
            LocalizationService.GetString("ListStartingValueTitle"),
            currentValue.ToString(CultureInfo.InvariantCulture),
            input => int.TryParse(input.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0,
            LocalizationService.GetString("ListStartingValueInvalid"))
        { Owner = GetDialogOwnerWindow() };
        if (dialog.ShowDialog() == true
            && int.TryParse(dialog.InputText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0)
            RunListEdit(() => ListEditor.SetNumbering(value));
    }
}
