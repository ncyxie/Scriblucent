namespace Scriblucent.Rendering;

/// <summary>
/// Off-thread renderable snapshot of the mind-map canvas: node cards + connector edges in
/// canvas DIP coordinates. Built on the UI thread from the already-measured live visuals
/// (no re-measure), consumed by <see cref="SkiaMindMapRenderer"/> on the worker threads.
/// </summary>
public sealed class MindMapTileSnapshot
{
    public required Guid MapId;
    public required int Revision;
    public required double CanvasWidthDip;
    public required double CanvasHeightDip;
    public required IReadOnlyList<MindMapTileNode> Nodes;
    public required IReadOnlyList<MindMapTileEdge> Edges;
}

public sealed class MindMapTileNode
{
    public required double X, Y, Width, Height;
    public required uint Background;
    public required uint Border;
    public uint BorderTop, BorderBottom;
    public required string OuterContour, BorderContour;
    public string? RimContour;
    public uint RimTop, RimBody, RimBottom;
    public MindMapTileText? Title;
    public required IReadOnlyList<MindMapTileGlyph> Glyphs;
    public required IReadOnlyList<MindMapTileText> Metadata;
}

public sealed class MindMapTileText
{
    public required double X, Y, Width, Height;
    public required string Text;
    public required string FontFamily;
    public required float FontSizeDip;
    public required int FontWeight;
    public required bool Italic;
    public required uint Color;
    public int MaxLines = 1;
}

public sealed class MindMapTileGlyph
{
    public required double X, Y, Width, Height;
    public required string Path;
    public required uint Color;
}

public sealed class MindMapTileEdge
{
    public required double SX, SY, C1X, C1Y, C2X, C2Y, EX, EY;
    public required uint Color;
}
