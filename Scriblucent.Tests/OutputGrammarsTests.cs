using Scriblucent.Services.AI;

namespace Scriblucent.Tests;

public sealed class OutputGrammarsTests
{
    [Fact]
    public void FlashcardGrammar_IsBoundedAndHasNoRefusal()
    {
        var grammar = OutputGrammars.FlashcardGrammar(3, 5);

        Assert.Contains("flashcards ::=", grammar);
        Assert.Contains("{3,5}", grammar);
        // No refusal branch: under constrained decoding the model took it constantly, and the
        // trailing optional "refusal?" let good outputs end with a REFUSE tail (see
        // OutputGrammarRefusalTests's comment).
        Assert.DoesNotContain("refusal", grammar);
        Assert.Contains("q:", grammar);
        Assert.Contains("a:", grammar);
    }

    [Fact]
    public void MindMapGrammar_IsBoundedAndHasNoUnderscoreRule()
    {
        var grammar = OutputGrammars.MindMapGrammar(3, 6);

        Assert.Contains("{3,6}", grammar);
        Assert.Contains("rootline", grammar);
        // Underscore in rule name crashes llama.cpp GBNF parser (verified)
        Assert.DoesNotContain("root_line", grammar);
        Assert.DoesNotContain("sub_branch", grammar);
        // No refusal branch (verified model takes shortest path otherwise)
        Assert.DoesNotContain("refusal", grammar);
        // Fixed 3-level nesting, not recursive unbounded
        Assert.Contains("subbranch{0,4}", grammar);
        Assert.Contains("subsubbranch{0,3}", grammar);
    }

    [Fact]
    public void QuizGrammar_TypedQuestionRules_EnforceDisplayReadyOptionCounts()
    {
        var grammar = OutputGrammars.QuizGrammar(4, 8);

        Assert.Contains("{4,8}", grammar);
        Assert.Contains("titleline", grammar);

        // Typed alternatives lock each declared type to its content shape - see QuizGrammar's
        // comment for the traced real-run failures this prevents.
        Assert.Contains("question ::= sq | mq | tfq", grammar);
        Assert.Contains("\"type:\" sp \"single\" nl correctlines wronglines", grammar);
        Assert.Contains("\"type:\" sp \"multi\" nl mcorrectlines wronglines", grammar);
        Assert.Contains("\"type:\" sp \"truefalse\" nl tfanswerline", grammar);

        // Minimums guarantee display-ready questions: singles get >=3 total options (1-3 correct +
        // 2-3 wrong), multi gets >=2 correct lines; caps keep totals within the parser's 6-option
        // limit. All bounded, none open-ended.
        Assert.Contains("correctlines ::= (\"correct:\" sp text nl){1,3}", grammar);
        Assert.Contains("mcorrectlines ::= (\"correct:\" sp text nl){2,3}", grammar);
        Assert.Contains("wronglines ::= (\"wrong:\" sp text nl){2,3}", grammar);
        Assert.Contains("tfanswerline ::= \"answer:\" sp (\"true\" | \"false\") nl", grammar);
        Assert.DoesNotContain("correct_line", grammar);
        Assert.DoesNotContain("refusal", grammar);
    }

    [Fact]
    public void GetGrammarForTask_ReturnsCorrectRoot()
    {
        Assert.Equal(OutputGrammars.FlashcardGrammar(2, 4), OutputGrammars.GetGrammarForTask("flashcard", 2, 4));
        Assert.Equal(OutputGrammars.MindMapGrammar(3, 6), OutputGrammars.GetGrammarForTask("mind_map", 3, 6));
        Assert.Equal(OutputGrammars.QuizGrammar(5, 9), OutputGrammars.GetGrammarForTask("quiz", 5, 9));
        Assert.Null(OutputGrammars.GetGrammarForTask("unknown", 1, 2));
    }
}
