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
}
