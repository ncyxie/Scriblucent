using Scriblucent.Services;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Scriblucent.Services.AI;

public enum StudyCoverageMode
{
    Auto,
    Focused,
    Balanced,
    Thorough
}

internal enum StudyFactImportance
{
    Core,
    Supporting,
    Detail
}

internal enum StudyAnswerKind
{
    Text,
    Date,
    Quantity,
    Entity,
    Term,
    Person,
    Place,
    Organization,
    List
}

internal enum StudyValueShape
{
    Text,
    CalendarDate,
    Year,
    ScopedQuantity,
    AtomicNumber,
    OrdinalRole
}

internal sealed record SourceSpan(int Start, int Length)
{
    public int End => Start + Length;
    public bool IsValidFor(string source) => Start >= 0 && Length > 0 && End <= source.Length;
}

internal sealed record StudyFact(
    string Id,
    string SourceUnitId,
    string Evidence,
    string Answer,
    StudyFactImportance Importance,
    int SourceOrder,
    StudyAnswerKind AnswerKind = StudyAnswerKind.Text,
    string QuestionContext = "",
    int ContextStart = -1,
    int AnswerStart = -1,
    int AnswerLength = 0,
    bool IsCoverageFallback = false,
    StudyValueShape ValueShape = StudyValueShape.Text,
    bool IsComplete = true,
    bool IsPrimaryCandidate = false,
    bool IsExplicitList = false,
    string ContextPacketId = "",
    IReadOnlyList<string> AnswerItems = null!,
    string CandidateQuestion = "");

internal sealed record StudySourceUnitSnapshot(
    string Id,
    string Text,
    int Order,
    int SectionIndex,
    int Start);

internal sealed record StudyContextPacket(
    string Id,
    string Context,
    IReadOnlyList<string> OwnedSourceUnitIds,
    int TargetCount,
    int CoreStart,
    int CoreLength);

internal sealed record StudyPlan(
    string ContentHash,
    string ModelKey,
    int PlannerVersion,
    DateTimeOffset CreatedAt,
    IReadOnlyList<StudyFact> Facts,
    int SourceUnitCount = 0,
    int CandidateCount = 0,
    bool InventoryTruncated = false,
    int ClassificationFallbackBatchCount = 0,
    IReadOnlyList<StudySourceUnitSnapshot> SourceUnits = null!,
    IReadOnlyList<StudyContextPacket> ContextPackets = null!,
    StudyCoverageMode CoverageMode = StudyCoverageMode.Auto,
    int SourceTokenCount = 0,
    int RequestedTargetCount = 0);

internal sealed record StudyGenerationContext(
    StudyPlan Plan,
    StudyCoverageMode CoverageMode,
    IReadOnlyList<StudyFact> SelectedFacts)
{
    public string ModelKey => Plan.ModelKey;
    public int TargetCount => SelectedFacts.Count;
}

internal sealed class StudyPlanCache
{
    public static StudyPlanCache Shared { get; } = new();

    internal const int MaximumEntries = 100;
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromDays(30);

    private readonly object _syncRoot = new();
    private List<StudyPlan> _plans = [];

    public StudyPlanCache()
    {
        // Plans contain source units, evidence, answers, and packet context. Keep them in memory
        // only so note content is not retained after the app exits.
        DeleteLegacyPlaintextCache();
    }

