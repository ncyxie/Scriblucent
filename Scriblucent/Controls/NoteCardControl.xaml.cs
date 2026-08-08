using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.ViewModels;
using Scriblucent.Localization;
using Scriblucent.Views;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Scriblucent.Controls;

public partial class NoteCardControl : UserControl
{
    private static WeakReference<NoteCardControl>? _dropGlowOwner;

    private Point _dragStart;
    private bool _suppressOpenOnMouseUp;

    public NoteCardControl()
    {
        InitializeComponent();

        // Use PreviewMouseLeftButtonUp so clicks inside child controls still trigger opening the editor
        PreviewMouseLeftButtonUp += NoteCardControl_PreviewMouseLeftButtonUp;
        Loaded += OnCardLoaded;
        Unloaded += OnUnloaded;
        Cursor = Cursors.Hand;
    }

    private void OnCardLoaded(object sender, RoutedEventArgs e)
    {
        if (ContentPreview is null)
            return;

        DataContextChanged += NoteCardControl_DataContextChanged;
        RebuildContentPreview(DataContext as NoteCardViewModel);
    }

    private void NoteCardControl_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => RebuildContentPreview(e.NewValue as NoteCardViewModel);

    /// <summary>Builds the card preview as inlines: a vector picture icon wherever a photo
    /// sentinel sits in the preview text (in-line photos at their text position, floating
    /// photos trailing the text) - so a photo-only note never reads as an empty card and
    /// text+photo notes show the photos where they belong.</summary>
    private void RebuildContentPreview(NoteCardViewModel? viewModel)
    {
        ContentPreview.Inlines.Clear();
        if (viewModel is null)
            return;

        var text = viewModel.Content ?? string.Empty;
        var segments = text.Split(Scriblucent.Services.FloatingImageCoordinator.InlineMarkerChar);
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length > 0)
                ContentPreview.Inlines.Add(new Run(segments[i]));

