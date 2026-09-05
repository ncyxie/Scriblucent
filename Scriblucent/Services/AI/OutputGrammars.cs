namespace Scriblucent.Services.AI;

internal static class OutputGrammars
{
    public const string FlashcardRoot = "flashcards";

    // Bounded {min,max} repetition instead of open-ended "+" - verified directly against the real
    // bundled model that an unbounded grammar lets the model keep generating new-but-superfluous
    // detail indefinitely once it runs out of things genuinely worth saying (a repetition penalty
    // alone doesn't catch this, since nothing is being literally repeated). The grammar's hard cap
    // makes the requested quantity mechanically guaranteed instead of a suggestion the model can
    // ignore in either direction. llama.cpp's own grammar docs recommend {min,max} over "+"/nested
    // "?" for performance too. Regular (non-raw) interpolated string, not "$"""": raw string
    // interpolation disambiguates braces by counting against the "$" prefix count, not by simple
    // "{{"/"}}" pairing, which makes literal GBNF "{min,max}" syntax fight the C# escaping rules.
    //
    // No "refusal" branch, unlike the original design - same verified finding as MindMap/Quiz below:
    // under grammar-constrained decoding the refusal path is the shortest valid completion, so the
    // model takes it constantly even for good notes. Worse, the trailing optional "refusal?" let the
    // model emit its full card set AND then append a literal "REFUSE:" tail (it even echoed the
    // prompt's instruction sentence back), which AiInputGuard's refusal heuristics then matched -
    // discarding five perfectly good cards in one traced real run. Input suitability is already
    // screened before generation via AiInputGuard.EnsureSuitableStudyText.
    public static string FlashcardGrammar(int min, int max) =>
        $"flashcards ::= flashcard{{{min},{max}}}\n" +
        "flashcard ::= \"q:\" sp text nl \"a:\" sp text nl nl?\n" +
        "text ::= [^\\n]+\n" +
        "sp ::= \" \"*\n" +
        "nl ::= \"\\n\"\n";

    public const string MindMapRoot = "mindmap";

    // Rule names deliberately avoid underscores - llama.cpp's GBNF grammar parser (as vendored by
    // LLamaSharp 0.27.0) hard-crashes (native access violation) on an underscore-containing rule
    // name referenced immediately after an opening paren, e.g. "(root_line branches...)". This
    // isn't a syntax error it reports and recovers from - it's a native crash that takes the whole
    // worker process down instantly, which is exactly what surfaced as "Worker pipe closed
    // unexpectedly" for every mind map/quiz generation. Verified directly: the identical grammar
    // shape with the underscore removed (e.g. "rootline") parses and samples correctly against a
    // real model. Flashcards never hit this because FlashcardGrammar has no underscored rule names.
    //
    // No "refusal" branch, unlike FlashcardGrammar - verified directly against the real bundled
    // models that offering a "refusal | (...)" choice here makes the model take the refusal path
    // almost every time, even for perfectly good notes, because under grammar-constrained decoding
    // it's the shortest, lowest-effort valid completion available at the very first token. Input
    // suitability is already screened before generation via AiInputGuard.EnsureSuitableStudyText.
    //
    // Nesting is capped at exactly 3 fixed levels (branch/subbranch/subsubbranch) instead of the
    // previous unbounded self-recursive "children ::= branch+". Verified directly: the unbounded
    // version let the model wander into one ever-deepening chain of single nested children instead
    // of separate top-level branches, and per-token grammar-stack validation cost grew with that
    // nesting depth - latency for 25 tokens went from ~1.1s early on to ~18s by token 300 and kept
    // climbing, which is what surfaced as generation "hanging"/timing out. The capped version holds
    // steady throughput for the full token budget and produces properly branched output.
    //
    // Top-level branch count is bounded ({min,max}), same rationale as FlashcardGrammar - see that
    // method's comment. subbranch/subsubbranch are ALSO bounded (were "*", unbounded) - verified
    // directly that leaving those open lets the model spiral into a repeated-list dump inside a
    // single branch (e.g. one "Legacy" branch alone burning an entire 700-token budget on the same
    // handful of sub-points repeated over and over) even with the top-level count correctly capped
    // and a repetition penalty applied - the runaway-generation failure mode isn't specific to the
    // outermost repeating rule, so every "+"/"*" in the grammar needs its own bound, not just one.
    public static string MindMapGrammar(int min, int max) =>
        $"mindmap ::= rootline branch{{{min},{max}}}\n" +
        "rootline ::= \"ROOT:\" sp text nl\n" +
        "branch ::= \"-\" sp text nl subbranch{0,4}\n" +
        "subbranch ::= \"  -\" sp text nl subsubbranch{0,3}\n" +
        "subsubbranch ::= \"    -\" sp text nl\n" +
        "text ::= [^\\n]+\n" +
        "sp ::= \" \"*\n" +
        "nl ::= \"\\n\"\n";

