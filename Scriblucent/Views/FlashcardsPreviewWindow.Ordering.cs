using Scriblucent.Services;

namespace Scriblucent.Views;

public partial class FlashcardsPreviewWindow
{
    internal ItemReorderAdapter CreateCardReorderAdapter() => new(this, "flashcards",
        source => source is FlashcardPreviewItem card
            ? BulkSelection.IsActive && BulkSelection.Contains(card.Id)
                ? _items.Where(item => BulkSelection.Contains(item.Id)).Cast<object>().ToArray() : [card] : [],
        (moving, target) => target is FlashcardPreviewItem anchor && moving.Count > 0
            && moving[0] is FlashcardPreviewItem card && card.SetIndex == anchor.SetIndex,
        (moving, target, after) => TryReorderCards(moving.Cast<FlashcardPreviewItem>().ToArray(), (FlashcardPreviewItem)target, after),
        () => !_isStudyMode && !_isCramMode);

    internal bool TryReorderCards(IReadOnlyList<FlashcardPreviewItem> moving, FlashcardPreviewItem anchor, bool after)
    {
        var live = _allItems.ToHashSet();
        if (moving.Count == 0 || moving.Contains(anchor) || !live.Contains(anchor)
            || moving.Any(item => item.SetIndex != anchor.SetIndex || !live.Contains(item))) return false;
        var selected = moving.ToHashSet();
        if (ItemOrdering.Move(_items, selected, anchor, after).SequenceEqual(_items)) return false;
        // Shuffle is a temporary viewing/study action. An intentional move adopts the
        // currently visible arrangement, while retaining the relative order of hidden cards.
        var visible = _items.ToHashSet();
        int displayIndex = 0;
        var baseline = _allItems.Select(item => visible.Contains(item) ? _items[displayIndex++] : item).ToList();
        var result = ItemOrdering.Move(baseline, selected, anchor, after);
        if (result.SequenceEqual(_allItems)) return false;
        var studyItem = _studyModeIndex >= 0 && _studyModeIndex < _items.Count ? _items[_studyModeIndex] : null;
        _allItems.Clear(); _allItems.AddRange(result);
        ApplyFilters(preserveStudyPosition: true);
        if (studyItem is not null && _items.Contains(studyItem)) _studyModeIndex = _items.IndexOf(studyItem);
        UpdateEditedIndicator(); return true;
    }
    private void InsertCardsAtStart(int set, IEnumerable<FlashcardPreviewItem> cards)
    {
        var added = cards.ToArray();
        if (added.Length == 0) return;
        int index = _allItems.FindIndex(item => item.SetIndex == set);
        _allItems.InsertRange(index < 0 ? 0 : index, added);
    }
}
