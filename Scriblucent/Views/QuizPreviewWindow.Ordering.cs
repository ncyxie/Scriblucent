using Scriblucent.Services;

namespace Scriblucent.Views;

public partial class QuizPreviewWindow
{
    internal ItemReorderAdapter CreateQuestionReorderAdapter() => new(this, "questions",
        source => source is QuizPreviewQuestion question
            ? BulkSelection.IsActive && BulkSelection.Contains(question.Id)
                ? _questions.Where(item => BulkSelection.Contains(item.Id)).Cast<object>().ToArray() : [question] : [],
        (moving, target) => target is QuizPreviewQuestion question && _trackedQuestions.Contains(question) && moving.Count > 0,
        (moving, target, after) => TryReorderQuestions(moving.Cast<QuizPreviewQuestion>().ToArray(), (QuizPreviewQuestion)target, after), () => IsEditMode);

    internal ItemReorderAdapter? CreateOptionReorderAdapter(object context) => context is QuizPreviewQuestion question
        ? new(question, "options", source => source is QuizPreviewOption option ? [option] : [],
            (moving, target) => question.CanReorderOptions && target is QuizPreviewOption option && question.Options.Contains(option)
                && moving.Count == 1 && moving[0] is QuizPreviewOption card && question.Options.Contains(card) && !ReferenceEquals(card, option),
            (moving, target, after) => TryReorderOptions(question, (QuizPreviewOption)moving[0], (QuizPreviewOption)target, after), () => IsEditMode)
        : null;

    internal bool TryReorderQuestions(IReadOnlyList<QuizPreviewQuestion> moving, QuizPreviewQuestion anchor, bool after)
    {
        var live = _questions.ToHashSet();
        if (!IsEditMode || moving.Count == 0 || moving.Any(item => !live.Contains(item)) || !live.Contains(anchor)) return false;
        var result = ItemOrdering.Move(_questions, moving.ToHashSet(), anchor, after);
        if (result.SequenceEqual(_questions)) return false;
        CommitWorkspaceEdits();
        _bulkQuestionEdit = true;
        try
        {
            // Retain instances, media, correctness and selection. Only their order changes.
            if (moving.Count == 1) _questions.Move(_questions.IndexOf(moving[0]), result.IndexOf(moving[0]));
            else _questions.ReplaceAll(result);
            RenumberQuestions();
        }
        finally { _bulkQuestionEdit = false; }
        _questionsChangeVersion++; BulkSelection.SetItems(_questions.Select(item => item.Id));
        UpdateSummary(); UpdateEditedIndicator(); SyncBulkSelectionChrome(); return true;
    }
    internal bool TryReorderOptions(QuizPreviewQuestion question, QuizPreviewOption moving, QuizPreviewOption anchor, bool after)
    {
        if (!IsEditMode || !question.CanReorderOptions || !_questions.Contains(question) || !question.Options.Contains(moving) || !question.Options.Contains(anchor)) return false;
        var result = ItemOrdering.Move(question.Options, new HashSet<QuizPreviewOption> { moving }, anchor, after);
        if (result.SequenceEqual(question.Options)) return false;
        int index = result.IndexOf(moving); question.Options.Move(question.Options.IndexOf(moving), index);
        question.RefreshOptionOrderState(); return true;
    }
    private void InsertQuestionsAtStart(IEnumerable<QuizPreviewQuestion> questions)
    {
        var added = questions.ToArray();
        if (added.Length == 0) return;
        _bulkQuestionEdit = true;
        try { _questions.ReplaceAll(added.Concat(_questions).ToArray()); RenumberQuestions(); }
        finally { _bulkQuestionEdit = false; }
        _questionsChangeVersion++; BulkSelection.SetItems(_questions.Select(item => item.Id));
        UpdateEditedIndicator();
    }
}
