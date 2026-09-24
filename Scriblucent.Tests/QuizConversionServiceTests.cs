using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.Services.AI;

namespace Scriblucent.Tests;

public sealed class QuizConversionServiceTests
{
    [Fact]
    public async Task ConvertToQuizAsync_UsesValidatedModelDistractorsWhenSourceHasNoAlternatives()
    {
        const string evidence = "Šilo observatorija buvo įkurta 1922 metais.";
        var fact = new StudyFact(
            "F001", "S001", evidence, "1922 metais", StudyFactImportance.Core, 0,
            StudyAnswerKind.Date, evidence, 0,
            evidence.IndexOf("1922 metais", StringComparison.Ordinal), "1922 metais".Length,
            IsCoverageFallback: true);
        var plan = new StudyPlan(
            "LT-DATE", AiModelManifest.RecommendedModelKey,
            StudyPlanCoordinator.CurrentPlannerVersion, DateTimeOffset.UtcNow, [fact]);
        using var backend = new FakeTextGenerationBackend();
        backend.EnqueueResponse("F001 | 1968 metais | 2005 metais");

        var quiz = await new QuizConversionService(backend).ConvertToQuizAsync(
            "Observatorija", StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Balanced));

        var question = Assert.Single(quiz!.Questions);
        Assert.Equal(QuizQuestionType.SingleChoice, question.Type);
        Assert.Equal("1922 metais", Assert.Single(question.Options, option => option.IsCorrect).Text);
        Assert.Equal(["1968 metais", "2005 metais"], question.Options
            .Where(option => !option.IsCorrect).Select(option => option.Text).ToArray());
        Assert.Equal(1, backend.GenerateCallCount);
    }

    [Fact]
    public async Task ConvertToQuizAsync_RejectsModelChoicesAlreadySupportedBySource()
    {
        const string evidence = "Šilo observatorija buvo įkurta 1922 metais.";
        var fact = new StudyFact(
            "F001", "S001", evidence, "1922 metais", StudyFactImportance.Core, 0,
            StudyAnswerKind.Date, evidence, 0,
            evidence.IndexOf("1922 metais", StringComparison.Ordinal), "1922 metais".Length,
            IsCoverageFallback: true);
        var plan = new StudyPlan(
            "LT-DATE", AiModelManifest.RecommendedModelKey,
            StudyPlanCoordinator.CurrentPlannerVersion, DateTimeOffset.UtcNow, [fact]);
        using var backend = new FakeTextGenerationBackend();
        backend.EnqueueResponse("F001 | 1922 metais | 2005 metais");

        var quiz = await new QuizConversionService(backend).ConvertToQuizAsync(
            "Observatorija", StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Balanced));

        Assert.Equal(QuizQuestionType.PlainText, Assert.Single(quiz!.Questions).Type);
    }

    [Fact]
    public async Task ConvertToQuizAsync_ClozeQuestionsUseSourceTermDistractorsWhenAvailable()
    {
        const string First = "Chlorofilas sugeria mėlyną šviesą.";
        const string Second = "Karotinas sugeria oranžinę šviesą.";
        const string Third = "Ksantofilas sugeria geltoną šviesą.";
        var facts = new[]
        {
            CreateTermFact("F001", "S001", First, "Chlorofilas", 0),
            CreateTermFact("F002", "S002", Second, "Karotinas", 1),
            CreateTermFact("F003", "S003", Third, "Ksantofilas", 2)
        };
        var plan = new StudyPlan(
            "LT-TERMS", AiModelManifest.RecommendedModelKey,
            StudyPlanCoordinator.CurrentPlannerVersion, DateTimeOffset.UtcNow, facts);
        var context = StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Balanced);
        using var backend = new FakeTextGenerationBackend();

        var quiz = await new QuizConversionService(backend).ConvertToQuizAsync("Pigmentai", context);

        var question = Assert.Single(quiz!.Questions, item => item.Question.Contains("mėlyną", StringComparison.Ordinal));
        Assert.Equal(QuizQuestionType.SingleChoice, question.Type);
        Assert.Equal("Chlorofilas", Assert.Single(question.Options, option => option.IsCorrect).Text);
        Assert.Equal(["Karotinas", "Ksantofilas"], question.Options
            .Where(option => !option.IsCorrect).Select(option => option.Text)
            .OrderBy(text => text).ToArray());
        Assert.Equal(0, backend.GenerateCallCount);
    }

    private static StudyFact CreateTermFact(string id, string sourceId, string evidence, string answer, int order)
        => new(id, sourceId, evidence, answer, StudyFactImportance.Core, order,
            StudyAnswerKind.Term, evidence, 0,
            evidence.IndexOf(answer, StringComparison.Ordinal), answer.Length,
            IsCoverageFallback: true);

    [Fact]
    public async Task ConvertToQuizAsync_FullCalendarDateUsesPlainTextInsteadOfInventedDistractors()
    {
        const string Evidence = "Mira Sol was born on June 14, 1946 in Bellhaven.";
        const string Answer = "June 14, 1946";
        var fact = new StudyFact(
            "F001",
            "S001",
            Evidence,
            Answer,
            StudyFactImportance.Core,
            0,
            StudyAnswerKind.Date,
            Evidence,
            0,
            Evidence.IndexOf(Answer, StringComparison.Ordinal),
            Answer.Length,
            ValueShape: StudyValueShape.CalendarDate);
        var plan = new StudyPlan(
            "CALENDAR",
            "Qwen3.5-4B",
            StudyPlanCoordinator.CurrentPlannerVersion,
            DateTimeOffset.UtcNow,
            [fact]);
        var context = StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Thorough);
        using var backend = new FakeTextGenerationBackend();
        backend.EnqueueResponse("fact: F001\nq: When was Mira Sol born?\nsupport: E01\n");

        var quiz = await new QuizConversionService(backend).ConvertToQuizAsync(
            "Mira Sol",
            context);

        var question = Assert.Single(quiz!.Questions);
        Assert.Equal(QuizQuestionType.PlainText, question.Type);
        Assert.Equal(Answer, question.PlainTextAnswer);
        Assert.Empty(question.Options);
    }

    [Fact]
    public async Task ConvertToQuizAsync_UsesOnlyOtherSourceYearsAsDateDistractors()
    {
        const string BirthEvidence = "Mira was born in 1978.";
        const string FoundedEvidence = "Mira founded Luma in 2009.";
        const string OpenedEvidence = "Mira opened the laboratory in 2019.";
        var facts = new[]
        {
            new StudyFact(
                "F001", "S001", BirthEvidence, "1978", StudyFactImportance.Core, 0,
                StudyAnswerKind.Date, BirthEvidence, 0, BirthEvidence.IndexOf("1978", StringComparison.Ordinal),
                4, ValueShape: StudyValueShape.Year, CandidateQuestion: "In which year was Mira born?"),
            new StudyFact(
                "F002", "S002", FoundedEvidence, "2009", StudyFactImportance.Supporting, 1,
                StudyAnswerKind.Date, FoundedEvidence, BirthEvidence.Length + 1,
                FoundedEvidence.IndexOf("2009", StringComparison.Ordinal), 4, ValueShape: StudyValueShape.Year,
                IsCoverageFallback: true),
            new StudyFact(
                "F003", "S003", OpenedEvidence, "2019", StudyFactImportance.Supporting, 2,
                StudyAnswerKind.Date, OpenedEvidence, BirthEvidence.Length + FoundedEvidence.Length + 2,
                OpenedEvidence.IndexOf("2019", StringComparison.Ordinal), 4, ValueShape: StudyValueShape.Year,
                IsCoverageFallback: true)
        };
        var plan = new StudyPlan(
            "YEARS", "Qwen3.5-4B", StudyPlanCoordinator.CurrentPlannerVersion,
            DateTimeOffset.UtcNow, facts, SourceUnitCount: 3);
        var context = StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Thorough);
        using var backend = new FakeTextGenerationBackend();

        var quiz = await new QuizConversionService(backend).ConvertToQuizAsync(
            "Mira", context);

        var question = Assert.Single(
            quiz!.Questions,
            item => item.Question == "In which year was Mira born?");
        Assert.Equal(QuizQuestionType.SingleChoice, question.Type);
        Assert.Contains(question.Options, option => option.IsCorrect && option.Text == "1978");
        Assert.Equal(["2009", "2019"], question.Options
            .Where(option => !option.IsCorrect)
            .Select(option => option.Text)
            .OrderBy(text => text)
            .ToArray());
        Assert.DoesNotContain(question.Options, option => option.Text is "1977" or "1979");
        Assert.Equal(0, backend.GenerateCallCount);
    }

    [Fact]
    public async Task ConvertToQuizAsync_AtomicNumbersNeverBecomeUnsafeMultipleChoice()
    {
        const string Evidence = "The procedure used 14 sample points before calibration.";
        var fact = new StudyFact(
            "F001",
            "S001",
            Evidence,
            "14",
            StudyFactImportance.Supporting,
            0,
            StudyAnswerKind.Quantity,
            Evidence,
            0,
            Evidence.IndexOf("14", StringComparison.Ordinal),
            2,
            ValueShape: StudyValueShape.AtomicNumber,
            IsComplete: false);
        var plan = new StudyPlan(
            "ATOMIC",
            "Qwen3.5-4B",
            StudyPlanCoordinator.CurrentPlannerVersion,
            DateTimeOffset.UtcNow,
            [fact]);
        var context = StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Thorough);
        using var backend = new FakeTextGenerationBackend();
        backend.EnqueueResponse("fact: F001\nq: How many sample points did the procedure use?\nsupport: E01\n");

        var quiz = await new QuizConversionService(backend).ConvertToQuizAsync(
            "Procedure",
            context);

        var question = Assert.Single(quiz!.Questions);
        Assert.Equal(QuizQuestionType.PlainText, question.Type);
        Assert.Equal("14", question.PlainTextAnswer);
    }

    [Fact]
    public async Task ConvertToQuizAsync_UsesTheFullSourceInventoryForSafeListDistractors()
    {
        const string CouncilEvidence = "The International Water Council maintains engineering standards.";
        const string PartnersEvidence =
            "Maris worked with Northbridge University, Harbor Research Institute, and Clear Current.";
        const string PartnerAnswer =
            "Northbridge University, Harbor Research Institute, and Clear Current";
        var facts = new[]
        {
            new StudyFact(
                "F001",
                "S001",
                CouncilEvidence,
                "The International Water Council",
                StudyFactImportance.Core,
                0,
                StudyAnswerKind.Organization,
                CouncilEvidence,
                0,
                0,
                "The International Water Council".Length),
            new StudyFact(
                "F002",
                "S002",
                PartnersEvidence,
                PartnerAnswer,
                StudyFactImportance.Core,
                1,
                StudyAnswerKind.Organization,
                PartnersEvidence,
                CouncilEvidence.Length + 1,
                PartnersEvidence.IndexOf(PartnerAnswer, StringComparison.Ordinal),
                PartnerAnswer.Length,
                IsExplicitList: true,
                AnswerItems: ["Northbridge University", "Harbor Research Institute", "Clear Current"],
                CandidateQuestion: "Which organizations did Maris work with?")
        };
        var plan = new StudyPlan(
            "LIST",
            "Qwen3.5-4B",
            StudyPlanCoordinator.CurrentPlannerVersion,
            DateTimeOffset.UtcNow,
            facts,
            SourceUnitCount: 2);
        var context = StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Thorough);
        using var backend = new FakeTextGenerationBackend();

        var quiz = await new QuizConversionService(backend).ConvertToQuizAsync(
            "Organizations",
            context);

        var multipleChoice = Assert.Single(
            quiz!.Questions,
            question => question.Type == QuizQuestionType.MultipleChoice);
        Assert.Equal(
            ["Clear Current", "Harbor Research Institute", "Northbridge University"],
            multipleChoice.Options
            .Where(option => option.IsCorrect)
            .Select(option => option.Text)
            .OrderBy(text => text)
            .ToArray());
        Assert.Contains(
            multipleChoice.Options,
            option => !option.IsCorrect && option.Text == "The International Water Council");
        Assert.Equal(1, backend.GenerateCallCount);
    }

    [Fact]
    public async Task ConvertToQuizAsync_UsesCompactTechnicalTermListsForMultiSelect()
    {
        const string ReactionEvidence = "The light-dependent reactions produce ATP and NADPH.";
        const string ProcessEvidence = "The light-dependent reactions consume ADP before producing ATP and NADPH.";
        const string PigmentEvidence = "Chlorophyll absorbs light in the blue and red spectrum.";
        var facts = new[]
        {
            new StudyFact(
                "F001", "S001", ReactionEvidence, "ATP and NADPH", StudyFactImportance.Core, 0,
                StudyAnswerKind.Term, ReactionEvidence, 0,
                ReactionEvidence.IndexOf("ATP and NADPH", StringComparison.Ordinal), "ATP and NADPH".Length,
                IsExplicitList: true, AnswerItems: ["ATP", "NADPH"],
                CandidateQuestion: "What do the light-dependent reactions produce?"),
            new StudyFact(
                "F002", "S002", ProcessEvidence, "ADP", StudyFactImportance.Supporting, 1,
                StudyAnswerKind.Term, ProcessEvidence, ReactionEvidence.Length + 1,
                ProcessEvidence.IndexOf("ADP", StringComparison.Ordinal), "ADP".Length,
                IsCoverageFallback: true),
            new StudyFact(
                "F003", "S003", PigmentEvidence, "Chlorophyll", StudyFactImportance.Supporting, 2,
                StudyAnswerKind.Term, PigmentEvidence, ReactionEvidence.Length + ProcessEvidence.Length + 2,
                0, "Chlorophyll".Length, IsCoverageFallback: true)
        };
        var plan = new StudyPlan(
            "TERMS", "Qwen3.5-4B", StudyPlanCoordinator.CurrentPlannerVersion,
            DateTimeOffset.UtcNow, facts, SourceUnitCount: 3);
        var context = StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Thorough);
        using var backend = new FakeTextGenerationBackend();

        var quiz = await new QuizConversionService(backend).ConvertToQuizAsync(
            "Photosynthesis",
            context);

        var question = Assert.Single(
            quiz!.Questions,
            item => item.Question == "What do the light-dependent reactions produce?");
        Assert.Equal(QuizQuestionType.MultipleChoice, question.Type);
        Assert.Equal(["ATP", "NADPH"], question.Options
            .Where(option => option.IsCorrect)
            .Select(option => option.Text)
            .OrderBy(text => text)
            .ToArray());
        Assert.Contains(question.Options, option => !option.IsCorrect && option.Text == "ADP");
    }

    [Fact]
    public async Task ConvertToQuizAsync_ActionListWithLeadingConnectorStaysPlainText()
    {
        const string Evidence =
            "Maris continues to teach, advise Clear Current, and study affordable monitoring equipment.";
        const string Answer =
            "teach, advise Clear Current, and study affordable monitoring equipment";
        var target = new StudyFact(
            "F001", "S001", Evidence, Answer, StudyFactImportance.Core, 0,
            StudyAnswerKind.Term, Evidence, 0, Evidence.IndexOf(Answer, StringComparison.Ordinal), Answer.Length,
            IsExplicitList: true,
            AnswerItems: ["teach", "advise Clear Current", "and study affordable monitoring equipment"],
            CandidateQuestion: "What does Maris continue to do?");
        var distractorEvidence = "Maris taught mathematics at Northbridge University.";
        var distractor = new StudyFact(
            "F002", "S002", distractorEvidence, "mathematics", StudyFactImportance.Supporting, 1,
            StudyAnswerKind.Term, distractorEvidence, Evidence.Length + 1,
            distractorEvidence.IndexOf("mathematics", StringComparison.Ordinal), "mathematics".Length,
            IsCoverageFallback: true);
        var plan = new StudyPlan(
            "ACTIONS", "Qwen3.5-4B", StudyPlanCoordinator.CurrentPlannerVersion,
            DateTimeOffset.UtcNow, [target, distractor], SourceUnitCount: 2);
        var context = StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Thorough);
        using var backend = new FakeTextGenerationBackend();

        var quiz = await new QuizConversionService(backend).ConvertToQuizAsync(
            "Maris", context);

        var question = Assert.Single(quiz!.Questions, item => item.Question == "What does Maris continue to do?");
        Assert.Equal(QuizQuestionType.PlainText, question.Type);
        Assert.Equal(Answer, question.PlainTextAnswer);
    }

    [Fact]
    public async Task ConvertToQuizAsync_MismatchedTermShapeDoesNotBecomeAWeakMultiSelect()
    {
        const string Evidence = "He withdrew the U.S. from agreements on climate and trade.";
        const string Answer = "climate and trade";
        var target = new StudyFact(
            "F001", "S001", Evidence, Answer, StudyFactImportance.Core, 0,
            StudyAnswerKind.Term, Evidence, 0, Evidence.IndexOf(Answer, StringComparison.Ordinal), Answer.Length,
            IsExplicitList: true, AnswerItems: ["climate", "trade"],
            CandidateQuestion: "What two types of agreements did he withdraw the U.S. from?");
        const string DistractorEvidence = "He ran as a political outsider in the election.";
        var distractor = new StudyFact(
            "F002", "S002", DistractorEvidence, "political outsider", StudyFactImportance.Supporting, 1,
            StudyAnswerKind.Term, DistractorEvidence, Evidence.Length + 1,
            DistractorEvidence.IndexOf("political outsider", StringComparison.Ordinal),
            "political outsider".Length, IsCoverageFallback: true);
        var plan = new StudyPlan(
            "AGREEMENTS", "Qwen3.5-9B", StudyPlanCoordinator.CurrentPlannerVersion,
            DateTimeOffset.UtcNow, [target, distractor], SourceUnitCount: 2);
        var context = StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Thorough);
        using var backend = new FakeTextGenerationBackend();

        var quiz = await new QuizConversionService(backend).ConvertToQuizAsync(
            "Agreements", context);

        var question = Assert.Single(
            quiz!.Questions,
            item => item.Question == "What two types of agreements did he withdraw the U.S. from?");
        Assert.Equal(QuizQuestionType.PlainText, question.Type);
        Assert.Equal(Answer, question.PlainTextAnswer);
    }

}
