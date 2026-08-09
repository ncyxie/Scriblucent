using System.IO;

namespace Scriblucent.Services;

/// <summary>Writes a file via a temp-file-then-rename so a crash or power loss mid-write can never
/// leave the real path holding a truncated/corrupt file - the rename either fully lands or doesn't.</summary>
internal static class AtomicFileWriter
{
    public static void WriteAllText(string path, string content)
    {
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>Writes <paramref name="write"/>'s stream content to <paramref name="path"/> via the
    /// same temp-file-then-rename, streaming - no full-content string or byte array in memory
    /// (the notes store can be tens of MB of photo Base64).</summary>
    public static void Write(string path, Action<Stream> write)
    {
        var tempPath = path + ".tmp";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            write(stream);
        File.Move(tempPath, path, overwrite: true);
    }
}