    public StudyPlan? Find(
        string contentHash,
        string modelKey,
        int plannerVersion,
        StudyCoverageMode coverageMode = StudyCoverageMode.Auto)
    {
        lock (_syncRoot)
        {
            var cutoff = DateTimeOffset.UtcNow - MaximumAge;
            return _plans.FirstOrDefault(plan =>
                plan.CreatedAt >= cutoff
                && plan.PlannerVersion == plannerVersion
                && plan.CoverageMode == coverageMode
                && string.Equals(plan.ContentHash, contentHash, StringComparison.Ordinal)
                && string.Equals(plan.ModelKey, modelKey, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Store(StudyPlan plan)
    {
        lock (_syncRoot)
        {
            var cutoff = DateTimeOffset.UtcNow - MaximumAge;
            _plans = _plans
                .Where(existing => existing.CreatedAt >= cutoff)
                .Where(existing => !(existing.PlannerVersion == plan.PlannerVersion
                    && existing.CoverageMode == plan.CoverageMode
                    && string.Equals(existing.ContentHash, plan.ContentHash, StringComparison.Ordinal)
                    && string.Equals(existing.ModelKey, plan.ModelKey, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(existing => existing.CreatedAt)
                .Take(MaximumEntries - 1)
                .ToList();
            _plans.Insert(0, plan);
        }
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            _plans.Clear();
        }
    }

    private static void DeleteLegacyPlaintextCache()
    {
        try
        {
            var legacyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Scriblucent",
                "study-plans.json");
            if (File.Exists(legacyPath))
                File.Delete(legacyPath);
        }
        catch
        {
            // Privacy migration is best effort. Failure does not make generation unavailable.
        }
    }
}

internal sealed class StudyPlanCoordinator
{
    internal const int CurrentPlannerVersion = 66;
    private const int PromptReserveTokens = 640;
    private const int MaximumAnswerCharacters = 240;

    private static readonly Regex LexicalTokenRegex = new(
        @"[\p{L}\p{Mn}\p{N}]+(?:['’\-][\p{L}\p{Mn}\p{N}]+)*",
        RegexOptions.Compiled);
    private static readonly Regex QuestionRecordRegex = new(
        @"(?im)^fact:[ \t]*(?<fact>F\d+)[ \t]*\r?\nsource:[ \t]*(?<source>S\d+)[ \t]*\r?\nq:[ \t]*(?<question>[^\r\n]+?)[ \t]*(?:\r?\nspan:[ \t]*(?<span>[^\r\n]+?)[ \t]*)?\r?(?=\n|\z)",
        RegexOptions.Compiled);
    private static readonly Regex AnswerRecordRegex = new(
        @"(?im)^fact:\s*(?<fact>F\d+)\s*\r?\nspan:\s*(?<span>[^\r\n]+)\s*$",
        RegexOptions.Compiled);
    private static readonly Regex DecisionRecordRegex = new(
        @"(?im)^fact:[ \t]*(?<fact>F\d+)[ \t]*\r?\nlanguage:[ \t]*(?<language>SAME|DIFFERENT)[ \t]*\r?\ndecision:[ \t]*(?<decision>KEEP|DROP)[ \t]*\r?(?=\n|\z)",
        RegexOptions.Compiled);
    private static readonly Regex MetadataRecordRegex = new(
        @"(?im)^fact:[ \t]*(?<fact>F\d+)[ \t]*\r?\nkind:[ \t]*(?<kind>PERSON|PLACE|ORGANIZATION|DATE|QUANTITY|TERM|ENTITY|TEXT)[ \t]*\r?\nitems:[ \t]*(?<items>[^\r\n]+?)[ \t]*\r?(?=\n|\z)",
        RegexOptions.Compiled);
    private static readonly Regex SinglePairRegex = new(
        @"(?im)^q:[ \t]*(?<question>[^\r\n]+)[ \t]*\r?\na:[ \t]*(?<answer>[^\r\n]+)[ \t]*$",
        RegexOptions.Compiled);
    private static readonly Regex IndexedSpanRegex = new(
        @"^T(?<start>\d{3})-T(?<end>\d{3})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly (char Open, char Close)[] DelimiterPairs =
    {
        ('"', '"'), ('“', '”'), ('„', '“'), ('«', '»'), ('‹', '›'),
        ('(', ')'), ('[', ']'), ('{', '}'), ('「', '」'), ('『', '』'), ('【', '】'), ('《', '》')
    };

    private readonly ITextGenerationBackend _backend;
    private readonly StudyPlanCache _cache;

    public StudyPlanCoordinator(ITextGenerationBackend backend, StudyPlanCache? cache = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _cache = cache ?? StudyPlanCache.Shared;
    }

    public async Task<StudyGenerationContext> GetOrCreateAsync(
        string sourceText,
        string modelKey,
        StudyCoverageMode coverageMode,
        IProgress<AiGenerationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelKey);
        AiInputGuard.EnsureSuitableStudyText(sourceText);

        coverageMode = NormalizeCoverage(coverageMode);
        var normalizedSource = NormalizeSource(sourceText);
        var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedSource)));
        var cached = _cache.Find(contentHash, modelKey, CurrentPlannerVersion, coverageMode);
        if (cached is not null)
            return new StudyGenerationContext(cached, coverageMode, cached.Facts);

        using var trace = StudyPlanningTrace.Start(modelKey, normalizedSource);
        progress?.Report(new AiGenerationProgress(AiGenerationStatus.SelectingStudyPoints));
        try
        {
            var tokenCount = await _backend.CountTokensAsync(normalizedSource, modelKey, ct);
            var contextSize = _backend.GetEffectiveContextSize(modelKey);
            var plan = await CreatePlanAsync(
                normalizedSource,
                contentHash,
                modelKey,
                coverageMode,
                Math.Max(1, tokenCount),
                contextSize,
                progress,
                trace,
                ct);
            _cache.Store(plan);
            trace?.LogOutcome(
                $"facts={plan.Facts.Count} fallback={plan.ClassificationFallbackBatchCount} packets={plan.ContextPackets.Count}");
            return new StudyGenerationContext(plan, coverageMode, plan.Facts);
        }
        catch (Exception ex)
        {
            trace?.LogOutcome($"failed: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    internal static StudyCoverageMode NormalizeCoverage(StudyCoverageMode coverageMode)
        => coverageMode == StudyCoverageMode.Focused ? StudyCoverageMode.Balanced : coverageMode;

    internal static StudyGenerationContext CreateContext(
        StudyPlan plan,
        StudyCoverageMode coverageMode)
    {
        coverageMode = NormalizeCoverage(coverageMode);
        return new StudyGenerationContext(plan, coverageMode, plan.Facts);
    }

    internal static int AttemptsPerViableUnit(StudyCoverageMode coverageMode)
        => NormalizeCoverage(coverageMode) == StudyCoverageMode.Thorough ? 2 : 1;

    internal static int CalculateAttemptCount(int viableUnitCount, StudyCoverageMode coverageMode)
        => viableUnitCount <= 0 ? 0 : viableUnitCount * AttemptsPerViableUnit(coverageMode);

    internal static int CalculateAttemptCount(
        IReadOnlyList<StudySourceUnitSnapshot> units,
        StudyCoverageMode coverageMode,
        string modelKey)
        => StudyAutoCoverage.TotalAttempts(units, coverageMode, modelKey);

    internal static int CountViableSourceUnits(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return 0;
        return SplitIntoSourceUnits(NormalizeSource(source))
            .Count(unit => GroundedClozeBuilder.IsViableSourceUnit(unit.Text));
    }

    internal static IReadOnlyList<StudySourceUnitSnapshot> SplitSource(string source)
        => string.IsNullOrWhiteSpace(source)
            ? []
            : SplitIntoSourceUnits(NormalizeSource(source));

    private async Task<StudyPlan> CreatePlanAsync(
        string source,
        string contentHash,
        string modelKey,
        StudyCoverageMode coverageMode,
        int sourceTokenCount,
        int contextSize,
        IProgress<AiGenerationProgress>? progress,
        StudyPlanningTrace? trace,
        CancellationToken ct)
    {
        var units = SplitIntoSourceUnits(source);
        if (units.Count == 0)
        {
            return new StudyPlan(
                contentHash, modelKey, CurrentPlannerVersion, DateTimeOffset.UtcNow, [],
                CoverageMode: coverageMode, SourceTokenCount: sourceTokenCount);
        }

        var attemptCaps = StudyAutoCoverage.ResolveAttempts(units, coverageMode, modelKey);
        var requestedTarget = attemptCaps.Values.Sum();
        var capabilities = AiModelCapabilities.Resolve(modelKey);
        var packets = BuildPackets(
            units,
            attemptCaps,
            sourceTokenCount,
            Math.Max(4096, contextSize),
            capabilities.ContextPacketTargetLimit);
        var facts = new List<StudyFact>(requestedTarget);
        var fallbackCount = 0;
        var usedSourceRanges = new HashSet<(string SourceUnitId, int Start, int Length)>();

        for (var index = 0; index < packets.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var packet = packets[index];
            progress?.Report(new AiGenerationProgress(
                AiGenerationStatus.SelectingStudyPoints,
                PacketIndex: index + 1,
                PacketCount: packets.Count));

            var packetFacts = capabilities.NaturalQuestionTier == NaturalQuestionPolicyTier.ClozeOnly
                ? new List<StudyFact>()
                : await SelectWithModelAsync(
                    packet,
                    units,
                    attemptCaps,
                    modelKey,
                    usedSourceRanges,
                    progress,
                    index + 1,
                    packets.Count,
                    trace,
                    ct);
            foreach (var fact in packetFacts)
                usedSourceRanges.Add((fact.SourceUnitId, fact.AnswerStart, fact.AnswerLength));
            FillUnusedUnitClozes(
                packet,
                units,
                packetFacts,
                usedSourceRanges);
            fallbackCount += packetFacts.Count(fact => fact.IsCoverageFallback);
            facts.AddRange(packetFacts);
        }

        for (var index = 0; index < facts.Count; index++)
            facts[index] = facts[index] with { Id = $"F{index + 1:000}" };

        var coveredUnits = facts.Select(fact => fact.SourceUnitId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var truncated = units.Any(unit =>
            GroundedClozeBuilder.IsViableSourceUnit(unit.Text) && !coveredUnits.Contains(unit.Id));

        return new StudyPlan(
            contentHash,
            modelKey,
            CurrentPlannerVersion,
            DateTimeOffset.UtcNow,
            facts,
            units.Count,
            facts.Count,
            truncated,
            fallbackCount,
            units,
            packets,
            coverageMode,
            sourceTokenCount,
            requestedTarget);
    }

    private async Task<List<StudyFact>> SelectWithModelAsync(
        StudyContextPacket packet,
        IReadOnlyList<StudySourceUnitSnapshot> units,
        IReadOnlyDictionary<string, int> attemptCaps,
        string modelKey,
        ISet<(string SourceUnitId, int Start, int Length)> usedSourceRanges,
        IProgress<AiGenerationProgress>? progress,
        int packetIndex,
        int packetCount,
        StudyPlanningTrace? trace,
        CancellationToken ct)
    {
        try
        {
            var capabilities = AiModelCapabilities.Resolve(modelKey);
            var owned = packet.OwnedSourceUnitIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ownedUnits = units.Where(unit => owned.Contains(unit.Id)).ToList();
            var slots = CreateQuestionSlots(ownedUnits, attemptCaps, packet.TargetCount);
            if (slots.Count == 0)
                return [];

            progress?.Report(new AiGenerationProgress(
                AiGenerationStatus.WritingQuestions,
                PacketIndex: packetIndex,
                PacketCount: packetCount));
            var questions = capabilities.QuestionStrategy == GroundedQuestionStrategy.SinglePair
                ? await GenerateSmallModelQuestionsAsync(packet, slots, modelKey, trace, ct)
                : await GenerateJointQuestionsAsync(packet, slots, modelKey, trace, ct);
            if (questions.Count == 0)
                return [];

            var accepted = new List<StudyFact>(questions.Count);
            var reservedRanges = new HashSet<(string SourceUnitId, int Start, int Length)>(usedSourceRanges);
            var groundingBatchSize = capabilities.GroundingBatchSize;
            foreach (var questionBatch in questions.Chunk(groundingBatchSize))
            {
                try
                {
                    progress?.Report(new AiGenerationProgress(
                        AiGenerationStatus.GroundingAnswers,
                        PacketIndex: packetIndex,
                        PacketCount: packetCount));
                    var smallModelPair = capabilities.QuestionStrategy == GroundedQuestionStrategy.SinglePair
                        && questionBatch.Length == 1;
                    string answerOutput;
                    if (smallModelPair)
                    {
                        var question = questionBatch[0];
                        answerOutput =
                            $"fact: {question.FactId}\nspan: {question.IntendedAnswer}\n";
                    }
                    else
                    {
                        var answerProfile = SamplingProfiles.Grounded;
                        var answerResponse = await GenerateAsync(trace, "answer-grounding", new AiGenerationRequest(
                            Prompt: BuildAnswerPrompt(packet, questionBatch),
                            MaxTokens: Math.Clamp(64 + (24 * questionBatch.Length), 64, 4096),
                            Temperature: answerProfile.Temperature,
                            TopP: answerProfile.TopP,
                            TopK: answerProfile.TopK,
                            PresencePenalty: answerProfile.PresencePenalty,
                            RepetitionPenalty: answerProfile.RepetitionPenalty,
                            Grammar: OutputGrammars.StudyAnswerGrammar(
                                questionBatch.Select(question => question.FactId).ToList()),
                            GrammarRoot: OutputGrammars.StudyAnswerRoot,
                            CancellationToken: ct,
                            ModelKey: modelKey));
                        answerOutput = answerResponse.Output;
                    }
                    var facts = ParseAnswers(
                        answerOutput,
                        packet,
                        questionBatch,
                        reservedRanges,
                        attemptCaps);
                    if (facts.Count == 0)
                        continue;

                    progress?.Report(new AiGenerationProgress(
                        AiGenerationStatus.ValidatingQuestionAnswer,
                        PacketIndex: packetIndex,
                        PacketCount: packetCount));
                    var decisionProfile = SamplingProfiles.Classification;
                    var decisionResponse = await GenerateAsync(trace, "pair-decision", new AiGenerationRequest(
                        Prompt: BuildDecisionPrompt(facts),
                        MaxTokens: Math.Clamp(48 + (28 * facts.Count), 64, 1024),
                        Temperature: decisionProfile.Temperature,
                        TopP: decisionProfile.TopP,
                        TopK: decisionProfile.TopK,
                        PresencePenalty: decisionProfile.PresencePenalty,
                        RepetitionPenalty: decisionProfile.RepetitionPenalty,
                        Grammar: OutputGrammars.StudyDecisionGrammar(
                            facts.Select(fact => fact.Id).ToList()),
                        GrammarRoot: OutputGrammars.StudyDecisionRoot,
                        CancellationToken: ct,
                        ModelKey: modelKey));
                    var finalized = ApplyDecisions(decisionResponse.Output, facts);
                    foreach (var fact in finalized)
                        reservedRanges.Add((fact.SourceUnitId, fact.AnswerStart, fact.AnswerLength));
                    accepted.AddRange(finalized);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // A failed precision batch falls back only for its uncovered source units.
                }
            }

            if (accepted.Count > 0 && capabilities.UsesSemanticMetadata)
            {
                try
                {
                    var metadataProfile = SamplingProfiles.Classification;
                    var metadataResponse = await GenerateAsync(trace, "semantic-metadata", new AiGenerationRequest(
                        Prompt: BuildMetadataPrompt(accepted),
                        MaxTokens: Math.Clamp(32 + (20 * accepted.Count), 64, 1024),
                        Temperature: metadataProfile.Temperature,
                        TopP: metadataProfile.TopP,
                        TopK: metadataProfile.TopK,
                        PresencePenalty: metadataProfile.PresencePenalty,
                        RepetitionPenalty: metadataProfile.RepetitionPenalty,
                        Grammar: OutputGrammars.StudyMetadataGrammar(
                            accepted.Select(fact => fact.Id).ToList()),
                        GrammarRoot: OutputGrammars.StudyMetadataRoot,
                        CancellationToken: ct,
                        ModelKey: modelKey));
                    accepted = ApplySemanticMetadata(metadataResponse.Output, accepted);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Optional metadata must never reduce accepted study coverage.
                }
            }

            return accepted.OrderBy(fact => fact.Id, StringComparer.Ordinal).ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private async Task<List<StudyQuestionCandidate>> GenerateJointQuestionsAsync(
        StudyContextPacket packet,
        IReadOnlyList<StudyQuestionSlot> slots,
        string modelKey,
        StudyPlanningTrace? trace,
        CancellationToken ct)
    {
        var profile = AiModelCapabilities.Resolve(modelKey).UseNaturalQuestionSampling
            ? SamplingProfiles.NaturalQuestion
            : SamplingProfiles.Grounded;
        var response = await GenerateAsync(trace, "joint-questions", new AiGenerationRequest(
            Prompt: BuildQuestionPrompt(packet, slots),
            MaxTokens: Math.Clamp(192 + (48 * slots.Count), 64, 4096),
            Temperature: profile.Temperature,
            TopP: profile.TopP,
            TopK: profile.TopK,
            PresencePenalty: profile.PresencePenalty,
            RepetitionPenalty: profile.RepetitionPenalty,
            Grammar: OutputGrammars.StudyQuestionGrammar(
                slots.Select(slot => slot.FactId).ToList(),
                slots.Select(slot => slot.Source.Id).ToList()),
            GrammarRoot: OutputGrammars.StudyQuestionRoot,
            CancellationToken: ct,
            ModelKey: modelKey));
        return ParseQuestions(response.Output, slots, packet.Context);
    }

    private async Task<List<StudyQuestionCandidate>> GenerateSmallModelQuestionsAsync(
        StudyContextPacket packet,
        IReadOnlyList<StudyQuestionSlot> slots,
        string modelKey,
        StudyPlanningTrace? trace,
        CancellationToken ct)
    {
        var questions = new List<StudyQuestionCandidate>(slots.Count);
        var profile = SamplingProfiles.SmallQuestionDraft;
        foreach (var slot in slots)
        {
            var questionResponse = await GenerateAsync(trace, "small-model-pair", new AiGenerationRequest(
                Prompt: BuildSmallModelPairPrompt(packet, slot),
                MaxTokens: 192,
                Temperature: profile.Temperature,
                TopP: profile.TopP,
                TopK: profile.TopK,
                PresencePenalty: profile.PresencePenalty,
                RepetitionPenalty: profile.RepetitionPenalty,
                Grammar: OutputGrammars.StudySinglePairGrammar(),
                GrammarRoot: OutputGrammars.StudySinglePairRoot,
                CancellationToken: ct,
                ModelKey: modelKey));
            var pairMatch = SinglePairRegex.Match(questionResponse.Output ?? string.Empty);
            if (!pairMatch.Success)
                continue;

            var question = NormalizeWhitespace(pairMatch.Groups["question"].Value);
            if (!TryResolveAnswerSpan(
                    pairMatch.Groups["answer"].Value,
                    slot.Source.Text,
                    out var answerStart,
                    out var answer)
                || !TryIncludeBalancedDelimiters(slot.Source.Text, ref answerStart, ref answer)
                || CoversWholeSourceUnit(slot.Source.Text, answerStart, answer)
                || LexicalTokenRegex.Matches(answer).Count is < 1 or > 8)
            {
                continue;
            }

            if (!GroundedQuestionValidator.TryValidateQuestionShape(
                    question, slot.Source.Text, packet.Context, out _)
                || GroundedQuestionValidator.ContainsWholePhrase(question, answer))
                continue;

            questions.Add(new StudyQuestionCandidate(
                slot.FactId,
                slot.Source,
                question,
                answerStart,
                answer));
        }

        return questions;
    }

    private Task<AiGenerationResponse> GenerateAsync(
        StudyPlanningTrace? trace,
        string kind,
        AiGenerationRequest request)
        => trace is null
            ? _backend.GenerateAsync(request)
            : trace.GenerateAsync(_backend, kind, request);

    private static IReadOnlyList<StudyQuestionSlot> CreateQuestionSlots(
        IReadOnlyList<StudySourceUnitSnapshot> ownedUnits,
        IReadOnlyDictionary<string, int> attemptCaps,
        int targetCount)
    {
        var assigned = new List<(StudySourceUnitSnapshot Source, int Focus)>();
        var maximumAttempts = ownedUnits.Select(unit => attemptCaps.GetValueOrDefault(unit.Id)).DefaultIfEmpty(0).Max();
        for (var attempt = 0; attempt < maximumAttempts && assigned.Count < targetCount; attempt++)
        {
            assigned.AddRange(ownedUnits
                .Where(unit => attemptCaps.GetValueOrDefault(unit.Id) > attempt)
                .Select(unit => (unit, attempt + 1)));
        }

        return assigned.Take(targetCount)
            .Select((assignment, index) => new StudyQuestionSlot(
                $"F{index + 1:000}",
                assignment.Source,
                assignment.Focus))
            .OrderBy(slot => slot.Source.Order)
            .ThenBy(slot => slot.Focus)
            .ToList();
    }

    private static string BuildSmallModelPairPrompt(
        StudyContextPacket packet,
        StudyQuestionSlot slot) => $"""
Create one reliable study flashcard from OWNED SOURCE. Write a natural question in exactly the same
language. Its answer must be the shortest complete 1-8 word span copied exactly from OWNED SOURCE.
CONTEXT only clarifies names and pronouns. Do not translate, copy the whole sentence, use outside
knowledge, or ask yes/no. Make the question stand alone by replacing an unclear pronoun with the
source name from CONTEXT. Do not choose an answer that is only an unclear pronoun. Return only q
and a.

<OWNED_SOURCE>
{slot.Source.Text}
</OWNED_SOURCE>
<CONTEXT>
{packet.Context}
</CONTEXT>
""";

    private static string BuildQuestionPrompt(
        StudyContextPacket packet,
        IReadOnlyList<StudyQuestionSlot> slots)
    {
        var assignments = string.Join("\n", slots.Select(slot =>
            $"{slot.FactId} -> {slot.Source.Id} -> FOCUS {slot.Focus}"));
        return $"""
For each FACT, first choose one missing piece from that OWNED unit, then write one grammatical recall
question for it in the unit's language. Copy the intended answer as one exact continuous SOURCE span.
Do not translate the question into the instruction language. Do not reverse who did what or add facts.
Choose a compact, complete target that can usually be answered with 1-8 consecutive source words.
For a compound sentence, ask about one atomic part rather than requesting the whole sentence.
Never ask a yes/no question. Copy every name and number exactly from SOURCE.
Write exactly one question sentence plus its intended span, with no introduction or source statement.
Make every question stand alone; replace an unclear pronoun with the source name from context.
If an answer would be only an unclear pronoun or generic reference, choose another concrete detail
from that owned unit and ask about it instead.
When a unit states a role or profession, ask which role or profession instead of vaguely asking what
the person was. Do not quote these instructions. Do not stitch source words without syntax.
FOCUS 2 must not reuse FOCUS 1's answer. No blank. Do not state the answer.

QUESTION EXAMPLES (do not return these):
SOURCE: Mira founded Luma in 2019.
GOOD QUESTION: What organization did Mira found in 2019?
GOOD SPAN: Luma
BAD: Mira founded Luma in 2019. What happened?
SOURCE: Mira worked as the laboratory director.
GOOD QUESTION: What role did Mira hold?
GOOD SPAN: laboratory director
BAD: What was Mira?

ASSIGNMENTS:
{assignments}

SOURCE:
{packet.Context}
""";
    }

    private static string BuildAnswerPrompt(
        StudyContextPacket packet,
        IReadOnlyList<StudyQuestionCandidate> questions)
    {
        var records = string.Join("\n\n", questions.Select(question => $"""
FACT {question.FactId}
QUESTION: {question.Question}
SOURCE {question.Source.Id}: {question.Source.Text}
"""));
        return $"""
Read each QUESTION literally. Copy the smallest grammatically complete continuous text span from
its adjacent SOURCE that answers exactly what the question asks. An answer may be a phrase; it does
not need to be a sentence. Include every required name, number, unit, date component, range marker,
or coordinated item, but exclude the subject and action already stated by the question. Never copy
an isolated article, modifier, connector, or unfinished phrase. Preserve the source text exactly,
including its spelling and internal punctuation. Return NONE when the question is yes/no, the source
does not answer it, or no compact direct answer exists. The source is the only authority. Do not
answer from memory or change questions. Copy every FACT ID in order and return only records.

SCOPE EXAMPLES (do not return these):
QUESTION: When did Mira open the laboratory?
SOURCE: Mira opened the laboratory in 2019 and hired two assistants.
CORRECT SPAN: 2019
WRONG SPAN: Mira opened the laboratory in 2019 and hired two assistants.

QUESTION: What did Mira build from spare parts?
SOURCE: At school, Mira built a solar sensor from spare parts.
CORRECT SPAN: a solar sensor
WRONG SPAN: At school, Mira built a solar sensor from spare parts.

{records}
""";
    }

    private static string BuildDecisionPrompt(IReadOnlyList<StudyFact> facts)
    {
        var records = string.Join("\n\n", facts.Select(fact => $"""
FACT {fact.Id}
QUESTION: {fact.CandidateQuestion}
ANSWER: {fact.Answer}
SOURCE {fact.SourceUnitId}: {fact.Evidence}
"""));
        return $"""
First classify whether QUESTION uses the same natural language as SOURCE. Make that classification
before and independently from deciding whether QUESTION and ANSWER form a reliable study pair.
KEEP only when ANSWER directly and completely answers QUESTION, the roles and requested meaning
match, the answer is a complete phrase, and the question is grammatical and understandable on its
own in the SOURCE language.
DROP a wrong question word, unsupported inference, reversed role, incomplete answer, answer echo,
mixed-language question, unresolved pronoun, or an answer containing source details the question
did not request. DROP when the answer is only a generic reference such as "the group" or "the
organization" and does not identify who or what it refers to outside the source sentence. Leading,
supervising, helping, or belonging to a group does not make a person the performer of the group's
action.
Before deciding, read the pair as if a student supplied ANSWER to QUESTION. DROP unless SOURCE
supports that exact relationship and ANSWER has the requested category (for example event, date,
place, person, role, reason, or quantity). A true SOURCE sentence is not enough when it answers a
different question.

DECISION EXAMPLES (do not return these):
QUESTION: When did Mira open the laboratory? ANSWER: 2019. SOURCE: Mira opened it in 2019.
LANGUAGE: SAME. DECISION: KEEP
QUESTION: Where did Mira open the laboratory? ANSWER: 2019. SOURCE: Mira opened it in 2019.
LANGUAGE: SAME. DECISION: DROP
QUESTION: When did Mira open the laboratory? ANSWER: Mira opened it in 2019 and hired two assistants.
SOURCE: Mira opened it in 2019 and hired two assistants. LANGUAGE: SAME. DECISION: DROP
QUESTION: What event affected the city? ANSWER: between 2004 and 2006.
SOURCE: A drought affected the city between 2004 and 2006. LANGUAGE: SAME. DECISION: DROP
QUESTION: Where did Mira live? ANSWER: Harbor City. SOURCE: Mira was born in Harbor City.
LANGUAGE: SAME. DECISION: DROP
QUESTION: Who installed the sensors? ANSWER: Mira. SOURCE: Mira led a team that installed the sensors.
LANGUAGE: SAME. DECISION: DROP
QUESTION: Who released the design? ANSWER: the group. SOURCE: In 2019, the group released the design.
LANGUAGE: SAME. DECISION: DROP

LANGUAGE is SAME only when the question naturally uses the SOURCE language. Names, numbers, and
copied technical terms alone do not prove a language match. Mark translated or mixed-language
questions DIFFERENT. Do not rewrite anything. Apply the same rules in every language. Copy every
FACT ID in order. Return only language and decision records without explanations.

{records}
""";
    }

    private static string BuildMetadataPrompt(IReadOnlyList<StudyFact> facts)
    {
        var records = string.Join("\n\n", facts.Select(fact => $"""
FACT {fact.Id}
QUESTION: {fact.CandidateQuestion}
ANSWER: {fact.Answer}
SOURCE: {fact.Evidence}
"""));
        return $"""
Classify each ANSWER by what its QUESTION requests: PERSON, PLACE, ORGANIZATION, DATE, QUANTITY,
TERM, ENTITY, or TEXT. Use TERM for a role, profession, named concept, process, method, or technical
value; ENTITY for another concrete thing; and TEXT for actions, duties, attributes, ordinary phrases,
or anything that does not fit a narrower kind.
If ANSWER contains two to four peer answers requested together, copy each complete item exactly into
ITEMS separated by ` || `. Never put a single answer in ITEMS; otherwise write ITEMS: NONE.
Do not judge, rewrite, translate, or answer the question. Copy every FACT ID in order. Return only
kind and items records.

EXAMPLE (do not return this):
QUESTION: Which molecules were produced? ANSWER: ATP and NADPH
KIND: TERM. ITEMS: ATP || NADPH

{records}
""";
    }

    private static List<StudyQuestionCandidate> ParseQuestions(
        string output,
        IReadOnlyList<StudyQuestionSlot> slots,
        string packetContext)
    {
        var expected = slots.ToDictionary(slot => slot.FactId, StringComparer.OrdinalIgnoreCase);
        var encountered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var questions = new List<StudyQuestionCandidate>();
        var questionKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in QuestionRecordRegex.Matches(output ?? string.Empty))
        {
            var factId = match.Groups["fact"].Value.ToUpperInvariant();
            var sourceId = match.Groups["source"].Value.ToUpperInvariant();
            if (!encountered.Add(factId)
                || !expected.TryGetValue(factId, out var slot)
                || !string.Equals(sourceId, slot.Source.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var question = NormalizeWhitespace(match.Groups["question"].Value);
            if (!GroundedQuestionValidator.TryValidateQuestionShape(
                    question, slot.Source.Text, packetContext, out _)
                || !questionKeys.Add(NormalizeAnswerKey(question)))
            {
                continue;
            }

            var intendedAnswer = string.Empty;
            var intendedAnswerStart = -1;
            if (match.Groups["span"].Success)
            {
                if (!TryResolveAnswerSpan(
                        match.Groups["span"].Value,
                        slot.Source.Text,
                        out intendedAnswerStart,
                        out intendedAnswer)
                    || !TryIncludeBalancedDelimiters(
                        slot.Source.Text,
                        ref intendedAnswerStart,
                        ref intendedAnswer))
                {
                    continue;
                }

                if (CoversWholeSourceUnit(slot.Source.Text, intendedAnswerStart, intendedAnswer))
                {
                    intendedAnswerStart = -1;
                    intendedAnswer = string.Empty;
                }
                else if (GroundedQuestionValidator.ContainsWholePhrase(question, intendedAnswer))
                {
                    continue;
                }
            }

            questions.Add(new StudyQuestionCandidate(
                factId,
                slot.Source,
                question,
                intendedAnswerStart,
                intendedAnswer));
        }

        return questions.OrderBy(question => question.FactId, StringComparer.Ordinal).ToList();
    }

    private static List<StudyFact> ParseAnswers(
        string output,
        StudyContextPacket packet,
        IReadOnlyList<StudyQuestionCandidate> questions,
        ISet<(string SourceUnitId, int Start, int Length)> existingRanges,
        IReadOnlyDictionary<string, int> attemptCaps)
    {
        var expected = questions.ToDictionary(question => question.FactId, StringComparer.OrdinalIgnoreCase);
        var encountered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var facts = new List<StudyFact>();
        var usedRanges = new HashSet<(string SourceUnitId, int Start, int Length)>(existingRanges);
        var sourceUse = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in AnswerRecordRegex.Matches(output ?? string.Empty))
        {
            var factId = match.Groups["fact"].Value.ToUpperInvariant();
            if (!encountered.Add(factId) || !expected.TryGetValue(factId, out var candidate))
                continue;

            var unit = candidate.Source;
            if (sourceUse.GetValueOrDefault(unit.Id) >= attemptCaps.GetValueOrDefault(unit.Id))
            {
                continue;
            }

            if (!TryResolveAnswerSpan(
                match.Groups["span"].Value,
                unit.Text,
                out var answerStart,
                out var answer))
            {
                continue;
            }

            if (!TryIncludeBalancedDelimiters(unit.Text, ref answerStart, ref answer))
                continue;

            if (candidate.IntendedAnswerStart >= 0)
            {
                if (!AreNestedSourceSpans(
                        candidate.IntendedAnswerStart,
                        candidate.IntendedAnswer.Length,
                        answerStart,
                        answer.Length))
                {
                    continue;
                }

                if (!IsMinimalTrailingCompletion(
                        candidate.IntendedAnswerStart,
                        candidate.IntendedAnswer,
                        answerStart,
                        answer))
                {
                    answerStart = candidate.IntendedAnswerStart;
                    answer = candidate.IntendedAnswer;
                }
            }
            else if (CoversWholeSourceUnit(unit.Text, answerStart, answer))
            {
                continue;
            }

            if (!usedRanges.Add((unit.Id, answerStart, answer.Length)))
            {
                continue;
            }

            var kind = InferStructuralKind(answer);
            var answerItems = StudyAnswerList.TrySplit(answer, out var parsedItems) ? parsedItems : [];
            if (answerItems.Count >= 2)
                kind = StudyAnswerKind.List;
            var fact = CreateFact(
                factId,
                unit,
                answer,
                answerStart,
                StudyFactImportance.Supporting,
                kind,
                packet,
                isFallback: false,
                answerItems) with { CandidateQuestion = candidate.Question };
            sourceUse[unit.Id] = sourceUse.GetValueOrDefault(unit.Id) + 1;
            facts.Add(fact);
        }

        return facts.OrderBy(fact => fact.Id, StringComparer.Ordinal).ToList();
    }

    private static bool CoversWholeSourceUnit(string source, int answerStart, string answer)
    {
        if (!string.IsNullOrWhiteSpace(source[..answerStart]))
            return false;

        var suffixStart = answerStart + answer.Length;
        return suffixStart <= source.Length
            && Regex.IsMatch(source[suffixStart..], @"^[\s\p{P}\p{S}]*$");
    }

    private static bool AreNestedSourceSpans(
        int firstStart,
        int firstLength,
        int secondStart,
        int secondLength)
    {
        var firstEnd = firstStart + firstLength;
        var secondEnd = secondStart + secondLength;
        return (firstStart <= secondStart && firstEnd >= secondEnd)
            || (secondStart <= firstStart && secondEnd >= firstEnd);
    }

    private static bool IsMinimalTrailingCompletion(
        int intendedStart,
        string intended,
        int extractedStart,
        string extracted)
    {
        if (extractedStart != intendedStart || extracted.Length <= intended.Length)
            return false;

        var suffix = extracted[intended.Length..];
        return LexicalTokenRegex.Matches(suffix).Count == 1
            && !suffix.Any(char.IsPunctuation)
            && !suffix.Any(char.IsSymbol);
    }

    private static List<StudyFact> ApplyDecisions(
        string output,
        IReadOnlyList<StudyFact> facts)
    {
        var expected = facts.ToDictionary(fact => fact.Id, StringComparer.OrdinalIgnoreCase);
        var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var questionKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in DecisionRecordRegex.Matches(output ?? string.Empty))
        {
            var factId = match.Groups["fact"].Value.ToUpperInvariant();
            if (accepted.Contains(factId)
                || !string.Equals(match.Groups["decision"].Value, "KEEP", StringComparison.OrdinalIgnoreCase)
                || string.Equals(match.Groups["language"].Value, "DIFFERENT", StringComparison.OrdinalIgnoreCase)
                || !expected.TryGetValue(factId, out var fact))
            {
                continue;
            }

            if (!GroundedQuestionValidator.TryValidateHard(
                    fact,
                    fact.CandidateQuestion,
                    fact.QuestionContext,
                    out _)
                || !questionKeys.Add(NormalizeAnswerKey(fact.CandidateQuestion)))
                continue;

            accepted.Add(factId);
        }

        return facts
            .Where(fact => accepted.Contains(fact.Id))
            .ToList();
    }

    private static List<StudyFact> ApplySemanticMetadata(
        string output,
        IReadOnlyList<StudyFact> facts)
    {
        var enriched = new Dictionary<string, StudyFact>(StringComparer.OrdinalIgnoreCase);
        var expected = facts.ToDictionary(fact => fact.Id, StringComparer.OrdinalIgnoreCase);
        foreach (Match match in MetadataRecordRegex.Matches(output ?? string.Empty))
        {
            var factId = match.Groups["fact"].Value.ToUpperInvariant();
            if (!enriched.ContainsKey(factId) && expected.TryGetValue(factId, out var fact))
                enriched.Add(factId, ApplySemanticMetadata(fact, match));
        }

        return facts.Select(fact => enriched.GetValueOrDefault(fact.Id, fact)).ToList();
    }

    private static StudyFact ApplySemanticMetadata(StudyFact fact, Match metadata)
    {
        var kind = Enum.TryParse<StudyAnswerKind>(
            metadata.Groups["kind"].Value,
            ignoreCase: true,
            out var classifiedKind)
            ? classifiedKind
            : fact.AnswerKind;
        if (!IsPlausibleSemanticKind(fact.Answer, kind))
            kind = fact.AnswerKind;
        var items = TryParseExactAnswerItems(fact.Answer, metadata.Groups["items"].Value, out var parsedItems)
            ? parsedItems
            : fact.AnswerItems ?? [];
        return fact with
        {
            AnswerKind = kind,
            ValueShape = InferValueShape(fact.Answer, kind),
            IsExplicitList = items.Count >= 2,
            AnswerItems = items
        };
    }

    private static bool IsPlausibleSemanticKind(string answer, StudyAnswerKind kind)
        => kind is not (StudyAnswerKind.Date or StudyAnswerKind.Quantity)
           || answer.Any(char.IsDigit);

    private static bool TryParseExactAnswerItems(
        string answer,
        string value,
        out IReadOnlyList<string> items)
    {
        items = [];
        value = value.Trim();
        if (value.Length == 0 || string.Equals(value, "NONE", StringComparison.OrdinalIgnoreCase))
            return false;

        var parsed = value.Split("||", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parsed.Length is < 2 or > 4
            || parsed.Any(item => !StudyAnswerList.IsUsefulComponent(item))
            || parsed.Distinct(StringComparer.Ordinal).Count() != parsed.Length)
        {
            return false;
        }

        var cursor = 0;
        foreach (var item in parsed)
        {
            var start = answer.IndexOf(item, cursor, StringComparison.Ordinal);
            if (start < 0 || !IsListJoiner(answer[cursor..start], allowOuterText: cursor == 0))
                return false;
            cursor = start + item.Length;
        }

        if (!IsListJoiner(answer[cursor..], allowOuterText: true))
            return false;

        items = parsed;
        return true;
    }

    private static bool IsListJoiner(string value, bool allowOuterText)
    {
        var lexical = LexicalTokenRegex.Matches(value);
        if (lexical.Count == 0)
            return true;
        if (allowOuterText)
            return false;
        return lexical.Count <= 2 && value.Length <= 16 && !value.Any(char.IsDigit);
    }

    private static void FillUnusedUnitClozes(
        StudyContextPacket packet,
        IReadOnlyList<StudySourceUnitSnapshot> units,
        List<StudyFact> packetFacts,
        ISet<(string SourceUnitId, int Start, int Length)> usedSourceRanges)
    {
        var owned = packet.OwnedSourceUnitIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedUnits = packetFacts.Select(fact => fact.SourceUnitId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in units.Where(item => owned.Contains(item.Id)))
        {
            if (usedUnits.Contains(unit.Id))
                continue;
            if (!TryBuildUnitCloze(unit, packet, usedSourceRanges, out var fact))
                continue;
            usedUnits.Add(unit.Id);
            usedSourceRanges.Add((fact.SourceUnitId, fact.AnswerStart, fact.AnswerLength));
            packetFacts.Add(fact);
        }
    }

    private static bool TryBuildUnitCloze(
        StudySourceUnitSnapshot unit,
        StudyContextPacket packet,
        ISet<(string SourceUnitId, int Start, int Length)> usedSourceRanges,
        out StudyFact fact)
    {
        fact = null!;
        if (!GroundedClozeBuilder.IsViableSourceUnit(unit.Text))
            return false;

        var whole = CreateFact(
            $"F{unit.Order + 1:000}",
            unit,
            unit.Text,
            0,
            StudyFactImportance.Supporting,
            InferStructuralKind(unit.Text),
            packet,
            isFallback: true,
            []);
        if (!GroundedClozeBuilder.TryCreate(whole, out _, out var answer)
            || answer.Length == 0
            || answer.Length > MaximumAnswerCharacters)
        {
            return false;
        }

        var start = unit.Text.IndexOf(answer, StringComparison.Ordinal);
        if (start < 0 || usedSourceRanges.Contains((unit.Id, start, answer.Length)))
            return false;
        fact = CreateFact(
            whole.Id,
            unit,
            answer,
            start,
            StudyFactImportance.Supporting,
            InferStructuralKind(answer),
            packet,
            isFallback: true,
            []);
        return true;
    }

    private static string NormalizeAnswerKey(string answer)
        => NormalizeWhitespace(answer).ToLowerInvariant();

    private static StudyFact CreateFact(
        string id,
        StudySourceUnitSnapshot unit,
        string answer,
        int answerStart,
        StudyFactImportance importance,
        StudyAnswerKind kind,
        StudyContextPacket packet,
        bool isFallback,
        IReadOnlyList<string> answerItems)
        => new(
            id,
            unit.Id,
            unit.Text,
            answer,
            importance,
            unit.Order,
            kind,
            packet.Context,
            unit.Start,
            answerStart,
            answer.Length,
            isFallback,
            InferValueShape(answer, kind),
            true,
            !isFallback,
            answerItems.Count >= 2 || StudyAnswerList.IsExplicitList(answer),
            packet.Id,
            answerItems);

    private static List<StudyContextPacket> BuildPackets(
        IReadOnlyList<StudySourceUnitSnapshot> units,
        IReadOnlyDictionary<string, int> attemptCaps,
        int sourceTokenCount,
        int contextSize,
        int targetLimit)
    {
        var targetCount = units.Sum(unit => attemptCaps.GetValueOrDefault(unit.Id));
        if (targetCount <= 0)
            return [];

        targetLimit = Math.Max(1, targetLimit);
        var maximumOutput = Math.Clamp(256 + (48 * Math.Min(targetCount, targetLimit)), 64, 4096);
        var sourceBudget = Math.Max(512, (int)Math.Floor(contextSize * 0.9) - PromptReserveTokens - maximumOutput);
        var countByTargets = Math.Max(1, (int)Math.Ceiling(targetCount / (double)targetLimit));
        var indexedSourceBudget = Math.Max(256, sourceBudget / 2);
        var countByContext = Math.Max(1, (int)Math.Ceiling(sourceTokenCount / (double)indexedSourceBudget));
        var packetCount = Math.Min(units.Count, Math.Max(countByTargets, countByContext));
        var groups = PartitionUnits(units, packetCount);
        var allocations = AllocateAttempts(groups, attemptCaps, targetLimit);
        var allFit = sourceTokenCount <= sourceBudget && groups.Count == 1;
        var packets = new List<StudyContextPacket>(groups.Count);

        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            var contextUnits = allFit ? units : ResolveContextUnits(units, group);
            var context = RenderPacketContext(contextUnits, group.Select(unit => unit.Id));
            var coreStart = group[0].Start;
            var last = group[^1];
            packets.Add(new StudyContextPacket(
                $"P{index + 1:000}",
                context,
                group.Select(unit => unit.Id).ToList(),
                allocations[index],
                coreStart,
                (last.Start + last.Text.Length) - coreStart));
        }

        return packets.Where(packet => packet.TargetCount > 0).ToList();
    }

    private static List<List<StudySourceUnitSnapshot>> PartitionUnits(
        IReadOnlyList<StudySourceUnitSnapshot> units,
        int packetCount)
    {
        var groups = new List<List<StudySourceUnitSnapshot>>();
        var totalWeight = units.Sum(unit => Math.Max(1, unit.Text.Length));
        var cursor = 0;
        var remainingWeight = totalWeight;
        for (var packet = 0; packet < packetCount; packet++)
        {
            var remainingPackets = packetCount - packet;
            var targetWeight = remainingWeight / (double)remainingPackets;
            var group = new List<StudySourceUnitSnapshot>();
            var weight = 0;
            while (cursor < units.Count && units.Count - cursor > remainingPackets - 1)
            {
                var next = units[cursor];
                if (group.Count > 0 && weight >= targetWeight)
                    break;
                group.Add(next);
                weight += Math.Max(1, next.Text.Length);
                cursor++;
            }

            groups.Add(group);
            remainingWeight -= weight;
        }

        return groups;
    }

    private static List<int> AllocateAttempts(
        IReadOnlyList<List<StudySourceUnitSnapshot>> groups,
        IReadOnlyDictionary<string, int> attemptCaps,
        int limit)
        => groups.Select(group =>
        {
            var allocated = group.Sum(unit => attemptCaps.GetValueOrDefault(unit.Id));
            return Math.Min(limit, allocated);
        }).ToList();

    private static IReadOnlyList<StudySourceUnitSnapshot> ResolveContextUnits(
        IReadOnlyList<StudySourceUnitSnapshot> all,
        IReadOnlyList<StudySourceUnitSnapshot> owned)
    {
        var indexes = owned.Select(unit => unit.Order).ToHashSet();
        indexes.Add(0);
        indexes.Add(Math.Max(0, owned[0].Order - 1));
        indexes.Add(Math.Min(all.Count - 1, owned[^1].Order + 1));
        return all.Where(unit => indexes.Contains(unit.Order)).OrderBy(unit => unit.Order).ToList();
    }

    private static string RenderPacketContext(
        IEnumerable<StudySourceUnitSnapshot> units,
        IEnumerable<string> ownedIds)
    {
        var owned = ownedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return string.Join("\n", units.Select(unit =>
            $"{(owned.Contains(unit.Id) ? "OWNED" : "REFERENCE")} {unit.Id}: {unit.Text}"));
    }

    private static IReadOnlyList<StudySourceUnitSnapshot> SplitIntoSourceUnits(string source)
    {
        var units = new List<StudySourceUnitSnapshot>();
        var section = 0;
        var order = 0;
        var paragraphStart = 0;
        foreach (Match paragraphMatch in Regex.Matches(source, @"[^\n]+"))
        {
            if (paragraphMatch.Index > paragraphStart
                && source.AsSpan(paragraphStart, paragraphMatch.Index - paragraphStart).Count('\n') > 1)
            {
                section++;
            }

            foreach (var span in SplitParagraph(paragraphMatch.Value))
            {
                var raw = paragraphMatch.Value.Substring(span.Start, span.Length);
                var text = raw.Trim();
                if (text.Length == 0)
                    continue;
                var leading = raw.IndexOf(text, StringComparison.Ordinal);
                var start = paragraphMatch.Index + span.Start + Math.Max(0, leading);
                units.Add(new StudySourceUnitSnapshot($"S{order + 1:000}", text, order++, section, start));
            }

            paragraphStart = paragraphMatch.Index + paragraphMatch.Length;
        }

        return units;
    }

    private static IEnumerable<SourceSpan> SplitParagraph(string paragraph)
    {
        var start = 0;
        for (var index = 0; index < paragraph.Length; index++)
        {
            var character = paragraph[index];
            var terminal = character is '.' or '!' or '?' or ';' or '。' or '！' or '？' or '؛' or '؟';
            if (character == '.'
                && index > 0
                && index + 1 < paragraph.Length
                && !char.IsWhiteSpace(paragraph[index + 1]))
            {
                terminal = false;
            }
            else if (character == '.' && IsAbbreviationPeriod(paragraph, index))
            {
                terminal = false;
            }

            var noSpaceTerminal = character is '。' or '！' or '？' or '؛' or '؟';
            var shouldSplit = terminal
                && (noSpaceTerminal || index + 1 == paragraph.Length || char.IsWhiteSpace(paragraph[index + 1]));
            if (!shouldSplit && index - start < 1200)
                continue;

            var end = shouldSplit ? index + 1 : FindPreviousWhitespace(paragraph, start, index);
            if (end > start)
                yield return new SourceSpan(start, end - start);
            start = end;
        }

        if (start < paragraph.Length)
            yield return new SourceSpan(start, paragraph.Length - start);
    }

    private static bool IsAbbreviationPeriod(string text, int periodIndex)
    {
        if (NextNonWhitespaceIsLowercase(text, periodIndex + 1))
            return true;
        if (FollowsSingleLetterInitial(text, periodIndex))
            return true;
        return FollowsShortTokenBeforeFullName(text, periodIndex);
    }

    private static bool FollowsSingleLetterInitial(string text, int periodIndex)
    {
        if (periodIndex < 1 || !char.IsLetter(text[periodIndex - 1]))
            return false;
        return periodIndex == 1 || !char.IsLetterOrDigit(text[periodIndex - 2]);
    }

    private static bool FollowsShortTokenBeforeFullName(string text, int periodIndex)
    {
        var start = periodIndex;
        while (start > 0 && char.IsLetter(text[start - 1]))
            start--;
        var token = text[start..periodIndex];
        if (token.Length is < 2 or > 5 || token.Any(char.IsUpper))
            return false;

        var following = LexicalTokenRegex.Matches(text, periodIndex + 1).Cast<Match>().Take(2).ToList();
        return following.Count == 2
               && char.IsUpper(following[0].Value[0])
               && char.IsUpper(following[1].Value[0]);
    }

    private static bool NextNonWhitespaceIsLowercase(string text, int start)
    {
        for (var index = start; index < text.Length; index++)
        {
            if (char.IsWhiteSpace(text[index]))
                continue;
            return char.IsLower(text[index]);
        }

        return false;
    }

    private static int FindPreviousWhitespace(string text, int start, int index)
    {
        for (var cursor = index; cursor > start; cursor--)
        {
            if (char.IsWhiteSpace(text[cursor]))
                return cursor;
        }
        return index;
    }

    private static IReadOnlyList<SourceAtom> BuildSourceAtoms(string source)
    {
        var atoms = new List<SourceAtom>();
        foreach (Match match in LexicalTokenRegex.Matches(source))
        {
            if (match.Length <= 24)
            {
                atoms.Add(new SourceAtom(match.Index, match.Length));
                continue;
            }

            // Long unbroken lexical runs occur in scripts without spaces. Split them into
            // bounded Unicode text-element groups so the range protocol remains usable without
            // maintaining language-specific tokenizers or dictionaries.
            var offsets = System.Globalization.StringInfo.ParseCombiningCharacters(match.Value);
            for (var index = 0; index < offsets.Length; index += 4)
            {
                var localStart = offsets[index];
                var localEnd = index + 4 < offsets.Length ? offsets[index + 4] : match.Value.Length;
                atoms.Add(new SourceAtom(match.Index + localStart, localEnd - localStart));
            }
        }

        return atoms;
    }

    private static bool TryResolveAnswerSpan(
        string value,
        string source,
        out int start,
        out string text)
    {
        start = -1;
        text = string.Empty;
        value = value.Trim();
        if (string.Equals(value, "NONE", StringComparison.OrdinalIgnoreCase))
            return false;

        var indexed = IndexedSpanRegex.Match(value);
        if (indexed.Success)
        {
            var atoms = BuildSourceAtoms(source);
            if (!int.TryParse(indexed.Groups["start"].Value, out var first)
                || !int.TryParse(indexed.Groups["end"].Value, out var last)
                || first < 1
                || last < first
                || last > atoms.Count)
            {
                return false;
            }

            var firstAtom = atoms[first - 1];
            var lastAtom = atoms[last - 1];
            start = firstAtom.Start;
            var end = lastAtom.Start + lastAtom.Length;
            text = source.Substring(start, end - start);
            return text.Length > 0;
        }

        if (value.Length > MaximumAnswerCharacters)
            return false;
        start = source.IndexOf(value, StringComparison.Ordinal);
        if (start < 0)
            start = source.IndexOf(value, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            var withoutSentencePunctuation = value.TrimEnd(
                '.', ',', ';', ':', '!', '?', '。', '！', '？', '؛', '،');
            if (withoutSentencePunctuation.Length > 0
                && withoutSentencePunctuation.Length < value.Length)
            {
                start = source.IndexOf(withoutSentencePunctuation, StringComparison.Ordinal);
                if (start < 0)
                    start = source.IndexOf(withoutSentencePunctuation, StringComparison.OrdinalIgnoreCase);
                if (start >= 0)
                    value = withoutSentencePunctuation;
            }
        }
        if (start < 0)
            return false;
        text = source.Substring(start, value.Length);
        return text.Length > 0;
    }

    private static bool TryIncludeBalancedDelimiters(string source, ref int start, ref string text)
    {
        var end = start + text.Length;
        if (start < 0 || end <= start || end > source.Length)
            return false;

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var pair in DelimiterPairs)
            {
                if (start > 0 && end < source.Length
                    && source[start - 1] == pair.Open
                    && source[end] == pair.Close)
                {
                    start--;
                    end++;
                    changed = true;
                    break;
                }

                var selected = source.AsSpan(start, end - start);
                var openingCount = selected.Count(pair.Open);
                var closingCount = selected.Count(pair.Close);
                if (pair.Open == pair.Close)
                {
                    if (openingCount % 2 != 0 && end < source.Length && source[end] == pair.Close)
                    {
                        end++;
                        changed = true;
                        break;
                    }
                }
                else if (openingCount > closingCount && end < source.Length && source[end] == pair.Close)
                {
                    end++;
                    changed = true;
                    break;
                }
            }
        }

        if (end - start > MaximumAnswerCharacters)
            return false;
        text = source.Substring(start, end - start);
        return text.Length > 0;
    }

    private static StudyAnswerKind InferStructuralKind(string answer)
        => answer.Any(char.IsDigit) ? StudyAnswerKind.Quantity : StudyAnswerKind.Text;

    private static StudyValueShape InferValueShape(string answer, StudyAnswerKind kind)
    {
        if (kind == StudyAnswerKind.Date)
            return Regex.IsMatch(answer, @"^\p{N}{4}$") ? StudyValueShape.Year : StudyValueShape.CalendarDate;
        if (kind == StudyAnswerKind.Quantity)
            return Regex.IsMatch(answer, @"^[-+]?\p{N}+(?:[.,]\p{N}+)?$")
                ? StudyValueShape.AtomicNumber
                : StudyValueShape.ScopedQuantity;
        return StudyValueShape.Text;
    }

    private static string NormalizeSource(string source)
    {
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        normalized = Regex.Replace(normalized, @"[\t\f\v ]+", " ");
        normalized = Regex.Replace(normalized, @" *\n *", "\n");
        return normalized.Trim();
    }

    private static string NormalizeWhitespace(string value)
        => Regex.Replace(value.Trim(), @"\s+", " ");

    private sealed class StudyPlanningTrace : IDisposable
    {
        private readonly AiGenerationTracer _tracer;
        private int _callId;

        private StudyPlanningTrace(AiGenerationTracer tracer)
        {
            _tracer = tracer;
        }

        public static StudyPlanningTrace? Start(string modelKey, string source)
        {
            var tracer = AiGenerationTracer.StartJob(
                "study-plan",
                "Grounded study planning",
                modelKey,
                source);
            return tracer is null ? null : new StudyPlanningTrace(tracer);
        }

        public async Task<AiGenerationResponse> GenerateAsync(
            ITextGenerationBackend backend,
            string kind,
            AiGenerationRequest request)
        {
            var callId = Interlocked.Increment(ref _callId);
            var tracedRequest = request with { TokenObserver = _tracer.AppendStream };
            _tracer.LogCallStart(callId, kind, tracedRequest);
            try
            {
                var response = await backend.GenerateAsync(tracedRequest);
                _tracer.LogCallEnd(callId, response);
                return response;
            }
            catch (Exception ex)
            {
                _tracer.LogCallAborted(callId, $"{ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        public void LogOutcome(string text) => _tracer.LogOutcome(text);

        public void Dispose() => _tracer.Dispose();
    }

    private sealed record SourceAtom(int Start, int Length);
    private sealed record StudyQuestionSlot(string FactId, StudySourceUnitSnapshot Source, int Focus);
    private sealed record StudyQuestionCandidate(
        string FactId,
        StudySourceUnitSnapshot Source,
        string Question,
        int IntendedAnswerStart,
        string IntendedAnswer);
}
