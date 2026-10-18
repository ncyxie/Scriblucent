using System.Collections;
using System.ComponentModel;
using System.Windows.Data;
using Scriblucent.Services;

namespace Scriblucent.ViewModels;

public partial class MainViewModel
{
    internal const string SortManual = "manual";
    private readonly ManualItemOrder _noteManualOrder = new(), _flashcardManualOrder = new(),
        _mindMapManualOrder = new(), _quizManualOrder = new();
    private int _manualOrderRevision;

    internal readonly record struct DashboardOrderItem(object Item, Guid Id, Guid? Group, bool Pinned, DashboardMotionScope Scope)
    {
        public static DashboardOrderItem From(object item) => item switch
        {
            NoteCardViewModel n => new(n, n.Document.Id, n.Document.GroupId, n.Document.IsPinned, DashboardMotionScope.Notes),
            FlashcardSetViewModel n => new(n, n.Document.Id, n.Document.GroupId, n.Document.IsPinned, DashboardMotionScope.Flashcards),
            MindMapViewModel n => new(n, n.Document.Id, n.Document.GroupId, n.Document.IsPinned, DashboardMotionScope.MindMaps),
            QuizViewModel n => new(n, n.Document.Id, n.Document.GroupId, n.Document.IsPinned, DashboardMotionScope.Quizzes),
            _ => throw new ArgumentException("Not a dashboard item.", nameof(item))
        };
        public void SetGroup(Guid? group)
        {
            switch (Item)
            {
                case NoteCardViewModel n: n.Document.GroupId = group; n.NotifyGroupChanged(); break;
                case FlashcardSetViewModel n: n.Document.GroupId = group; n.NotifyChanged(); break;
                case MindMapViewModel n: n.Document.GroupId = group; n.NotifyChanged(); break;
                case QuizViewModel n: n.Document.GroupId = group; n.NotifyChanged(); break;
            }
        }
    }

