using Scriblucent.Models;
using Scriblucent.Services;

namespace Scriblucent.Tests;

public sealed class QuizOutputParserTests
{
    [Fact]
    public void Parse_SingleMultiAndTrueFalseQuestions_ReturnsAllTypes()
    {
        var output = """
        title: Cell biology

        q: Which molecule stores energy in cells?
        type: single
        correct: ATP
        wrong: DNA
        wrong: Water
        explanation: ATP stores chemical energy in cells.

        q: Which statements describe photosynthesis?
        type: multi
        correct: It uses light energy.
        correct: It produces oxygen.
        wrong: It consumes oxygen as its main product.
        explanation: These facts describe the process.

        q: Chlorophyll absorbs light.
        type: truefalse
        answer: true
        explanation: Chlorophyll absorbs light for the process.
        """;

        var parsed = QuizOutputParser.Parse(output);

        Assert.Equal(3, parsed.Questions.Count);
        Assert.Equal(QuizQuestionType.SingleChoice, parsed.Questions[0].Type);
        Assert.Equal(QuizQuestionType.MultipleChoice, parsed.Questions[1].Type);
        Assert.Equal(QuizQuestionType.TrueFalse, parsed.Questions[2].Type);
        Assert.Single(parsed.Questions[0].Options, option => option.IsCorrect);
        Assert.Equal(2, parsed.Questions[1].Options.Count(option => option.IsCorrect));
        Assert.Single(parsed.Questions[2].Options, option => option.IsCorrect && option.Text == "True");
    }

    [Fact]
    public void Parse_LithuanianKeys_IsAccepted()
    {
        var output = """
        pavadinimas: Ląstelės
        klausimas: Ką gamina šviesos reakcijos?
        tipas: vienas
        teisingas: ATP ir NADPH
        neteisingas: Tik vandenį
        neteisingas: Tik deguonį
        paaiškinimas: Šviesos reakcijos gamina ATP ir NADPH.
        """;

        var parsed = QuizOutputParser.Parse(output);

        var question = Assert.Single(parsed.Questions);
        Assert.Equal(QuizQuestionType.SingleChoice, question.Type);
        Assert.Contains(question.Options, option => option.IsCorrect && option.Text == "ATP ir NADPH");
    }

    [Fact]
    public void Parse_ThinkingArtifactsAndFence_AreIgnored()
    {
        var output = """
        <think>Internal reasoning.</think>
        ```
        q: What is released during water splitting?
        type: single
        correct: Oxygen
        wrong: Carbon dioxide
        wrong: Nitrogen
        explanation: Oxygen is released.
        ```
        <|end|>
        """;

        var parsed = QuizOutputParser.Parse(output);

        var question = Assert.Single(parsed.Questions);
        Assert.Equal("What is released during water splitting?", question.Question);
        Assert.Contains(question.Options, option => option.IsCorrect && option.Text == "Oxygen");
    }

    [Fact]
    public void Parse_MissingRequiredOptions_IsRejected()
    {
        var output = """
        q: Which molecule stores energy in cells?
        type: single
        correct: ATP
        explanation: ATP stores energy.
        """;

        Assert.Empty(QuizOutputParser.Parse(output).Questions);
    }

    [Fact]
    public void Parse_TrueFalseWithUnrecognizedAnswer_IsRejected()
    {
        // A garbled/unrecognized answer must not silently default to "False is correct" -
        // the question should be dropped instead of published as wrong-by-construction.
        var output = """
        q: Chlorophyll absorbs light.
        type: truefalse
        answer: unsure
        explanation: Chlorophyll absorbs light for the process.
        """;

        Assert.Empty(QuizOutputParser.Parse(output).Questions);
    }

    [Fact]
    public void Parse_TrueFalseWithRecognizedFalseAnswer_IsAccepted()
    {
        var output = """
        q: The mitochondria is the powerhouse of the cell.
        type: truefalse
        answer: false
        explanation: This is a well-known biology fact.
        """;

        var question = Assert.Single(QuizOutputParser.Parse(output).Questions);

        Assert.Equal(QuizQuestionType.TrueFalse, question.Type);
        Assert.Single(question.Options, option => option.IsCorrect && option.Text == "False");
    }

