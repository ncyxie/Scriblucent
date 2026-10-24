using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Services;

namespace Scriblucent.Views;

public partial class FlashcardsPreviewWindow
{
    private ToggleButton? _hintToggle;
    private Window? _hintOwner;

    private void CardFlipButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject source)
        {
            if (FlashcardFaceContext.GetIsStudy(source))
                FlipCurrentStudyCard();
            else
            {
                for (var parent = source; parent is not null; parent = EditorSelectionInput.Parent(parent))
                    if (parent is FlashcardFlipHost host)
                    {
                        if (host.DataContext is FlashcardPreviewItem item) SelectFlashcard(item);
                        AnimateFlashcardFlip(host);
                        break;
                    }
            }
        }
        e.Handled = true;
    }

    private void CardAudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject source && FlashcardFaceContext.GetIsAnswer(source))
            PlayAnswerAudioButton_Click(sender, e);
        else
            PlayQuestionAudioButton_Click(sender, e);
        e.Handled = true;
    }

    private void CardMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FlashcardPreviewItem item } button) return;
        CloseFlashcardHint();
        var menu = new ContextMenu();
        menu.SetBinding(DataContextProperty, new Binding("PlacementTarget.DataContext") { RelativeSource = new RelativeSource(RelativeSourceMode.Self) });
        void Command(string key, RoutedEventHandler handler)
        {
            var command = new MenuItem();
            command.SetBinding(HeaderedItemsControl.HeaderProperty,
                new Binding($"[{key}]") { Source = LocalizationProvider.Instance });
            command.Click += handler;
            menu.Items.Add(command);
        }

        Command("MoveToSet", MoveToSetMenuItem_Click);
        Command("AddFlashcardToSet", AddFlashcardToSetMenuItem_Click);
        bool answer = FlashcardFaceContext.GetIsAnswer(button);
        if (answer ? item.HasAnswerAudio : item.HasQuestionAudio)
            Command("FlashcardRestartAudio", (_, args) =>
            {
                if (answer) RestartAnswerAudioButton_Click(button, args);
                else RestartQuestionAudioButton_Click(button, args);
            });
        menu.Items.Add(new Separator());
        if (!FlashcardFaceContext.GetIsStudy(button)) Command("MassSelectModeTitle", SelectFlashcardMenuItem_Click);
        Command("Delete", DeleteFlashcardMenuItem_Click);
        button.ContextMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(button.ContextMenu, menu)) button.ClearValue(ContextMenuProperty);
        };
        UiMotion.OpenContextMenu(menu, button);
        e.Handled = true;
    }

    private void FlashcardCardContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { PlacementTarget: FrameworkElement { DataContext: FlashcardPreviewItem item } target } menu) return;
        bool study = FlashcardFaceContext.GetIsStudy(target);
        bool audio = FlashcardFaceContext.GetIsAnswer(target) ? item.HasAnswerAudio : item.HasQuestionAudio;
        foreach (var command in menu.Items.OfType<MenuItem>())
            if (command.Tag as string == "Selection") command.Visibility = study ? Visibility.Collapsed : Visibility.Visible;
            else if (command.Tag as string == "Restart") command.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        if (menu.Items[1] is Separator separator) separator.Visibility = study ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RestartFlashcardAudioMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement target } }) return;
        if (FlashcardFaceContext.GetIsAnswer(target)) RestartAnswerAudioButton_Click(target, e);
        else RestartQuestionAudioButton_Click(target, e);
    }

    private void CardHintButton_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: FlashcardPreviewItem item } toggle) return;
        if (!_isStudyMode || !item.HasHint || FlashcardFaceContext.GetIsAnswer(toggle))
        {
            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            return;
        }
        CloseFlashcardHint();
        _hintToggle = toggle;
        _hintOwner = WorkspaceOwner;
        _hintOwner.Deactivated += HintOwner_Deactivated;
        _hintOwner.LocationChanged += HintOwner_LayoutChanged;
        _hintOwner.SizeChanged += HintOwner_LayoutChanged;
        _hintOwner.PreviewMouseDown += HintOwner_PreviewMouseDown;
        FlashcardHintPopup.DataContext = item;
        FlashcardHintPopup.PlacementTarget = toggle;
        UiMotion.AttachPopupOpenAnimation(FlashcardHintPopup);
        FlashcardHintPopup.IsOpen = true;
        e.Handled = true;
    }

    private void CardHintButton_Unchecked(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _hintToggle)) CloseFlashcardHint();
        e.Handled = true;
    }

    private void FlashcardHintPopup_Opened(object? sender, EventArgs e)
    {
        FlashcardHintScrollViewer.ScrollToTop();
        FlashcardHintScrollViewer.Focus();
    }

    private void FlashcardHintPopup_Closed(object? sender, EventArgs e)
    {
        if (!FlashcardHintPopup.IsOpen) CloseFlashcardHint();
    }

    private void FlashcardHintPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseFlashcardHint(restoreFocus: true);
        e.Handled = true;
    }

    private void HintOwner_Deactivated(object? sender, EventArgs e) => CloseFlashcardHint();

    private void HintOwner_LayoutChanged(object? sender, EventArgs e) => CloseFlashcardHint();

    private void HintOwner_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        for (var parent = source; parent is not null; parent = EditorSelectionInput.Parent(parent))
            if (ReferenceEquals(parent, _hintToggle) || ReferenceEquals(parent, FlashcardHintPopup.Child)) return;
        CloseFlashcardHint();
    }

    private void CloseFlashcardHint(bool restoreFocus = false)
    {
        var toggle = _hintToggle;
        _hintToggle = null;
        if (_hintOwner is { } owner)
        {
            owner.Deactivated -= HintOwner_Deactivated;
            owner.LocationChanged -= HintOwner_LayoutChanged;
            owner.SizeChanged -= HintOwner_LayoutChanged;
            owner.PreviewMouseDown -= HintOwner_PreviewMouseDown;
            _hintOwner = null;
        }
        if (FlashcardHintPopup is not null)
        {
            FlashcardHintPopup.IsOpen = false;
            FlashcardHintPopup.DataContext = null;
            FlashcardHintPopup.PlacementTarget = null;
        }
        toggle?.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
        if (restoreFocus && toggle is { IsVisible: true }) toggle.Focus();
    }
}
