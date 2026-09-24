namespace Scriblucent.Services.AI;

internal sealed record AiModelInfo(
    string Key,
    string FileName,
    string DisplayName,
    string DescriptionResourceKey,
    string DefaultDescription,
    string DownloadStatusKey,
    long MinimumBytes,
    long MinimumSupportedMemoryBytes,
    long MinimumRecommendedMemoryBytes,
    string ExpectedSha256,
    string[] Urls);
