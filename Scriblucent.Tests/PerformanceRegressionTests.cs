using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Data;
using Scriblucent.Models;
using Scriblucent.ViewModels;

namespace Scriblucent.Tests;

public sealed class PerformanceRegressionTests
{
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
