namespace Scriblucent.Services.AI;

internal sealed record TextChunk(
    string Text,
    int Index,
    int TotalCount,
    int StartCharOffset,
    int EndCharOffset,
    int TokenCount
);
