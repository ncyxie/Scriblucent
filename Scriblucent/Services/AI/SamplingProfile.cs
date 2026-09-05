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
    // Qwen3.5 non-thinking mode, grammar-constrained structured output.
    // Based on Unsloth recommendations with presence_penalty=0.0 - grammar handles format
    // enforcement, and presence penalty actively hurts here: it penalizes any previously-used
    // token regardless of frequency, which includes structural tokens the grammar requires to
    // repeat many times (e.g. "q:", "correct:", newlines) - verified directly that a nonzero
    // presence penalty makes the model bail out early since continuing the format itself gets
    // penalized, undershooting the requested card/question count.
    //
    // RepetitionPenalty is NOT neutral (1.0) though, unlike the original tuning - grammar only
    // constrains syntax, not semantic novelty, so with no frequency penalty at all the model would
    // happily keep emitting grammatically-valid but semantically duplicate content (the same
    // flashcard/branch/question reworded, or verbatim) until MaxTokens, once it ran out of
    // genuinely new things to say about the note. Verified directly against the real bundled
    // model: RepetitionPenalty=1.0 produced runaway repeat loops burning the entire token budget
    // (partly masked by DeduplicateFlashcards/MergeMindMap's exact-match dedup, but still wasting
    // most of the generation's wall-clock time); 1.08 lets the model stop naturally once it's
    // actually said everything relevant, with no loss of quality or coverage.
    public static SamplingProfile Default { get; } = new(
        Temperature: 0.7,
        TopP: 0.8,
        TopK: 20,
        PresencePenalty: 0.0,
        RepetitionPenalty: 1.08
    );

    // Lower temperature variant for repairs (more conservative).
    public static SamplingProfile Repair { get; } = new(
        Temperature: 0.4,
        TopP: 0.8,
        TopK: 20,
        PresencePenalty: 0.0,
        RepetitionPenalty: 1.08
    );
}
