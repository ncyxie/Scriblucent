using Scriblucent.Services;

namespace Scriblucent.Tests;

public sealed class MindMapOutputParserTests
{
    [Fact]
    public void Parse_BulletedHierarchy_ReturnsRootAndChildren()
    {
        var output = """
        ROOT: Photosynthesis
        - Light reactions
          - ATP production
        - Calvin cycle
        """;

        var map = MindMapOutputParser.Parse(output, "Photosynthesis");

        Assert.NotNull(map);
        Assert.Equal("Photosynthesis", map.Text);
        Assert.Equal(2, map.Children.Count);
        Assert.Equal("ATP production", map.Children[0].Children[0].Text);
    }

    [Fact]
    public void Parse_ThinkingAndMarkdownArtifacts_AreIgnored()
    {
        var output = """
        <think>Do not keep this.</think>
        ```text
        - Main branch
          - Child point
        ```
        """;

        var map = MindMapOutputParser.Parse(output, "Main topic");

        Assert.NotNull(map);
        Assert.Equal("Main topic", map.Text);
        Assert.Equal("Main branch", map.Children[0].Text);
    }

    [Fact]
    public void ParseOutlineText_PlainIndentedTextBuildsTree()
    {
        var nodes = MindMapOutputParser.ParseOutlineText("""
        Main topic
          First branch
            Child point
        Second branch
        """);

        Assert.Equal(2, nodes.Count);
        Assert.Equal("Main topic", nodes[0].Text);
        Assert.Equal("First branch", nodes[0].Children[0].Text);
        Assert.Equal("Second branch", nodes[1].Text);
    }

    [Fact]
    public void Parse_UnstructuredOutput_ReturnsNull()
    {
        Assert.Null(MindMapOutputParser.Parse("This is only a sentence.", "Topic"));
    }
}
