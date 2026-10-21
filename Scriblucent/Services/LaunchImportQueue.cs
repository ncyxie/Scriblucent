using System.IO;

namespace Scriblucent.Services;

internal sealed class LaunchImportQueue
{
    private readonly Queue<string> _paths = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    internal int Count => _paths.Count;

    internal static string[] ParseArguments(string[] args)
    {
        if (args.Length == 0) return [];
        if (args[0] != "--import" || args.Length < 2 || args.Length > 129)
            throw new ArgumentException("LaunchArgumentsInvalid");
        return args.Skip(1).Select(Path.GetFullPath).ToArray();
    }

    internal void Add(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var fullPath = Path.GetFullPath(path);
            if (_pending.Add(fullPath)) _paths.Enqueue(fullPath);
        }
    }

    internal string? Peek() => _paths.TryPeek(out var path) ? path : null;
    internal void Complete()
    {
        if (_paths.TryDequeue(out var path)) _pending.Remove(path);
    }

    internal static string? DashboardFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".scribnote" => "Notes", ".scribflash" => "Flashcards", ".scribmap" => "MindMaps", ".scribquiz" => "Quizzes",
        _ => null
    };
}