            if (i < segments.Length - 1)
                ContentPreview.Inlines.Add(new InlineUIContainer(BuildPreviewPictureIcon())
                {
                    BaselineAlignment = BaselineAlignment.Center
                });
        }
    }

    private static UIElement BuildPreviewPictureIcon()
    {
        var icon = new Icons.AppIcon
        {
            Kind = Icons.AppIconKind.Image,
            Width = 11,
            Height = 11,
            Margin = new Thickness(0, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.SetResourceReference(TextElement.ForegroundProperty, "TextColorSecondary");
        return icon;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DataContextChanged -= NoteCardControl_DataContextChanged;
        ContentPreview.Inlines.Clear();
        CardSurfaceMotionBehavior.Cleanup(this);
    }

    private void NoteCardControl_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_suppressOpenOnMouseUp)
        {
            _suppressOpenOnMouseUp = false;
            return;
        }

        // Don't open the editor when the three-dot menu button was clicked
        if (MenuButton.IsMouseOver)
            return;

        if (DashboardMassSelectCheckBoxBehavior.IsMassSelectCheckBox(e.OriginalSource as DependencyObject))
            return;

        // Get the ViewModel from DataContext
        var viewModel = DataContext as NoteCardViewModel;
        var mainWindow = Window.GetWindow(this) as MainWindow;
        var mainVm = mainWindow?.DataContext as MainViewModel;

        if (viewModel != null && mainVm?.IsMassSelectMode == true)
        {
            mainVm.ToggleMassSelectForNote(viewModel);
            MassSelectHighlightBehavior.RequestVisualSync(this);
            return;
        }

        // Only proceed if both exist
        if (viewModel != null && mainWindow != null)
            mainWindow.OpenNoteEditor(viewModel);
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
    }

    private void OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow { DataContext: MainViewModel { IsMassSelectMode: true } })
            return;

        if (DataContext is NoteCardViewModel vm)
            vm.IsMenuVisible = true;

        OpenContextMenu(this, PlacementMode.MousePoint);
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow { DataContext: MainViewModel { IsMassSelectMode: true } })
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        if (DataContext is not NoteCardViewModel draggedNote)
            return;

        if (MenuButton.IsMouseOver)
            return;

        var currentPosition = e.GetPosition(this);
        var delta = currentPosition - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _suppressOpenOnMouseUp = true;
        MainWindow.SetNoteDragInProgress(true);
        try
        {
            DashboardDragGhost.Execute(
                this,
                new DataObject(typeof(NoteCardViewModel), draggedNote),
                DragDropEffects.Move,
                _dragStart);
        }
        finally
        {
            MainWindow.SetNoteDragInProgress(false);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (DashboardDragGhost.IsActiveDragSource(this))
        {
            ResetDropGlow();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (Window.GetWindow(this) is MainWindow { DataContext: MainViewModel { IsMassSelectMode: true } })
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (DataContext is not NoteCardViewModel target)
        {
            ResetDropGlow();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var dragged = e.Data.GetData(typeof(NoteCardViewModel)) as NoteCardViewModel;
        var canDrop = dragged is not null && !ReferenceEquals(dragged, target);

        if (!DashboardDragGhost.IsNoteDropGlowSyncActive)
        {
            if (canDrop)
                SetDropGlowOwner(this);
            else if (_dropGlowOwner?.TryGetTarget(out var owner) == true && ReferenceEquals(owner, this))
                ResetDropGlow();
        }

        e.Effects = canDrop ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (DashboardDragGhost.IsActiveDragSource(this))
            ResetDropGlow();
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        ReleaseDropGlowOwnership();
        ResetDropGlowVisual();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (Window.GetWindow(this) is MainWindow { DataContext: MainViewModel { IsMassSelectMode: true } })
                return;

            if (DataContext is not NoteCardViewModel targetNote)
                return;

            var draggedNote = e.Data.GetData(typeof(NoteCardViewModel)) as NoteCardViewModel;
            var mainWindow = Window.GetWindow(this) as MainWindow;
            var mainVm = mainWindow?.DataContext as MainViewModel;
            if (draggedNote is null || mainVm is null)
                return;

            var sameGroup = draggedNote.Document.GroupId.HasValue
                && draggedNote.Document.GroupId == targetNote.Document.GroupId;

            var changed = false;
            if (sameGroup)
            {
                var dropPosition = e.GetPosition(this);
                var placeAfter = dropPosition.X >= (ActualWidth / 2d);
                changed = mainVm.TryReorderNotesWithinGroup(draggedNote, targetNote, placeAfter);
            }
            else
            {
                changed = mainVm.TryGroupNotes(draggedNote, targetNote);
            }

            e.Handled = true;
        }
        finally
        {
            ResetDropGlowVisual();
            SetDropGlowSuppressed(false);
        }
    }

    private bool CanAcceptDrop(DragEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow { DataContext: MainViewModel { IsMassSelectMode: true } })
            return false;

        if (DataContext is not NoteCardViewModel target)
            return false;

        var dragged = e.Data.GetData(typeof(NoteCardViewModel)) as NoteCardViewModel;
        return dragged is not null && !ReferenceEquals(dragged, target);
    }

    internal static void SyncDropGlowAtScreenPoint(
        DependencyObject? searchRoot,
        Point screenPoint,
        NoteCardViewModel? draggedNote,
        NoteCardControl? dragSource)
    {
        if (searchRoot is null || draggedNote is null)
            return;

        if (Window.GetWindow(searchRoot) is MainWindow { DataContext: MainViewModel { IsMassSelectMode: true } })
        {
            ClearDropGlowOwner();
            return;
        }

        NoteCardControl? glowTarget = null;
        ForEachNoteCard(searchRoot, candidate =>
        {
            if (ReferenceEquals(candidate, dragSource)
                || candidate.DataContext is not NoteCardViewModel targetNote
                || ReferenceEquals(targetNote, draggedNote)
                || !IsScreenPointWithinCard(candidate, screenPoint))
            {
                return;
            }

            glowTarget = candidate;
        });

        SetDropGlowOwner(glowTarget);
    }

    private static void ForEachNoteCard(DependencyObject root, Action<NoteCardControl> visit)
    {
        if (root is NoteCardControl card)
            visit(card);

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
            ForEachNoteCard(VisualTreeHelper.GetChild(root, i), visit);
    }

    private static bool IsScreenPointWithinCard(NoteCardControl card, Point screenPoint)
    {
        if (!card.IsVisible || card.ActualWidth <= 0 || card.ActualHeight <= 0)
            return false;

        if (PresentationSource.FromVisual(card) is null)
            return false;

        try
        {
            var local = card.PointFromScreen(screenPoint);
            var margin = card.Margin;
            return local.X >= -margin.Left
                && local.Y >= -margin.Top
                && local.X <= card.ActualWidth + margin.Right
                && local.Y <= card.ActualHeight + margin.Bottom;
        }
        catch
        {
            return false;
        }
    }

    internal static void ClearDropGlowOwner()
    {
        if (_dropGlowOwner?.TryGetTarget(out var owner) == true)
            owner.ResetDropGlowVisual();

        _dropGlowOwner = null;
    }

    internal void ResetDropGlow()
    {
        ReleaseDropGlowOwnership();
        ResetDropGlowVisual();
    }

    private static void SetDropGlowOwner(NoteCardControl? owner)
    {
        if (owner is null)
        {
            ClearDropGlowOwner();
            return;
        }

        if (_dropGlowOwner?.TryGetTarget(out var previous) == true)
        {
            if (ReferenceEquals(previous, owner))
                return;

            previous.ResetDropGlowVisual();
        }

        _dropGlowOwner = new WeakReference<NoteCardControl>(owner);
        owner.EnsureDropGlow(1, 2.5);
    }

    private void ReleaseDropGlowOwnership()
    {
        if (_dropGlowOwner?.TryGetTarget(out var owner) == true && ReferenceEquals(owner, this))
            _dropGlowOwner = null;
    }

    private void ResetDropGlowVisual()
        => DashboardDropGlowVisual.Reset(DropGlow);

    internal void SetDropGlowSuppressed(bool suppressed)
    {
        if (suppressed)
            ResetDropGlow();

        DropGlow.Visibility = suppressed ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void SetDragDimmed(bool dimmed, double fadedOpacity = 0.22)
    {
        DragDimOverlay.Visibility = Visibility.Collapsed;
        DragDimOverlay.Opacity = 0;

        if (dimmed)
        {
            SetDropGlowSuppressed(true);
            Opacity = fadedOpacity;
        }
        else
        {
            SetDropGlowSuppressed(false);
            ClearValue(OpacityProperty);
        }

        IsHitTestVisible = true;
    }

    internal void ClearDragVisualState()
    {
        if (DashboardDragGhost.IsActiveDragSource(this))
            return;

        SetDragDimmed(false);
    }

    internal void PrepareForMoveExit()
    {
        ReleaseDropGlowOwnership();
        ResetDropGlowVisual();
        SetDropGlowSuppressed(true);
        UiMotion.Stop(this);
        SetDragDimmed(false);
    }

    private void EnsureDropGlow(double opacity, double thickness)
        => DashboardDropGlowVisual.Animate(DropGlow, opacity, thickness);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        ClearDragVisualState();
        MassSelectHighlightBehavior.SyncContainer(this);
        DashboardMassSelectCheckBoxBehavior.SyncContainer(this);
        IndicatorRevealMotionBehavior.SyncContainer(this);
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow { DataContext: MainViewModel { IsMassSelectMode: true } })
            return;

        if (DataContext is NoteCardViewModel vm)
            vm.IsMenuVisible = true;
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (DataContext is NoteCardViewModel vm)
            vm.IsMenuVisible = false;
    }

    private void OnStartMassSelectMenuClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not NoteCardViewModel note)
            return;

        if (Window.GetWindow(this) is not MainWindow { DataContext: MainViewModel mainVm })
            return;

        mainVm.EnterMassSelect(note);
    }

    private void OnMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
            OpenContextMenu(button, PlacementMode.Bottom);
    }

    private void OpenContextMenu(UIElement placementTarget, PlacementMode placement)
    {
        if (MenuButton.ContextMenu == null)
            return;

        MenuButton.ContextMenu.DataContext = DataContext;
        UiMotion.OpenContextMenu(MenuButton.ContextMenu, placementTarget, placement);
    }

    private void OnOpenMenuClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is NoteCardViewModel viewModel)
        {
            var mainWindow = Window.GetWindow(this) as MainWindow;
            mainWindow?.OpenNoteEditor(viewModel);
        }
    }

    private void OnOpenFromFileClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not NoteCardViewModel viewModel)
            return;

        var dlg = new Microsoft.Win32.OpenFileDialog();
        dlg.Filter = LocalizationService.GetString("OpenFileDialogFilter");
        if (dlg.ShowDialog() == true)
        {
            try
            {
                var path = dlg.FileName;
                var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();

                if (ext == ".rtf")
                {
                    // Read raw bytes from the RTF file and store as Base64 so we don't corrupt encoding
                    var bytes = System.IO.File.ReadAllBytes(path);
                    viewModel.Document.Content = Convert.ToBase64String(bytes);
                }
                else
                {
                    // Read raw bytes first
                    var rawBytes = System.IO.File.ReadAllBytes(path);

                    string? content = null;

                    // 1) Try strict UTF-8 (throws on invalid sequences)
                    try
                    {
                        content = new System.Text.UTF8Encoding(false, true).GetString(rawBytes);
                    }
                    catch
                    {
                        // 2) Try BOM detection / StreamReader fallback
                        try
                        {
                            using var ms = new System.IO.MemoryStream(rawBytes);
                            using var sr = new System.IO.StreamReader(ms, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                            content = sr.ReadToEnd();
                        }
                        catch
                        {
                            // 3) Try system ANSI
                            try { content = System.Text.Encoding.Default.GetString(rawBytes); }
                            catch
                            {
                                // 4) Try Lithuanian code page
                                try { content = System.Text.Encoding.GetEncoding(1257).GetString(rawBytes); }
                                catch (Exception ex)
                                {
                                    content = string.Empty;
                                    Scriblucent.Services.CrashLog.Append("NoteCardControl.ImportTextFile", ex);
                                }
                            }
                        }
                    }

                    viewModel.Document.Content = content ?? string.Empty;
                    // Set note title to file name (without extension) if empty
                    if (string.IsNullOrWhiteSpace(viewModel.Document.Title))
                        viewModel.Document.Title = System.IO.Path.GetFileNameWithoutExtension(path);
                }

                var mainWindow = Window.GetWindow(this) as MainWindow;
                mainWindow?.OpenNoteEditor(viewModel);
            }
            catch
            {
                ModernMessageBox.Show(LocalizationService.GetString("FailedToOpenFile"), LocalizationService.GetString("Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void OnAssignScheduleMenuClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not NoteCardViewModel noteVm)
            return;

        if (Window.GetWindow(this) is not MainWindow mainWindow)
            return;

        mainWindow.OpenNoteSchedule(noteVm);
    }

    private void OnInfoMenuClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not NoteCardViewModel noteVm)
            return;

        if (Window.GetWindow(this) is not MainWindow mainWindow)
            return;

        mainWindow.ShowNoteInfo(noteVm);
    }
}