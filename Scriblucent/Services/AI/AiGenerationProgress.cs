namespace Scriblucent.Services.AI;

internal sealed record AiGenerationProgress(
    AiGenerationStatus Status,
    int? Percent = null,
    int? GeneratedChars = null,
    int? PacketIndex = null,
    int? PacketCount = null,
    int? SuccessfulPacketCount = null,
    int? TotalPacketCount = null,
    int? ModelGeneratedItemCount = null,
    int? FallbackItemCount = null,
    string? GenerationDiagnostics = null
);
