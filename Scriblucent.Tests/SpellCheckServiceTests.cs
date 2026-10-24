using Scriblucent.Services;
using WeCantSpell.Hunspell;

namespace Scriblucent.Tests;

/// <summary>Tests against a real Hunspell dictionary (TestData\en_US, the same LibreOffice source
/// SpellCheckDictionaryService downloads at runtime) so tokenization/checking is verified against
/// genuine dictionary behavior, not a hand-rolled fake. Loaded once and shared across tests via
/// IClassFixture - WordList construction from the ~550KB word list isn't free.</summary>
public class SpellCheckServiceTests : IClassFixture<SpellCheckServiceTests.EnglishDictionaryFixture>
{
    public sealed class EnglishDictionaryFixture
    {
        public WordList English { get; } = WordList.CreateFromFiles(
            Path.Combine(AppContext.BaseDirectory, "TestData", "en_US", "en_US.dic"),
            Path.Combine(AppContext.BaseDirectory, "TestData", "en_US", "en_US.aff"));
    }

    private readonly WordList _english;

    public SpellCheckServiceTests(EnglishDictionaryFixture fixture)
    {
        _english = fixture.English;
    }

    [Fact]
    public void FindMisspelledWordSpans_ObviousMisspelling_IsFlagged()
    {
        var spans = SpellCheckService.FindMisspelledWordSpans("This is a mispelled wrod.", _english);

        Assert.Equal(2, spans.Count);
        Assert.Equal("mispelled", "This is a mispelled wrod.".Substring(spans[0].Start, spans[0].Length));
        Assert.Equal("wrod", "This is a mispelled wrod.".Substring(spans[1].Start, spans[1].Length));
    }

    [Fact]
    public void FindMisspelledWordSpans_CorrectlySpelledSentence_ReturnsEmpty()
    {
        var spans = SpellCheckService.FindMisspelledWordSpans("The quick brown fox jumps over the lazy dog.", _english);

        Assert.Empty(spans);
    }

    [Fact]
    public void FindMisspelledWordSpans_WordInCustomList_IsNotFlagged()
    {
        var customWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Scriblucent" };

        var spans = SpellCheckService.FindMisspelledWordSpans("Welcome to Scriblucent.", _english, customWords);

        Assert.Empty(spans);
    }

    [Fact]
    public void FindMisspelledWordSpans_UrlAndEmail_AreExcludedEvenThoughFragmentsAreNotDictionaryWords()
    {
        var spans = SpellCheckService.FindMisspelledWordSpans(
            "See https://example.com/xyzzy or email plugh@example.com for help.",
            _english);

        Assert.Empty(spans);
    }

    [Fact]
    public void FindMisspelledWordSpans_NoActiveDictionary_ReturnsEmpty()
    {
        var spans = SpellCheckService.FindMisspelledWordSpans("mispelled", null);

        Assert.Empty(spans);
    }

    [Theory]
    [InlineData("ąžuolas", true)]
    [InlineData("don't", true)]
    [InlineData("well-known", true)]
    [InlineData("two words", false)]
    [InlineData("word!", false)]
    [InlineData("123", false)]
    [InlineData("", false)]
    public void Custom_dictionary_accepts_one_complete_word(string word, bool accepted)
        => Assert.Equal(accepted, SpellCheckService.IsDictionaryWord(word));

    [Fact]
    public void Reversal_actions_resolve_the_word_at_a_text_position()
    {
        const string text = "ąžuolas and well-known";
        var first = SpellCheckService.WordAtOffset(text, 3)!.Value;
        Assert.Equal("ąžuolas", text.Substring(first.Start, first.Length));
        var last = SpellCheckService.WordAtOffset(text, text.Length)!.Value;
        Assert.Equal("well-known", text.Substring(last.Start, last.Length));
        Assert.Null(SpellCheckService.WordAtOffset(text, -1));
        Assert.Null(SpellCheckService.WordAtOffset(text, text.Length + 1));
        Assert.Null(SpellCheckService.WordAtOffset("  word", 0));
    }

    [Fact]
    public void FindMisspelledWordSpans_WordNotInActiveDictionary_IsFlagged()
    {
        // A synthetic single-word dictionary, not the real English one - isolates the "checked
        // against exactly one active dictionary" behavior from real dictionary content.
        var dictionary = WordList.CreateFromWords(["foo"]);

        var spans = SpellCheckService.FindMisspelledWordSpans("foo bar", dictionary);

        Assert.Single(spans);
        Assert.Equal("bar", "foo bar".Substring(spans[0].Start, spans[0].Length));
    }

    [Fact]
    public void GetSuggestions_MisspelledWord_ReturnsNonEmptySuggestions()
    {
        var suggestions = SpellCheckService.GetSuggestions("mispelled", _english);

        Assert.NotEmpty(suggestions);
        Assert.Contains(suggestions, suggestion => string.Equals(suggestion, "misspelled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetSuggestions_NoActiveDictionary_ReturnsEmpty()
    {
        var suggestions = SpellCheckService.GetSuggestions("mispelled", null);

        Assert.Empty(suggestions);
    }
}
