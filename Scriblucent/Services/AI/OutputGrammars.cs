namespace Scriblucent.Services.AI;

internal static class OutputGrammars
{
    public const string StudyQuestionRoot = "studyquestions";
    public const string StudyAnswerRoot = "studyanswers";
    public const string StudyDecisionRoot = "studydecisions";
    public const string StudyMetadataRoot = "studymetadata";
    public const string StudySinglePairRoot = "singlepair";

    public static string StudySinglePairGrammar() =>
        "singlepair ::= \"q: \" question nl \"a: \" answer nl\n" +
        "question ::= qstart qchar{4,178} qmark\n" +
        "qstart ::= [^ \\t\\n_\\[\\]<>:?？؟]\n" +
        "qchar ::= [^\\n_\\[\\]<>:?？؟]\n" +
        "qmark ::= \"?\" | \"？\" | \"؟\"\n" +
        "answer ::= [^\\n]{1,120}\n" +
        "nl ::= \"\\n\"\n";

    public static string StudyQuestionGrammar(IReadOnlyList<string> factIds, IReadOnlyList<string> sourceIds)
    {
        var facts = factIds.Count > 0 ? factIds : ["F000"];
        var sources = sourceIds.Count == facts.Count ? sourceIds : Enumerable.Repeat("S000", facts.Count).ToList();
        var grammar = $"studyquestions ::= {string.Join(' ', Enumerable.Range(0, facts.Count).Select(index => $"record{index}"))}\n";
        for (var index = 0; index < facts.Count; index++)
        {
            grammar += $"record{index} ::= \"fact: \" \"{EscapeGrammarLiteral(facts[index])}\" nl " +
                $"\"source: \" \"{EscapeGrammarLiteral(sources[index])}\" nl " +
                "\"q: \" question nl \"span: \" span nl nl?\n";
        }
        return grammar + "question ::= qstart qchar{4,178} qmark\n" +
            "qstart ::= [^ \\t\\n_\\[\\]<>]\n" + "qchar ::= [^\\n_\\[\\]<>]\n" +
            "qmark ::= \"?\" | \"？\" | \"؟\"\n" + "span ::= [^\\n]{1,240}\n" + "nl ::= \"\\n\"\n";
    }

    public static string StudyAnswerGrammar(IReadOnlyList<string> factIds)
    {
        var facts = factIds.Count > 0 ? factIds : ["F000"];
        var grammar = $"studyanswers ::= {string.Join(' ', Enumerable.Range(0, facts.Count).Select(index => $"record{index}"))}\n";
        for (var index = 0; index < facts.Count; index++)
            grammar += $"record{index} ::= \"fact: \" \"{EscapeGrammarLiteral(facts[index])}\" nl \"span: \" span nl nl?\n";
        return grammar + "span ::= \"NONE\" | answer\nanswer ::= [^\\n]{1,240}\nnl ::= \"\\n\"\n";
    }

    public static string StudyDecisionGrammar(IReadOnlyList<string> factIds)
    {
        var facts = factIds.Count > 0 ? factIds : ["F000"];
        var grammar = $"studydecisions ::= {string.Join(' ', Enumerable.Range(0, facts.Count).Select(index => $"record{index}"))}\n";
        for (var index = 0; index < facts.Count; index++)
            grammar += $"record{index} ::= \"fact: \" \"{EscapeGrammarLiteral(facts[index])}\" nl \"language: \" (\"SAME\" | \"DIFFERENT\") nl \"decision: \" (\"KEEP\" | \"DROP\") nl nl?\n";
        return grammar + "nl ::= \"\\n\"\n";
    }

    public static string StudyMetadataGrammar(IReadOnlyList<string> factIds)
    {
        var facts = factIds.Count > 0 ? factIds : ["F000"];
        var grammar = $"studymetadata ::= {string.Join(' ', Enumerable.Range(0, facts.Count).Select(index => $"record{index}"))}\n";
        for (var index = 0; index < facts.Count; index++)
            grammar += $"record{index} ::= \"fact: \" \"{EscapeGrammarLiteral(facts[index])}\" nl \"kind: \" (\"PERSON\" | \"PLACE\" | \"ORGANIZATION\" | \"DATE\" | \"QUANTITY\" | \"TERM\" | \"ENTITY\" | \"TEXT\") nl \"items: \" items nl nl?\n";
        return grammar + "items ::= \"NONE\" | item\nitem ::= [^\\n]{1,240}\nnl ::= \"\\n\"\n";
    }

    private static string EscapeGrammarLiteral(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);
}
