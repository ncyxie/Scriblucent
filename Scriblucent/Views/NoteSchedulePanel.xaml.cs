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

    private CalendarScheduledItemViewModel? _plannerOwner;
    private Action<CalendarScheduledItemViewModel, IEnumerable<NoteScheduleEntry>, DateTime>? _plannerSave;
    private NoteScheduleEntry? _editingEntry;
    private IReadOnlyList<NoteScheduleEntry> _baseline = [];
    private DateTime _entryDate;
    private IReadOnlyList<CalendarScheduledItemViewModel> _owners = [];
    public ObservableCollection<CalendarScheduledItemViewModel> OwnerChoices { get; } = new();
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
        ResetPlanner();
        _mainViewModel = viewModel;
        _selectedNote = note;
        _saveCallback = null;
        ScheduleTitleText.Text = Scriblucent.Localization.LocalizationService.GetString("CalendarScheduleHeading");
        ScheduleSubtitleText.Text = note.Document.Title ?? string.Empty;
        ScheduleSubtitleText.Visibility = string.IsNullOrEmpty(ScheduleSubtitleText.Text) ? Visibility.Collapsed : Visibility.Visible;

        _scheduleItems.ReplaceAll(BuildWorkingSchedules(note));

        PopulateTimeSelectors();
        SetEntryEditorsFromNow();

        _overlayAnimator.Show(backdropOwner: Window.GetWindow(this));
    }

    public void ShowAnimated(MainViewModel viewModel, IEnumerable<NoteScheduleEntry> existingSchedules, Action<IEnumerable<NoteScheduleEntry>> onSave, string title, string subtitle = "")
    {
        ResetPlanner();
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

        _overlayAnimator.Show(backdropOwner: Window.GetWindow(this));
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
        ScheduleDatePicker.FirstDayOfWeek = AppSettings.ResolveCalendarFirstDayOfWeek(
            Scriblucent.Services.AppSettingsService.Load().CalendarFirstDayOfWeek);
        var now = DateTime.Now;
        ScheduleDatePicker.SelectedDate = _entryDate == default ? now.Date : _entryDate;
        HourComboBox.SelectedItem = now.Hour.ToString("00");
        MinuteComboBox.SelectedItem = now.Minute.ToString("00");
        ScheduleNoteTextBox.Text = string.Empty;
    }

    private void AddScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadEntry(out var date, out var note)) return;
        if (_editingEntry is not null) { _editingEntry.ScheduledAt = date; _editingEntry.Note = note; }
        else ScheduleItems.Add(new NoteScheduleEntry { ScheduledAt = date, Note = note });
        _entryDate = date.Date; _editingEntry = null; _clearedDraft = false; _newDraft = false;
        SortScheduleItems(); ScheduleNoteTextBox.Clear(); SetEditingLabel();
    }

    private bool TryReadEntry(out DateTime date, out string note)
    {
        date = default; note = ScheduleNoteTextBox.Text.Trim();
        var hourText = HourComboBox.SelectedItem?.ToString() ?? HourComboBox.Text;
        var minuteText = MinuteComboBox.SelectedItem?.ToString() ?? MinuteComboBox.Text;
        if ((_plannerSave is not null && _plannerOwner is null) || !ScheduleDatePicker.SelectedDate.HasValue
            || !int.TryParse(hourText, out var hour) || hour is < 0 or > 23 || !int.TryParse(minuteText, out var minute) || minute is < 0 or > 59)
        { ReminderValidation.Show(Scriblucent.Localization.LocalizationService.GetString("CalendarInvalidReminder")); return false; }
        date = ScheduleDatePicker.SelectedDate.Value.Date.AddHours(hour).AddMinutes(minute);
        ReminderValidation.Show(string.Empty); return true;
    }

    private void RemoveScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: NoteScheduleEntry entry })
            return;

        ScheduleItems.Remove(entry); if (ReferenceEquals(_editingEntry, entry)) { _editingEntry = null; SetEditingLabel(); }
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
        if (_editingEntry is not null)
        { if (!TryReadEntry(out var editedDate, out var editedNote)) return; _editingEntry.ScheduledAt = editedDate; _editingEntry.Note = editedNote; SortScheduleItems(); }
        if (_plannerSave is not null)
        {
            if (_plannerOwner is null) { ReminderValidation.Show(Scriblucent.Localization.LocalizationService.GetString("CalendarInvalidReminder")); return; }
            if (_plannerOwner.IsDeleted) { ReminderValidation.Show(Scriblucent.Localization.LocalizationService.GetString("CalendarStaleReminder")); return; }
            var current = MainViewModel.GetCalendarOwnerSchedules(_plannerOwner);
            if (current.Count != _baseline.Count || current.Zip(_baseline).Any(pair => pair.First.ScheduledAt != pair.Second.ScheduledAt || pair.First.Note != pair.Second.Note))
            { ReminderValidation.Show(Scriblucent.Localization.LocalizationService.GetString("CalendarStaleReminder")); return; }
            if (_staleOccurrence) { ReminderValidation.Show(Scriblucent.Localization.LocalizationService.GetString("CalendarStaleReminder")); return; }
            // A first reminder can be saved directly, without the intermediate Add click.
            if (_newDraft && !_clearedDraft) { if (!TryReadEntry(out var firstDate, out var firstNote)) return; ScheduleItems.Add(new() { ScheduledAt = firstDate, Note = firstNote }); _entryDate = firstDate.Date; }
            _plannerSave(_plannerOwner, ScheduleItems.ToArray(), _editingEntry?.ScheduledAt.Date ?? _entryDate);
            HideAnimated(); return;
        }
        if (_saveCallback != null)
            _saveCallback(ScheduleItems);
        else if (_mainViewModel != null && _selectedNote != null)
            _mainViewModel.SetNoteSchedules(_selectedNote, ScheduleItems);

        _saveCallback = null;
        HideAnimated();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        ScheduleItems.Clear(); _clearedDraft = true; _editingEntry = null; ScheduleNoteTextBox.Clear(); SetEditingLabel();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        HideAnimated();
    }

    private bool _clearedDraft, _newDraft, _staleOccurrence, _filteringOwners;
    private void ResetPlanner()
    {
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag);
        _plannerSave = null; _plannerOwner = null; _editingEntry = null; _entryDate = DateTime.Today; _clearedDraft = false; _newDraft = false; _staleOccurrence = false;
        OwnerPickerSection.Visibility = Visibility.Collapsed; ReminderValidation.Show(string.Empty); SetEditingLabel();
    }
    public void ShowPlanner(IReadOnlyList<CalendarScheduledItemViewModel> owners, CalendarScheduledItemViewModel? owner,
        NoteScheduleEntry? occurrence, DateTime date, Action<CalendarScheduledItemViewModel, IEnumerable<NoteScheduleEntry>, DateTime> save)
    {
        ResetPlanner(); _mainViewModel = null; _selectedNote = null; _saveCallback = null;
        _plannerSave = save; _owners = owners; _entryDate = date.Date; _newDraft = owner is null;
        OwnerPickerSection.Visibility = owner is null ? Visibility.Visible : Visibility.Collapsed;
        OwnerSearchTextBox.Clear(); OwnerChoices.Clear(); foreach (var choice in owners) OwnerChoices.Add(choice);
        OwnerPicker.SelectedItem = null; ScheduleItems.Clear(); PopulateTimeSelectors(); SetEntryEditorsFromNow();
        ScheduleTitleText.Text = Scriblucent.Localization.LocalizationService.GetString("CalendarScheduleHeading");
        if (owner is not null) LoadPlannerOwner(owner, occurrence); else { ScheduleSubtitleText.Text = string.Empty; ScheduleSubtitleText.Visibility = Visibility.Collapsed; }
        _overlayAnimator.Show(backdropOwner: Window.GetWindow(this));
    }
    private void LoadPlannerOwner(CalendarScheduledItemViewModel owner, NoteScheduleEntry? occurrence = null)
    {
        _plannerOwner = owner; _editingEntry = null; _clearedDraft = false;
        var original = MainViewModel.GetCalendarOwnerSchedules(owner);
        _baseline = original.Select(entry => new NoteScheduleEntry { ScheduledAt = entry.ScheduledAt, Note = entry.Note }).ToArray();
        var drafts = original.Select(entry => new NoteScheduleEntry { ScheduledAt = entry.ScheduledAt, Note = entry.Note }).ToArray();
        if (occurrence is not null)
        {
            var index = original.ToList().FindIndex(entry => ReferenceEquals(entry, occurrence));
            if (index < 0) { _staleOccurrence = true; ReminderValidation.Show(Scriblucent.Localization.LocalizationService.GetString("CalendarStaleReminder")); }
            else _editingEntry = drafts[index];
        }
        ScheduleItems.Clear(); foreach (var entry in drafts.OrderBy(entry => entry.ScheduledAt)) ScheduleItems.Add(entry);
        ScheduleSubtitleText.Text = owner.TypeText + " \u00b7 " + owner.Title; ScheduleSubtitleText.Visibility = Visibility.Visible;
        if (_editingEntry is not null) LoadEntryEditors(_editingEntry); SetEditingLabel();
    }
    private void OwnerSearch_TextChanged(object s, TextChangedEventArgs e)
    {
        if (OwnerPicker is null) return;
        var query = OwnerSearchTextBox.Text.Trim(); var selected = _plannerOwner;
        _filteringOwners = true;
        OwnerChoices.Clear(); foreach (var owner in _owners.Where(owner => string.IsNullOrEmpty(query) || owner.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))) OwnerChoices.Add(owner);
        if (selected is not null && !OwnerChoices.Contains(selected)) OwnerChoices.Insert(0, selected);
        OwnerPicker.SelectedItem = selected; _filteringOwners = false;
    }
    private void OwnerPicker_SelectionChanged(object s, SelectionChangedEventArgs e)
    { if (!_filteringOwners && OwnerPicker.SelectedItem is CalendarScheduledItemViewModel owner && !ReferenceEquals(owner, _plannerOwner)) LoadPlannerOwner(owner); }
    private void EditReminder_Click(object s, RoutedEventArgs e)
    { if (s is FrameworkElement { Tag: NoteScheduleEntry entry }) { _editingEntry = entry; _newDraft = false; LoadEntryEditors(entry); SetEditingLabel(); } }
    private void NewReminder_Click(object s, RoutedEventArgs e)
    { _editingEntry = null; _newDraft = true; _clearedDraft = false; ScheduleNoteTextBox.Clear(); SetEditingLabel(); }
    private void LoadEntryEditors(NoteScheduleEntry entry)
    {
        ScheduleDatePicker.SelectedDate = entry.ScheduledAt.Date; HourComboBox.SelectedItem = entry.ScheduledAt.Hour.ToString("00"); MinuteComboBox.SelectedItem = entry.ScheduledAt.Minute.ToString("00"); ScheduleNoteTextBox.Text = entry.Note;
    }
    private void SetEditingLabel()
    {
        if (StageReminderButton is not null) StageReminderButton.Content = Scriblucent.Localization.LocalizationService.GetString(_editingEntry is null ? "CalendarAddSchedule" : "CalendarUpdateReminder");
        if (ComposerHeading is not null) ComposerHeading.Text = Scriblucent.Localization.LocalizationService.GetString(_editingEntry is null ? "CalendarNewReminder" : "CalendarEditReminderHeading");
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
