using Scriblucent.Models;
using Scriblucent.Services.AI;
using System.Text.RegularExpressions;

namespace Scriblucent.Services;

internal sealed class MindMapConversionService
{
    internal const int MaximumDetailsPerGroupingCall = 16;
    private static readonly Regex WordRegex = new(@"[\p{L}\p{Mn}\p{N}]+(?:['’\-][\p{L}\p{Mn}\p{N}]+)*", RegexOptions.Compiled);
    private static readonly Regex IndependentClauseBoundary = new(
        @",\s+(?:o|while|whereas|but|bet|tačiau|and\s+(?=(?:he|she|it|they|we|I)\b))\s+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> EnglishCategoryWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "history", "equipment", "activity", "activities", "facts", "details", "overview",
        "process", "processes", "effects", "causes", "results", "structure", "features",
        "research", "people", "events", "growth", "habitat", "timeline", "other"
    };
    private readonly ITextGenerationBackend _backend;
    private sealed record MapDetail(string Id, string Text, string Evidence, string SourceFactId,
        int SourceOrder, int SectionIndex);

    public MindMapConversionService(ITextGenerationBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public async Task<MindMapNode?> ConvertToMindMapAsync(
        string noteTitle,
        StudyGenerationContext studyContext,
        IProgress<AiGenerationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(studyContext);
        ct.ThrowIfCancellationRequested();

        var normalizedTitle = MindMapOutputParser.NormalizeNodeText(noteTitle);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            normalizedTitle = MindMapOutputParser.NormalizeNodeText(
                studyContext.Plan.SourceUnits?.FirstOrDefault()?.Text ?? studyContext.SelectedFacts.FirstOrDefault()?.Evidence ?? "");
        }

        var root = new MindMapNode { Text = normalizedTitle };
        var orderedFacts = studyContext.SelectedFacts.OrderBy(fact => fact.SourceOrder).ToArray();
        var details = orderedFacts.Length >= 3
            ? BuildMapDetails(orderedFacts, studyContext.Plan.SourceUnits ?? [])
            : [];
        var categories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var capabilities = AiModelCapabilities.Resolve(studyContext.ModelKey);
        if (details.Count >= 3
            && capabilities.NaturalQuestionTier != NaturalQuestionPolicyTier.ClozeOnly)
        {
            foreach (var batch in details.Chunk(MaximumDetailsPerGroupingCall))
            {
                if (batch.Length < 3)
                    continue;
                ct.ThrowIfCancellationRequested();
                progress?.Report(new AiGenerationProgress(AiGenerationStatus.Processing));
                try
                {
                    var response = await _backend.GenerateAsync(new AiGenerationRequest(
                        Prompt: BuildGroupingPrompt(normalizedTitle, batch),
                        MaxTokens: Math.Clamp(64 + (32 * batch.Length), 160, 768),
                        Temperature: 0.1,
                        TopP: 0.8,
                        TopK: 20,
                        CancellationToken: ct,
                        ModelKey: studyContext.ModelKey));
                    var parsed = ParseCategories(response.Output, batch, normalizedTitle);
                    if (parsed.Count == 0 && capabilities.QuestionStrategy == GroundedQuestionStrategy.Joint)
                    {
                        var regrouped = await _backend.GenerateAsync(new AiGenerationRequest(
                            Prompt: BuildRegroupingPrompt(normalizedTitle, batch),
                            MaxTokens: Math.Clamp(64 + (32 * batch.Length), 160, 768),
                            Temperature: 0.1,
                            TopP: 0.8,
                            TopK: 20,
                            CancellationToken: ct,
                            ModelKey: studyContext.ModelKey));
                        parsed = ParseCategories(regrouped.Output, batch, normalizedTitle);
                    }
                    foreach (var (id, label) in parsed)
                        categories.TryAdd(id, label);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // A bad or unavailable grouping must not discard the grounded study plan.
                }
            }
        }

        if (orderedFacts.Length < 3)
            BuildGroundedTree(root, orderedFacts);
        else
            BuildCategorizedTree(root, details, categories);
        var modelCount = details.Where(detail => categories.ContainsKey(detail.Id))
            .Select(detail => detail.SourceFactId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var fallbackCount = studyContext.SelectedFacts.Count - modelCount;
        progress?.Report(new AiGenerationProgress(
            AiGenerationStatus.Finalizing,
            SuccessfulPacketCount: studyContext.Plan.ContextPackets?.Count ?? 0,
            TotalPacketCount: studyContext.Plan.ContextPackets?.Count ?? 0,
            ModelGeneratedItemCount: modelCount,
            FallbackItemCount: fallbackCount,
            GenerationDiagnostics: $"model={modelCount} fallback={fallbackCount}"));

        return root.Children.Count > 0 ? root : null;
    }

    internal static int ExpectedGroupingCallCount(StudyGenerationContext context)
    {
        if (context.SelectedFacts.Count < 3
            || AiModelCapabilities.Resolve(context.ModelKey).NaturalQuestionTier == NaturalQuestionPolicyTier.ClozeOnly)
            return 0;
        var facts = context.SelectedFacts.OrderBy(fact => fact.SourceOrder).ToArray();
        return BuildMapDetails(facts, context.Plan.SourceUnits ?? [])
            .Chunk(MaximumDetailsPerGroupingCall).Count(batch => batch.Length >= 3);
    }

    private static IReadOnlyList<MapDetail> BuildMapDetails(
        IReadOnlyList<StudyFact> facts,
        IReadOnlyList<StudySourceUnitSnapshot> sourceUnits)
    {
        var sourceSections = sourceUnits.ToDictionary(unit => unit.Id, unit => unit.SectionIndex,
            StringComparer.OrdinalIgnoreCase);
        var found = new List<(string Text, StudyFact Owner, int Index)>();
        foreach (var evidenceGroup in facts.GroupBy(fact => fact.Evidence, StringComparer.OrdinalIgnoreCase))
        {
            var evidence = NormalizeWhitespace(evidenceGroup.Key);
            var clauses = IndependentClauseBoundary.Split(evidence)
                .Select(CleanReadableClause)
                .Where(clause => clause.Length > 0)
                .ToArray();
            // An answer can itself cross a clause boundary. Keep the complete source
            // statement in that case so selected facts never disappear from the map.
            if (evidenceGroup.Any(fact => !clauses.Any(clause => ContainsSpan(clause, fact.Answer))))
                clauses = [CleanReadableClause(evidence)];

            foreach (var clause in clauses)
            {
                if (found.Any(item => string.Equals(item.Text, clause, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var owner = evidenceGroup.FirstOrDefault(fact => ContainsSpan(clause, fact.Answer))
                    ?? evidenceGroup.First();
                found.Add((clause, owner, found.Count));
            }
        }

        return found.OrderBy(item => item.Owner.SourceOrder).ThenBy(item => item.Index)
            .Select((item, index) => new MapDetail($"M{index + 1:000}", item.Text,
                item.Owner.Evidence, item.Owner.Id, item.Owner.SourceOrder,
                sourceSections.GetValueOrDefault(item.Owner.SourceUnitId, -1)))
            .ToArray();
    }

    private static string CleanReadableClause(string source)
    {
        var clause = Regex.Replace(source.Trim(), @"^(?:[-*•]\s+|\d{1,2}[.)]\s+)", string.Empty)
            .TrimEnd('.', '!', '?', ';', ' ');
        return clause.Length == 0 ? string.Empty : char.ToUpperInvariant(clause[0]) + clause[1..];
    }

    private static void BuildCategorizedTree(
        MindMapNode root,
        IReadOnlyList<MapDetail> details,
        IReadOnlyDictionary<string, string> categories)
    {
        var groups = new List<(string Label, List<MapDetail> Details)>();
        foreach (var detail in details)
        {
            if (categories.TryGetValue(detail.Id, out var modelLabel))
            {
                var existing = groups.FindIndex(group => string.Equals(group.Label, modelLabel,
                    StringComparison.OrdinalIgnoreCase));
                if (existing >= 0)
                    groups[existing].Details.Add(detail);
                else
                    groups.Add((modelLabel, [detail]));
                continue;
            }

            var fallback = groups.LastOrDefault(group => group.Details.Count < 4
                && !categories.ContainsKey(group.Details[0].Id)
                && group.Details[0].SectionIndex == detail.SectionIndex);
            if (fallback.Details is not null)
                fallback.Details.Add(detail);
            else
                groups.Add((FallbackCategory(detail, root.Text), [detail]));
        }

        // A model may return a unique heading for every sentence. Treat that as a failed
        // categorization and group nearby source facts instead of reproducing the same problem.
        if (groups.Count == details.Count)
        {
            groups.Clear();
            foreach (var chunk in details.Chunk(Math.Clamp((details.Count + 1) / 2, 2, 4)))
                groups.Add((FallbackCategory(chunk[0], root.Text), [.. chunk]));
        }

        foreach (var (label, groupDetails) in groups)
        {
            var category = string.Equals(label, root.Text, StringComparison.OrdinalIgnoreCase)
                ? root
                : FindOrAddChild(root, label);
            foreach (var detail in groupDetails)
                category.Children.Add(new MindMapNode { Text = detail.Text });
        }
    }

    private static string FallbackCategory(MapDetail detail, string rootTitle)
    {
        var separator = detail.Text.IndexOf(" — ", StringComparison.Ordinal);
        var candidate = separator < 0
            ? string.Join(" ", WordRegex.Matches(detail.Text).Cast<Match>().Take(2).Select(word => word.Value))
            : detail.Text[..separator];
        return CountWords(candidate) is >= 1 and <= 4 ? candidate : rootTitle;
    }

    private static void BuildGroundedTree(MindMapNode root, IReadOnlyList<StudyFact> facts)
    {
        var representedPoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fact in facts)
        {
            if (TryBuildListPoints(fact, out var listPoints))
            {
                var listBranch = FindOrAddChild(root, BuildFallbackLabels(fact).Branch);
                foreach (var item in listPoints.Where(representedPoints.Add))
                    listBranch.Children.Add(new MindMapNode { Text = item });
                continue;
            }

            var labels = BuildFallbackLabels(fact);
            if (string.IsNullOrWhiteSpace(labels.Point) || !representedPoints.Add(labels.Point))
                continue;

            var branch = string.Equals(root.Text, labels.Branch, StringComparison.OrdinalIgnoreCase)
                ? root
                : FindOrAddChild(root, labels.Branch);
            branch.Children.Add(new MindMapNode { Text = labels.Point });
        }

        AddUnrepresentedClauses(root, facts, representedPoints);
        AddUnrepresentedConjunctions(root, facts, representedPoints);
    }

    private static bool TryBuildListPoints(StudyFact fact, out IReadOnlyList<string> points)
    {
        points = [];
        if (CountWords(fact.Answer) <= 6)
            return false;

        var candidates = fact.AnswerItems is { Count: >= 2 }
            ? fact.AnswerItems
            : fact.Answer.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (candidates.Count is < 2 or > 4)
            return false;

        var accepted = new List<string>();
        foreach (var candidate in candidates)
        {
            var item = MindMapOutputParser.NormalizeNodeText(candidate);
            var words = WordRegex.Matches(item).Cast<Match>().ToArray();
            if (words.Length > 2 && words[0].Length <= 4)
            {
                var withoutConnector = item[words[1].Index..];
                if (ContainsSpan(fact.Evidence, withoutConnector))
                    item = withoutConnector;
            }

            if (CountWords(item) is < 1 or > 7 || !ContainsSpan(fact.Evidence, item))
                return false;
            accepted.Add(item);
        }

        points = accepted;
        return true;
    }

    private static void AddUnrepresentedClauses(
        MindMapNode root,
        IReadOnlyList<StudyFact> facts,
        HashSet<string> representedPoints)
    {
        foreach (var evidence in facts.Select(fact => fact.Evidence).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var clause in evidence.Split(',').Skip(1))
            {
                var words = WordRegex.Matches(clause).Cast<Match>().ToArray();
                if (words.Length is < 5 or > 12)
                    continue;
                var clauseWords = words.Select(word => word.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (representedPoints.Any(point =>
                        WordRegex.Matches(point).Cast<Match>()
                            .Count(word => clauseWords.Contains(word.Value)) >= Math.Ceiling(words.Length * 0.6)))
                    continue;

                var first = words.Length >= 5 && words[0].Length <= 4 ? 1 : 0;
                if (words.Length - first < 4)
                    continue;
                var branch = clause[words[first].Index..(words[first + 1].Index + words[first + 1].Length)];
                var last = Math.Min(words.Length - 1, first + 7);
                var point = clause[words[first + 2].Index..(words[last].Index + words[last].Length)];
                branch = MindMapOutputParser.NormalizeNodeText(branch);
                point = MindMapOutputParser.NormalizeNodeText(point);
                if (branch.Length == 0 || point.Length == 0 || !representedPoints.Add(point))
                    continue;

                var parent = string.Equals(root.Text, branch, StringComparison.OrdinalIgnoreCase)
                    ? root
                    : FindOrAddChild(root, branch);
                parent.Children.Add(new MindMapNode { Text = point });
                break;
            }
        }
    }

    private static void AddUnrepresentedConjunctions(
        MindMapNode root,
        IReadOnlyList<StudyFact> facts,
        HashSet<string> representedPoints)
    {
        foreach (var evidence in facts.Select(fact => fact.Evidence).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (Match connector in Regex.Matches(evidence, @"\s+(?:and|ir)\s+", RegexOptions.IgnoreCase))
            {
                if (facts.Any(fact => string.Equals(fact.Evidence, evidence, StringComparison.OrdinalIgnoreCase)
                    && evidence.IndexOf(fact.Answer, StringComparison.OrdinalIgnoreCase) is var answerStart and >= 0
                    && connector.Index >= answerStart
                    && connector.Index < answerStart + fact.Answer.Length))
                    continue;

                var before = evidence[..connector.Index];
                var after = evidence[(connector.Index + connector.Length)..].TrimEnd(' ', '.');
                var beforeWords = WordRegex.Matches(before).Cast<Match>().ToArray();
                if (beforeWords.Length is >= 5 and <= 12)
                {
                    var previousWords = beforeWords.Select(word => word.Value)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (!representedPoints.Any(point => before.Contains(point, StringComparison.OrdinalIgnoreCase)
                        || point.Contains(before, StringComparison.OrdinalIgnoreCase)
                        || WordRegex.Matches(point).Cast<Match>()
                            .Count(word => previousWords.Contains(word.Value)) >= Math.Ceiling(beforeWords.Length * 0.6)))
                    {
                        var priorBranch = MindMapOutputParser.NormalizeNodeText(
                            before[beforeWords[0].Index..(beforeWords[1].Index + beforeWords[1].Length)]);
                        var priorPoint = MindMapOutputParser.NormalizeNodeText(
                            before[beforeWords[2].Index..(beforeWords[^1].Index + beforeWords[^1].Length)]);
                        if (priorBranch.Length > 0 && priorPoint.Length > 0 && representedPoints.Add(priorPoint))
                            FindOrAddChild(root, priorBranch).Children.Add(new MindMapNode { Text = priorPoint });
                    }
                }

                var words = WordRegex.Matches(after).Cast<Match>().ToArray();
                if (CountWords(before) < 5 || words.Length is < 4 or > 10)
                    continue;
                var afterWords = words.Select(word => word.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (representedPoints.Any(point =>
                        after.Contains(point, StringComparison.OrdinalIgnoreCase)
                        || point.Contains(after, StringComparison.OrdinalIgnoreCase)
                        || WordRegex.Matches(point).Cast<Match>()
                            .Count(word => afterWords.Contains(word.Value)) >= Math.Ceiling(words.Length * 0.6)))
                    continue;

                var branch = MindMapOutputParser.NormalizeNodeText(
                    after[words[0].Index..(words[1].Index + words[1].Length)]);
                var point = MindMapOutputParser.NormalizeNodeText(
                    after[words[2].Index..(words[^1].Index + words[^1].Length)]);
                if (branch.Length == 0 || point.Length == 0 || !representedPoints.Add(point))
                    continue;
                FindOrAddChild(root, branch).Children.Add(new MindMapNode { Text = point });
                break;
            }
        }
    }

    private static string BuildGroupingPrompt(string title, IReadOnlyList<MapDetail> details)
        => $"""
Organize these mind-map DETAILS about "{title}" into shared themes. The details are complete statements grounded in the note; do not rewrite them.
Output 2 to {Math.Clamp(details.Count / 2, 2, 4)} DIFFERENT category lines for these {details.Count} details. Put 2 or more detail IDs on each line whenever possible. Every ID must occur exactly once. A one-detail category is allowed only if it truly does not fit another theme. Never make one category per detail or put everything under one category.
Use a short, meaningful category name (1-3 words) in the SAME LANGUAGE as the details. It must accurately describe EVERY detail assigned to it. Group by concept, not sentence position or date. Do not use the title, a sentence opening, or an individual detail as a category.
Example of the grouping idea (do not copy these categories):
M001 | Buses carry passengers
M002 | Mechanics inspect brakes
M003 | Trains carry passengers
M004 | Technicians replace worn tires
Transport services | M001, M003
Maintenance | M002, M004

Return only category lines in this format:
CATEGORY | DETAIL_ID, DETAIL_ID

{string.Join("\n", details.Select(detail => $"{detail.Id} | {detail.Text}"))}
""";

    private static string BuildRegroupingPrompt(
        string title,
        IReadOnlyList<MapDetail> details)
        => $"""
Create a NEW mind-map grouping for "{title}". The first attempt was too fragmented or incomplete. Start fresh from the details below.
Return EXACTLY {Math.Clamp(details.Count / 2, 2, 4)} category lines. {(details.Count >= 4 ? "Each line must contain at least two DIFFERENT detail IDs." : "One line may contain a single detail ID.")} Every detail ID must occur exactly once across all lines. Choose broad, meaningful category names in the SAME LANGUAGE as the details. Do not summarize, translate, or invent details. Return only lines of CATEGORY | ID, ID.

{string.Join("\n", details.Select(detail => $"{detail.Id} | {detail.Text}"))}
""";

    private static Dictionary<string, string> ParseCategories(
        string output,
        IReadOnlyList<MapDetail> details,
        string title)
    {
        var expected = details.ToDictionary(detail => detail.Id, StringComparer.OrdinalIgnoreCase);
        var accepted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Trim().Split('|', StringSplitOptions.TrimEntries);
            if (fields.Length != 2)
                continue;

            var label = MindMapOutputParser.NormalizeNodeText(fields[0]);
            if (label.Length is < 2 or > 64
                || CountWords(label) is < 1 or > 6
                || !label.Any(char.IsLetter)
                || label.Any(char.IsDigit)
                || label.Contains('.')
                || string.Equals(label, title, StringComparison.OrdinalIgnoreCase))
                continue;

            var ids = fields[1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (ids.Length == 0
                || ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Length
                || ids.Any(id => !expected.ContainsKey(id) || accepted.ContainsKey(id)))
            {
                accepted.Clear();
                return accepted;
            }
            foreach (var id in ids)
                accepted.Add(id, label);
        }

        var sourceIsLithuanian = details.SelectMany(detail => detail.Evidence)
            .Count(character => "ąčęėįšųūžĄČĘĖĮŠŲŪŽ".Contains(character)) >= 2;
        var categoryCount = accepted.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (accepted.Count < Math.Max(3, (int)Math.Ceiling(details.Count * 0.6))
            || categoryCount < 2
            || accepted.Count - categoryCount < (details.Count >= 4 ? 2 : 1)
            || (sourceIsLithuanian && accepted.Values.Any(label =>
                WordRegex.Matches(label).Cast<Match>().Any(word => EnglishCategoryWords.Contains(word.Value)))))
            accepted.Clear();
        return accepted;
    }

    private static (string Branch, string Point) BuildFallbackLabels(StudyFact fact)
    {
        var source = fact.Evidence.Trim();
        var answerStart = source.IndexOf(fact.Answer, StringComparison.OrdinalIgnoreCase);
        var clauseStart = answerStart > 0 ? source.LastIndexOf(',', answerStart - 1) + 1 : 0;
        var clauseEnd = answerStart >= 0 ? source.IndexOf(',', answerStart) : -1;
        if (clauseEnd < 0)
            clauseEnd = source.Length;
        var clause = source[clauseStart..clauseEnd].Trim();
        var clauseWords = WordRegex.Matches(clause).Cast<Match>().ToArray();
        var branchSource = clauseStart > 0 && clauseWords.Length > 0 && clauseWords[0].Length > 2
            ? source
            : clause;
        var branchWords = WordRegex.Matches(branchSource).Cast<Match>().ToArray();
        var firstBranchWord = branchWords.Length > 2 && branchWords[0].Length <= 2 ? 1 : 0;
        var branch = branchWords.Length == 0
            ? MindMapOutputParser.NormalizeNodeText(fact.Answer)
            : branchWords.Length == firstBranchWord + 1 || branchWords[firstBranchWord + 1].Length <= 3
                ? branchWords[firstBranchWord].Value
                : branchSource[branchWords[firstBranchWord].Index..(branchWords[firstBranchWord + 1].Index + branchWords[firstBranchWord + 1].Length)];
        if (answerStart >= 0
            && string.IsNullOrWhiteSpace(source[clauseStart..answerStart])
            && CountWords(fact.Answer) <= 5)
            branch = fact.Answer;
        branch = MindMapOutputParser.NormalizeNodeText(branch);
        var point = BuildFallbackPoint(fact);
        if (point.StartsWith(branch + " ", StringComparison.OrdinalIgnoreCase))
            point = point[(branch.Length + 1)..];
        else
        {
            var firstWord = WordRegex.Match(point);
            if (firstWord.Success && firstWord.Length <= 3
                && point[(firstWord.Index + firstWord.Length)..].TrimStart()
                    .StartsWith(branch + " ", StringComparison.OrdinalIgnoreCase))
            {
                var branchStart = point.IndexOf(branch, firstWord.Index + firstWord.Length,
                    StringComparison.OrdinalIgnoreCase);
                point = point[(branchStart + branch.Length)..].TrimStart();
            }
        }
        return (branch, point);
    }

    private static string BuildFallbackPoint(StudyFact fact)
    {
        var source = fact.Evidence.Trim();
        var answerStart = source.IndexOf(fact.Answer, StringComparison.OrdinalIgnoreCase);
        if (answerStart < 0)
            return MindMapOutputParser.NormalizeNodeText(fact.Answer);
        var clauseStart = answerStart > 0 ? source.LastIndexOf(',', answerStart - 1) + 1 : 0;
        var clauseEnd = answerStart >= 0 ? source.IndexOf(',', answerStart) : -1;
        if (clauseEnd < 0)
            clauseEnd = source.Length;
        var clause = source[clauseStart..clauseEnd].Trim();
        if (clause.IndexOf(fact.Answer, StringComparison.OrdinalIgnoreCase) >= 0)
            source = clause;
        var words = WordRegex.Matches(source).Cast<Match>().ToArray();
        if (words.Length == 0)
            return MindMapOutputParser.NormalizeNodeText(fact.Answer);

        answerStart = source.IndexOf(fact.Answer, StringComparison.OrdinalIgnoreCase);
        if (answerStart < 0)
            return MindMapOutputParser.NormalizeNodeText(fact.Answer);
        var answerEnd = answerStart + fact.Answer.Length;
        var firstAnswerWord = Array.FindIndex(words, word => word.Index + word.Length > answerStart);
        var lastAnswerWord = Array.FindLastIndex(words, word => word.Index < answerEnd);
        if (firstAnswerWord < 0 || lastAnswerWord < 0)
            return MindMapOutputParser.NormalizeNodeText(fact.Answer);
        if (lastAnswerWord - firstAnswerWord + 1 > 9)
            return MindMapOutputParser.NormalizeNodeText(fact.Answer);

        int start;
        int end;
        if (firstAnswerWord == 0 && lastAnswerWord < words.Length - 1)
        {
            start = words.Length - lastAnswerWord - 1 <= 9
                ? lastAnswerWord + 1
                : Math.Max(lastAnswerWord + 1, words.Length - 8);
            end = words.Length - 1;
        }
        else if (fact.AnswerKind is StudyAnswerKind.Date or StudyAnswerKind.Quantity
                 || lastAnswerWord == words.Length - 1)
        {
            end = fact.AnswerKind == StudyAnswerKind.Quantity
                  && words.Length - lastAnswerWord - 1 is >= 1 and <= 3
                ? words.Length - 1
                : lastAnswerWord;
            start = Math.Max(0, firstAnswerWord - 5);
            if (fact.AnswerKind == StudyAnswerKind.Quantity)
            {
                var connector = Array.FindLastIndex(words, firstAnswerWord,
                    word => word.Value is "and" or "ir");
                if (connector >= start && connector < firstAnswerWord - 1)
                    start = connector + 1;
            }
        }
        else
        {
            start = Math.Max(0, firstAnswerWord - 2);
            end = Math.Min(words.Length - 1, lastAnswerWord + 6);
            if (end - start + 1 > 9)
                end = Math.Min(words.Length - 1, start + 8);
            if (lastAnswerWord + 1 < words.Length
                && words[lastAnswerWord + 1].Value is "and" or "ir")
            {
                start = Math.Max(0, firstAnswerWord - 3);
                end = lastAnswerWord;
            }
        }

        if (end - start + 1 > 9)
            start = end - 8;
        if (start == 0 && end == words.Length - 1 && words.Length > 9)
        {
            start = Math.Max(0, firstAnswerWord - 2);
            end = Math.Min(words.Length - 1, start + 8);
        }

        var point = source[words[start].Index..(words[end].Index + words[end].Length)];
        return MindMapOutputParser.NormalizeNodeText(point);
    }

    private static bool ContainsSpan(string source, string span)
        => source.Contains(NormalizeWhitespace(span), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeWhitespace(string value)
        => Regex.Replace(value.Trim(), @"\s+", " ");

    private static int CountWords(string value)
        => WordRegex.Matches(value).Count;

    private static MindMapNode FindOrAddChild(MindMapNode parent, string text)
    {
        var existing = parent.Children.FirstOrDefault(child =>
            string.Equals(child.Text, text, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;

        var added = new MindMapNode { Text = text };
        parent.Children.Add(added);
        return added;
    }

    public static List<MindMapNode> ParseOutlineText(string text)
        => MindMapOutputParser.ParseOutlineText(text);
}
