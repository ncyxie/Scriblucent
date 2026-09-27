using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Scriblucent.Models;
using Scriblucent.ViewModels;

namespace Scriblucent.Tests;

public sealed class PerformanceRegressionTests
{
    [Fact]
    public void MillionItemItemsControl_TemplateOwnedScrollerRealizesOnlyViewport()
    {
        RunOnSta(() =>
        {
            var items = Enumerable.Range(0, 1_000_000).ToArray();
            var control = new ItemsControl { Width = 500, Height = 420 };
            var viewerFactory = new FrameworkElementFactory(typeof(ScrollViewer));
            viewerFactory.SetValue(ScrollViewer.CanContentScrollProperty, true);
            viewerFactory.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            viewerFactory.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
            control.Template = new ControlTemplate(typeof(ItemsControl)) { VisualTree = viewerFactory };
            control.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
            VirtualizingPanel.SetIsVirtualizing(control, true);
            VirtualizingPanel.SetVirtualizationMode(control, VirtualizationMode.Recycling);
            VirtualizingPanel.SetScrollUnit(control, ScrollUnit.Pixel);
            var row = new FrameworkElementFactory(typeof(TextBlock));
            row.SetValue(FrameworkElement.HeightProperty, 36.0);
            row.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
            control.ItemTemplate = new DataTemplate { VisualTree = row };

            control.Measure(new Size(500, 420));
            control.Arrange(new Rect(0, 0, 500, 420));
            control.ItemsSource = items;
            control.UpdateLayout();

            var viewer = FindVisualChild<ScrollViewer>(control);
            var panel = FindVisualChild<VirtualizingStackPanel>(control);
            Assert.NotNull(viewer);
            Assert.NotNull(panel);
            Assert.True(viewer.ScrollableHeight > 0);
            Assert.InRange(panel.Children.Count, 1, 100);

            viewer.ScrollToVerticalOffset(viewer.ScrollableHeight / 2);
            control.UpdateLayout();
            Assert.InRange(panel.Children.Count, 1, 100);

            viewer.ScrollToEnd();
            control.UpdateLayout();
            Assert.InRange(panel.Children.Count, 1, 100);
        });
    }