    [Fact]
    public void Parse_TrueFalseWithWhQuestionPhrasing_IsRejected()
    {
        // A "when/what/who/..." style question can never be a coherent true/false item - verified
        // directly against the real model that this mismatch happens even with an explicit prompt
        // instruction against it, so the parser drops it instead of publishing a broken question.
        var output = """
        q: What mobile radiography units did she develop during World War I?
        type: truefalse
        answer: true
        explanation: She developed mobile X-ray units known as petites Curies.
        """;

        Assert.Empty(QuizOutputParser.Parse(output).Questions);
    }

    [Fact]
    public void Parse_TrueFalseEndingInQuestionMark_IsRejected()
    {
        var output = """
        q: Marie Curie was born in Warsaw?
        type: truefalse
        answer: true
        explanation: The note states she was born in Warsaw.
        """;

        Assert.Empty(QuizOutputParser.Parse(output).Questions);
    }

    [Fact]
    public void Parse_TrueFalseProperStatement_IsAccepted()
    {
        var output = """
        q: Marie Curie was born in Warsaw.
        type: truefalse
        answer: true
        explanation: The note states she was born in Warsaw.
        """;

        var question = Assert.Single(QuizOutputParser.Parse(output).Questions);
        Assert.Equal(QuizQuestionType.TrueFalse, question.Type);
    }

    [Fact]
    public void DeduplicateQuestions_SameStemDifferentCorrectAnswer_KeepsBothQuestions()
    {
        // Regression test: dedup used to key on the normalized question text alone, so two
        // genuinely different questions sharing a normalized stem (generic model phrasing
        // repeated across chunks/sections) would collapse into one, silently dropping content.
        var first = new QuizQuestion
        {
            Question = "Which molecule stores energy in cells?",
            Type = QuizQuestionType.SingleChoice,
            Options =
            [
                new QuizOption { Text = "ATP", IsCorrect = true },
                new QuizOption { Text = "DNA", IsCorrect = false }
            ]
        };
        var second = new QuizQuestion
        {
            Question = "Which molecule stores energy in cells?",
            Type = QuizQuestionType.SingleChoice,
            Options =
            [
                new QuizOption { Text = "Glucose", IsCorrect = true },
                new QuizOption { Text = "Water", IsCorrect = false }
            ]
        };

        var result = QuizOutputParser.DeduplicateQuestions([first, second]);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void DeduplicateQuestions_SameStemSameCorrectAnswer_CollapsesToOne()
    {
        var first = new QuizQuestion
        {
            Question = "Which molecule stores energy in cells?",
            Type = QuizQuestionType.SingleChoice,
            Options =
            [
                new QuizOption { Text = "ATP", IsCorrect = true },
                new QuizOption { Text = "DNA", IsCorrect = false }
            ]
        };
        var duplicate = new QuizQuestion
        {
            Question = "Which molecule stores energy in cells?",
            Type = QuizQuestionType.SingleChoice,
            Options =
            [
                new QuizOption { Text = "ATP", IsCorrect = true },
                new QuizOption { Text = "Water", IsCorrect = false }
            ]
        };

        var result = QuizOutputParser.DeduplicateQuestions([first, duplicate]);

        Assert.Single(result);
    }

    [Fact]
    public void PrepareDisplayQuestions_ProducesStableOptionOrder()
    {
        var parsed = QuizOutputParser.Parse("""
        q: Which molecule stores energy in cells?
        type: single
        correct: ATP
        wrong: DNA
        wrong: Water
        explanation: ATP stores chemical energy in cells.
        """);

        var first = QuizOutputParser.PrepareDisplayQuestions(parsed.Questions.ToList());
        var second = QuizOutputParser.PrepareDisplayQuestions(parsed.Questions.ToList());

        Assert.Equal(
            first[0].Options.Select(option => option.Text),
            second[0].Options.Select(option => option.Text));
    }
}
