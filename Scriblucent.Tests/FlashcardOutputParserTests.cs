using Scriblucent.Models;
using Scriblucent.Services;

namespace Scriblucent.Tests;

public sealed class FlashcardOutputParserTests
{
    [Fact]
    public void Parse_StandardRecords_ReturnsCardsInOrder()
    {
        var output = """
        q: What does ATP store in a cell?
        a: Chemical energy.
        q: Which pigment absorbs light during photosynthesis?
        a: Chlorophyll.
        """;

        var cards = FlashcardOutputParser.Parse(output);

        Assert.Equal(2, cards.Count);
        Assert.Equal("What does ATP store in a cell?", cards[0].Question);
        Assert.Equal("Chemical energy.", cards[0].Answer);
        Assert.Equal("Chlorophyll.", cards[1].Answer);
    }

    [Fact]
    public void Parse_LithuanianFormatKeys_IsAccepted()
    {
        var output = """
        Klausimas: Ką sugeria chlorofilas?
        Atsakymas: Šviesą.
        """;

        var cards = FlashcardOutputParser.Parse(output);

        var card = Assert.Single(cards);
        Assert.Equal("Ką sugeria chlorofilas?", card.Question);
        Assert.Equal("Šviesą.", card.Answer);
    }

    [Fact]
    public void Parse_ThinkingFenceAndTrailingArtifacts_AreRemoved()
    {
        var output = """
        <think>Internal reasoning should not reach the card.</think>
        ```
        q: What is released when water is split?
        a: Oxygen.
        ```
        <|end|>
        """;

        var cards = FlashcardOutputParser.Parse(output);

        var card = Assert.Single(cards);
        Assert.Equal("Oxygen.", card.Answer);
    }

    [Fact]
    public void Parse_PromptLeak_IsRejected()
    {
        var output = """
        q: Focus on definitions and use only information from the source note.
        a: Output format rules.
        """;

        Assert.Empty(FlashcardOutputParser.Parse(output));
    }

    [Fact]
    public void Parse_MissingAnswer_DoesNotCreateCard()
    {
        var output = """
        q: What does chlorophyll absorb?
        """;

        Assert.Empty(FlashcardOutputParser.Parse(output));
    }
}
