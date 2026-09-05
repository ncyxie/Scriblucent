using Scriblucent.Services;

namespace Scriblucent.Tests;

public sealed class AiInputGuardTests
{
    [Fact]
    public void IsSuitableStudyText_EnglishStudyNote_ReturnsNone()
    {
        var note = ReadNote("english-study-note.txt");

        Assert.Equal(AiInputRejectionReason.None, AiInputGuard.IsSuitableStudyText(note));
    }

    [Fact]
    public void IsSuitableStudyText_LithuanianStudyNote_ReturnsNone()
    {
        var note = ReadNote("lithuanian-study-note.txt");

        Assert.Equal(AiInputRejectionReason.None, AiInputGuard.IsSuitableStudyText(note));
    }

    [Fact]
    public void IsSuitableStudyText_ThinNote_ReturnsTooShort()
    {
        var note = ReadNote("short-note.txt");

        Assert.Equal(AiInputRejectionReason.TooShort, AiInputGuard.IsSuitableStudyText(note));
    }

    [Fact]
    public void IsSuitableStudyText_RepetitiveNoise_ReturnsNonNone()
    {
        var note = ReadNote("noisy-note.txt");

        Assert.NotEqual(AiInputRejectionReason.None, AiInputGuard.IsSuitableStudyText(note));
    }

    [Fact]
    public void IsSuitableStudyText_ImagePlaceholderOnly_ReturnsTooShort()
    {
        Assert.Equal(
            AiInputRejectionReason.TooShort,
            AiInputGuard.IsSuitableStudyText("[image] [photo] [nuotrauka]"));
    }

    [Fact]
    public void IsSuitableStudyText_RepeatedCharacters_ReturnsRepeatedOrLowVariety()
    {
        var note = "This note is otherwise long enough but contains a run of repeats: " + new string('a', 20) + " right in the middle of it.";

        Assert.Equal(AiInputRejectionReason.RepeatedOrLowVariety, AiInputGuard.IsSuitableStudyText(note));
    }

    [Fact]
    public void IsSuitableStudyText_RepeatedPhrase_ReturnsRepeatedOrLowVariety()
    {
        var note = string.Join(" ", Enumerable.Repeat("the same short phrase repeats here", 15));

        Assert.Equal(AiInputRejectionReason.RepeatedOrLowVariety, AiInputGuard.IsSuitableStudyText(note));
    }

    [Fact]
    public void IsSuitableStudyText_MostlyDigitsAndSymbols_ReturnsLowTextRatio()
    {
        // Enough real letters to clear MinimumLetterCount (45), but heavily diluted by numeric
        // padding so the letter-to-visible-character ratio still falls below the 0.35 threshold.
        var digitPadding = string.Concat(Enumerable.Range(0, 60).Select(i => $"{i:D4} "));
        var note = digitPadding + "The quick brown fox jumps over the lazy dog and cat runs fast today in the yard.";

        Assert.Equal(AiInputRejectionReason.LowTextRatio, AiInputGuard.IsSuitableStudyText(note));
    }

    [Fact]
    public void IsSuitableStudyText_KeyboardMashGibberish_ReturnsLooksLikeGibberish()
    {
        // Distinct (not repeated - that would trip RepeatedOrLowVariety first) consonant-only
        // tokens with no vowels at all, including 'y' (IsLatinVowel treats y as a vowel) - drives
        // the overall vowel ratio below the 0.16 gibberish threshold.
        string[] words =
        [
            "qwrtpsdf", "zxcvbnkl", "hjklqwrt", "tpsdfgkl", "zxcbvnml",
            "sdfghjkl", "wrtpqzxc", "mnbvcxzq", "lkjhgfds", "wsxdcrft",
            "dcrftgbh", "rftgbnhj", "tgbnhjmk", "gbnhjmkl", "bnhjmklp",
            "njmklpqw", "cdxswzqt", "vfrtgbnh", "xswzqast", "dfjklzxq"
        ];
        var note = string.Join(" ", words);

        Assert.Equal(AiInputRejectionReason.LooksLikeGibberish, AiInputGuard.IsSuitableStudyText(note));
    }

    [Fact]
    public void EnsureSuitableStudyText_ThinNote_ThrowsTypedRejection()
    {
        var exception = Assert.Throws<AiInputRejectedException>(
            () => AiInputGuard.EnsureSuitableStudyText(ReadNote("short-note.txt")));

        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }

    [Fact]
    public void IsRefusalOutput_RecognizesRefusalPrefixIgnoringWhitespaceAndCase()
    {
        Assert.True(AiInputGuard.IsRefusalOutput("  refuse: insufficient meaningful note content"));
        Assert.False(AiInputGuard.IsRefusalOutput("The note is insufficient."));
        Assert.False(AiInputGuard.IsRefusalOutput(null));
    }

    [Fact]
    public void IsRefusalOutput_DetectsShortOutput()
    {
        Assert.True(AiInputGuard.IsRefusalOutput("ok"));
        Assert.True(AiInputGuard.IsRefusalOutput("yes"));
        Assert.False(AiInputGuard.IsRefusalOutput("This is a valid response with enough content."));
    }

    [Fact]
    public void IsRefusalOutput_DetectsNaturalLanguageRefusals()
    {
        Assert.True(AiInputGuard.IsRefusalOutput("I cannot generate flashcards from this content."));
        Assert.True(AiInputGuard.IsRefusalOutput("I can't process this note."));
        Assert.True(AiInputGuard.IsRefusalOutput("There is insufficient content to create study material."));
        Assert.True(AiInputGuard.IsRefusalOutput("Not enough meaningful information in the note."));
        Assert.True(AiInputGuard.IsRefusalOutput("Refuse: the input is not suitable."));
    }

    [Fact]
    public void IsRefusalOutput_DoesNotFalsePositiveOnValidOutput()
    {
        Assert.False(AiInputGuard.IsRefusalOutput("q: What is ATP?\na: Energy currency of cells."));
        Assert.False(AiInputGuard.IsRefusalOutput("ROOT: Biology Notes\n- Photosynthesis\n- Cellular respiration"));
        Assert.False(AiInputGuard.IsRefusalOutput("title: Biology Quiz\n\nq: What is ATP?\ntype: single\ncorrect: Energy\nwrong: Water\nexplanation: ATP stores energy."));
    }

    [Fact]
    public void IsRefusalOutput_EmptyAndNull_ReturnsFalse()
    {
        Assert.False(AiInputGuard.IsRefusalOutput(""));
        Assert.False(AiInputGuard.IsRefusalOutput("   "));
        Assert.False(AiInputGuard.IsRefusalOutput(null));
    }

    private static string ReadNote(string fileName)
    {
        return File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "AI",
            "Notes",
            fileName));
    }
}
