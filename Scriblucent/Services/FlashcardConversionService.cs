using Scriblucent.Models;
using Scriblucent.Services.AI;

namespace Scriblucent.Services;

internal sealed class FlashcardConversionService
{
    private const int GeneratedSetIndex = 1;
    private readonly GroundedQuestionPlanner _questionPlanner;

    public FlashcardConversionService(
        ITextGenerationBackend backend,
        GroundedQuestionPlanner? questionPlanner = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _questionPlanner = questionPlanner ?? new GroundedQuestionPlanner();
    }

    public async Task<IReadOnlyList<FlashcardItem>> ConvertToFlashcardsAsync(
        StudyGenerationContext studyContext,
        IProgress<AiGenerationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(studyContext);

        var questionPlan = await _questionPlanner.GetOrCreateAsync(studyContext, progress, ct);
        return questionPlan.Questions.Select(question => new FlashcardItem
        {
            Question = question.Question,
            Answer = question.Answer,
            SetIndex = GeneratedSetIndex
        }).ToList();
    }
}
