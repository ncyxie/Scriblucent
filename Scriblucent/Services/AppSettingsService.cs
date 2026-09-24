using Scriblucent.Models;
using Scriblucent.Services.AI;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Scriblucent.Services;

public static class AppSettingsService
{
    private static readonly object SyncRoot = new();
    private static readonly TimeSpan SaveDebounceDelay = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };
    private static AppSettings? _cachedSettings;
    private static string? _pendingJson;
    private static Timer? _saveTimer;

    private static string GetSettingsFilePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scriblucent");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    public static void MigrateLegacyDataFolder()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var legacyDir = Path.Combine(localAppData, "NoteCards");
            var currentDir = Path.Combine(localAppData, "Scriblucent");

            if (Directory.Exists(legacyDir) && !Directory.Exists(currentDir))
                Directory.Move(legacyDir, currentDir);
        }
        catch
        {
            // Best-effort migration only; the app will start fresh if this fails.
        }
    }

    /// <summary>Returns the shared in-memory settings instance, reading from disk only on first call per process.</summary>
    public static AppSettings Load()
    {
        lock (SyncRoot)
        {
            return _cachedSettings ??= LoadFromDisk();
        }
    }

    private static AppSettings LoadFromDisk()
    {
        try
        {
            var path = GetSettingsFilePath();
            if (!File.Exists(path))
            {
                var defaultSettings = new AppSettings
                {
                    FlashcardModelKey = BundledModelHostService.GetRecommendedFlashcardModelKeyForCurrentMachine()
                };

                WriteToDiskImmediate(defaultSettings);
                return defaultSettings;
            }

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            if (settings is null)
                return new AppSettings();

            var defaultTools = BundledModelHostService.BuildDefaultAiToolSettings();
            if (settings.AiTools is null || settings.AiTools.Count == 0)
                settings.AiTools = defaultTools;
            else
                AiModelManifest.Instance.MergeMissingCatalogTools(settings);

            if (string.IsNullOrWhiteSpace(settings.FlashcardModelKey)
                || settings.AiTools.All(tool => tool.IsRemoved || !tool.IsEnabled || !string.Equals(tool.Key, settings.FlashcardModelKey, StringComparison.OrdinalIgnoreCase)))
            {
                settings.FlashcardModelKey = BundledModelHostService.GetEnabledFlashcardModelKeys(settings).FirstOrDefault()
                    ?? BundledModelHostService.GetRecommendedFlashcardModelKeyForCurrentMachine();
            }

            settings.FlashcardFlipDelayMilliseconds = AppSettings.NormalizeFlashcardFlipDelayMilliseconds(
                settings.FlashcardFlipDelayMilliseconds);

            if (!Enum.IsDefined(settings.StudyCoverage))
                settings.StudyCoverage = StudyCoverageMode.Auto;
            else
                settings.StudyCoverage = StudyPlanCoordinator.NormalizeCoverage(settings.StudyCoverage);

            return settings;
        }
        catch (Exception ex)
        {
            CrashLog.Append("AppSettingsService.LoadFromDisk", ex);
            return new AppSettings();
        }
    }

    /// <summary>Updates the shared in-memory instance and captures its current state as JSON immediately,
    /// synchronously, on the caller's thread - Save() always runs right after the caller finishes mutating
    /// the object, so nothing else touches it at this instant. The debounced background flush then only
    /// ever writes that already-captured, immutable string to disk; it never re-reads the live mutable
    /// AppSettings instance, so it can't race a UI-thread mutation the way re-serializing it later would.</summary>
    public static void Save(AppSettings settings)
    {
        lock (SyncRoot)
        {
            _cachedSettings = settings;
            _pendingJson = JsonSerializer.Serialize(settings, SerializerOptions);
            _saveTimer ??= new Timer(_ => FlushPendingSave(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(SaveDebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Synchronously writes any pending debounced save. Call before the process can exit.</summary>
    public static void Flush()
    {
        lock (SyncRoot)
        {
            _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            FlushPendingJsonLocked();
        }
    }

    private static void FlushPendingSave()
    {
        lock (SyncRoot)
        {
            FlushPendingJsonLocked();
        }
    }

    /// <summary>Must be called with SyncRoot already held.</summary>
    private static void FlushPendingJsonLocked()
    {
        if (_pendingJson is null)
            return;

        WriteJsonToDisk(_pendingJson);
        _pendingJson = null;
    }

    private static void WriteToDiskImmediate(AppSettings settings)
        => WriteJsonToDisk(JsonSerializer.Serialize(settings, SerializerOptions));

    private static void WriteJsonToDisk(string json)
    {
        try
        {
            AtomicFileWriter.WriteAllText(GetSettingsFilePath(), json);
        }
        catch (Exception ex)
        {
            CrashLog.Append("AppSettingsService.WriteJsonToDisk", ex);
        }
    }
}
