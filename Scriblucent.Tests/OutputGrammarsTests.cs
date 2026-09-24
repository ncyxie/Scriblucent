using Scriblucent.Services.AI;

namespace Scriblucent.Tests;

public sealed class OutputGrammarsTests
{
    [Fact]
    public void StudyGrammars_RequireFixedAssignmentsAndBoundedSpans()
    {
        var ids = new[] { "F003", "F019" };
        var owned = new[] { "S003", "S019" };
        var questions = OutputGrammars.StudyQuestionGrammar(ids, owned);
        var answers = OutputGrammars.StudyAnswerGrammar(ids);
        var decisions = OutputGrammars.StudyDecisionGrammar(ids);
        var metadata = OutputGrammars.StudyMetadataGrammar(ids);

        Assert.Contains("record0 ::= \"fact: \" \"F003\"", questions);
        Assert.Contains("\"source: \" \"S003\"", questions);
        Assert.DoesNotContain("\"S003\" | \"S019\"", questions);
        Assert.Contains("span ::= [^\\n]{1,240}", questions);
        Assert.Contains("span ::= \"NONE\" | answer", answers);
        Assert.Contains("\"decision: \" (\"KEEP\" | \"DROP\")", decisions);
        Assert.Contains("studymetadata ::= record0 record1", metadata);
        Assert.Contains("\"items: \" items", metadata);
    }

    [Fact]
    public void SinglePairGrammar_IsBounded()
    {
        var grammar = OutputGrammars.StudySinglePairGrammar();

        Assert.Contains("qchar{4,178}", grammar);
        Assert.Contains("answer ::= [^\\n]{1,120}", grammar);
        Assert.DoesNotContain("refusal", grammar, StringComparison.OrdinalIgnoreCase);
    }
}
