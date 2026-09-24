using Scriblucent.Services;

namespace Scriblucent.Tests;

public sealed class AiInputGuardTests
{
    [Fact]
    public void IsSuitableStudyText_EnglishStudyNote_ReturnsNone()
        => Assert.Equal(AiInputRejectionReason.None, AiInputGuard.IsSuitableStudyText(ReadNote("english-study-note.txt")));

    [Fact]
    public void IsSuitableStudyText_LithuanianStudyNote_ReturnsNone()
        => Assert.Equal(AiInputRejectionReason.None, AiInputGuard.IsSuitableStudyText(ReadNote("lithuanian-study-note.txt")));

    [Fact]
    public void IsSuitableStudyText_ThinNote_ReturnsTooShort()
        => Assert.Equal(AiInputRejectionReason.TooShort, AiInputGuard.IsSuitableStudyText(ReadNote("short-note.txt")));

    [Fact]
    public void IsSuitableStudyText_RepetitiveNoise_ReturnsNonNone()
        => Assert.NotEqual(AiInputRejectionReason.None, AiInputGuard.IsSuitableStudyText(ReadNote("noisy-note.txt")));

    [Fact]
    public void IsSuitableStudyText_RepeatedCharacters_ReturnsRepeatedOrLowVariety()
    {
        var note = "This note is otherwise long enough but contains a run of repeats: " + new string('a', 20) + " right in the middle of it.";
        Assert.Equal(AiInputRejectionReason.RepeatedOrLowVariety, AiInputGuard.IsSuitableStudyText(note));
    }

    [Fact]
    public void IsSuitableStudyText_MostlyDigitsAndSymbols_ReturnsLowTextRatio()
    {
        var digitPadding = string.Concat(Enumerable.Range(0, 60).Select(i => $"{i:D4} "));
        var note = digitPadding + "The quick brown fox jumps over the lazy dog and cat runs fast today in the yard.";
        Assert.Equal(AiInputRejectionReason.LowTextRatio, AiInputGuard.IsSuitableStudyText(note));
    }

    [Fact]
    public void EnsureSuitableStudyText_ThinNote_ThrowsTypedRejection()
    {
        var exception = Assert.Throws<AiInputRejectedException>(
            () => AiInputGuard.EnsureSuitableStudyText(ReadNote("short-note.txt")));
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }

    private static string ReadNote(string fileName) => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "TestData", "AI", "Notes", fileName));
}
