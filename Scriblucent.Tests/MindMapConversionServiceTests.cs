using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.Services.AI;

namespace Scriblucent.Tests;

public sealed class MindMapConversionServiceTests
{
    private const string First = "Šilo observatorija buvo įkurta 1922 metais universiteto studentams mokyti.";
    private const string Second = "Pirmasis teleskopas turėjo 28 centimetrų skersmens veidrodį.";

    [Fact]
    public async Task ContextualModel_GroupsRelatedFactsUnderSharedThemes()
    {
        using var backend = new FakeTextGenerationBackend();
        backend.EnqueueResponse("Istorija | M001, M003\nĮranga | M002, M004\nVeikla | M005, M006");

        var map = await new MindMapConversionService(backend).ConvertToMindMapAsync(
            "Observatorija", CreateGroupedContext(AiModelManifest.RecommendedModelKey));

        Assert.NotNull(map);
        Assert.Equal(1, backend.GenerateCallCount);
        Assert.Equal(AiModelManifest.RecommendedModelKey, backend.Requests[0].ModelKey);
        Assert.Equal(3, map.Children.Count);
        Assert.All(map.Children, category => Assert.True(category.Children.Count >= 2));
        Assert.Contains(map.Children, category => category.Text == "Istorija"
            && category.Children.Any(point => point.Text.Contains("1922", StringComparison.Ordinal))
            && category.Children.Any(point => point.Text.Contains("1968", StringComparison.Ordinal)));
        Assert.Contains(map.Children, category => category.Text == "Įranga"
            && category.Children.Any(point => point.Text.Contains("28", StringComparison.Ordinal))
            && category.Children.Any(point => point.Text.Contains("1997", StringComparison.Ordinal)));
        Assert.Contains(map.Children, category => category.Text == "Veikla"
            && category.Children.Any(point => point.Text.Contains("2005", StringComparison.Ordinal))
            && category.Children.Any(point => point.Text.Contains("ekskursijas", StringComparison.Ordinal)));
        Assert.DoesNotContain(Flatten(map), node => node.Text == First || node.Text == Second);
        Assert.DoesNotContain(Flatten(map), node => node.Text.Contains(" — ", StringComparison.Ordinal)
            || node.Text.EndsWith("...", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnrelatedEnglishLabels_FallBackToShortLithuanianSourcePhrases()
    {
        using var backend = new FakeTextGenerationBackend();
        backend.EnqueueResponse("History | M001, M003\nEquipment | M002, M004\nActivities | M005, M006");

        var map = await new MindMapConversionService(backend).ConvertToMindMapAsync(
            "Observatorija", CreateGroupedContext(AiModelManifest.RecommendedModelKey));

        Assert.NotNull(map);
        Assert.True(map.Children.Count < 6);
        Assert.DoesNotContain(Flatten(map), node => node.Text is "History" or "Equipment" or "Activities");
        Assert.Contains(Flatten(map), node => node.Text.Contains("1922", StringComparison.Ordinal));
        Assert.Contains(Flatten(map), node => node.Text.Contains("28", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OneCategoryPerFact_IsRejectedInsteadOfRecreatingSentenceBranches()
    {
        using var backend = new FakeTextGenerationBackend();
        backend.EnqueueResponse("Įkūrimas | M001\nTeleskopas | M002\nTyrimai | M003\n" +
                                "Fotometras | M004\nAtidarymas | M005\nEkskursijos | M006");

        var map = await new MindMapConversionService(backend).ConvertToMindMapAsync(
            "Observatorija", CreateGroupedContext(AiModelManifest.RecommendedModelKey));

        Assert.NotNull(map);
        Assert.True(map.Children.Count < 6);
        Assert.Contains(map.Children, category => category.Children.Count >= 2);
        Assert.DoesNotContain(map.Children, category => category.Text == "Įkūrimas");
    }

    [Fact]
    public async Task UnusableGrouping_GetsOneBoundedRegroupingAttempt()
    {
        using var backend = new FakeTextGenerationBackend();
        backend.EnqueueResponse("Įkūrimas | M001\nTeleskopas | M002\nTyrimai | M003\n" +
                                "Fotometras | M004\nAtidarymas | M005\nEkskursijos | M006");
        backend.EnqueueResponse("Istorija | M001, M003\nĮranga | M002, M004\nVeikla | M005, M006");

        var map = await new MindMapConversionService(backend).ConvertToMindMapAsync(
            "Observatorija", CreateGroupedContext(AiModelManifest.RecommendedModelKey));

        Assert.NotNull(map);
        Assert.Equal(2, backend.GenerateCallCount);
        Assert.Contains("EXACTLY 3 category lines", backend.Requests[1].Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Įkūrimas | M001", backend.Requests[1].Prompt, StringComparison.Ordinal);
        Assert.Equal(3, map.Children.Count);
        Assert.All(map.Children, category => Assert.Equal(2, category.Children.Count));
    }

    [Fact]
    public async Task FiveWordSourceLanguageTheme_IsNotDiscarded()
    {
        using var backend = new FakeTextGenerationBackend();
        backend.EnqueueResponse("Observatorijos istorija ir mokslinė raida | M001, M003\n" +
                                "Įranga | M002, M004\nVeikla | M005, M006");

        var map = await new MindMapConversionService(backend).ConvertToMindMapAsync(
            "Observatorija", CreateGroupedContext(AiModelManifest.RecommendedModelKey));

        Assert.NotNull(map);
        Assert.Equal(1, backend.GenerateCallCount);
        Assert.Contains(map.Children, category => category.Text == "Observatorijos istorija ir mokslinė raida"
            && category.Children.Count == 2);
    }

    [Fact]
    public async Task TwoTopicsInOneSentence_CanLandInDifferentCategories()
    {
        const string compound =
            "Šviesos reakcijose gaminami ATP ir NADPH, o Kalvino ciklas juos naudoja cukrams sintetinti.";
        const string chlorophyll = "Chlorofilas sugeria šviesą mėlynoje spektro dalyje.";
        const string calvin = "Kalvino ciklas gamina cukrų iš anglies dioksido.";
        var facts = new[]
        {
            CreateFact("F001", "S001", compound, "ATP ir NADPH", 0),
            CreateFact("F002", "S002", chlorophyll, "mėlynoje", 1),
            CreateFact("F003", "S003", calvin, "cukrų", 2)
        };
        var plan = new StudyPlan("TWO-TOPICS", AiModelManifest.RecommendedModelKey,
            StudyPlanCoordinator.CurrentPlannerVersion, DateTimeOffset.UtcNow, facts,
            SourceUnits: [
                new StudySourceUnitSnapshot("S001", compound, 0, 0, 0),
                new StudySourceUnitSnapshot("S002", chlorophyll, 1, 0, compound.Length + 1),
                new StudySourceUnitSnapshot("S003", calvin, 2, 0, compound.Length + chlorophyll.Length + 2)
            ]);
        using var backend = new FakeTextGenerationBackend();
        backend.EnqueueResponse("Šviesa | M001, M003\nKalvino ciklas | M002, M004");

        var map = await new MindMapConversionService(backend).ConvertToMindMapAsync(
            "Fotosintezė", StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Balanced));

        Assert.NotNull(map);
        Assert.Contains(map.Children, category => category.Text == "Šviesa"
            && category.Children.Any(point => point.Text.Contains("ATP ir NADPH", StringComparison.Ordinal))
            && category.Children.Any(point => point.Text.Contains("Chlorofilas", StringComparison.Ordinal)));
        Assert.Contains(map.Children, category => category.Text == "Kalvino ciklas"
            && category.Children.Any(point => point.Text.Contains("juos naudoja", StringComparison.Ordinal))
            && category.Children.Any(point => point.Text.Contains("cukrų", StringComparison.Ordinal)));
        Assert.Contains(Flatten(map), node => node.Text == "Šviesos reakcijose gaminami ATP ir NADPH");
        Assert.Contains(Flatten(map), node => node.Text == "Kalvino ciklas juos naudoja cukrams sintetinti");
        Assert.DoesNotContain(Flatten(map), node => node.Text.Contains(" — ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnglishContrast_KeepsBothSidesAsReadableStatements()
    {
        const string contrast =
            "Her mother taught mathematics, while her father repaired navigation instruments for fishing vessels.";
        var facts = new[]
        {
            CreateFact("F001", "S001", contrast, "mathematics", 0),
            CreateFact("F002", "S002", "Elena built a tide gauge from a glass tube.", "tide gauge", 1),
            CreateFact("F003", "S003", "The device recorded water levels every fifteen minutes.", "fifteen minutes", 2)
        };
        var plan = new StudyPlan("CONTRAST", AiModelManifest.BasicModelKey,
            StudyPlanCoordinator.CurrentPlannerVersion, DateTimeOffset.UtcNow, facts);
        using var backend = new FakeTextGenerationBackend();

        var map = await new MindMapConversionService(backend).ConvertToMindMapAsync(
            "Elena", StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Balanced));

        Assert.NotNull(map);
        Assert.Contains(Flatten(map), node => node.Text == "Her mother taught mathematics");
        Assert.Contains(Flatten(map), node => node.Text ==
            "Her father repaired navigation instruments for fishing vessels");
        Assert.DoesNotContain(Flatten(map), node => node.Text.Contains(" — ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BulletedSourceDetails_DropTheListMarker()
    {
        const string dhcp = "- DHCP keeps each lease for 24 hours.";
        const string dns = "- DNS translates domain names into IP addresses.";
        const string tls = "- TLS verifies the server certificate.";
        const string dated = "1974. The system was installed.";
        var facts = new[]
        {
            CreateFact("F001", "S001", dhcp, "24 hours", 0),
            CreateFact("F002", "S002", dns, "IP addresses", 1),
            CreateFact("F003", "S003", tls, "server certificate", 2),
            CreateFact("F004", "S004", dated, "1974", 3)
        };
        var plan = new StudyPlan("BULLETS", AiModelManifest.BasicModelKey,
            StudyPlanCoordinator.CurrentPlannerVersion, DateTimeOffset.UtcNow, facts);
        using var backend = new FakeTextGenerationBackend();

        var map = await new MindMapConversionService(backend).ConvertToMindMapAsync(
            "Network operations", StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Balanced));

        Assert.NotNull(map);
        Assert.Contains(Flatten(map), node => node.Text == "DHCP keeps each lease for 24 hours");
        Assert.Contains(Flatten(map), node => node.Text == "DNS translates domain names into IP addresses");
        Assert.Contains(Flatten(map), node => node.Text == "1974. The system was installed");
        Assert.DoesNotContain(Flatten(map), node => node.Text.StartsWith("- ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingOneModelAssignment_KeepsValidGroupsAndGroundsTheRemainder()
    {
        using var backend = new FakeTextGenerationBackend();
        backend.EnqueueResponse("Istorija | M001, M003\nĮranga | M002, M004\nVeikla | M005");

        var map = await new MindMapConversionService(backend).ConvertToMindMapAsync(
            "Observatorija", CreateGroupedContext(AiModelManifest.RecommendedModelKey));

        Assert.NotNull(map);
        Assert.Contains(map.Children, category => category.Text == "Istorija" && category.Children.Count == 2);
        Assert.Contains(map.Children, category => category.Text == "Įranga" && category.Children.Count == 2);
        Assert.Contains(Flatten(map), node => node.Text.Contains("ekskursijas", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MoreThemesThanRequested_AreKeptWhenSeveralFactsStillShareThemes()
    {
        using var backend = new FakeTextGenerationBackend();
        backend.EnqueueResponse("Istorija | M001, M003\nĮranga | M002, M004\n" +
                                "Atidarymas | M005\nEkskursijos | M006");

        var map = await new MindMapConversionService(backend).ConvertToMindMapAsync(
            "Observatorija", CreateGroupedContext(AiModelManifest.AdvancedModelKey));

        Assert.NotNull(map);
        Assert.Contains(map.Children, category => category.Text == "Istorija" && category.Children.Count == 2);
        Assert.Contains(map.Children, category => category.Text == "Įranga" && category.Children.Count == 2);
        Assert.Contains(map.Children, category => category.Text == "Ekskursijos");
    }

    [Fact]
    public async Task WholeClauseAnswer_DoesNotRepeatItselfInMapDetail()
    {
        const string process = "Fotosinteze yra procesas, kurio metu augalai šviesą paverčia chemine energija.";
        const string pigment = "Chlorofilas sugeria mėlyną ir raudoną šviesą.";
        const string product = "Šviesos reakcijose gaminami ATP ir NADPH.";
        var facts = new[]
        {
            CreateFact("F001", "S001", process, "Fotosinteze yra procesas", 0),
            CreateFact("F002", "S002", pigment, "Chlorofilas", 1),
            CreateFact("F003", "S003", product, "ATP ir NADPH", 2)
        };
        var plan = new StudyPlan("NO-DUPLICATE", AiModelManifest.BasicModelKey,
            StudyPlanCoordinator.CurrentPlannerVersion, DateTimeOffset.UtcNow, facts);
        using var backend = new FakeTextGenerationBackend();

        var map = await new MindMapConversionService(backend).ConvertToMindMapAsync(
            "Fotosinteze", StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Balanced));

        Assert.NotNull(map);
        Assert.Contains(Flatten(map), node => node.Text ==
            "Fotosinteze yra procesas, kurio metu augalai šviesą paverčia chemine energija");
        Assert.DoesNotContain(Flatten(map), node => node.Text.Contains(" — ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IncompleteModelMeasurement_UsesACompleteSourcePhrase()
    {
        using var backend = new FakeTextGenerationBackend();
        var quantity = new StudyFact("F002", "S002", Second, "28 centimetrų",
            StudyFactImportance.Core, 1, StudyAnswerKind.Quantity, Second, First.Length + 1,
            Second.IndexOf("28 centimetrų", StringComparison.Ordinal), "28 centimetrų".Length);
        var plan = new StudyPlan("MEASUREMENT", AiModelManifest.RecommendedModelKey,
            StudyPlanCoordinator.CurrentPlannerVersion, DateTimeOffset.UtcNow,
            [CreateFact("F001", "S001", First, "1922", 0), quantity]);

        var map = await new MindMapConversionService(backend).ConvertToMindMapAsync(
            "Observatorija", StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Balanced));

        Assert.NotNull(map);
        Assert.True(map.Children.Any(branch => branch.Text == "Pirmasis teleskopas"
            && branch.Children.Any(point => point.Text == "turėjo 28 centimetrų skersmens veidrodį")),
            string.Join(" | ", Flatten(map).Select(node => node.Text)));
    }

    [Fact]
    public async Task BasicTier_UsesCompactSourcePhrasesWithoutModelCall()
    {
        using var backend = new FakeTextGenerationBackend();
        var map = await new MindMapConversionService(backend).ConvertToMindMapAsync(
            "Observatorija", CreateContext(AiModelManifest.BasicModelKey));

        Assert.NotNull(map);
        Assert.Equal(0, backend.GenerateCallCount);
        Assert.DoesNotContain(Flatten(map), node => node.Text is "Date" or "Quantity" or "Facts"
            || node.Text == First || node.Text == Second);
    }

    [Fact]
    public async Task CompoundSentence_AddsAConciseSecondRelationship()
    {
        const string evidence =
            "Šviesos reakcijose gaminami ATP ir NADPH, o Kalvino ciklas juos naudoja cukrams sintetinti.";
        var fact = CreateFact("F001", "S001", evidence, "ATP ir NADPH", 0);
        var plan = new StudyPlan("COMPOUND", AiModelManifest.BasicModelKey,
            StudyPlanCoordinator.CurrentPlannerVersion, DateTimeOffset.UtcNow, [fact]);
        using var backend = new FakeTextGenerationBackend();

        var map = await new MindMapConversionService(backend).ConvertToMindMapAsync(
            "Fotosintezė", StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Balanced));

        Assert.NotNull(map);
        Assert.Contains(map.Children, branch => branch.Text == "Kalvino ciklas"
            && branch.Children.Any(point => point.Text == "juos naudoja cukrams sintetinti"));
    }

    [Fact]
    public async Task ShortConjunction_DoesNotBecomeABranchPrefix()
    {
        const string evidence =
            "Anglies dioksidas patenka į Kalvino ciklą, o skaidant vandenį išsiskiria deguonis.";
        var fact = CreateFact("F001", "S001", evidence, "Anglies dioksidas", 0);
        var plan = new StudyPlan("OXYGEN", AiModelManifest.BasicModelKey,
            StudyPlanCoordinator.CurrentPlannerVersion, DateTimeOffset.UtcNow, [fact]);
        using var backend = new FakeTextGenerationBackend();

        var map = await new MindMapConversionService(backend).ConvertToMindMapAsync(
            "Fotosintezė", StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Balanced));

        Assert.NotNull(map);
        Assert.Contains(map.Children, branch => branch.Text == "skaidant vandenį"
            && branch.Children.Any(point => point.Text == "išsiskiria deguonis"));
    }

    [Fact]
    public async Task TwoActionsJoinedByConjunction_AreBothRepresented()
    {
        const string evidence =
            "Naujas fotometras buvo įrengtas 1997 metais ir matavimų tikslumą padidino 15 procentų.";
        var fact = CreateFact("F001", "S001", evidence, "1997 metais", 0);
        var plan = new StudyPlan("PHOTOMETER", AiModelManifest.BasicModelKey,
            StudyPlanCoordinator.CurrentPlannerVersion, DateTimeOffset.UtcNow, [fact]);
        using var backend = new FakeTextGenerationBackend();

        var map = await new MindMapConversionService(backend).ConvertToMindMapAsync(
            "Observatorija", StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Balanced));

        Assert.NotNull(map);
        Assert.Contains(map.Children, branch => branch.Text == "matavimų tikslumą"
            && branch.Children.Any(point => point.Text == "padidino 15 procentų"));
    }

    [Fact]
    public async Task QuantityAfterConjunction_DropsTheDanglingPriorClause()
    {
        const string evidence =
            "Naujas fotometras buvo įrengtas 1997 metais ir matavimų tikslumą padidino 15 procentų.";
        const string answer = "15 procentų";
        var fact = new StudyFact("F001", "S001", evidence, answer, StudyFactImportance.Core, 0,
            StudyAnswerKind.Quantity, evidence, 0,
            evidence.IndexOf(answer, StringComparison.Ordinal), answer.Length);
        var plan = new StudyPlan("ACCURACY", AiModelManifest.BasicModelKey,
            StudyPlanCoordinator.CurrentPlannerVersion, DateTimeOffset.UtcNow, [fact]);
        using var backend = new FakeTextGenerationBackend();

        var map = await new MindMapConversionService(backend).ConvertToMindMapAsync(
            "Observatorija", StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Balanced));

        Assert.NotNull(map);
        Assert.Contains(map.Children, branch => branch.Text == "Naujas fotometras"
            && branch.Children.Any(point => point.Text == "matavimų tikslumą padidino 15 procentų"));
    }

    [Fact]
    public async Task SelectedSecondAction_DoesNotHideTheFirstAction()
    {
        const string evidence =
            "Dabar darbuotojai kiekvieną mėnesį rengia dvi naktines ekskursijas ir mokiniams aiškina, kaip žvaigždžių šviesa atskleidžia jų temperatūrą.";
        var fact = CreateFact("F001", "S001", evidence, "temperatūrą", 0);
        var plan = new StudyPlan("TOURS", AiModelManifest.BasicModelKey,
            StudyPlanCoordinator.CurrentPlannerVersion, DateTimeOffset.UtcNow, [fact]);
        using var backend = new FakeTextGenerationBackend();

        var map = await new MindMapConversionService(backend).ConvertToMindMapAsync(
            "Observatorija", StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Balanced));

        Assert.NotNull(map);
        Assert.Contains(map.Children, branch => branch.Text == "Dabar darbuotojai"
            && branch.Children.Any(point => point.Text == "kiekvieną mėnesį rengia dvi naktines ekskursijas"));
    }

    [Fact]
    public async Task OrganizationAtSentenceStart_RemainsACompleteBranch()
    {
        const string evidence =
            "The International Water Council appointed Maris to its standards committee in 2019.";
        const string answer = "The International Water Council";
        var fact = CreateFact("F001", "S001", evidence, answer, 0);
        var plan = new StudyPlan("ORG", AiModelManifest.BasicModelKey,
            StudyPlanCoordinator.CurrentPlannerVersion, DateTimeOffset.UtcNow, [fact]);
        using var backend = new FakeTextGenerationBackend();

        var map = await new MindMapConversionService(backend).ConvertToMindMapAsync(
            "Maris", StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Balanced));

        Assert.NotNull(map);
        Assert.Contains(map.Children, branch => branch.Text == answer
            && branch.Children.Any(point => point.Text == "appointed Maris to its standards committee in 2019"));
    }

    [Fact]
    public async Task LongActionList_BecomesShortSeparatePoints()
    {
        const string evidence =
            "Maris continues to teach, advise Clear Current, and study affordable monitoring equipment.";
        const string answer = "teach, advise Clear Current, and study affordable monitoring equipment";
        var fact = new StudyFact("F001", "S001", evidence, answer, StudyFactImportance.Core, 0,
            StudyAnswerKind.Text, evidence, 0, evidence.IndexOf(answer, StringComparison.Ordinal), answer.Length,
            IsExplicitList: true, AnswerItems: ["teach", "advise Clear Current", "study affordable monitoring equipment"]);
        var plan = new StudyPlan("ACTIONS", AiModelManifest.BasicModelKey,
            StudyPlanCoordinator.CurrentPlannerVersion, DateTimeOffset.UtcNow, [fact]);
        using var backend = new FakeTextGenerationBackend();

        var map = await new MindMapConversionService(backend).ConvertToMindMapAsync(
            "Maris", StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Balanced));

        Assert.NotNull(map);
        var branch = Assert.Single(map.Children, node => node.Text == "Maris continues");
        Assert.Equal(["teach", "advise Clear Current", "study affordable monitoring equipment"],
            branch.Children.Select(child => child.Text).ToArray());
    }

    private static StudyGenerationContext CreateContext(string modelKey)
    {
        var facts = new[]
        {
            CreateFact("F001", "S001", First, "1922", 0),
            CreateFact("F002", "S002", Second, "28", 1)
        };
        var plan = new StudyPlan("LT-MAP", modelKey,
            StudyPlanCoordinator.CurrentPlannerVersion, DateTimeOffset.UtcNow, facts,
            SourceUnits: [
                new StudySourceUnitSnapshot("S001", First, 0, 0, 0),
                new StudySourceUnitSnapshot("S002", Second, 1, 0, First.Length + 1)
            ]);
        return StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Balanced);
    }

    private static StudyGenerationContext CreateGroupedContext(string modelKey)
    {
        string[] evidence =
        [
            First,
            Second,
            "1968 metais tyrėjai pradėjo reguliariai stebėti kintamąsias žvaigždes.",
            "Naujas fotometras buvo įrengtas 1997 metais.",
            "Observatorija visuomenei duris atvėrė 2005 metais.",
            "Dabar darbuotojai kiekvieną mėnesį rengia dvi naktines ekskursijas."
        ];
        string[] answers = ["1922", "28", "1968", "1997", "2005", "dvi naktines ekskursijas"];
        var facts = evidence.Select((sentence, index) =>
            CreateFact($"F{index + 1:000}", $"S{index + 1:000}", sentence, answers[index], index)).ToArray();
        var units = evidence.Select((sentence, index) =>
            new StudySourceUnitSnapshot($"S{index + 1:000}", sentence, index, index < 4 ? 0 : 1, index)).ToArray();
        var plan = new StudyPlan("GROUPED-LT-MAP", modelKey,
            StudyPlanCoordinator.CurrentPlannerVersion, DateTimeOffset.UtcNow, facts, SourceUnits: units);
        return StudyPlanCoordinator.CreateContext(plan, StudyCoverageMode.Balanced);
    }

    private static StudyFact CreateFact(string id, string sourceId, string evidence, string answer, int order)
        => new(id, sourceId, evidence, answer, StudyFactImportance.Core, order,
            StudyAnswerKind.Date, evidence, 0,
            evidence.IndexOf(answer, StringComparison.Ordinal), answer.Length);

    private static IEnumerable<MindMapNode> Flatten(MindMapNode root)
    {
        yield return root;
        foreach (var child in root.Children)
        foreach (var nested in Flatten(child))
            yield return nested;
    }
}