    [Fact]
    public void TrashList_PixelScrollingKeepsRealizedRowsBounded()
    {
        RunOnSta(() =>
        {
            const int itemCount = 1_000_000;
            var source = new object();
            var entries = new TrashItemEntry[itemCount];
            var longTitle = string.Concat(Enumerable.Repeat("A long note title ", 30));
            for (var i = 0; i < entries.Length; i++)
                entries[i] = new TrashItemEntry(TrashItemKind.Note, Guid.Empty,
                    i % 3 == 0 ? longTitle : "Row", DateTime.UtcNow, source);
            var items = new LazyTrashItemList(entries, 30);

            var list = new ListBox
            {
                Width = 700,
                Height = 480,
                SelectionMode = SelectionMode.Extended
            };
            var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
            scroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
            scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
            list.Template = new ControlTemplate(typeof(ListBox)) { VisualTree = scroll };
            list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
            VirtualizingPanel.SetIsVirtualizing(list, true);
            VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);

            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TrashItemViewModel.Title)));
            text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
            text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            list.ItemTemplate = new DataTemplate { VisualTree = text };
            list.Measure(new Size(700, 480));
            list.Arrange(new Rect(0, 0, 700, 480));
            list.UpdateLayout();
            list.ItemsSource = items;
            list.UpdateLayout();

            Assert.Equal(itemCount, list.Items.Count);
            Assert.Empty(list.SelectedItems);

            var viewer = FindVisualChild<ScrollViewer>(list);
            Assert.NotNull(viewer);
            Assert.True(viewer.ScrollableHeight > 0);
            Assert.InRange(items.CreatedItemCount, 1, 100);

            viewer.ScrollToVerticalOffset(viewer.ScrollableHeight / 2);
            list.UpdateLayout();
            Assert.InRange(items.CreatedItemCount, 1, 200);

            viewer.ScrollToVerticalOffset(viewer.ScrollableHeight);
            list.UpdateLayout();
            Assert.InRange(items.CreatedItemCount, 1, 300);
        });
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;
            if (FindVisualChild<T>(child) is { } nested)
                return nested;
        }

        return null;
    }

    [Fact]
    public void TrashList_WpfViewKeepsRowsLazyAcrossFiltering()
    {
        RunOnSta(() =>
        {
            var entries = Enumerable.Range(0, 100_000)
                .Select(i => new TrashItemEntry(
                    i % 2 == 0 ? TrashItemKind.Note : TrashItemKind.Quiz,
                    Guid.NewGuid(), $"Item {i}", DateTime.UtcNow, new object()))
                .ToArray();
            var list = new LazyTrashItemList(entries, 30);
            var view = CollectionViewSource.GetDefaultView(list);

            Assert.IsType<ListCollectionView>(view);
            Assert.Equal(entries.Length, ((ListCollectionView)view).Count);
            Assert.InRange(list.CreatedItemCount, 0, 1);

            var first = list[0];
            var last = list[^1];
            Assert.Same(first, list[0]);
            Assert.Equal(2, list.CreatedItemCount);
            Assert.Equal(0, list.IndexOf(first));
            Assert.Equal(entries.Length - 1, list.IndexOf(last));

            var filtered = list.Filter(TrashItemKind.Note, "Item 2");
            Assert.True(filtered.Count > 0);
            Assert.Same(list[2], filtered[0]);
            Assert.Equal(0, filtered.IndexOf(list[2]));
            Assert.Equal(-1, filtered.IndexOf(first));
            Assert.Equal(-1, filtered.IndexOf(last));
            Assert.Equal(3, list.CreatedItemCount);
            Assert.Equal(entries.Length, list.EnumerateSources().Count());
            Assert.Equal(filtered.Count, filtered.EnumerateSources().Count());
            Assert.Equal(3, list.CreatedItemCount);

            var withoutFirst = list.ExcludeSources(new HashSet<object>(ReferenceEqualityComparer.Instance)
            {
                first.Source
            });
            Assert.Equal(entries.Length - 1, withoutFirst.Count);
            Assert.Equal(-1, withoutFirst.IndexOf(first));
            Assert.Same(list[1], withoutFirst[0]);
            Assert.Equal(4, list.CreatedItemCount);
        });
    }

    [Fact]
    public void ReplaceAll_SendsOneResetAndCountChange()
    {
        var collection = new BulkObservableCollection<int>([1, 2]);
        var changes = new List<NotifyCollectionChangedEventArgs>();
        var properties = new List<string?>();
        collection.CollectionChanged += (_, e) => changes.Add(e);
        ((INotifyPropertyChanged)collection).PropertyChanged += (_, e) => properties.Add(e.PropertyName);

        collection.ReplaceAll([3, 4, 5]);

        Assert.Equal([3, 4, 5], collection);
        Assert.Single(changes);
        Assert.Equal(NotifyCollectionChangedAction.Reset, changes[0].Action);
        Assert.Contains(nameof(collection.Count), properties);
        Assert.Contains("Item[]", properties);
    }

    [Fact]
    public void AddRange_TenThousandItemsSendsOneReset()
    {
        var collection = new BulkObservableCollection<int>();
        var changes = new List<NotifyCollectionChangedEventArgs>();
        var properties = new List<string?>();
        collection.CollectionChanged += (_, e) => changes.Add(e);
        ((INotifyPropertyChanged)collection).PropertyChanged += (_, e) => properties.Add(e.PropertyName);

        collection.AddRange(Enumerable.Range(0, 10_000));

        Assert.Equal(10_000, collection.Count);
        Assert.Single(changes);
        Assert.Equal(NotifyCollectionChangedAction.Reset, changes[0].Action);
        Assert.Contains(nameof(collection.Count), properties);
        Assert.Contains("Item[]", properties);
    }

    [Fact]
    public void LargeNoteDeleteAndRestore_UseOneResetPerTransfer()
    {
        RunOnSta(() =>
        {
            var cards = Enumerable.Range(0, 1_000)
                .Select(_ => new NoteCardViewModel(new NoteDocument(), _ => { }))
                .ToArray();
            var active = new BulkObservableCollection<NoteCardViewModel>(cards);
            var trash = new BulkObservableCollection<NoteCardViewModel>();
            var selected = cards.Take(800).ToArray();
            var activeChanges = new List<NotifyCollectionChangedAction>();
            var trashChanges = new List<NotifyCollectionChangedAction>();
            active.CollectionChanged += (_, e) => activeChanges.Add(e.Action);
            trash.CollectionChanged += (_, e) => trashChanges.Add(e.Action);

            MainViewModel.RemoveManyFromObservableCollection(active, selected);
            trash.AddRange(selected);

            Assert.Equal(cards.Skip(800), active);
            Assert.Equal(selected, trash);
            Assert.Equal([NotifyCollectionChangedAction.Reset], activeChanges);
            Assert.Equal([NotifyCollectionChangedAction.Reset], trashChanges);

            foreach (var card in selected)
            {
                card.Document.IsDeleted = true;
                card.Document.DeletedAt = DateTime.UtcNow;
            }
            MainViewModel.RemoveManyFromObservableCollection(trash, selected);
            MainViewModel.AddRestoredNotes(active, selected);

            Assert.Empty(trash);
            Assert.Equal(cards.Skip(800).Concat(selected), active);
            Assert.All(selected, card =>
            {
                Assert.False(card.Document.IsDeleted);
                Assert.Null(card.Document.DeletedAt);
            });
            Assert.Equal(2, activeChanges.Count);
            Assert.Equal(2, trashChanges.Count);

            MainViewModel.RemoveManyFromObservableCollection(active, active.ToArray());
            Assert.Empty(active);
            Assert.Equal(3, activeChanges.Count);
            Assert.All(activeChanges, action => Assert.Equal(NotifyCollectionChangedAction.Reset, action));
            Assert.All(trashChanges, action => Assert.Equal(NotifyCollectionChangedAction.Reset, action));
        });
    }

    [Fact]
    public void AddRange_RefreshesSortedFilteredWpfView()
    {
        RunOnSta(() =>
        {
            var collection = new BulkObservableCollection<SortableItem>([new(3, true)]);
            var view = CollectionViewSource.GetDefaultView(collection);
            view.Filter = item => ((SortableItem)item).Visible;
            view.SortDescriptions.Add(new SortDescription(nameof(SortableItem.Order), ListSortDirection.Ascending));

            collection.AddRange([new(2, true), new(1, true), new(0, false)]);

            Assert.Equal([1, 2, 3], view.Cast<SortableItem>().Select(item => item.Order));
            Assert.Equal(3, ((CollectionView)view).Count);
        });
    }

    [Theory]
    [InlineData("last-modified-desc")]
    [InlineData("last-modified-asc")]
    [InlineData("created-at-desc")]
    [InlineData("created-at-asc")]
    [InlineData("title-asc")]
    [InlineData("title-desc")]
    public void DirectNoteSort_MatchesPreviousWpfSort(string sortOptionKey)
    {
        RunOnSta(() =>
        {
            string[] titles = ["apple", "Apple", "Éclair", "eclair", "Zulu", "ä", "Beta", "beta"];
            var cards = Enumerable.Range(0, 32).Select(i => new NoteCardViewModel(new NoteDocument
            {
                Title = titles[i % titles.Length],
                IsPinned = i % 4 == 0,
                LastModified = new DateTime(2025, 1, 1).AddDays(i % 5),
                CreatedAt = new DateTime(2024, 1, 1).AddDays(i % 11)
            }, _ => { })).ToArray();

            var previous = CollectionViewSource.GetDefaultView(new BulkObservableCollection<NoteCardViewModel>(cards));
            previous.SortDescriptions.Add(new SortDescription("Document.IsPinned", ListSortDirection.Descending));
            switch (sortOptionKey)
            {
                case "last-modified-asc":
                    previous.SortDescriptions.Add(new SortDescription("Document.LastModified", ListSortDirection.Ascending));
                    previous.SortDescriptions.Add(new SortDescription("Document.Title", ListSortDirection.Ascending));
                    break;
                case "created-at-desc":
                case "created-at-asc":
                    previous.SortDescriptions.Add(new SortDescription("Document.CreatedAt", sortOptionKey == "created-at-asc" ? ListSortDirection.Ascending : ListSortDirection.Descending));
                    previous.SortDescriptions.Add(new SortDescription("Document.LastModified", ListSortDirection.Descending));
                    break;
                case "title-asc":
                case "title-desc":
                    previous.SortDescriptions.Add(new SortDescription("Document.Title", sortOptionKey == "title-asc" ? ListSortDirection.Ascending : ListSortDirection.Descending));
                    previous.SortDescriptions.Add(new SortDescription("Document.LastModified", ListSortDirection.Descending));
                    break;
                default:
                    previous.SortDescriptions.Add(new SortDescription("Document.LastModified", ListSortDirection.Descending));
                    previous.SortDescriptions.Add(new SortDescription("Document.CreatedAt", ListSortDirection.Descending));
                    break;
            }

            var direct = (ListCollectionView)CollectionViewSource.GetDefaultView(new BulkObservableCollection<NoteCardViewModel>(cards));
            direct.CustomSort = new MainViewModel.NoteCardSortComparer(sortOptionKey);

            Assert.Equal(previous.Cast<NoteCardViewModel>(), direct.Cast<NoteCardViewModel>());
        });
    }

    [Fact]
    public void DuplicatedCard_ReusesAlreadyDecodedPreview()
    {
        RunOnSta(() =>
        {
            var document = new NoteDocument { Content = "A cached preview" };
            var source = new NoteCardViewModel(document, _ => { });
            source.WarmContentPreview();
            var copy = new NoteCardViewModel(new NoteDocument { Content = document.Content }, _ => { });

            Assert.False(copy.IsContentPreviewCached);
            copy.CopyCachedContentPreviewFrom(source);

            Assert.True(copy.IsContentPreviewCached);
            Assert.Same(source.Content, copy.Content);

            var different = new NoteCardViewModel(new NoteDocument { Content = "Different" }, _ => { });
            different.CopyCachedContentPreviewFrom(source);
            Assert.False(different.IsContentPreviewCached);
        });
    }

    [Fact]
    public void DuplicatedImage_KeepsInlineLayoutAndCaption()
    {
        var source = new NoteImageAttachment
        {
            DataFilePath = "photo.png",
            WrapMode = NoteImageWrapMode.Inline,
            InlineAnchorOffset = 12,
            ZIndex = 3,
            CaptionLabel = "Figure",
            CaptionText = "A caption"
        };

        var copy = Assert.Single(MainViewModel.CloneImageAttachments([source]));

        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal(source.DataFilePath, copy.DataFilePath);
        Assert.Equal(source.WrapMode, copy.WrapMode);
        Assert.Equal(source.InlineAnchorOffset, copy.InlineAnchorOffset);
        Assert.Equal(source.ZIndex, copy.ZIndex);
        Assert.Equal(source.CaptionLabel, copy.CaptionLabel);
        Assert.Equal(source.CaptionText, copy.CaptionText);
    }

    [Fact]
    public void NoteCardMenuCommands_StayStableAndUseCurrentGroupState()
    {
        RunOnSta(() =>
        {
            var document = new NoteDocument();
            var actions = new List<string>();
            var card = new NoteCardViewModel(document,
                _ => actions.Add("delete"),
                _ => actions.Add("ungroup"),
                _ => actions.Add("duplicate"),
                _ => actions.Add("pin"));

            Assert.Same(card.DeleteCommand, card.DeleteCommand);
            Assert.Same(card.RemoveFromGroupCommand, card.RemoveFromGroupCommand);
            Assert.Same(card.DuplicateCommand, card.DuplicateCommand);
            Assert.Same(card.TogglePinCommand, card.TogglePinCommand);
            Assert.False(card.RemoveFromGroupCommand.CanExecute(null));

            document.GroupId = Guid.NewGuid();
            Assert.True(card.RemoveFromGroupCommand.CanExecute(null));
            card.DeleteCommand.Execute(null);
            card.RemoveFromGroupCommand.Execute(null);
            card.DuplicateCommand.Execute(null);
            card.TogglePinCommand.Execute(null);

            Assert.Equal(["delete", "ungroup", "duplicate", "pin"], actions);
        });
    }

    [Fact]
    public void NotesSaveSignature_IsFixedSizeAndDetectsStructuralChanges()
    {
        var first = new NoteDocument { Content = "one" };
        var second = new NoteDocument { Content = "two" };
        var notes = new List<NoteDocument> { first, second };
        var trash = new List<NoteDocument>();
        var groups = new List<NoteGroupData>();

        string Signature() => MainViewModel.ComputeNotesStoreSignature(
            notes, trash, trash.Count, groups);

        var original = Signature();
        Assert.Equal(64, original.Length);
        Assert.Equal(original, Signature());

        notes.Reverse();
        Assert.NotEqual(original, Signature());
        notes.Reverse();

        first.IsPinned = true;
        Assert.NotEqual(original, Signature());
        first.IsPinned = false;

        first.LastModified = first.LastModified.AddTicks(1);
        Assert.NotEqual(original, Signature());
        first.LastModified = first.LastModified.AddTicks(-1);

        trash.Add(notes[1]);
        notes.RemoveAt(1);
        Assert.NotEqual(original, Signature());
        notes.Add(second);
        trash.Clear();

        groups.Add(new NoteGroupData { GroupId = Guid.NewGuid(), Name = "A group" });
        Assert.NotEqual(original, Signature());
    }

    private sealed record SortableItem(int Order, bool Visible);

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
