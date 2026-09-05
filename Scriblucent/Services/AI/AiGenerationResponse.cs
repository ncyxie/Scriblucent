namespace Scriblucent.Services.AI;

internal sealed record AiGenerationResponse(
    string Output,
    int GeneratedChars,
    TimeSpan Elapsed
);
