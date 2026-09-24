using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Scriblucent.Services.AI;

internal sealed record GroundedPlannedQuestion(StudyFact Fact, string Question, string Answer, bool IsNatural);

internal sealed record GroundedQuestionPlan(
    IReadOnlyList<GroundedPlannedQuestion> Questions,
    int ModelGeneratedItemCount,
    int FallbackItemCount,
    string Diagnostics,
    IReadOnlyList<GroundedQuestionDecision> Decisions);

internal sealed class GroundedQuestionPlanner
{
    internal const int CurrentPolicyVersion = 32;
    private const int MaximumCacheEntries = 100;

    private readonly NaturalQuestionPolicyTier? _policyOverride;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, GroundedQuestionPlan> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();

    public GroundedQuestionPlanner(NaturalQuestionPolicyTier? policyOverride = null)
    {
        _policyOverride = policyOverride;
    }

    public async Task<GroundedQuestionPlan> GetOrCreateAsync(
        StudyGenerationContext context,
        IProgress<AiGenerationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var key = CreateCacheKey(context);
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                ReportCompletion(progress, cached, 0);
                return cached;
            }

            var created = await CreateAsync(context, progress, ct);
            _cache[key] = created;
            _cacheOrder.Enqueue(key);
            while (_cacheOrder.Count > MaximumCacheEntries)
                _cache.Remove(_cacheOrder.Dequeue());
            return created;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<GroundedQuestionPlan> CreateAsync(
        StudyGenerationContext context,
        IProgress<AiGenerationProgress>? progress,
        CancellationToken ct)
    {
        var planned = context.SelectedFacts.ToDictionary(
            fact => fact.Id,
            CreateFallback,
            StringComparer.OrdinalIgnoreCase);
        var decisions = context.SelectedFacts.ToDictionary(
            fact => fact.Id,
            fact => new GroundedQuestionDecision(
                fact.Id,
                GroundedQuestionDecisionKind.Rejected,
                string.Empty,
                GroundedRejectionReason.MissingRecord),
            StringComparer.OrdinalIgnoreCase);
        var policy = _policyOverride ?? AiModelCapabilities.Resolve(context.ModelKey).NaturalQuestionTier;
        if (policy == NaturalQuestionPolicyTier.ClozeOnly)
        {
            foreach (var fact in context.SelectedFacts)
            {
                decisions[fact.Id] = new GroundedQuestionDecision(
                    fact.Id,
                    GroundedQuestionDecisionKind.PolicyExcluded,
                    string.Empty,
                    null);
            }

            var clozePlan = BuildPlan(context, planned, decisions, packetCount: 0);
            ReportCompletion(progress, clozePlan, 0);
            return Task.FromResult(clozePlan);
        }

        foreach (var fact in context.SelectedFacts.Where(fact => fact.IsCoverageFallback))
        {
            decisions[fact.Id] = new GroundedQuestionDecision(
                fact.Id,
                GroundedQuestionDecisionKind.PolicyExcluded,
                string.Empty,
                null);
        }

        var questionKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in context.SelectedFacts.Where(fact => !fact.IsCoverageFallback))
        {
            ct.ThrowIfCancellationRequested();
            var natural = AcceptCandidateQuestion(fact, questionKeys, decisions);
            if (natural is not null)
                planned[fact.Id] = natural;
        }

        var packetCount = context.SelectedFacts
            .Where(fact => !fact.IsCoverageFallback && !string.IsNullOrWhiteSpace(fact.ContextPacketId))
            .Select(fact => fact.ContextPacketId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var plan = BuildPlan(context, planned, decisions, packetCount);
        ReportCompletion(progress, plan, packetCount);
        return Task.FromResult(plan);
    }

    private static GroundedPlannedQuestion? AcceptCandidateQuestion(
        StudyFact fact,
        ISet<string> questionKeys,
        IDictionary<string, GroundedQuestionDecision> decisions)
    {
        var question = fact.CandidateQuestion.Trim();
        if (!GroundedQuestionValidator.TryValidateHard(fact, question, fact.QuestionContext, out var reason))
        {
            decisions[fact.Id] = new GroundedQuestionDecision(
                fact.Id,
                GroundedQuestionDecisionKind.Rejected,
                question,
                reason);
            return null;
        }

        var key = Normalize(question);
        if (!questionKeys.Add(key))
        {
            decisions[fact.Id] = new GroundedQuestionDecision(
                fact.Id,
                GroundedQuestionDecisionKind.Rejected,
                question,
                GroundedRejectionReason.Duplicate);
            return null;
        }

        decisions[fact.Id] = new GroundedQuestionDecision(
            fact.Id,
            GroundedQuestionDecisionKind.AcceptedNatural,
            question,
            null,
            fact.Answer);
        return new GroundedPlannedQuestion(fact, question, fact.Answer, true);
    }

    private static GroundedQuestionPlan BuildPlan(
        StudyGenerationContext context,
        IReadOnlyDictionary<string, GroundedPlannedQuestion> planned,
        IReadOnlyDictionary<string, GroundedQuestionDecision> decisions,
        int packetCount)
    {
        var ordered = context.SelectedFacts.Select(fact => planned[fact.Id]).ToList();
        var modelCount = ordered.Count(question => question.IsNatural);
        var reasons = decisions.Values
            .Where(decision => decision.RejectionReason is not null)
            .GroupBy(decision => decision.RejectionReason)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key}={group.Count()}");
        var diagnostics = $"model={modelCount} fallback={ordered.Count - modelCount} packets={packetCount} " +
                          $"rejected=[{(reasons.Any() ? string.Join(", ", reasons) : "none")}]";
        return new GroundedQuestionPlan(
            ordered,
            modelCount,
            ordered.Count - modelCount,
            diagnostics,
            context.SelectedFacts.Select(fact => decisions[fact.Id]).ToList());
    }

    private static GroundedPlannedQuestion CreateFallback(StudyFact fact)
    {
        var seed = GroundedClozeBuilder.CreateSeed(fact);
        return new GroundedPlannedQuestion(fact, seed.Cloze, seed.Answer, false);
    }

    private static void ReportCompletion(
        IProgress<AiGenerationProgress>? progress,
        GroundedQuestionPlan plan,
        int packetCount)
        => progress?.Report(new AiGenerationProgress(
            AiGenerationStatus.Finalizing,
            PacketIndex: packetCount,
            PacketCount: packetCount,
            SuccessfulPacketCount: packetCount,
            TotalPacketCount: packetCount,
            ModelGeneratedItemCount: plan.ModelGeneratedItemCount,
            FallbackItemCount: plan.FallbackItemCount,
            GenerationDiagnostics: plan.Diagnostics));

    private static string CreateCacheKey(StudyGenerationContext context)
    {
        var raw = $"{CurrentPolicyVersion}\u001f{context.Plan.ContentHash}\u001f{context.ModelKey}\u001f" +
                  string.Join("\u001e", context.SelectedFacts.Select(fact =>
                      $"{fact.Id}\u001f{fact.SourceUnitId}\u001f{fact.Answer}\u001f" +
                      $"{fact.CandidateQuestion}\u001f{fact.ContextPacketId}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static string Normalize(string value)
        => Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");
}
