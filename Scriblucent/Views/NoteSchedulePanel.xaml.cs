using Scriblucent.Animations;
using Scriblucent.Models;
using Scriblucent.ViewModels;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Scriblucent.Views;

public partial class NoteSchedulePanel : UserControl
{
    private readonly OverlayPanelAnimator _overlayAnimator;
    private MainViewModel? _mainViewModel;
    private NoteCardViewModel? _selectedNote;
    private Action<IEnumerable<NoteScheduleEntry>>? _saveCallback;

    private readonly BulkObservableCollection<NoteScheduleEntry> _scheduleItems = new();
    public ObservableCollection<NoteScheduleEntry> ScheduleItems => _scheduleItems;

    public NoteSchedulePanel()
    {
        InitializeComponent();
        UiMotion.ConfigurePopupsForElement(this);
        _overlayAnimator = OverlayPanelAnimator.Bind(this, OverlayDim, PanelMotionHost, BackdropSnapshot, ScrimHost);
        DataContext = this;
    }

    public void ShowAnimated(MainViewModel viewModel, NoteCardViewModel note)
    {
        _mainViewModel = viewModel;
        _selectedNote = note;
        _saveCallback = null;
        ScheduleTitleText.Text = Scriblucent.Localization.LocalizationService.GetString("CalendarAssignTitle");
        ScheduleSubtitleText.Text = note.Document.Title ?? string.Empty;
        ScheduleSubtitleText.Visibility = string.IsNullOrEmpty(ScheduleSubtitleText.Text) ? Visibility.Collapsed : Visibility.Visible;

        _scheduleItems.ReplaceAll(BuildWorkingSchedules(note));

        PopulateTimeSelectors();
        SetEntryEditorsFromNow();

        _overlayAnimator.Show(captureBackdrop: () => OverlayBackdropCapture.TryCaptureForDimAnimation(Window.GetWindow(this)));
    }

    public void ShowAnimated(MainViewModel viewModel, IEnumerable<NoteScheduleEntry> existingSchedules, Action<IEnumerable<NoteScheduleEntry>> onSave, string title, string subtitle = "")
    {
        _mainViewModel = viewModel;
        _selectedNote = null;
        _saveCallback = onSave;
        ScheduleTitleText.Text = title;
        ScheduleSubtitleText.Text = subtitle;
        ScheduleSubtitleText.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;

        var sorted = existingSchedules
            .Select(e => new NoteScheduleEntry { ScheduledAt = e.ScheduledAt, Note = e.Note ?? string.Empty })
            .OrderBy(e => e.ScheduledAt)
            .ThenBy(e => e.Note, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _scheduleItems.ReplaceAll(sorted);

        PopulateTimeSelectors();
        SetEntryEditorsFromNow();

        _overlayAnimator.Show(captureBackdrop: () => OverlayBackdropCapture.TryCaptureForDimAnimation(Window.GetWindow(this)));
    }

    private static List<NoteScheduleEntry> BuildWorkingSchedules(NoteCardViewModel note)
    {
        var schedules = note.Document.Schedules?.ToList() ?? new List<NoteScheduleEntry>();
        if (schedules.Count == 0 && note.Document.ScheduledAt.HasValue)
        {
            schedules.Add(new NoteScheduleEntry
            {
                ScheduledAt = note.Document.ScheduledAt.Value,
                Note = note.Document.ScheduleNote ?? string.Empty
            });
        }

        return schedules
            .Select(entry => new NoteScheduleEntry
            {
                ScheduledAt = entry.ScheduledAt,
                Note = entry.Note ?? string.Empty
            })
            .OrderBy(entry => entry.ScheduledAt)
            .ThenBy(entry => entry.Note, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void PopulateTimeSelectors()
    {
        if (HourComboBox.Items.Count == 24)
            return;

        HourComboBox.Items.Clear();
        MinuteComboBox.Items.Clear();

        for (var hour = 0; hour < 24; hour++)
            HourComboBox.Items.Add(hour.ToString("00"));

        for (var minute = 0; minute < 60; minute++)
            MinuteComboBox.Items.Add(minute.ToString("00"));
    }

    private void SetEntryEditorsFromNow()
    {
        var now = DateTime.Now;
        ScheduleDatePicker.SelectedDate = now.Date;
        HourComboBox.SelectedItem = now.Hour.ToString("00");
        MinuteComboBox.Text = now.Minute.ToString("00");
        ScheduleNoteTextBox.Text = string.Empty;
    }

    private void AddScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ScheduleDatePicker.SelectedDate.HasValue)
            return;

        var hour = HourComboBox.SelectedItem?.ToString() ?? "00";
        var minute = string.IsNullOrWhiteSpace(MinuteComboBox.Text)
            ? MinuteComboBox.SelectedItem?.ToString() ?? "00"
            : MinuteComboBox.Text;

        if (!int.TryParse(hour, out var selectedHour))
            selectedHour = 0;
        if (!int.TryParse(minute, out var selectedMinute))
            selectedMinute = 0;

        selectedMinute = Math.Clamp(selectedMinute, 0, 59);

        var entry = new NoteScheduleEntry
        {
            ScheduledAt = ScheduleDatePicker.SelectedDate.Value.Date.AddHours(selectedHour).AddMinutes(selectedMinute),
            Note = (ScheduleNoteTextBox.Text ?? string.Empty).Trim()
        };

        ScheduleItems.Add(entry);
        SortScheduleItems();
        SetEntryEditorsFromNow();
    }

    private void RemoveScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: NoteScheduleEntry entry })
            return;

        ScheduleItems.Remove(entry);
    }

    private void SortScheduleItems()
    {
        var ordered = ScheduleItems
            .OrderBy(item => item.ScheduledAt)
            .ThenBy(item => item.Note, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _scheduleItems.ReplaceAll(ordered);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_saveCallback != null)
            _saveCallback(ScheduleItems);
        else if (_mainViewModel != null && _selectedNote != null)
            _mainViewModel.SetNoteSchedules(_selectedNote, ScheduleItems);

        _saveCallback = null;
        HideAnimated();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        ScheduleItems.Clear();
        if (_saveCallback != null)
            _saveCallback(ScheduleItems);
        else if (_mainViewModel != null && _selectedNote != null)
            _mainViewModel.SetNoteSchedules(_selectedNote, ScheduleItems);

        _saveCallback = null;
        HideAnimated();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        HideAnimated();
    }

    public void HideAnimated()
    {
        _overlayAnimator.Hide();
    }

    private void OverlayDim_MouseDown(object sender, MouseButtonEventArgs e)
    {
        HideAnimated();
    }
}
