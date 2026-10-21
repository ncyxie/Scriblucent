using Scriblucent.Services;

namespace Scriblucent.Tests;

public sealed class InstallationLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scriblucent-install-tests-" + Guid.NewGuid().ToString("N"));
    public InstallationLifecycleTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyLibraryMovesWithMissingOrProfileOnlyDestination(bool profileExists)
    {
        var legacy = Path.Combine(_root, "NoteCards");
        var current = Path.Combine(_root, "Scriblucent");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "notes.json"), "legacy-library");
        if (profileExists)
        {
            Directory.CreateDirectory(current);
            File.WriteAllText(Path.Combine(current, "startup.profile"), "jit-profile");
        }
        Assert.Equal(LegacyMigrationResult.Migrated, LegacyDataMigration.Run(_root));
        Assert.Equal("legacy-library", File.ReadAllText(Path.Combine(current, "notes.json")));
        Assert.False(Directory.Exists(legacy));
        Assert.Equal(LegacyMigrationResult.NotNeeded, LegacyDataMigration.Run(_root));
    }

    [Theory]
    [InlineData("notes.json")]
    [InlineData("settings.json")]
    [InlineData("unknown.txt")]
    public void CoexistingDataIsNeverOverwritten(string existingFile)
    {
        foreach (var folder in new[] { "NoteCards", "Scriblucent" }) Directory.CreateDirectory(Path.Combine(_root, folder));
        File.WriteAllText(Path.Combine(_root, "NoteCards", "notes.json"), "legacy");
        File.WriteAllText(Path.Combine(_root, "Scriblucent", existingFile), "current");
        Assert.Equal(LegacyMigrationResult.Conflict, LegacyDataMigration.Run(_root));
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(_root, "NoteCards", "notes.json")));
        Assert.Equal("current", File.ReadAllText(Path.Combine(_root, "Scriblucent", existingFile)));
    }

    [Fact]
    public void QueueCoalescesPendingImportsButAllowsLaterExplicitImport()
    {
        var queue = new LaunchImportQueue();
        var path = Path.Combine(_root, "Žinių rinkinys.scribflash");
        queue.Add([path, path.ToUpperInvariant()]);
        Assert.Equal(1, queue.Count);
        Assert.Equal(path, queue.Peek());
        Assert.Equal("Flashcards", LaunchImportQueue.DashboardFor(queue.Peek()!));
        queue.Complete();
        Assert.Null(queue.Peek());
        queue.Add([path]);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void LaunchArgumentsPreserveSpacesAndRejectOtherCommands()
    {
        var path = Path.Combine(_root, "My notes.scribnote");
        Assert.Equal(new[] { path }, LaunchImportQueue.ParseArguments(["--import", path]));
        Assert.Empty(LaunchImportQueue.ParseArguments([]));
        Assert.Throws<ArgumentException>(() => LaunchImportQueue.ParseArguments(["--other", path]));
        Assert.Throws<ArgumentException>(() => LaunchImportQueue.ParseArguments(["--import"]));
        Assert.Null(LaunchImportQueue.DashboardFor("file.txt"));
    }

    [Fact]
    public void CompetingInstanceForwardsToOwnerAndOwnershipCanBeReacquired()
    {
        var data = Path.Combine(_root, "library");
        using var owner = new AppInstanceCoordinator(data);
        Assert.True(owner.IsOwner);
        using var delivered = new ManualResetEventSlim();
        string[]? received = null;
        owner.Listen(paths => { received = paths; delivered.Set(); });
        Exception? failure = null;
        var contender = new Thread(() =>
        {
            try
            {
                using var instance = new AppInstanceCoordinator(data);
                Assert.False(instance.IsOwner);
                Assert.True(instance.ForwardAsync([Path.Combine(_root, "with spaces.scribnote")]).GetAwaiter().GetResult());
                using var otherLibrary = new AppInstanceCoordinator(Path.Combine(_root, "other"));
                Assert.True(otherLibrary.IsOwner);
            }
            catch (Exception ex) { failure = ex; }
        });
        contender.Start();
        Assert.True(contender.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) throw failure;
        Assert.True(delivered.Wait(TimeSpan.FromSeconds(1)));
        Assert.Equal(new[] { Path.Combine(_root, "with spaces.scribnote") }, received);
        // The owner still holds its lock after the competing launch has exited.
        Assert.False(Directory.Exists(data));
        owner.Dispose();
        using var replacement = new AppInstanceCoordinator(data);
        Assert.True(replacement.IsOwner);
    }
}
