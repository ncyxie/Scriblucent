using Scriblucent.Services.AI;
using System.Text.RegularExpressions;

namespace Scriblucent.Tests;

public sealed class StudyPlanningTests
{
    private const string Source = """
        Plants use chlorophyll in green leaves to absorb sunlight and begin photosynthesis.
        They convert the captured sunlight into chemical energy that supports later growth.
        """;

    [Fact]
    public void AttemptCount_MatchesViableUnitsNotWordQuota()
    {
        var eightSentences = string.Join(
            " ",
            Enumerable.Range(1, 8).Select(index =>
                $"Sentence {index} records a distinct botanical fact with enough tokens for a cloze."));
        var eightWords = Regex.Matches(eightSentences, @"[\p{L}\p{N}]+").Count;
        var eightViable = StudyPlanCoordinator.CountViableSourceUnits(eightSentences);

        Assert.Equal(8, eightViable);
        Assert.InRange(eightWords, 70, 120);
        var eightUnits = StudyPlanCoordinator.SplitSource(eightSentences);
        Assert.Equal(8, StudyPlanCoordinator.CalculateAttemptCount(eightUnits, StudyCoverageMode.Auto, "Qwen3.5-4B"));
        Assert.Equal(8, StudyPlanCoordinator.CalculateAttemptCount(eightViable, StudyCoverageMode.Balanced));
        Assert.Equal(8, StudyPlanCoordinator.CalculateAttemptCount(eightUnits, StudyCoverageMode.Thorough, "Qwen3.5-4B"));
        Assert.True(StudyPlanCoordinator.CalculateAttemptCount(eightUnits, StudyCoverageMode.Auto, "Qwen3.5-4B")
                    > (int)Math.Round(eightWords / 22.0, MidpointRounding.AwayFromZero));

        var wiki = string.Join(
            " ",
            Enumerable.Range(1, 40).Select(index =>
                $"Wiki sentence {index} records a distinct encyclopedia fact with enough tokens to study."));
        var wikiWords = Regex.Matches(wiki, @"[\p{L}\p{N}]+").Count;
        var wikiViable = StudyPlanCoordinator.CountViableSourceUnits(wiki);

        Assert.Equal(40, wikiViable);
        var wikiUnits = StudyPlanCoordinator.SplitSource(wiki);
        Assert.Equal(40, StudyPlanCoordinator.CalculateAttemptCount(wikiUnits, StudyCoverageMode.Auto, "Qwen3.5-4B"));
        Assert.Equal(40, StudyPlanCoordinator.CalculateAttemptCount(wikiUnits, StudyCoverageMode.Thorough, "Qwen3.5-4B"));
        Assert.NotEqual(
            (int)Math.Round(wikiWords / 11.0, MidpointRounding.AwayFromZero),
            StudyPlanCoordinator.CalculateAttemptCount(wikiUnits, StudyCoverageMode.Thorough, "Qwen3.5-4B"));
    }

    [Fact]
    public void AutoCoverage_GrantsSecondAttemptOnlyOnRichUnits()
    {
        const string thin = "Elena was born in 1946.";
        const string travelBan =
            "Trump imposed a travel ban on seven Muslim-majority countries, expanded the Mexico-United States border wall, and enforced a family separation policy.";
        var thinUnits = StudyPlanCoordinator.SplitSource(thin);
        var richUnits = StudyPlanCoordinator.SplitSource(travelBan);
        var mixed = StudyPlanCoordinator.SplitSource($"{thin} {travelBan}");

        Assert.False(StudyAutoCoverage.IsRich(thin));
        Assert.True(StudyAutoCoverage.IsRich(travelBan));
        Assert.Equal(1, StudyPlanCoordinator.CalculateAttemptCount(thinUnits, StudyCoverageMode.Auto, "Qwen3.5-4B"));
        Assert.Equal(1, StudyPlanCoordinator.CalculateAttemptCount(thinUnits, StudyCoverageMode.Thorough, "Qwen3.5-4B"));
        Assert.Equal(2, StudyPlanCoordinator.CalculateAttemptCount(richUnits, StudyCoverageMode.Auto, "Qwen3.5-4B"));
        Assert.Equal(2, StudyPlanCoordinator.CalculateAttemptCount(richUnits, StudyCoverageMode.Thorough, "Qwen3.5-4B"));
        Assert.Equal(3, StudyPlanCoordinator.CalculateAttemptCount(mixed, StudyCoverageMode.Auto, "Qwen3.5-4B"));
        Assert.Equal(2, StudyPlanCoordinator.CalculateAttemptCount(mixed, StudyCoverageMode.Balanced, "Qwen3.5-4B"));
        Assert.Equal(3, StudyPlanCoordinator.CalculateAttemptCount(mixed, StudyCoverageMode.Thorough, "Qwen3.5-4B"));
        Assert.Equal(2, StudyPlanCoordinator.CalculateAttemptCount(mixed, StudyCoverageMode.Auto, "Qwen3.5-0.8B"));
        Assert.All(
            StudyAutoCoverage.ResolveAttempts(mixed, StudyCoverageMode.Auto, "Qwen3.5-0.8B").Values,
            attempts => Assert.InRange(attempts, 0, 1));
    }

    [Fact]
    public void AutoCoverage_CapsExtrasOnLongRichNotesAndStaysBelowThorough()
    {
        var wiki = string.Join(
            " ",
            Enumerable.Range(1, 40).Select(index =>
                $"Wiki sentence {index} records a distinct encyclopedia fact with enough tokens to study."));
        var dense = string.Join(
            " ",
            Enumerable.Range(1, 40).Select(index =>
                $"In {1900 + index} Trump imposed a travel ban on seven Muslim-majority countries, expanded the Mexico-United States border wall, and enforced a family separation policy."));
        var wikiUnits = StudyPlanCoordinator.SplitSource(wiki);
        var denseUnits = StudyPlanCoordinator.SplitSource(dense);

        var wikiAuto = StudyPlanCoordinator.CalculateAttemptCount(wikiUnits, StudyCoverageMode.Auto, "Qwen3.5-4B");
        var denseAuto = StudyPlanCoordinator.CalculateAttemptCount(denseUnits, StudyCoverageMode.Auto, "Qwen3.5-4B");
        var denseThorough = StudyPlanCoordinator.CalculateAttemptCount(denseUnits, StudyCoverageMode.Thorough, "Qwen3.5-4B");

        Assert.Equal(40, wikiAuto);
        Assert.Equal(52, denseAuto);
        Assert.Equal(80, denseThorough);
        Assert.True(denseAuto < denseThorough);
        Assert.True(denseAuto - 40 <= 12);
    }