    private ManualItemOrder ManualOrder(DashboardMotionScope scope) => scope switch
    {
        DashboardMotionScope.Flashcards => _flashcardManualOrder,
        DashboardMotionScope.MindMaps => _mindMapManualOrder,
        DashboardMotionScope.Quizzes => _quizManualOrder, _ => _noteManualOrder
    };
    internal string DashboardSortKey(DashboardMotionScope scope) => scope switch
    {
        DashboardMotionScope.Flashcards => _selectedFlashcardSortOptionKey,
        DashboardMotionScope.MindMaps => _selectedMindMapSortOptionKey,
        DashboardMotionScope.Quizzes => _selectedQuizSortOptionKey, _ => _selectedSortOptionKey
    };
    internal IEnumerable<object> DashboardOrderItems(DashboardMotionScope scope, bool trash = false) => scope switch
    {
        DashboardMotionScope.Flashcards => trash ? TrashedFlashcardSets : FlashcardSets,
        DashboardMotionScope.MindMaps => trash ? TrashedMindMaps : MindMaps,
        DashboardMotionScope.Quizzes => trash ? TrashedQuizzes : Quizzes,
        _ => trash ? TrashedNotes : Notes
    };
    private IEnumerable<object> SortedDashboardOrderItems(DashboardMotionScope scope) => scope switch
    {
        DashboardMotionScope.Flashcards => SortFlashcardSets(FlashcardSets),
        DashboardMotionScope.MindMaps => SortMindMaps(MindMaps),
        DashboardMotionScope.Quizzes => SortQuizzes(Quizzes), _ => SortNotes(Notes)
    };
    private void ReconcileManualOrder(DashboardMotionScope scope)
    {
        var ids = DashboardOrderItems(scope).Concat(DashboardOrderItems(scope, true)).Select(item => DashboardOrderItem.From(item).Id);
        if (!ManualOrder(scope).HasOrder && DashboardSortKey(scope) == SortManual) ManualOrder(scope).Set(ids);
        else ManualOrder(scope).Reconcile(ids);
    }
    private void RememberDashboardCopies(DashboardMotionScope scope, IEnumerable<Guid> sources, IEnumerable<Guid> copies)
    {
        var order = ManualOrder(scope);
        if (!order.HasOrder) return;
        var pairs = sources.Zip(copies).ToDictionary(pair => pair.First, pair => pair.Second);
        order.Set(order.Ids.SelectMany(id => pairs.TryGetValue(id, out var copy) ? new[] { id, copy } : new[] { id }));
    }
    private void EnsureManualOrder(DashboardMotionScope scope)
    {
        if (!ManualOrder(scope).HasOrder)
        {
            ManualOrder(scope).Set(SortedDashboardOrderItems(scope).Concat(DashboardOrderItems(scope, true))
                .Select(item => DashboardOrderItem.From(item).Id));
            SaveManualOrder(scope);
        }
        else ReconcileManualOrder(scope);
    }
    private bool ApplyManualViewSort(ICollectionView view, DashboardMotionScope scope)
    {
        if (view is not ListCollectionView list) return false;
        if (DashboardSortKey(scope) != SortManual) { list.CustomSort = null; return false; }
        list.CustomSort = new ManualDashboardComparer(ManualOrder(scope));
        return true;
    }
    private sealed class ManualDashboardComparer(ManualItemOrder order) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            var left = DashboardOrderItem.From(x!); var right = DashboardOrderItem.From(y!);
            var pinned = right.Pinned.CompareTo(left.Pinned);
            return pinned != 0 ? pinned : order.Position(left.Id).CompareTo(order.Position(right.Id));
        }
    }
    internal IReadOnlyList<object> DashboardDragItems(object source)
    {
        var item = DashboardOrderItem.From(source);
        if (!IsMassSelectMode || !DashboardSelection.Contains(item.Id)) return [source];
        var selected = SortedDashboardOrderItems(item.Scope).Where(value => DashboardSelection.Contains(DashboardOrderItem.From(value).Id)).ToArray();
        if (selected.All(value => DashboardOrderItem.From(value) is var other && other.Group == item.Group && other.Pinned == item.Pinned)) return selected;
        _dashboardSelectionScope?.ReportReorderStatus();
        return [];
    }
    internal bool CanReorderDashboard(IReadOnlyList<object> moving, object target)
    {
        if (moving.Count == 0 || moving.Contains(target)) return false;
        var destination = DashboardOrderItem.From(target); var first = DashboardOrderItem.From(moving[0]);
        return moving.All(value => DashboardOrderItem.From(value) is var item && item.Scope == destination.Scope
            && item.Pinned == destination.Pinned && item.Group == first.Group);
    }
    internal bool TryReorderDashboard(IReadOnlyList<object> moving, object target, bool after)
    {
        if (!CanReorderDashboard(moving, target)) return false;
        var destination = DashboardOrderItem.From(target);
        var entries = SortedDashboardOrderItems(destination.Scope).Select(DashboardOrderItem.From).ToArray();
        var live = entries.Select(item => item.Item).ToHashSet();
        if (!live.Contains(target) || moving.Any(item => !live.Contains(item))) return false;
        var ids = moving.Select(item => DashboardOrderItem.From(item).Id).ToHashSet();
        var sameGroup = moving.All(item => DashboardOrderItem.From(item).Group == destination.Group);
        var context = entries.Where(item => item.Group == destination.Group && item.Pinned == destination.Pinned).Select(item => item.Id).ToList();
        if (sameGroup && ItemOrdering.Move(context, ids, destination.Id, after).SequenceEqual(context)) return false;
        var order = ManualOrder(destination.Scope);
        // Capture the complete, unfiltered automatic arrangement only on a successful move.
        if (DashboardSortKey(destination.Scope) != SortManual || !order.HasOrder)
            order.Set(entries.Select(item => item.Id).Concat(DashboardOrderItems(destination.Scope, true).Select(item => DashboardOrderItem.From(item).Id)));
        else ReconcileManualOrder(destination.Scope);
        if (!order.Move(ids, destination.Id, after) && sameGroup) return false;
        foreach (var item in moving.Select(DashboardOrderItem.From))
            if (item.Group != destination.Group) item.SetGroup(destination.Group);
        switch (destination.Scope)
        {
            case DashboardMotionScope.Flashcards: _selectedFlashcardSortOptionKey = SortManual; NormalizeFlashcardSetGroups(); break;
            case DashboardMotionScope.MindMaps: _selectedMindMapSortOptionKey = SortManual; NormalizeMindMapGroups(); break;
            case DashboardMotionScope.Quizzes: _selectedQuizSortOptionKey = SortManual; NormalizeQuizGroups(); break;
            default: _selectedSortOptionKey = SortManual; NormalizeGroups(); break;
        }
        _manualOrderRevision++;
        RefreshManualOrder(destination.Scope);
        SaveManualOrder(destination.Scope); SaveAppSettings();
        return true;
    }
    private void RefreshManualOrder(DashboardMotionScope scope)
    {
        switch (scope)
        {
            case DashboardMotionScope.Flashcards: ApplySortToFlashcardSetsView(); RebuildFlashcardSetGroups(); UpdateFlashcardSortOptionSelection(); break;
            case DashboardMotionScope.MindMaps: ApplySortToMindMapsView(); RebuildMindMapGroups(); UpdateMindMapSortOptionSelection(); break;
            case DashboardMotionScope.Quizzes: ApplySortToQuizzesView(); RebuildQuizGroups(); UpdateQuizSortOptionSelection(); break;
            default: ApplySortToUngroupedView(); RebuildGroups(); UpdateSortOptionSelection(); OnPropertyChanged(nameof(SortButtonText)); break;
        }
        OnPropertyChanged(nameof(ActiveSortButtonText));
        _dashboardSelectionScope?.RefreshItems();
    }
    private void SaveManualOrder(DashboardMotionScope scope)
    {
        switch (scope)
        {
            case DashboardMotionScope.Flashcards: SaveFlashcardSets(); break;
            case DashboardMotionScope.MindMaps: SaveMindMaps(); break;
            case DashboardMotionScope.Quizzes: SaveQuizzes(); break;
            default: SaveNotes(); break;
        }
    }

    internal ItemReorderAdapter CreateDashboardReorderAdapter(bool groups) => groups
        ? new(this, "dashboard-groups", source => IsDashboardGroup(source) ? [source] : [],
            (moving, target) => moving.Count == 1 && IsDashboardGroup(target) && moving[0].GetType() == target.GetType() && !ReferenceEquals(moving[0], target),
            TryReorderDashboardGroups, () => IsActiveDashboardReady)
        : new(this, "dashboard-items", DashboardDragItems,
            (moving, target) => moving.Count > 0 && DashboardOrderItem.From(moving[0]) is var first
                && DashboardOrderItem.From(target) is var destination && first.Scope == destination.Scope && first.Pinned == destination.Pinned,
            TryReorderDashboard, () => IsActiveDashboardReady);

    internal static bool IsDashboardGroup(object item) => item is NoteGroupViewModel or FlashcardSetGroupViewModel or MindMapGroupViewModel or QuizGroupViewModel;
    internal bool TryReorderDashboardGroups(IReadOnlyList<object> moving, object target, bool after)
    {
        if (moving.Count != 1 || moving[0].GetType() != target.GetType() || ReferenceEquals(moving[0], target)) return false;
        bool Apply<T>(IList<T> groups, Func<T, Guid> key, IEnumerable<Guid> completeOrder, Action<Guid, int> setOrder, DashboardMotionScope scope) where T : class
        {
            if (!groups.Contains((T)moving[0]) || !groups.Contains((T)target)) return false;
            var ids = completeOrder.ToList();
            var result = ItemOrdering.Move(ids, new HashSet<Guid> { key((T)moving[0]) }, key((T)target), after);
            if (result.SequenceEqual(ids)) return false;
            for (int i = 0; i < result.Count; i++) setOrder(result[i], i);
            var ranks = result.Select((id, rank) => (id, rank)).ToDictionary(pair => pair.id, pair => pair.rank);
            _manualOrderRevision++;
            SyncObservableCollectionOrder((System.Collections.ObjectModel.ObservableCollection<T>)groups, groups.OrderBy(item => ranks[key(item)]).ToList());
            SaveManualOrder(scope); return true;
        }
        return target switch
        {
            NoteGroupViewModel => Apply(NoteGroups, item => item.GroupId, _groupMetadata.Values.OrderBy(item => item.SortOrder ?? int.MaxValue).Select(item => item.GroupId), (id, rank) => EnsureGroupMetadata(id).SortOrder = rank, DashboardMotionScope.Notes),
            FlashcardSetGroupViewModel => Apply(FlashcardSetGroups, item => item.GroupId, _flashcardSetGroupMetadata.Values.OrderBy(item => item.SortOrder ?? int.MaxValue).Select(item => item.GroupId), (id, rank) => EnsureFlashcardSetGroupMetadata(id).SortOrder = rank, DashboardMotionScope.Flashcards),
            MindMapGroupViewModel => Apply(MindMapGroups, item => item.GroupId, _mindMapGroupMetadata.Values.OrderBy(item => item.SortOrder ?? int.MaxValue).Select(item => item.GroupId), (id, rank) => _mindMapGroupMetadata[id].SortOrder = rank, DashboardMotionScope.MindMaps),
            QuizGroupViewModel => Apply(QuizGroups, item => item.GroupId, _quizGroupMetadata.Values.OrderBy(item => item.SortOrder ?? int.MaxValue).Select(item => item.GroupId), (id, rank) => EnsureQuizGroupMetadata(id).SortOrder = rank, DashboardMotionScope.Quizzes),
            _ => false
        };
    }
}