    public const string QuizRoot = "quiz";

    // See MindMapGrammar's comments above - same underscore-crash fix, same "refusal" branch
    // removal (verified directly: with it, the model outputs the literal refusal text almost every
    // time even for good notes; AiInputGuard.EnsureSuitableStudyText already screens input upfront),
    // and same bounded {min,max} question count instead of open-ended "+". Every repeating line
    // group is bounded too - the identical unbounded-repetition shape caused runaway generation in
    // MindMap's subbranch/subsubbranch, just nested one level deeper here.
    public static string QuizGrammar(int min, int max) =>
        $"quiz ::= titleline nl question{{{min},{max}}}\n" +
        "titleline ::= \"title:\" sp text\n" +
        // Typed question alternatives: the declared type mechanically locks the content shape.
        // The original single free-choice "typeline" let the model declare any type while emitting
        // any content shape - traced real runs show it declaring "multi" with one combined correct
        // line plus a single wrong (rejected by the parser: multi needs >=2 correct lines and >=3
        // options), and declaring "truefalse" with correct:/wrong: lines instead of answer:true|false
        // (garbled into an empty option set). Splitting per type makes each mismatch impossible to
        // generate. Line minimums are deliberately >=2 for wrongs and >=2 for multi-corrects:
        // under constrained decoding the model satisfies the minimum allowed - with {1,x} wrongs it
        // produced 1-correct/1-wrong singles that passed parsing but were all dropped by display
        // validation (singles need >=3 options). Caps keep total options within the parser's 6 limit
        // and every repetition bounded (same runaway rationale as everywhere above).
        "question ::= sq | mq | tfq\n" +
        "sq ::= \"q:\" sp text nl \"type:\" sp \"single\" nl correctlines wronglines explanationline nl?\n" +
        "mq ::= \"q:\" sp text nl \"type:\" sp \"multi\" nl mcorrectlines wronglines explanationline nl?\n" +
        "tfq ::= \"q:\" sp text nl \"type:\" sp \"truefalse\" nl tfanswerline explanationline nl?\n" +
        "correctlines ::= (\"correct:\" sp text nl){1,3}\n" +
        "mcorrectlines ::= (\"correct:\" sp text nl){2,3}\n" +
        "wronglines ::= (\"wrong:\" sp text nl){2,3}\n" +
        "tfanswerline ::= \"answer:\" sp (\"true\" | \"false\") nl\n" +
        "explanationline ::= \"explanation:\" sp text\n" +
        "text ::= [^\\n]+\n" +
        "sp ::= \" \"*\n" +
        "nl ::= \"\\n\"\n";

    public static string? GetGrammarForTask(string taskType, int min, int max)
    {
        return taskType.ToLowerInvariant() switch
        {
            "flashcard" or "flashcards" => FlashcardGrammar(min, max),
            "mindmap" or "mind_map" => MindMapGrammar(min, max),
            "quiz" or "test" => QuizGrammar(min, max),
            _ => null
        };
    }

    public static string? GetGrammarRootForTask(string taskType)
    {
        return taskType.ToLowerInvariant() switch
        {
            "flashcard" or "flashcards" => FlashcardRoot,
            "mindmap" or "mind_map" => MindMapRoot,
            "quiz" or "test" => QuizRoot,
            _ => null
        };
    }
}
