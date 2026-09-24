namespace Scriblucent.Services.AI;

internal enum NaturalQuestionPolicyTier
{
    ClozeOnly,
    Contextual
}

internal enum GroundedQuestionStrategy
{
    ClozeOnly,
    SinglePair,
    Joint
}

internal sealed record AiModelCapabilities(
    NaturalQuestionPolicyTier NaturalQuestionTier,
    GroundedQuestionStrategy QuestionStrategy,
    bool UsesSemanticMetadata,
    bool UseNaturalQuestionSampling,
    TimeSpan InferenceTimeout,
    int ContextSize,
    int ContextPacketTargetLimit,
    int GroundingBatchSize)
{
    public static AiModelCapabilities Resolve(string modelKey)
    {
        if (string.Equals(modelKey, AiModelManifest.BasicModelKey, StringComparison.OrdinalIgnoreCase))
        {
            return new(NaturalQuestionPolicyTier.ClozeOnly, GroundedQuestionStrategy.ClozeOnly,
                false, false, TimeSpan.FromMinutes(2), 4096, 20, 4);
        }

        if (string.Equals(modelKey, AiModelManifest.CompactModelKey, StringComparison.OrdinalIgnoreCase))
        {
            return new(NaturalQuestionPolicyTier.Contextual, GroundedQuestionStrategy.SinglePair,
                false, false, TimeSpan.FromMinutes(4), 6144, 1, 1);
        }

        if (string.Equals(modelKey, AiModelManifest.AdvancedModelKey, StringComparison.OrdinalIgnoreCase))
        {
            return new(NaturalQuestionPolicyTier.Contextual, GroundedQuestionStrategy.Joint,
                true, true, TimeSpan.FromMinutes(10), 16384, 20, 6);
        }

        return new(NaturalQuestionPolicyTier.Contextual, GroundedQuestionStrategy.Joint,
            true, false, TimeSpan.FromMinutes(6), 8192, 12, 4);
    }

    public IReadOnlyList<int> ContextFallbackSizes => ContextSize switch
    {
        >= 16384 => [16384, 12288, 8192, 6144, 4096],
        >= 8192 => [8192, 6144, 4096],
        >= 6144 => [6144, 4096],
        _ => [4096]
    };
}
