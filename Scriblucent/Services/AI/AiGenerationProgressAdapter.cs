namespace Scriblucent.Services.AI;

internal static class AiGenerationProgressAdapter
{
    public static AiGenerationProgress FromFlashcardProgress(BundledModelHostService.FlashcardProgress status)
    {
        var genStatus = MapStatusKey(status.StatusKey);
        return new AiGenerationProgress(
            Status: genStatus,
            Percent: status.Percent,
            GeneratedChars: status.GeneratedChars,
            ChunkIndex: status.ChunkIndex,
            ChunkCount: status.ChunkCount
        );
    }

    private static AiGenerationStatus MapStatusKey(string statusKey)
    {
        return statusKey switch
        {
            "ConvertToFlashcardsStatusPreparingAssets"
            or "ConvertToMindMapStatusPreparingAssets"
            or "ConvertToTestStatusPreparingAssets" => AiGenerationStatus.PreparingAssets,

            "ConvertToFlashcardsStatusAssetsReady"
            or "ConvertToMindMapStatusAssetsReady"
            or "ConvertToTestStatusAssetsReady" => AiGenerationStatus.AssetsReady,

            "ConvertToFlashcardsStatusProcessing"
            or "ConvertToMindMapStatusProcessing"
            or "ConvertToTestStatusProcessing" => AiGenerationStatus.Processing,

            "ConvertToFlashcardsStatusRepairing"
            or "ConvertToMindMapStatusRepairing"
            or "ConvertToTestStatusRepairing" => AiGenerationStatus.Repairing,

            "ConvertToFlashcardsStatusFinalizing"
            or "ConvertToMindMapStatusFinalizing"
            or "ConvertToTestStatusFinalizing" => AiGenerationStatus.Finalizing,

            _ => AiGenerationStatus.Processing
        };
    }
}
