using Scriblucent.Services;
using Scriblucent.Services.AI;

namespace Scriblucent.Tests;

[CollectionDefinition(DiagnosticsCollectionName, DisableParallelization = true)]
public sealed class AiGenerationTraceCollection
{
    public const string DiagnosticsCollectionName = "AI generation diagnostics";
}

[Collection(AiGenerationTraceCollection.DiagnosticsCollectionName)]
public sealed class AiGenerationTraceTests
{
    [Fact]
    public void StartJob_WithAllProductionGates_WritesSourceAndCallDetails()
    {
        using var scope = new TemporaryDiagnosticsScope();
        var settings = AppSettingsService.Load();
        var previousDiagnostics = settings.EnableDiagnosticLogging;
        var previousContent = settings.EnableAiContentDiagnostics;
        var previousTrace = Environment.GetEnvironmentVariable("SCRIBLUCENT_AI_TRACE");
        try
        {
            settings.EnableDiagnosticLogging = true;
            settings.EnableAiContentDiagnostics = true;
            Environment.SetEnvironmentVariable("SCRIBLUCENT_AI_TRACE", "1");
            using var tracer = AiGenerationTracer.StartJob(
                "test-trace", "Study planning", "fake-model", "private note");
            Assert.NotNull(tracer);

            var request = new AiGenerationRequest(
            "the prompt body", 1600, 0.4, Grammar: "root ::= \"x\"\n", GrammarRoot: "studyquestions");
            tracer.LogCallStart(0, "question-grounding", request);
            tracer.AppendStream("tokens");
            tracer.LogCallEnd(0, new AiGenerationResponse("tokens", 6, TimeSpan.FromMilliseconds(1234)));
            tracer.LogOutcome("success");

            var created = Assert.Single(Directory.GetFiles(scope.Path, "gen-test-trace-*.log"));
            var log = File.ReadAllText(created);
            Assert.Contains("Purpose: Study planning", log);
            Assert.Contains("ModelKey: fake-model", log);
            Assert.Contains("private note", log);
            Assert.Contains("the prompt body", log);
            Assert.Contains("root ::= \"x\"", log);
            Assert.Contains("tokens", log);
            Assert.Contains("ElapsedMs=1234", log);
            Assert.Contains("=== JOB OUTCOME === success", log);
        }
        finally
        {
            settings.EnableDiagnosticLogging = previousDiagnostics;
            settings.EnableAiContentDiagnostics = previousContent;
            Environment.SetEnvironmentVariable("SCRIBLUCENT_AI_TRACE", previousTrace);
        }
    }

    [Theory]
    [InlineData(false, true, "1")]
    [InlineData(true, false, "1")]
    [InlineData(true, true, null)]
    public void StartJob_WithoutAllProductionGates_DoesNotCreateTrace(
        bool diagnosticLogging, bool contentDiagnostics, string? traceGate)
    {
        using var scope = new TemporaryDiagnosticsScope();
        var settings = AppSettingsService.Load();
        var previousDiagnostics = settings.EnableDiagnosticLogging;
        var previousContent = settings.EnableAiContentDiagnostics;
        var previousTrace = Environment.GetEnvironmentVariable("SCRIBLUCENT_AI_TRACE");
        try
        {
            settings.EnableDiagnosticLogging = diagnosticLogging;
            settings.EnableAiContentDiagnostics = contentDiagnostics;
            Environment.SetEnvironmentVariable("SCRIBLUCENT_AI_TRACE", traceGate);

            using var tracer = AiGenerationTracer.StartJob("test-trace", "Study planning", "fake-model", "private note");

            Assert.Null(tracer);
            Assert.Empty(Directory.GetFiles(scope.Path, "gen-test-trace-*.log"));
        }
        finally
        {
            settings.EnableDiagnosticLogging = previousDiagnostics;
            settings.EnableAiContentDiagnostics = previousContent;
            Environment.SetEnvironmentVariable("SCRIBLUCENT_AI_TRACE", previousTrace);
        }
    }

    private sealed class TemporaryDiagnosticsScope : IDisposable
    {
        private readonly string? _previousPath;
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"scriblucent-ai-trace-{Guid.NewGuid():N}");

        public TemporaryDiagnosticsScope()
        {
            _previousPath = Environment.GetEnvironmentVariable("SCRIBLUCENT_AI_DIAGNOSTICS_DIR");
            Directory.CreateDirectory(Path);
            Environment.SetEnvironmentVariable("SCRIBLUCENT_AI_DIAGNOSTICS_DIR", Path);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("SCRIBLUCENT_AI_DIAGNOSTICS_DIR", _previousPath);
            Directory.Delete(Path, recursive: true);
        }
    }
}
