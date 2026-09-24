namespace Scriblucent.Services.AI;

public enum AiGenerationStatus
{
    PreparingAssets,
    VerifyingModel,
    DownloadingModel,
    AssetsReady,
    Processing,
    Finalizing,
    AnalyzingSource,
    ValidatingQuestionAnswer,
    SelectingStudyPoints,
    WritingQuestions,
    GroundingAnswers,
    FallingBackToCpu
}
