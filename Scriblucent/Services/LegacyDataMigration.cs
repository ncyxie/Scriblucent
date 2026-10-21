using System.IO;

namespace Scriblucent.Services;

internal enum LegacyMigrationResult { NotNeeded, Migrated, Conflict, Failed }

internal static class LegacyDataMigration
{
    // This must run before any service that creates the Scriblucent data directory.
    internal static LegacyMigrationResult Run(string localAppData)
    {
        var legacy = Path.Combine(localAppData, "NoteCards");
        var current = Path.Combine(localAppData, "Scriblucent");
        try
        {
            if (!Directory.Exists(legacy)) return LegacyMigrationResult.NotNeeded;
            if (Directory.GetFileSystemEntries(legacy).Length == 0) return LegacyMigrationResult.NotNeeded;
            if (Directory.Exists(current))
            {
                var entries = Directory.GetFileSystemEntries(current);
                if (entries.Any(entry => !File.Exists(entry)
                    || !string.Equals(Path.GetFileName(entry), "startup.profile", StringComparison.OrdinalIgnoreCase)))
                    return LegacyMigrationResult.Conflict;

                // Remove only the known disposable profile; never recursively delete user data.
                foreach (var entry in entries) File.Delete(entry);
                Directory.Delete(current);
            }
            Directory.Move(legacy, current);
            return LegacyMigrationResult.Migrated;
        }
        catch (IOException) { return LegacyMigrationResult.Failed; }
        catch (UnauthorizedAccessException) { return LegacyMigrationResult.Failed; }
    }
}