    [Fact]
    public async Task AutoCoverage_PacketTargetCountFollowsAttemptSumAndHintsExtraUnits()
    {
        const string source = """
            Elena was born in 1946.
            Trump imposed a travel ban on seven Muslim-majority countries, expanded the Mexico-United States border wall, and enforced a family separation policy.
            """;
        using var backend = new FakeTextGenerationBackend { TokenCountOverride = 80 };
        EnqueueGroundedPairs(backend, """
            fact: F001
            source: S001
            q: When was Elena born?

            fact: F002
            source: S002
            q: What travel ban did Trump impose?

            fact: F003
            source: S002
            q: Which border wall did Trump expand?
            """, """
            fact: F001
            span: T005-T005

            fact: F002
            span: T001-T008

            fact: F003
            span: T010-T016
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-4B", StudyCoverageMode.Auto);
        var packet = Assert.Single(context.Plan.ContextPackets);

        Assert.Equal(3, packet.TargetCount);
        Assert.Equal(3, context.Plan.RequestedTargetCount);
        Assert.Contains("F002 -> S002 -> FOCUS 1", backend.Requests[0].Prompt, StringComparison.Ordinal);
        Assert.Contains("F003 -> S002 -> FOCUS 2", backend.Requests[0].Prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Qwen3.5-0.8B", 4096, 20)]
    [InlineData("Qwen3.5-2B", 6144, 1)]
    [InlineData("Qwen3.5-4B", 8192, 12)]
    [InlineData("Qwen3.5-9B", 16384, 20)]
    public void ModelCapabilities_ScaleContextAndPacketResponsibility(
        string modelKey,
        int contextSize,
        int packetLimit)
    {
        var capabilities = AiModelCapabilities.Resolve(modelKey);
        Assert.Equal(contextSize, capabilities.ContextSize);
        Assert.Equal(packetLimit, capabilities.ContextPacketTargetLimit);
    }

    [Fact]
    public async Task Coordinator_2BRejectsAnswerThatExceedsItsAtomicSpanContract()
    {
        const string source =
            "The device recorded water levels every fifteen minutes and won a regional science prize in 1991.";
        using var backend = new FakeTextGenerationBackend { TokenCountOverride = 22 };
        backend.EnqueueResponse("""
            q: What did the device record?
            a: water levels every fifteen minutes and won a regional science prize in 1991
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-2B", StudyCoverageMode.Auto);

        var fact = Assert.Single(context.SelectedFacts);
        Assert.True(fact.IsCoverageFallback);
        Assert.Empty(fact.CandidateQuestion);
        Assert.Equal(1, backend.GenerateCallCount);
        Assert.Equal(1.0, backend.Requests[0].RepetitionPenalty);
    }

    [Fact]
    public async Task Coordinator_2BUsesGroundedDraftAnswerWithoutOpenAnswerRewrite()
    {
        const string source =
            "Her mother taught mathematics, while her father repaired navigation instruments for fishing vessels.";
        using var backend = new FakeTextGenerationBackend { TokenCountOverride = 22 };
        backend.EnqueueResponses("""
            q: What did her mother teach?
            a: Mathematics.
            """, """
            fact: F001
            language: SAME
            decision: KEEP
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-2B", StudyCoverageMode.Auto);

        var fact = Assert.Single(context.SelectedFacts);
        Assert.False(fact.IsCoverageFallback);
        Assert.Equal("What did her mother teach?", fact.CandidateQuestion);
        Assert.Equal("mathematics", fact.Answer);
        Assert.Equal(StudyAnswerKind.Text, fact.AnswerKind);
        Assert.Equal(2, backend.GenerateCallCount);
        Assert.Contains("language and decision records", backend.Requests[1].Prompt, StringComparison.Ordinal);
        Assert.Contains("does not identify who or what", backend.Requests[1].Prompt, StringComparison.Ordinal);
        Assert.Contains("led a team that installed", backend.Requests[1].Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coordinator_SelectsExactSourceSpansAndKeepsFullShortNoteContext()
    {
        using var backend = CreateTwoFactBackend();
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(Source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        Assert.Equal(2, context.TargetCount);
        Assert.Equal(["green leaves", "chemical energy"], context.SelectedFacts.Select(fact => fact.Answer));
        Assert.All(context.SelectedFacts, fact => Assert.Contains(fact.Answer, fact.Evidence, StringComparison.Ordinal));
        Assert.Contains("OWNED S001", backend.Requests[0].Prompt);
        Assert.Contains("OWNED S002", backend.Requests[0].Prompt);
        Assert.Contains("grammatical recall", backend.Requests[0].Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unit's own words", backend.Requests[0].Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("category word", backend.Requests[0].Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(context.SelectedFacts, fact => fact.IsCoverageFallback);
    }

    [Fact]
    public async Task Coordinator_AllowsTheSameAnswerTextForDifferentSourceFacts()
    {
        const string source = """
            Mira opened the laboratory in 2019. Leon founded the archive in 2019.
            """;
        using var backend = new FakeTextGenerationBackend { TokenCountOverride = 24 };
        EnqueueGroundedPairs(backend, """
            fact: F001
            source: S001
            q: When did Mira open the laboratory?
            span: 2019

            fact: F002
            source: S002
            q: When did Leon found the archive?
            span: 2019
            """, """
            fact: F001
            span: 2019

            fact: F002
            span: 2019
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        Assert.Equal(2, context.SelectedFacts.Count);
        Assert.All(context.SelectedFacts, fact => Assert.Equal("2019", fact.Answer));
        Assert.All(context.SelectedFacts, fact => Assert.False(fact.IsCoverageFallback));
        Assert.Equal(2, context.SelectedFacts.Select(fact => fact.SourceUnitId).Distinct().Count());
    }

    [Fact]
    public async Task Coordinator_InvalidModelSpanUsesStructuralFallbackWithoutRetry()
    {
        using var backend = new FakeTextGenerationBackend { TokenCountOverride = 29 };
        EnqueueGroundedPairs(backend, """
            fact: F001
            source: S001
            q: Where do plants use chlorophyll to absorb sunlight?
            """, """
            fact: F001
            span: T999-T999
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(Source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        Assert.Equal(2, context.SelectedFacts.Count);
        Assert.All(context.SelectedFacts, fact => Assert.True(fact.IsCoverageFallback));
        Assert.Equal(2, context.SelectedFacts.Select(fact => fact.SourceUnitId).Distinct().Count());
        Assert.Equal(2, backend.GenerateCallCount);
    }

    [Fact]
    public async Task Coordinator_GarbageSpanUsesDeterministicUnitFallback()
    {
        const string source =
            "Schools and small laboratories could assemble one sensor for less than fifty dollars.";
        using var backend = new FakeTextGenerationBackend { TokenCountOverride = 40 };
        EnqueueGroundedPairs(backend, """
            fact: F001
            source: S001
            q: How much could schools and small laboratories assemble one sensor for?
            """, """
            fact: F001
            span: Yes
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        var fact = Assert.Single(context.SelectedFacts);
        Assert.Equal("fifty dollars", fact.Answer);
        Assert.True(fact.IsCoverageFallback);
        Assert.Equal(2, backend.GenerateCallCount);
    }

    [Fact]
    public async Task SmallestModel_IsDeterministicClozeOnly()
    {
        using var backend = new FakeTextGenerationBackend { TokenCountOverride = 86 };
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(Source, "Qwen3.5-0.8B", StudyCoverageMode.Auto);
        var questions = await new GroundedQuestionPlanner().GetOrCreateAsync(context);

        Assert.Equal(2, context.TargetCount);
        Assert.All(questions.Questions, question =>
        {
            Assert.False(question.IsNatural);
            Assert.Contains("_____", question.Question);
            Assert.Contains(question.Answer, question.Fact.Evidence, StringComparison.Ordinal);
            Assert.InRange(Regex.Matches(question.Answer, @"[\p{L}\p{N}]+").Count, 1, 2);
            Assert.Matches(@"\p{L}", question.Question.Replace("_____", "", StringComparison.Ordinal));
        });
        Assert.Equal(0, backend.GenerateCallCount);
    }

    [Fact]
    public async Task QuestionGeneration_UsesImmutableAnswersAndContextualBatch()
    {
        using var backend = CreateTwoFactBackend();
        var coordinator = new StudyPlanCoordinator(backend, new StudyPlanCache());
        var context = await coordinator.GetOrCreateAsync(Source, "Qwen3.5-4B", StudyCoverageMode.Auto);
    var result = await new GroundedQuestionPlanner().GetOrCreateAsync(context);

        Assert.Equal(2, result.ModelGeneratedItemCount);
        Assert.Equal(["green leaves", "chemical energy"], result.Questions.Select(question => question.Answer));
        Assert.Contains("SOURCE:", backend.Requests[0].Prompt);
        Assert.Contains("F001 -> S001 -> FOCUS 1", backend.Requests[0].Prompt);
        Assert.Contains("F002 -> S002 -> FOCUS 1", backend.Requests[0].Prompt);
        Assert.Contains("SOURCE S001:", backend.Requests[1].Prompt);
        Assert.Contains("smallest grammatically complete continuous text span", backend.Requests[1].Prompt);
        Assert.Contains("SOURCE S001: Plants", backend.Requests[1].Prompt);
        Assert.DoesNotContain("a:", backend.Requests[0].Grammar!, StringComparison.Ordinal);
        Assert.DoesNotContain("match:", backend.Requests[0].Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, backend.GenerateCallCount);
    }

    [Fact]
    public async Task Coordinator_RejectsWholeUnitLegacySpans()
    {
        using var backend = new FakeTextGenerationBackend { TokenCountOverride = 29 };
        EnqueueGroundedPairs(backend, """
            fact: F001
            source: S001
            q: Where do plants use chlorophyll to absorb sunlight?
            """, """
            fact: F001
            span: T001-T012
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(Source, "Qwen3.5-4B", StudyCoverageMode.Auto);
        var questions = await new GroundedQuestionPlanner().GetOrCreateAsync(context);

        Assert.Equal(2, context.TargetCount);
        Assert.All(context.SelectedFacts, fact => Assert.True(fact.IsCoverageFallback));
        Assert.All(questions.Questions, question => Assert.False(question.IsNatural));
        Assert.Equal(2, backend.GenerateCallCount);
    }

    [Fact]
    public async Task Coordinator_InvalidSpan_FallsBackToWholeTokenUnitCloze()
    {
        const string longSource =
            "The observatory recorded the supernova after several cloudy nights delayed the first measurement and then published the complete annual report for the following observing season with calibrated magnitudes tables and comparison plates from the archive.";
        using var backend = new FakeTextGenerationBackend { TokenCountOverride = 29 };
        EnqueueGroundedPairs(backend, """
            fact: F001
            source: S001
            q: What did the observatory publish after the delayed measurement?
            """, """
            fact: F001
            span: T999-T999
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(longSource, "Qwen3.5-4B", StudyCoverageMode.Auto);

        var fact = Assert.Single(context.SelectedFacts);
        Assert.True(fact.IsCoverageFallback);
        Assert.Equal("archive", fact.Answer);
        Assert.Equal(2, backend.GenerateCallCount);
    }

    [Fact]
    public async Task BlankQuestion_FallsBackPerRecordWithoutRepair()
    {
        using var backend = new FakeTextGenerationBackend { TokenCountOverride = 29 };
        backend.EnqueueResponse("fact: F001\nsource: S001\nq: _____?");
        var context = await CreateUncachedContextAsync(backend, StudyCoverageMode.Auto);

        var result = await new GroundedQuestionPlanner().GetOrCreateAsync(context);

        Assert.Equal(2, context.SelectedFacts.Count);
        Assert.All(context.SelectedFacts, fact => Assert.True(fact.IsCoverageFallback));
        Assert.Equal(2, result.Questions.Count);
        Assert.All(result.Questions, question => Assert.False(question.IsNatural));
        Assert.Equal(1, backend.GenerateCallCount);
    }

    [Fact]
    public void HardValidation_IsLanguageNeutralAndRejectsOnlyProvableFailures()
    {
        var fact = new StudyFact(
            "F001", "S001", "月は地球の周りを公転する。", "地球", StudyFactImportance.Core, 0,
            QuestionContext: "OWNED S001: 月は地球の周りを公転する。");

        Assert.True(GroundedQuestionValidator.TryValidateHard(
            fact, "月は何の周りを公転する？", fact.QuestionContext, out _));
        Assert.False(GroundedQuestionValidator.TryValidateHard(
            fact, "月は地球の周りを公転する？", fact.QuestionContext, out var leakage));
        Assert.Equal(GroundedRejectionReason.AnswerLeakage, leakage);
        Assert.True(GroundedQuestionValidator.TryValidateHard(
            new StudyFact(
                "F001b", "S001",
                "In 2016 he ran as a political outsider in the election.",
                "he",
                StudyFactImportance.Core, 0,
                QuestionContext: "OWNED S001: In 2016 he ran as a political outsider in the election."),
            "Who ran as a political outsider in the election?",
            "OWNED S001: In 2016 he ran as a political outsider in the election.",
            out _));
        Assert.False(GroundedQuestionValidator.TryValidateHard(
            fact, "_____?", fact.QuestionContext, out var placeholder));
        Assert.Equal(GroundedRejectionReason.Placeholder, placeholder);
        Assert.True(GroundedQuestionValidator.TryValidateHard(
            new StudyFact(
                "F002", "S001",
                "After losing the 2020 election, she left office.",
                "she left office",
                StudyFactImportance.Core, 0,
                QuestionContext: "OWNED S001: After losing the 2020 election, she left office."),
            "Losing support after the vote, what happened next?",
            "OWNED S001: After losing the 2020 election, she left office.",
            out _));
        Assert.False(GroundedQuestionValidator.TryValidateHard(
            new StudyFact(
                "F003", "S001",
                "Gitanas Nausėda yra Lietuvos ekonomistas ir politikas.",
                "ekonomistas ir politikas",
                StudyFactImportance.Core, 0),
            "Gitanas Nausėda?",
            "OWNED S001: Gitanas Nausėda yra Lietuvos ekonomistas ir politikas.",
            out var fragment));
        Assert.Equal(GroundedRejectionReason.SourceEcho, fragment);
        Assert.False(GroundedQuestionValidator.TryValidateHard(
            new StudyFact(
                "F003b", "S002",
                "Nuo 2019 m. eina Lietuvos Respublikos Prezidento pareigas.",
                "Prezidento pareigas",
                StudyFactImportance.Core, 1),
            "Gitanas Nausėda?",
            "REFERENCE S001: Gitanas Nausėda yra Lietuvos ekonomistas ir politikas.\n" +
            "OWNED S002: Nuo 2019 m. eina Lietuvos Respublikos Prezidento pareigas.",
            out var contextFragment));
        Assert.Equal(GroundedRejectionReason.SourceEcho, contextFragment);
        Assert.False(GroundedQuestionValidator.TryValidateHard(
            new StudyFact(
                "F004", "S001", "Šviesos reakcijose gaminami ATP ir NADPH.",
                "Šviesos reakcijose", StudyFactImportance.Core, 0),
            "ATP ir NADPH gaminami?",
            "Šviesos reakcijose gaminami ATP ir NADPH.",
            out var declarativeQuestion));
        Assert.Equal(GroundedRejectionReason.SourceEcho, declarativeQuestion);
        Assert.False(GroundedQuestionValidator.TryValidateHard(
            new StudyFact(
                "F005", "S001",
                "Dabar darbuotojai rengia dvi naktines ekskursijas kiekvieną mėnesį.",
                "kiekvieną mėnesį", StudyFactImportance.Core, 0),
            "Dabar darbuotojai rengia ekskursijas?",
            "Dabar darbuotojai rengia dvi naktines ekskursijas kiekvieną mėnesį.",
            out var yesNoQuestion));
        Assert.Equal(GroundedRejectionReason.SourceEcho, yesNoQuestion);
    }

    [Fact]
    public async Task Planner_DoesNotGuessLanguageSemanticsFromVocabulary()
    {
        const string evidence = "After losing the 2020 election, she left office.";
        var fact = new StudyFact(
            "F001",
            "S001",
            evidence,
            "she left office",
            StudyFactImportance.Core,
            0,
            QuestionContext: $"OWNED S001: {evidence}",
            AnswerStart: evidence.IndexOf("she left office", StringComparison.Ordinal),
            AnswerLength: "she left office".Length,
            CandidateQuestion: "Losing support after the vote, what happened next?");
        var plan = new StudyPlan(
            "setup-echo",
            "Qwen3.5-4B",
            StudyPlanCoordinator.CurrentPlannerVersion,
            DateTimeOffset.UtcNow,
            [fact]);
        var context = new StudyGenerationContext(plan, StudyCoverageMode.Auto, [fact]);
        using var backend = new FakeTextGenerationBackend();

        var result = await new GroundedQuestionPlanner().GetOrCreateAsync(context);

        var question = Assert.Single(result.Questions);
        Assert.True(question.IsNatural);
        Assert.Equal("Losing support after the vote, what happened next?", question.Question);
        Assert.Null(Assert.Single(result.Decisions).RejectionReason);
        Assert.Equal(0, backend.GenerateCallCount);
    }

    [Fact]
    public void Cloze_FullSentenceSpan_KeepsStemAndAtMostTwoTokens()
    {
        const string evidence = "Donald Trump is the president convicted of a felony.";
        var fact = new StudyFact(
            "F001", "S001", evidence, evidence.TrimEnd('.'), StudyFactImportance.Core, 0,
            AnswerStart: 0,
            AnswerLength: evidence.TrimEnd('.').Length);

        var seed = GroundedClozeBuilder.CreateSeed(fact);

        Assert.Contains("_____", seed.Cloze);
        Assert.NotEqual("_____.", seed.Cloze.Trim());
        Assert.Matches(@"\p{L}", seed.Cloze.Replace("_____", "", StringComparison.Ordinal));
        Assert.InRange(Regex.Matches(seed.Answer, @"[\p{L}\p{N}]+").Count, 1, 2);
        Assert.Contains(seed.Answer, evidence, StringComparison.Ordinal);
        Assert.Equal("felony", seed.Answer);
        Assert.Equal("Donald Trump is the president convicted of a _____.", seed.Cloze);
    }

    [Fact]
    public void Cloze_LongPhrase_BlanksLastTwoTokens()
    {
        const string evidence = "Plants use chlorophyll in green leaves to absorb sunlight.";
        const string answer = "chlorophyll in green leaves to absorb sunlight";
        var fact = new StudyFact(
            "F001", "S001", evidence, answer, StudyFactImportance.Core, 0,
            AnswerStart: evidence.IndexOf(answer, StringComparison.Ordinal),
            AnswerLength: answer.Length);

        var seed = GroundedClozeBuilder.CreateSeed(fact);

        Assert.Equal("absorb sunlight", seed.Answer);
        Assert.Equal("Plants use chlorophyll in green leaves to _____.", seed.Cloze);
    }

    [Fact]
    public void Cloze_CalendarDate_StaysWhole()
    {
        const string evidence = "Donald John Trump was born on June 14, 1946 in New York City.";
        const string answer = "June 14, 1946";
        var fact = new StudyFact(
            "F001", "S001", evidence, answer, StudyFactImportance.Core, 0,
            StudyAnswerKind.Date,
            AnswerStart: evidence.IndexOf(answer, StringComparison.Ordinal),
            AnswerLength: answer.Length,
            ValueShape: StudyValueShape.CalendarDate);

        var seed = GroundedClozeBuilder.CreateSeed(fact);

        Assert.Equal(answer, seed.Answer);
        Assert.Equal("Donald John Trump was born on _____ in New York City.", seed.Cloze);
    }

    [Fact]
    public void Cloze_ShortScopedQuantity_StaysWhole()
    {
        const string evidence = "The reaction takes 24 hours to complete.";
        const string answer = "24 hours";
        var fact = new StudyFact(
            "F001", "S001", evidence, answer, StudyFactImportance.Core, 0,
            StudyAnswerKind.Quantity,
            AnswerStart: evidence.IndexOf(answer, StringComparison.Ordinal),
            AnswerLength: answer.Length,
            ValueShape: StudyValueShape.ScopedQuantity);

        var seed = GroundedClozeBuilder.CreateSeed(fact);

        Assert.Equal(answer, seed.Answer);
        Assert.Equal("The reaction takes _____ to complete.", seed.Cloze);
    }

    [Fact]
    public void Cloze_NoSpaceScript_LeavesAReadableStem()
    {
        const string evidence = "月は地球の周りを公転する。";
        const string answer = "地球";
        var fact = new StudyFact(
            "F001", "S001", evidence, answer, StudyFactImportance.Core, 0,
            AnswerStart: evidence.IndexOf(answer, StringComparison.Ordinal),
            AnswerLength: answer.Length);

        var seed = GroundedClozeBuilder.CreateSeed(fact);

        Assert.Contains("_____", seed.Cloze);
        Assert.Contains(seed.Answer, answer, StringComparison.Ordinal);
        Assert.Contains("月", seed.Cloze, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitSource_NoSpaceSentenceTerminators_CreateSeparateUnits()
    {
        var units = StudyPlanCoordinator.SplitSource("月は地球の周りを公転する。地球は太陽の周りを公転する。火星も太陽を公転する。");

        Assert.Equal(3, units.Count);
    }

    [Fact]
    public void Cloze_DoesNotSplitInsideAWord()
    {
        const string label = "Biografija.";
        Assert.False(GroundedClozeBuilder.IsViableSourceUnit(label));

        var hangman = new StudyFact(
            "F001", "S001", label, "Biografija", StudyFactImportance.Supporting, 0,
            AnswerStart: 0,
            AnswerLength: "Biografija".Length);
        Assert.False(GroundedClozeBuilder.TryCreate(hangman, out var cloze, out _));
        Assert.DoesNotContain("_____", cloze);
        Assert.DoesNotContain("_____", GroundedClozeBuilder.CreateSeed(hangman).Cloze);

        const string evidence = "Trump biografija.";
        var suffix = new StudyFact(
            "F002", "S001", evidence, "ja", StudyFactImportance.Supporting, 0,
            AnswerStart: evidence.LastIndexOf("ja", StringComparison.Ordinal),
            AnswerLength: 2);
        Assert.False(GroundedClozeBuilder.TryCreate(suffix, out var suffixCloze, out _));
        Assert.DoesNotContain("_____", suffixCloze);
        Assert.DoesNotContain("_____", GroundedClozeBuilder.CreateSeed(suffix).Cloze);
    }

    [Fact]
    public async Task SmallestModel_SkipsNonViableUnitsAndDoesNotPadThorough()
    {
        const string source = """
            Biografija.
            Plants use chlorophyll in green leaves to absorb sunlight and begin photosynthesis.
            They convert the captured sunlight into chemical energy that supports later growth.
            """;
        using var backend = new FakeTextGenerationBackend();
        var coordinator = new StudyPlanCoordinator(backend, new StudyPlanCache());
        var auto = await coordinator.GetOrCreateAsync(source, "Qwen3.5-0.8B", StudyCoverageMode.Auto);
        var thorough = await coordinator.GetOrCreateAsync(source, "Qwen3.5-0.8B", StudyCoverageMode.Thorough);

        Assert.Equal(2, auto.TargetCount);
        Assert.Equal(2, thorough.TargetCount);
        Assert.DoesNotContain(auto.SelectedFacts, fact =>
            fact.Evidence.Contains("Biografija", StringComparison.OrdinalIgnoreCase));
        Assert.All(auto.SelectedFacts, fact =>
            Assert.InRange(Regex.Matches(fact.Answer, @"[\p{L}\p{N}]+").Count, 1, 2));
        Assert.Equal(0, backend.GenerateCallCount);
    }

    [Fact]
    public async Task Thorough_DoesNotPadUsedSentencesToHitAQuota()
    {
        using var backend = CreateTwoFactBackend();
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(Source, "Qwen3.5-4B", StudyCoverageMode.Thorough);

        Assert.Equal(2, context.TargetCount);
        Assert.Equal(["green leaves", "chemical energy"], context.SelectedFacts.Select(fact => fact.Answer));
        Assert.DoesNotContain(context.SelectedFacts, fact => fact.IsCoverageFallback);
        Assert.Equal(4, backend.GenerateCallCount);
    }

    [Fact]
    public async Task Coordinator_KeepsMinimalPersonAnswer()
    {
        const string source = """
            In 2016 he ran as a political outsider in the election against Hillary Clinton.
            He later signed a travel ban covering several Muslim-majority countries.
            """;
        using var backend = new FakeTextGenerationBackend();
        EnqueueGroundedPairs(backend, """
            fact: F001
            source: S001
            q: Who did he run against in the 2016 election?

            fact: F002
            source: S002
            q: What travel policy did he later sign?
            """, """
            fact: F001
            span: T013-T014

            fact: F002
            span: T005-T006
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        Assert.Equal("Hillary Clinton", context.SelectedFacts[0].Answer);
        Assert.Equal("travel ban", context.SelectedFacts[1].Answer);
    }

    [Fact]
    public async Task Coordinator_DoesNotExpandShortSpanThroughALongTail()
    {
        using var backend = CreateTwoFactBackend();
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(Source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        Assert.Equal("green leaves", context.SelectedFacts[0].Answer);
        Assert.DoesNotContain("photosynthesis", context.SelectedFacts[0].Answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coordinator_UsesExactSelectedNounPhrase()
    {
        const string source = """
            In Latin America he ordered a military raid into Venezuela to capture the country's president.
            He later signed a travel ban covering several Muslim-majority countries.
            """;
        using var backend = new FakeTextGenerationBackend();
        EnqueueGroundedPairs(backend, """
            fact: F001
            source: S001
            q: What military action did he order in Venezuela?

            fact: F002
            source: S002
            q: What travel policy did he later sign?
            """, """
            fact: F001
            span: T006-T008

            fact: F002
            span: T005-T006
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        Assert.Equal("a military raid", context.SelectedFacts[0].Answer);
        Assert.DoesNotContain("Venezuela", context.SelectedFacts[0].Answer, StringComparison.Ordinal);
        Assert.Equal("travel ban", context.SelectedFacts[1].Answer);
    }

    [Fact]
    public async Task Coordinator_KeepsCompleteSelectedListItem()
    {
        const string source = """
            Trump imposed a travel ban on seven Muslim-majority countries, expanded the Mexico-United States border wall, and enforced a family separation policy.
            He later appointed three Supreme Court justices.
            """;
        using var backend = new FakeTextGenerationBackend();
        EnqueueGroundedPairs(backend, """
            fact: F001
            source: S001
            q: How many Muslim-majority countries did the travel ban cover?

            fact: F002
            source: S002
            q: How many Supreme Court justices did he appoint?
            """, """
            fact: F001
            span: T007-T009

            fact: F002
            span: T004-T007
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        Assert.Equal("seven Muslim-majority countries", context.SelectedFacts[0].Answer);
        Assert.DoesNotContain("border wall", context.SelectedFacts[0].Answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coordinator_DoesNotRetargetSelectedAnswer()
    {
        const string source = """
            In Latin America he ordered a military raid into Venezuela to capture the country's president.
            He later signed a travel ban covering several Muslim-majority countries.
            """;
        using var backend = new FakeTextGenerationBackend();
        EnqueueGroundedPairs(backend, """
            fact: F001
            source: S001
            q: What military action did he order in Latin America?

            fact: F002
            source: S002
            q: What travel policy did he later sign?
            """, """
            fact: F001
            span: T006-T008

            fact: F002
            span: T005-T006
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        Assert.Equal("a military raid", context.SelectedFacts[0].Answer);
        Assert.DoesNotContain("Venezuela", context.SelectedFacts[0].Answer, StringComparison.Ordinal);
        Assert.Equal("travel ban", context.SelectedFacts[1].Answer);
    }

    [Fact]
    public async Task Coordinator_UsesSelectedCountedNounPhrase()
    {
        const string source = """
            During that period, Maris led a team that installed forty solar-powered purification units in nearby villages.
            She completed her undergraduate degree in 2000.
            """;
        using var backend = new FakeTextGenerationBackend();
        EnqueueGroundedPairs(backend, """
            fact: F001
            source: S001
            q: How many solar-powered purification units were installed by Maris's team?

            fact: F002
            source: S002
            q: When did she complete her undergraduate degree?
            """, """
            fact: F001
            span: T010-T013

            fact: F002
            span: T007-T007
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        Assert.Contains("forty", context.SelectedFacts[0].Answer, StringComparison.Ordinal);
        Assert.Contains("units", context.SelectedFacts[0].Answer, StringComparison.Ordinal);
        Assert.DoesNotContain("villages", context.SelectedFacts[0].Answer, StringComparison.Ordinal);
        Assert.Equal("2000", context.SelectedFacts[1].Answer);
    }

    [Fact]
    public async Task Coordinator_KeepsDateSpanForWhenQuestions()
    {
        const string source = """
            Donald John Trump was born on June 14, 1946 in New York City.
            He later entered politics and won the presidential election.
            """;
        using var backend = new FakeTextGenerationBackend();
        EnqueueGroundedPairs(backend, """
            fact: F001
            source: S001
            q: When was he born?

            fact: F002
            source: S002
            q: What did he later win?
            """, """
            fact: F001
            span: T007-T009

            fact: F002
            span: T007-T009
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        Assert.Equal("June 14, 1946", context.SelectedFacts[0].Answer);
    }

    [Fact]
    public async Task Coordinator_KeepsMinimalCapitalizedName()
    {
        const string source = """
            In 2009, Maris founded Clear Current, a nonprofit organization focused on community water systems.
            She completed her undergraduate degree in 2000.
            """;
        using var backend = new FakeTextGenerationBackend();
        EnqueueGroundedPairs(backend, """
            fact: F001
            source: S001
            q: What company did Maris found in 2009?

            fact: F002
            source: S002
            q: When did she complete her undergraduate degree?
            """, """
            fact: F001
            span: T005-T006

            fact: F002
            span: T007-T007
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        Assert.Equal("Clear Current", context.SelectedFacts[0].Answer);
        Assert.DoesNotContain("nonprofit", context.SelectedFacts[0].Answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coordinator_KeepsCompleteSelectedNumericMeasure()
    {
        const string source = """
            The device recorded water levels every fifteen minutes and won a regional science prize in 1991.
            She completed her undergraduate degree in 2000.
            """;
        using var backend = new FakeTextGenerationBackend();
        EnqueueGroundedPairs(backend, """
            fact: F001
            source: S001
            q: When did the device record water levels?

            fact: F002
            source: S002
            q: When did she complete her undergraduate degree?
            """, """
            fact: F001
            span: T006-T008

            fact: F002
            span: T007-T007
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        Assert.Equal("every fifteen minutes", context.SelectedFacts[0].Answer);
        Assert.DoesNotContain("prize", context.SelectedFacts[0].Answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuestionWithNumberAbsentFromDeclaredUnit_FallsBackToCloze()
    {
        const string travel = "Trump signed an executive order implementing a travel ban on several Muslim-majority countries.";
        const string election = "In 2016 he ran as a political outsider in the election against Hillary Clinton.";
        var packet = $"OWNED S001: {travel}\nOWNED S002: {election}";
        var fact = new StudyFact(
            "F001",
            "S002",
            election,
            "a political outsider in the election against",
            StudyFactImportance.Core,
            1,
            QuestionContext: packet,
            AnswerStart: election.IndexOf("a political outsider in the election against", StringComparison.Ordinal),
            AnswerLength: "a political outsider in the election against".Length,
            CandidateQuestion: "What happened in the 2020 election?");
        var plan = new StudyPlan(
            "unit-mismatch",
            "Qwen3.5-4B",
            StudyPlanCoordinator.CurrentPlannerVersion,
            DateTimeOffset.UtcNow,
            [fact]);
        var context = new StudyGenerationContext(plan, StudyCoverageMode.Auto, [fact]);
        using var backend = new FakeTextGenerationBackend();

        Assert.False(GroundedQuestionValidator.TryValidateHard(
            fact, fact.CandidateQuestion, packet, out var reason));
        Assert.Equal(GroundedRejectionReason.InvalidEvidence, reason);

        var result = await new GroundedQuestionPlanner().GetOrCreateAsync(context);
        var question = Assert.Single(result.Questions);
        Assert.False(question.IsNatural);
        Assert.Contains("_____", question.Question);
        Assert.Equal(GroundedRejectionReason.InvalidEvidence, Assert.Single(result.Decisions).RejectionReason);
        Assert.Equal(0, backend.GenerateCallCount);
    }

    [Fact]
    public void BornOnDateQuestion_StaysNaturalWithoutAnswerOverlap()
    {
        const string birth = "Donald John Trump was born on June 14, 1946 in New York City.";
        const string later = "He later entered politics and won the presidential election.";
        var packet = $"OWNED S001: {birth}\nOWNED S002: {later}";
        var fact = new StudyFact(
            "F001",
            "S001",
            birth,
            "June 14, 1946",
            StudyFactImportance.Core,
            0,
            StudyAnswerKind.Date,
            packet,
            0,
            birth.IndexOf("June 14, 1946", StringComparison.Ordinal),
            "June 14, 1946".Length,
            ValueShape: StudyValueShape.CalendarDate);

        Assert.True(GroundedQuestionValidator.TryValidateHard(
            fact, "When was he born?", packet, out var reason), reason.ToString());
    }

    [Fact]
    public async Task Coordinator_KeepsUsAbbreviationInTheSameSentence()
    {
        const string source = """
            He withdrew the U.S. from agreements on climate and trade.
            In May 2024 he became the first U.S. president convicted of a felony.
            """;
        using var backend = new FakeTextGenerationBackend();
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-0.8B", StudyCoverageMode.Auto);

        Assert.Equal(2, context.Plan.SourceUnitCount);
        Assert.Contains(
            context.SelectedFacts,
            fact => fact.Evidence.Contains("from agreements on climate and trade", StringComparison.Ordinal));
        Assert.Contains(
            context.SelectedFacts,
            fact => fact.Evidence.Contains("U.S. president convicted of a felony", StringComparison.Ordinal));
        Assert.Equal(0, backend.GenerateCallCount);
    }

    [Fact]
    public async Task Coordinator_KeepsProfAbbreviationInTheSameSentence()
    {
        const string source =
            "1987–1989 m. VU Ekonomikos fakulteto aspirantas (vadovas prof. Vladas Astrauskas). Jis gimė Klaipėdoje 1964 metais.";
        using var backend = new FakeTextGenerationBackend();
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-0.8B", StudyCoverageMode.Auto);

        Assert.Equal(2, context.Plan.SourceUnitCount);
        Assert.Contains(
            context.SelectedFacts,
            fact => fact.Evidence.Contains("prof. Vladas Astrauskas", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Coordinator_PreservesPairedUnicodeQuotesAroundSelectedAnswer()
    {
        const string source = """
            Gitanas Nausėda dainavo berniukų chore „Gintarėlis“.
            Vėliau jis studijavo ekonomiką Vilniaus universitete.
            """;
        using var backend = new FakeTextGenerationBackend();
        EnqueueGroundedPairs(backend, """
            fact: F001
            source: S001
            q: Kokiame berniukų chore dainavo Gitanas Nausėda?

            fact: F002
            source: S002
            q: Kur jis vėliau studijavo ekonomiką?
            """, """
            fact: F001
            span: Gintarėlis

            fact: F002
            span: Vilniaus universitete
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        Assert.Equal("„Gintarėlis“", context.SelectedFacts[0].Answer);
        Assert.Equal("Vilniaus universitete", context.SelectedFacts[1].Answer);
    }

    [Fact]
    public async Task Coordinator_PreservesClosingQuoteAfterBroadSpan()
    {
        const string source = """
            Gitanas Nausėda dainavo berniukų chore "Gintarėlis".
            Vėliau jis studijavo ekonomiką Vilniaus universitete.
            """;
        using var backend = new FakeTextGenerationBackend();
        EnqueueGroundedPairs(backend, """
            fact: F001
            source: S001
            q: Kuo užsiimdavo Gitanas Nausėda berniukų chore "Gintarėlis"?

            fact: F002
            source: S002
            q: Kur jis vėliau studijavo ekonomiką?
            """, """
            fact: F001
            span: dainavo berniukų chore "Gintarėlis

            fact: F002
            span: Vilniaus universitete
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        Assert.Equal("dainavo berniukų chore \"Gintarėlis\"", context.SelectedFacts[0].Answer);
    }

    [Fact]
    public void Cloze_DoesNotLeadTheBlankWithAPreposition()
    {
        const string evidence = "Elena Maris was born in 1978 in the coastal city of Bellhaven.";
        var fact = new StudyFact(
            "F001", "S001", evidence, evidence, StudyFactImportance.Core, 0,
            AnswerStart: 0,
            AnswerLength: evidence.Length);
        var seed = GroundedClozeBuilder.CreateSeed(fact);

        Assert.Equal("Bellhaven", seed.Answer);
        Assert.DoesNotContain("of Bellhaven", seed.Answer, StringComparison.Ordinal);
        Assert.Contains("_____", seed.Cloze);
    }

    [Fact]
    public void DistinctiveNames_MustOccurInThePacketAndAllowInflection()
    {
        const string evidence = "Gitanas Nausėda yra Lietuvos ekonomistas ir politikas.";
        var fact = new StudyFact(
            "F001",
            "S001",
            evidence,
            "Lietuvos ekonomistas ir politikas",
            StudyFactImportance.Core,
            0,
            QuestionContext: $"OWNED S001: {evidence}");

        Assert.True(GroundedQuestionValidator.TryValidateHard(
            fact, "Kas yra Gitanas Nausėda?", fact.QuestionContext, out _));
        Assert.True(GroundedQuestionValidator.TryValidateHard(
            fact, "Koks yra Gitanas Nausėdos darbas?", fact.QuestionContext, out _));
        Assert.False(GroundedQuestionValidator.TryValidateHard(
            fact, "Kas yra Gitanas Naskeda?", fact.QuestionContext, out var misspelled));
        Assert.Equal(GroundedRejectionReason.InvalidEvidence, misspelled);
        Assert.False(GroundedQuestionValidator.TryValidateHard(
            fact, "Kuo užsiima Gitanansėda?", fact.QuestionContext, out var mashed));
        Assert.Equal(GroundedRejectionReason.InvalidEvidence, mashed);
    }

    [Fact]
    public void ShortInflectedNames_KeepTheStemWithoutAllowingUnrelatedNames()
    {
        const string evidence = "Milda Vaitkutė yra ekonomistė ir bendruomenės organizatorė.";
        var fact = new StudyFact(
            "F001", "S001", evidence, "ekonomistė ir bendruomenės organizatorė",
            StudyFactImportance.Core, 0, QuestionContext: $"OWNED S001: {evidence}");

        Assert.True(GroundedQuestionValidator.TryValidateHard(
            fact, "Kokia yra Mildos Vaitkutės profesija?", fact.QuestionContext, out var accepted),
            accepted.ToString());
        Assert.False(GroundedQuestionValidator.TryValidateHard(
            fact, "Kokia yra Mildred Vaitkutės profesija?", fact.QuestionContext, out var rejected));
        Assert.Equal(GroundedRejectionReason.InvalidEvidence, rejected);
    }

    [Fact]
    public void ReferencePacketNames_RemainValidOnLaterUnits()
    {
        const string birth = "Elena Maris was born in 1978 in the coastal city of Bellhaven.";
        const string university = "Maris entered Northbridge University in 1996 and studied environmental engineering.";
        var packet = $"OWNED S001: {birth}\nOWNED S005: {university}";
        var fact = new StudyFact(
            "F004",
            "S005",
            university,
            "1996",
            StudyFactImportance.Supporting,
            4,
            StudyAnswerKind.Quantity,
            packet,
            0,
            university.IndexOf("1996", StringComparison.Ordinal),
            4,
            ValueShape: StudyValueShape.Year);

        Assert.True(GroundedQuestionValidator.TryValidateHard(
            fact, "What year did Elena Maris enter Northbridge University?", packet, out var reason),
            reason.ToString());
    }

    [Fact]
    public void QuestionWithInstructionPreamble_IsUngrounded()
    {
        const string evidence = "Gitanas Nausėda yra Lietuvos ekonomistas ir politikas.";
        var fact = new StudyFact(
            "F001",
            "S001",
            evidence,
            "Lietuvos ekonomistas ir politikas",
            StudyFactImportance.Core,
            0,
            QuestionContext: $"OWNED S001: {evidence}");

        Assert.False(GroundedQuestionValidator.TryValidateHard(
            fact,
            "Kiekvieno fakto atsakymas yra trumpa nuosekli frazė iš šio vieneto. Kas Gitanas Nausėda gimus?",
            fact.QuestionContext,
            out var reason));
        Assert.Equal(GroundedRejectionReason.UngroundedQuestion, reason);
    }

    [Fact]
    public void PromptEchoQuestion_IsUngrounded()
    {
        const string evidence = "Gitanas Nausėda yra Lietuvos ekonomistas ir politikas.";
        var fact = new StudyFact(
            "F001",
            "S001",
            evidence,
            "Lietuvos ekonomistas ir politikas",
            StudyFactImportance.Core,
            0,
            QuestionContext: $"OWNED S001: {evidence}");

        Assert.False(GroundedQuestionValidator.TryValidateHard(
            fact,
            "Kuris žodis trūksta? Do not reverse who did what or add facts?",
            fact.QuestionContext,
            out var echoed));
        Assert.Equal(GroundedRejectionReason.UngroundedQuestion, echoed);
        Assert.False(GroundedQuestionValidator.TryValidateHard(
            fact,
            "Kas yra Gitanas Nausėda missing piece from that unit?",
            fact.QuestionContext,
            out var labeled));
        Assert.Equal(GroundedRejectionReason.UngroundedQuestion, labeled);
    }

    [Fact]
    public void LithuanianInterrogativePlusCategory_IsGroundedRecall()
    {
        const string evidence = "Jis gimė 1964 m. gegužės 19 d. Klaipėdoje.";
        var packet =
            "OWNED S001: Gitanas Nausėda yra Lietuvos ekonomistas ir politikas.\n" +
            "OWNED S002: " + evidence;
        var fact = new StudyFact(
            "F002",
            "S002",
            evidence,
            "Klaipėdoje",
            StudyFactImportance.Supporting,
            1,
            QuestionContext: packet);

        Assert.True(GroundedQuestionValidator.TryValidateHard(
            fact,
            "Kurioje mieste gimė Gitanas Nausėda?",
            packet,
            out var placeReason),
            placeReason.ToString());
        Assert.True(GroundedQuestionValidator.TryValidateHard(
            fact,
            "Kokiais metais Gitanas Nausėda gimė?",
            packet,
            out var yearReason),
            yearReason.ToString());
    }

    [Fact]
    public void InflectedLithuanianNamesRemainValidAfterAnswerIsLocked()
    {
        const string evidence = "Prieš tai dirbo Lietuvos banko valdybos pirmininko pavaduotoju.";
        var packet =
            "OWNED S001: Gitanas Nausėda yra Lietuvos ekonomistas ir politikas.\n" +
            "OWNED S004: " + evidence;
        var fact = new StudyFact(
            "F005",
            "S004",
            evidence,
            evidence.TrimEnd('.'),
            StudyFactImportance.Supporting,
            3,
            QuestionContext: packet);

        Assert.True(GroundedQuestionValidator.TryValidateHard(
            fact with { Answer = "Lietuvos banko valdybos pirmininko pavaduotoju" },
            "Kuo dirbo Gitanas Nausėda prieš tai?",
            packet,
            out var ok),
            ok.ToString());
        Assert.True(GroundedQuestionValidator.TryValidateHard(
            fact with { Answer = "Lietuvos ekonomistas ir politikas" },
            "Koks yra Gitanas Nausėdos darbas?",
            "OWNED S001: Gitanas Nausėda yra Lietuvos ekonomistas ir politikas.",
            out var job),
            job.ToString());
    }

    [Fact]
    public void Validator_DoesNotInferSemanticAnswerSlotsFromTokenOrder()
    {
        const string evidence =
            "The International Water Council appointed Maris to its standards committee in 2019.";
        var packet = "OWNED S022: " + evidence;
        var council = "The International Water Council";
        var committee = "its standards committee";
        var councilFact = new StudyFact(
            "F023",
            "S022",
            evidence,
            council,
            StudyFactImportance.Supporting,
            21,
            QuestionContext: packet,
            AnswerStart: evidence.IndexOf(council, StringComparison.Ordinal),
            AnswerLength: council.Length);

        Assert.True(GroundedQuestionValidator.TryValidateHard(
            councilFact,
            "What committee did Maris join in 2019?",
            packet,
            out _));
        Assert.True(GroundedQuestionValidator.TryValidateHard(
            councilFact,
            "Which organization appointed Maris to its standards committee in 2019?",
            packet,
            out var orgOk),
            orgOk.ToString());

        var committeeStart = evidence.IndexOf(committee, StringComparison.Ordinal);
        var committeeFact = councilFact with
        {
            Answer = committee,
            AnswerStart = committeeStart,
            AnswerLength = committee.Length
        };
        Assert.True(GroundedQuestionValidator.TryValidateHard(
            committeeFact,
            "What committee did Maris join in 2019?",
            packet,
            out var committeeOk),
            committeeOk.ToString());

        const string birth = "Elena Maris was born in 1978 in the coastal city of Bellhaven.";
        var birthPacket = "OWNED S001: " + birth;
        var year = "1978";
        var birthFact = new StudyFact(
            "F001",
            "S001",
            birth,
            year,
            StudyFactImportance.Supporting,
            0,
            QuestionContext: birthPacket,
            AnswerStart: birth.IndexOf(year, StringComparison.Ordinal),
            AnswerLength: year.Length);
        Assert.True(GroundedQuestionValidator.TryValidateHard(
            birthFact,
            "When was Elena Maris born?",
            birthPacket,
            out var bornOk),
            bornOk.ToString());

        const string placeEvidence = "Jis gimė 1964 m. gegužės 19 d. Klaipėdoje.";
        var placePacket =
            "OWNED S001: Gitanas Nausėda yra Lietuvos ekonomistas ir politikas.\nOWNED S002: " + placeEvidence;
        var place = "Klaipėdoje";
        var placeFact = new StudyFact(
            "F002",
            "S002",
            placeEvidence,
            place,
            StudyFactImportance.Supporting,
            1,
            QuestionContext: placePacket,
            AnswerStart: placeEvidence.IndexOf(place, StringComparison.Ordinal),
            AnswerLength: place.Length);
        Assert.True(GroundedQuestionValidator.TryValidateHard(
            placeFact,
            "Kur gimė Gitanas Nausėda?",
            placePacket,
            out var placeOk),
            placeOk.ToString());

        const string birthCity = "Elena Maris was born in 1978 in the coastal city of Bellhaven.";
        var birthCityPacket = "OWNED S001: " + birthCity;
        var yearAnswer = "1978";
        var yearFact = new StudyFact(
            "F001",
            "S001",
            birthCity,
            yearAnswer,
            StudyFactImportance.Supporting,
            0,
            QuestionContext: birthCityPacket,
            AnswerStart: birthCity.IndexOf(yearAnswer, StringComparison.Ordinal),
            AnswerLength: yearAnswer.Length);
        Assert.True(GroundedQuestionValidator.TryValidateHard(
            yearFact,
            "What city was Elena Maris born in?",
            birthCityPacket,
            out _));
        Assert.True(GroundedQuestionValidator.TryValidateHard(
            yearFact,
            "When was Elena Maris born?",
            birthCityPacket,
            out var whenOk),
            whenOk.ToString());
        var city = "Bellhaven";
        var cityFact = yearFact with
        {
            Answer = city,
            AnswerStart = birthCity.IndexOf(city, StringComparison.Ordinal),
            AnswerLength = city.Length
        };
        Assert.True(GroundedQuestionValidator.TryValidateHard(
            cityFact,
            "What city was Elena Maris born in?",
            birthCityPacket,
            out var cityOk),
            cityOk.ToString());
    }

    [Fact]
    public async Task Coordinator_DropsPairWhenCopiedSpanDoesNotAnswerQuestion()
    {
        const string source =
            "The International Water Council appointed Maris to its standards committee in 2019.";
        using var backend = new FakeTextGenerationBackend { TokenCountOverride = 29 };
        backend.EnqueueResponses("""
            fact: F001
            source: S001
            q: What committee did Maris join in 2019?
            """, """
            fact: F001
            span: The International Water Council
            """, """
            fact: F001
            language: SAME
            decision: DROP
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        var fact = Assert.Single(context.SelectedFacts);
        Assert.True(fact.IsCoverageFallback);
        Assert.Empty(fact.CandidateQuestion);
        Assert.Equal(3, backend.GenerateCallCount);
    }

    [Fact]
    public async Task Coordinator_RejectsWholeSourceSentenceAsAnAnswer()
    {
        const string source =
            "In 2018, Mira announced her candidacy for president during the national election campaign.";
        using var backend = new FakeTextGenerationBackend { TokenCountOverride = 29 };
        backend.EnqueueResponses("""
            fact: F001
            source: S001
            q: When did Mira announce her candidacy for president?
            """, """
            fact: F001
            span: In 2018, Mira announced her candidacy for president during the national election campaign
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        var fact = Assert.Single(context.SelectedFacts);
        Assert.True(fact.IsCoverageFallback);
        Assert.Empty(fact.CandidateQuestion);
        Assert.Equal(2, backend.GenerateCallCount);
    }

    [Fact]
    public async Task Coordinator_DroppedSemanticPairUsesClozeCoverage()
    {
        const string source = "Gitanas Nausėda eina Lietuvos Respublikos Prezidento pareigas.";
        using var backend = new FakeTextGenerationBackend { TokenCountOverride = 29 };
        backend.EnqueueResponses("""
            fact: F001
            source: S001
            q: Kuo užsiima Gitanas Nausėda?
            """, """
            fact: F001
            span: Lietuvos Respublikos Prezidento pareigas
            """, """
            fact: F001
            language: SAME
            decision: DROP
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        var fact = Assert.Single(context.SelectedFacts);
        Assert.True(fact.IsCoverageFallback);
        Assert.Equal("Prezidento pareigas", fact.Answer);
        Assert.Empty(fact.CandidateQuestion);
        Assert.Equal(3, backend.GenerateCallCount);
    }

    [Fact]
    public async Task Coordinator_SemanticConfirmationKeepsValidDraft()
    {
        const string source = "Elena Maris was born in 1978 in the coastal city of Bellhaven.";
        using var backend = new FakeTextGenerationBackend { TokenCountOverride = 29 };
        backend.EnqueueResponses("""
            fact: F001
            source: S001
            q: What year was Elena Maris born?
            span: 1978
            """, """
            fact: F001
            span: 1978
            """, """
            fact: F001
            language: SAME
            decision: KEEP
            """, """
            fact: F001
            kind: DATE
            items: NONE
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        var fact = Assert.Single(context.SelectedFacts);
        Assert.Equal("What year was Elena Maris born?", fact.CandidateQuestion);
        Assert.Equal("1978", fact.Answer);
        Assert.Equal(StudyAnswerKind.Date, fact.AnswerKind);
        Assert.Equal(StudyValueShape.Year, fact.ValueShape);
        Assert.False(fact.IsCoverageFallback);
        Assert.Equal(4, backend.GenerateCallCount);
        Assert.Equal(SamplingProfiles.Grounded.RepetitionPenalty, backend.Requests[1].RepetitionPenalty);
        Assert.Contains("language and decision records", backend.Requests[2].Prompt, StringComparison.Ordinal);
        Assert.Contains("QUESTION: What year was Elena Maris born?", backend.Requests[2].Prompt, StringComparison.Ordinal);
        Assert.Contains("Classify each ANSWER", backend.Requests[3].Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coordinator_SemanticConfirmationPreservesExactListItems()
    {
        const string source =
            "The light-dependent reactions produce ATP and NADPH during the energy conversion process.";
        using var backend = new FakeTextGenerationBackend { TokenCountOverride = 18 };
        backend.EnqueueResponses("""
            fact: F001
            source: S001
            q: Which molecules do the light-dependent reactions produce?
            span: ATP and NADPH
            """, """
            fact: F001
            span: ATP and NADPH
            """, """
            fact: F001
            language: SAME
            decision: KEEP
            """, """
            fact: F001
            kind: TERM
            items: ATP || NADPH
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        var fact = Assert.Single(context.SelectedFacts);
        Assert.Equal(StudyAnswerKind.Term, fact.AnswerKind);
        Assert.True(fact.IsExplicitList);
        Assert.Equal(["ATP", "NADPH"], fact.AnswerItems);
    }

    [Fact]
    public async Task Coordinator_RequiresIndependentAnswerToMatchDeclaredSpan()
    {
        const string source = "Elena Maris was born in 1978 in the coastal city of Bellhaven.";
        using var backend = new FakeTextGenerationBackend { TokenCountOverride = 29 };
        backend.EnqueueResponses("""
            fact: F001
            source: S001
            q: What year was Elena Maris born?
            span: Bellhaven
            """, """
            fact: F001
            span: 1978
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        var fact = Assert.Single(context.SelectedFacts);
        Assert.True(fact.IsCoverageFallback);
        Assert.Empty(fact.CandidateQuestion);
        Assert.Equal(2, backend.GenerateCallCount);
    }

    [Fact]
    public async Task Coordinator_WholeSentenceHintDefersToIndependentAnswerGrounding()
    {
        const string source = "Elena Maris was born in 1978 in the coastal city of Bellhaven.";
        using var backend = new FakeTextGenerationBackend { TokenCountOverride = 29 };
        backend.EnqueueResponses("""
            fact: F001
            source: S001
            q: What year was Elena Maris born?
            span: Elena Maris was born in 1978 in the coastal city of Bellhaven.
            """, """
            fact: F001
            span: 1978
            """, """
            fact: F001
            language: SAME
            decision: KEEP
            """, """
            fact: F001
            kind: DATE
            items: NONE
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        var fact = Assert.Single(context.SelectedFacts);
        Assert.False(fact.IsCoverageFallback);
        Assert.Equal("What year was Elena Maris born?", fact.CandidateQuestion);
        Assert.Equal("1978", fact.Answer);
        Assert.Equal(StudyAnswerKind.Date, fact.AnswerKind);
        Assert.Equal(4, backend.GenerateCallCount);
    }

    [Fact]
    public async Task Coordinator_AcceptsDeclaredSpanInsideBroaderIndependentAnswer()
    {
        const string source = "Jis gimė 1964 m. gegužės 19 d. Klaipėdoje, Lietuvos pajūrio mieste.";
        using var backend = new FakeTextGenerationBackend { TokenCountOverride = 29 };
        backend.EnqueueResponses("""
            fact: F001
            source: S001
            q: Kur jis gimė?
            span: Klaipėdoje
            """, """
            fact: F001
            span: 1964 m. gegužės 19 d. Klaipėdoje
            """, """
            fact: F001
            language: SAME
            decision: KEEP
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        var fact = Assert.Single(context.SelectedFacts);
        Assert.False(fact.IsCoverageFallback);
        Assert.Equal("Klaipėdoje", fact.Answer);
        Assert.Equal(4, backend.GenerateCallCount);
    }

    [Fact]
    public async Task Coordinator_UsesOneWordTrailingCompletionFromIndependentAnswer()
    {
        const string source = "Gitanas Nausėda eina Lietuvos Respublikos Prezidento pareigas.";
        using var backend = new FakeTextGenerationBackend { TokenCountOverride = 29 };
        backend.EnqueueResponses("""
            fact: F001
            source: S001
            q: Kokias pareigas eina Gitanas Nausėda?
            span: Lietuvos Respublikos Prezidento
            """, """
            fact: F001
            span: Lietuvos Respublikos Prezidento pareigas
            """, """
            fact: F001
            language: SAME
            decision: KEEP
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        var fact = Assert.Single(context.SelectedFacts);
        Assert.False(fact.IsCoverageFallback);
        Assert.Equal("Lietuvos Respublikos Prezidento pareigas", fact.Answer);
        Assert.Equal(4, backend.GenerateCallCount);
    }

    [Fact]
    public async Task Coordinator_RejectsQuestionWhenLanguageDecisionDiffers()
    {
        const string source = "Elena Maris was born in 1978 in the coastal city of Bellhaven.";
        using var backend = new FakeTextGenerationBackend { TokenCountOverride = 29 };
        backend.EnqueueResponses("""
            fact: F001
            source: S001
            q: What year was Elena Maris born?
            span: 1978
            """, """
            fact: F001
            span: 1978
            """, """
            fact: F001
            language: DIFFERENT
            decision: KEEP
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        var fact = Assert.Single(context.SelectedFacts);
        Assert.True(fact.IsCoverageFallback);
        Assert.Empty(fact.CandidateQuestion);
        Assert.Equal(3, backend.GenerateCallCount);
    }

    [Fact]
    public async Task Coordinator_UnconfirmedQuestionsFallBackPerUncoveredUnit()
    {
        const string source = """
            Elena Maris was born in 1978 in the coastal city of Bellhaven.
            She completed her undergraduate degree in 2000.
            """;
        using var backend = new FakeTextGenerationBackend { TokenCountOverride = 29 };
        backend.EnqueueResponses("""
            fact: F001
            source: S001
            q: Elena Maris was born in 1978 in the coastal city of Bellhaven?

            fact: F002
            source: S002
            q: In what year did she complete her undergraduate degree?
            """, """
            fact: F001
            span: 1978

            fact: F002
            span: 2000
            """, """
            fact: F001
            language: SAME
            decision: DROP

            fact: F002
            language: SAME
            decision: DROP
            """);
        var context = await new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(source, "Qwen3.5-4B", StudyCoverageMode.Auto);

        Assert.Equal(2, context.SelectedFacts.Count);
        Assert.All(context.SelectedFacts, item => Assert.True(item.IsCoverageFallback));
        Assert.Equal(2, context.SelectedFacts.Select(item => item.SourceUnitId).Distinct().Count());
        Assert.Equal(3, backend.GenerateCallCount);
    }

    [Theory]
    [InlineData("In 2003, she published a paper", false)]
    [InlineData("Her mother taught mathematics, while her father repaired instruments", false)]
    [InlineData("red, blue, green", true)]
    [InlineData("alpha; beta", true)]
    [InlineData("红色、蓝色", true)]
    [InlineData("alpha；beta", true)]
    [InlineData("raudona，mėlyna，žalia", true)]
    public void ExplicitListDetection_RequiresUnambiguousPunctuation(string answer, bool expected)
        => Assert.Equal(expected, StudyAnswerList.IsExplicitList(answer));

    private static void EnqueueGroundedPairs(
        FakeTextGenerationBackend backend,
        string questions,
        string answers)
    {
        var decisions = string.Join(
            "\n\n",
            Regex.Matches(questions, @"(?im)^fact:\s*(F\d+)\s*$")
                .Select(match => $"fact: {match.Groups[1].Value}\nlanguage: SAME\ndecision: KEEP"));
        var metadata = string.Join(
            "\n\n",
            Regex.Matches(questions, @"(?im)^fact:\s*(F\d+)\s*$")
                .Select(match => $"fact: {match.Groups[1].Value}\nkind: TEXT\nitems: NONE"));
        backend.EnqueueResponses(questions, answers, decisions, metadata);
    }

    private static FakeTextGenerationBackend CreateTwoFactBackend()
    {
        var backend = new FakeTextGenerationBackend { TokenCountOverride = 57 };
        EnqueueGroundedPairs(backend, """
            fact: F001
            source: S001
            q: Where do plants use chlorophyll to absorb sunlight?

            fact: F002
            source: S002
            q: What do the plants convert captured sunlight into?
            """, """
            fact: F001
            span: green leaves

            fact: F002
            span: chemical energy
            """);
        return backend;
    }

    private static Task<StudyGenerationContext> CreateUncachedContextAsync(
        FakeTextGenerationBackend backend,
        StudyCoverageMode coverage)
        => new StudyPlanCoordinator(backend, new StudyPlanCache())
            .GetOrCreateAsync(Source, "Qwen3.5-4B", coverage);
}
