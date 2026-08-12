using Scriblucent.Icons;

namespace Scriblucent.Rendering;

/// <summary>
/// Off-thread renderable snapshot of the mind-map canvas: node cards + connector edges in
/// canvas DIP coordinates. Built on the UI thread from the already-measured live visuals
/// (no re-measure), consumed by <see cref="SkiaMindMapRenderer"/> on the worker threads.
/// </summary>
public sealed class MindMapTileSnapshot
{
    public required int Revision;
    public required double CanvasWidthDip;
    public required double CanvasHeightDip;
    public required IReadOnlyList<MindMapTileNode> Nodes;
    public required IReadOnlyList<MindMapTileEdge> Edges;
}

public sealed class MindMapTileNode
{
    public required double X, Y, Width, Height;
    public required bool IsRoot;
    public required string Shape;
    public required uint Background;
    public required uint Border;
    public required double BorderThickness;
    public required string Title;
    public required string FontFamily;
    public required float FontSizeDip;
    public required int FontWeight;
    public required bool Italic;
    public required uint TitleColor;
    public required bool HasChevron;
    public required bool Folded;
    public required IReadOnlyList<string> IconPaths;
    public required uint IconColor;
    public required int IconOverflow;
}

public sealed class MindMapTileEdge
{
    public required double SX, SY, C1X, C1Y, C2X, C2Y, EX, EY;
    public required uint Color;
}
