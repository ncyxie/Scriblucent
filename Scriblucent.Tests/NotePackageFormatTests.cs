using System.Text.Json;
using Scriblucent.Models;
using Scriblucent.Services;

namespace Scriblucent.Tests;

public class NotePackageFormatTests
{
    [Fact]
    public void ScribNotePackage_RoundTripsDocument()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scriblucent-note-{Guid.NewGuid():N}.scribnote");
        try
        {
            NoteFileService.SaveNotePackage(path, new NoteDocument
            {
                Title = "Hello",
                Content = "Body"
            });

            var loaded = NoteFileService.ReadNotePackage(path);
            Assert.Equal("Hello", loaded.Title);
            Assert.Equal("Body", loaded.Content);

            using var probe = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(1, probe.RootElement.GetProperty("FormatVersion").GetInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LegacyNoteCard_StillLoads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scriblucent-note-{Guid.NewGuid():N}.notecard");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new NoteDocument
            {
                Title = "Legacy",
                Content = "Old body"
            }));

            var loaded = NoteFileService.ReadNotePackage(path);
            Assert.Equal("Legacy", loaded.Title);
            Assert.Equal("Old body", loaded.Content);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NewerScribNote_IsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scriblucent-note-{Guid.NewGuid():N}.scribnote");
        try
        {
            File.WriteAllText(path, "{\"FormatVersion\":99,\"Document\":{\"Title\":\"Future\",\"Content\":\"x\"}}");
            Assert.Throws<InvalidOperationException>(() => NoteFileService.ReadNotePackage(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
