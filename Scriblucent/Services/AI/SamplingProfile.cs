namespace Scriblucent.Services.AI;

internal sealed record SamplingProfile(
    double Temperature,
    double TopP,
    int TopK,
    double PresencePenalty,
    double RepetitionPenalty
);

internal static class SamplingProfiles
{
    public static SamplingProfile Grounded { get; } = new(
        Temperature: 0.4,
        TopP: 0.8,
        TopK: 20,
        PresencePenalty: 0.0,
        RepetitionPenalty: 1.0
    );

    public static SamplingProfile SmallQuestionDraft { get; } = new(
        Temperature: 0.4,
        TopP: 0.8,
        TopK: 20,
        PresencePenalty: 0.0,
        RepetitionPenalty: 1.0
    );

    public static SamplingProfile NaturalQuestion { get; } = new(
        Temperature: 0.7,
        TopP: 0.8,
        TopK: 20,
        PresencePenalty: 0.0,
        RepetitionPenalty: 1.0
    );

    public static SamplingProfile Classification { get; } = new(
        Temperature: 0.0,
        TopP: 1.0,
        TopK: 1,
        PresencePenalty: 0.0,
        RepetitionPenalty: 1.0
    );
}
