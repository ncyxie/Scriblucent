using Scriblucent.Models;
using Scriblucent.Services;

namespace Scriblucent.Tests;

public sealed class NativeFileImportTests
{
    [Theory]
    [InlineData(".scribnote")]
    [InlineData(".scribflash")]
    [InlineData(".scribmap")]
    [InlineData(".scribquiz")]
    public void FutureFormatIsRejectedWithoutChangingTheSource(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + extension);
        const string manifest = "{\"FormatVersion\":99,\"Document\":{\"Title\":\"Future\"}}";
        try
        {
            if (extension == ".scribflash")
            {
                using var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
                using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open());
                writer.Write(manifest);
            }
            else File.WriteAllText(path, manifest);
            var original = File.ReadAllBytes(path);
            Assert.Throws<InvalidOperationException>(() =>
            {
                switch (extension)
                {
                    case ".scribnote": NoteFileService.LoadNotePackage(path); break;
                    case ".scribflash": FlashcardFileService.LoadAnyFlashcardDocument(path, "fallback", false, out _); break;
                    case ".scribmap": MindMapFileService.LoadAnyDocument(path, "fallback"); break;
                    case ".scribquiz": QuizFileService.LoadAnyQuizDocument(path, "fallback", false, out _); break;
                }
            });
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(".scribnote")]
    [InlineData(".scribflash")]
    [InlineData(".scribmap")]
    [InlineData(".scribquiz")]
    public void ImportCreatesIndependentLibraryCopyWithoutChangingSource(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + extension);
        var originalId = Guid.NewGuid();
        try
        {
            switch (extension)
            {
                case ".scribnote": NoteFileService.SaveNotePackage(path, new NoteDocument { Id = originalId, Title = "Žinios", Content = "Body" }); break;
                case ".scribflash": FlashcardFileService.SaveScribFlashFile(path, new FlashcardSetDocument { Id = originalId, Title = "Žinios" }); break;
                case ".scribmap": MindMapFileService.SaveScribmapFile(path, new MindMapDocument { Id = originalId, Title = "Žinios" }); break;
                case ".scribquiz": QuizFileService.SaveScribQuizFile(path, new QuizDocument { Id = originalId, Title = "Žinios" }); break;
            }
            var sourceBytes = File.ReadAllBytes(path);
            var importedId = extension switch
            {
                ".scribnote" => NoteFileService.LoadNotePackage(path).Id,
                ".scribflash" => FlashcardFileService.LoadAnyFlashcardDocument(path, "fallback", false, out _).Id,
                ".scribmap" => MindMapFileService.LoadAnyDocument(path, "fallback").Id,
                _ => QuizFileService.LoadAnyQuizDocument(path, "fallback", false, out _).Id
            };
            Assert.NotEqual(originalId, importedId);
            Assert.Equal(sourceBytes, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }
}
