using Scriblucent.Services;
using Scriblucent.Services.AI;

namespace Scriblucent.Tests;

public sealed class FlashcardCountFormulaTests
{
    [Theory]
    [InlineData(300, 3, 6)]   // Repetitive text: ~3-6 cards (low density)
    [InlineData(500, 4, 9)]   // Medium content: ~4-9 cards
    [InlineData(100, 2, 3)]   // Short content: ~2-3 cards
    [InlineData(1000, 7, 15)] // Long content: ~7-15 cards
    public void CalculateTargetFlashcardCount_ProducesReasonableRange(int wordCount, int minExpected, int maxExpected)
    {
        // Build text with approximately the target word count
        var text = string.Join(" ", Enumerable.Repeat("The mitochondria is the powerhouse of the cell and produces ATP through oxidative phosphorylation.", wordCount / 10));

        var count = FlashcardConversionService.CalculateTargetFlashcardCount(text);

        Assert.InRange(count, minExpected, maxExpected);
    }

    [Fact]
    public void CalculateTargetFlashcardCount_VeryShortText_ReturnsMinimum()
    {
        var text = "ATP is energy.";

        var count = FlashcardConversionService.CalculateTargetFlashcardCount(text);

        Assert.Equal(2, count);
    }

    [Fact]
    public void CalculateTargetFlashcardCount_DenseText_ProducesMoreCards()
    {
        // Dense text with many unique words (~200 words)
        var denseText = "Photosynthesis converts light energy into chemical energy through chlorophyll pigments. " +
            "The Calvin cycle fixes carbon dioxide using RuBisCO enzyme in the stroma. " +
            "Thylakoid membranes host the light-dependent reactions producing ATP and NADPH. " +
            "C3 plants use direct Calvin cycle while C4 plants employ an additional pathway. " +
            "CAM plants open stomata at night to reduce water loss in arid environments. " +
            "Oxygen is released as a byproduct of water splitting during photolysis.";

        // Repetitive text with similar word count
        var repetitiveText = string.Join(" ", Enumerable.Repeat("The cell produces energy through the process of photosynthesis in chloroplasts.", 25));

        var denseCount = FlashcardConversionService.CalculateTargetFlashcardCount(denseText);
        var repetitiveCount = FlashcardConversionService.CalculateTargetFlashcardCount(repetitiveText);

        Assert.True(denseCount >= repetitiveCount,
            $"Dense text ({denseCount}) should produce at least as many cards as repetitive text ({repetitiveCount})");
    }
}

public sealed class SamplingProfileTests
{
    [Fact]
    public void DefaultProfile_HasCorrectValues()
    {
        var profile = SamplingProfiles.Default;

        Assert.Equal(0.7, profile.Temperature);
        Assert.Equal(0.8, profile.TopP);
        Assert.Equal(20, profile.TopK);
        Assert.Equal(0.0, profile.PresencePenalty);
        Assert.Equal(1.08, profile.RepetitionPenalty);
    }

    [Fact]
    public void RepairProfile_HasLowerTemperature()
    {
        var profile = SamplingProfiles.Repair;

        Assert.Equal(0.4, profile.Temperature);
        Assert.Equal(0.8, profile.TopP);
        Assert.Equal(20, profile.TopK);
        Assert.Equal(0.0, profile.PresencePenalty);
        Assert.Equal(1.08, profile.RepetitionPenalty);
    }

    [Fact]
    public void RepairProfile_IsMoreConservativeThanDefault()
    {
        Assert.True(SamplingProfiles.Repair.Temperature < SamplingProfiles.Default.Temperature);
    }
}

public sealed class OutputGrammarRefusalTests
{
    // Flashcards originally kept a "refusal |" branch (and a trailing optional "refusal?") after
    // MindMap/Quiz had theirs removed - traced real-model runs then showed exactly the predicted
    // failure: the model emitted its full card set AND appended a literal "REFUSE:" tail (echoing
    // the prompt's instruction sentence), which refusal heuristics matched and used to discard the
    // whole output. All three grammars now refuse nothing; input suitability is screened upfront
    // via AiInputGuard.EnsureSuitableStudyText instead.
    [Theory]
    [InlineData(OutputGrammars.FlashcardRoot)]
    [InlineData(OutputGrammars.MindMapRoot)]
    [InlineData(OutputGrammars.QuizRoot)]
    public void Grammars_DoNotOfferRefusal(string root)
    {
        var grammar = root switch
        {
            "flashcards" => OutputGrammars.FlashcardGrammar(2, 6),
            "mindmap" => OutputGrammars.MindMapGrammar(3, 8),
            "quiz" => OutputGrammars.QuizGrammar(3, 8),
            _ => ""
        };

        Assert.DoesNotContain("refusal", grammar);
    }

    [Fact]
    public void QuizGrammar_SupportsTruefalseAnswerFormat()
    {
        // Rule names are deliberately underscore-free - see OutputGrammars.cs's comment on
        // QuizGrammar: llama.cpp's GBNF parser crashes natively on an underscore-containing rule
        // name referenced right after an opening paren.
        Assert.Contains("tfanswerline ::= \"answer:\" sp (\"true\" | \"false\") nl", OutputGrammars.QuizGrammar(3, 8));
    }

    [Fact]
    public void QuizGrammar_SupportsSingleMultiCorrectWrong()
    {
        // Typed question alternatives (sq/mq/tfq) lock each declared type to its content shape;
        // multi gets its own >=2-correct rule so a "multi" declared with one combined correct line
        // can no longer be generated.
        var grammar = OutputGrammars.QuizGrammar(3, 8);
        Assert.Contains("question ::= sq | mq | tfq", grammar);
        Assert.Contains("mcorrectlines", grammar);
        Assert.Contains("correctlines", grammar);
        Assert.Contains("wronglines", grammar);
    }

    [Fact]
    public void AllGrammars_HaveRequiredRules()
    {
        Assert.Contains("text ::=", OutputGrammars.FlashcardGrammar(2, 6));
        Assert.Contains("sp ::=", OutputGrammars.FlashcardGrammar(2, 6));
        Assert.Contains("nl ::=", OutputGrammars.FlashcardGrammar(2, 6));

        Assert.Contains("rootline ::=", OutputGrammars.MindMapGrammar(3, 8));
        Assert.Contains("branch ::=", OutputGrammars.MindMapGrammar(3, 8));

        Assert.Contains("titleline ::=", OutputGrammars.QuizGrammar(3, 8));
        Assert.Contains("question ::=", OutputGrammars.QuizGrammar(3, 8));
        Assert.Contains("sq ::=", OutputGrammars.QuizGrammar(3, 8));
        Assert.Contains("tfanswerline ::=", OutputGrammars.QuizGrammar(3, 8));
    }

    [Fact]
    public void GrammarsEnforceRequestedBoundsInGeneratedRuleText()
    {
        // The whole point of switching from open-ended "+" to {min,max}: the requested count is
        // baked into the grammar text itself as a bounded-repetition quantifier, not just
        // mentioned in the prompt as a suggestion the model can ignore.
        Assert.Contains("flashcard{2,6}", OutputGrammars.FlashcardGrammar(2, 6));
        Assert.Contains("branch{4,9}", OutputGrammars.MindMapGrammar(4, 9));
        Assert.Contains("question{5,11}", OutputGrammars.QuizGrammar(5, 11));
    }
}
