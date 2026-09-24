using Scriblucent.Services;

namespace Scriblucent.Tests;

public sealed class MindMapOutputParserTests
{
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
}
