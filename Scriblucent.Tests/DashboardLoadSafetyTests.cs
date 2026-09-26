using System.Reflection;
using Scriblucent.ViewModels;

namespace Scriblucent.Tests;

public class DashboardLoadSafetyTests
{
    [Theory]
    [InlineData("ReadFlashcardsFromDisk", "{ invalid json")]
    [InlineData("ReadMindMapsFromDisk", "{ invalid json")]
    [InlineData("ReadQuizzesFromDisk", "{ invalid json")]
    [InlineData("ReadFlashcardsFromDisk", "null")]
    [InlineData("ReadMindMapsFromDisk", "null")]
    [InlineData("ReadQuizzesFromDisk", "null")]
    [InlineData("ReadNotesFromDisk", "{ invalid json")]
    [InlineData("ReadNotesFromDisk", "null")]
    public void InvalidDashboardFile_DoesNotBecomeAnEmptyLibrary(string readerName, string invalidJson)
    {
        var path = Path.Combine(Path.GetTempPath(), $"scriblucent-load-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, invalidJson);

        try
        {
            var reader = typeof(MainViewModel).GetMethod(readerName,
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(reader);

            object[] arguments = readerName == "ReadFlashcardsFromDisk"
                ? [path, path + ".legacy"]
                : [path];

            var error = Assert.Throws<TargetInvocationException>(() => reader.Invoke(null, arguments));
            Assert.NotNull(error.InnerException);
            Assert.Equal(invalidJson, File.ReadAllText(path));
            var backup = Directory.GetFiles(
                Path.GetDirectoryName(path)!,
                Path.GetFileNameWithoutExtension(path) + ".corrupt-*" + Path.GetExtension(path));
            Assert.NotEmpty(backup);
            Assert.Equal(invalidJson, File.ReadAllText(backup[0]));
        }
        finally
        {
            DeleteCorruptCopies(path);
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("ReadFlashcardsFromDisk")]
    [InlineData("ReadMindMapsFromDisk")]
    [InlineData("ReadQuizzesFromDisk")]
    [InlineData("ReadNotesFromDisk")]
    public void LegacyArrayDashboardFile_StillLoads(string readerName)
    {
        var path = Path.Combine(Path.GetTempPath(), $"scriblucent-legacy-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "[]");

        try
        {
            var reader = typeof(MainViewModel).GetMethod(readerName,
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(reader);

            object[] arguments = readerName == "ReadFlashcardsFromDisk"
                ? [path, path + ".legacy"]
                : [path];

            Assert.NotNull(reader.Invoke(null, arguments));
        }
        finally
        {
            DeleteCorruptCopies(path);
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("ReadFlashcardsFromDisk")]
    [InlineData("ReadMindMapsFromDisk")]
    [InlineData("ReadQuizzesFromDisk")]
    [InlineData("ReadNotesFromDisk")]
    public void LockedDashboardFile_IsAnErrorRatherThanAnEmptyLibrary(string readerName)
    {
        var path = Path.Combine(Path.GetTempPath(), $"scriblucent-locked-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "[]");

        try
        {
            var reader = typeof(MainViewModel).GetMethod(readerName,
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(reader);

            object[] arguments = readerName == "ReadFlashcardsFromDisk"
                ? [path, path + ".legacy"]
                : [path];

            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var error = Assert.Throws<TargetInvocationException>(() => reader.Invoke(null, arguments));
                Assert.IsAssignableFrom<IOException>(error.InnerException);
            }

            Assert.Equal("[]", File.ReadAllText(path));
        }
        finally
        {
            DeleteCorruptCopies(path);
            File.Delete(path);
        }
    }

    private static void DeleteCorruptCopies(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            return;

        var pattern = Path.GetFileNameWithoutExtension(path) + ".corrupt-*" + Path.GetExtension(path);
        foreach (var backup in Directory.GetFiles(directory, pattern))
            File.Delete(backup);
    }
}
