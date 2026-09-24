using Scriblucent.Services;

namespace Scriblucent.Tests;

public sealed class DiagnosticCleanupTests
{
    [Fact]
    public void CrashLogCleanup_RetainsOnlyRecentMetadataAndDropsLegacyContent()
    {
        var now = DateTimeOffset.UtcNow;
        var recent = $"[{now:O}] META AutoSave: System.InvalidOperationException (0x80131509)";
        var expired = $"[{now.AddDays(-31):O}] META AutoSave: System.InvalidOperationException (0x80131509)";
        var legacy = $"[{now:O}] AutoSave: System.InvalidOperationException: private note content";

        var retained = DiagnosticCleanup.RetainCrashMetadataLines(
            [recent, expired, legacy, "   at Save(private note content)"],
            now.AddDays(-30));

        Assert.Equal([recent], retained);
    }
}
