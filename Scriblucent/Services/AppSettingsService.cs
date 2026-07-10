using Scriblucent.Models;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Scriblucent.Services;

public static class AppSettingsService
{
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

    public static AppSettings Load()
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

                Save(defaultSettings);
                return defaultSettings;
            }

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            if (settings is null)
                return new AppSettings();

            var defaultTools = BundledModelHostService.BuildDefaultAiToolSettings();
            if (settings.AiTools is null || settings.AiTools.Count == 0)
                settings.AiTools = defaultTools;

            if (string.IsNullOrWhiteSpace(settings.FlashcardModelKey)
                || settings.AiTools.All(tool => tool.IsRemoved || !tool.IsEnabled || !string.Equals(tool.Key, settings.FlashcardModelKey, StringComparison.OrdinalIgnoreCase)))
            {
                settings.FlashcardModelKey = BundledModelHostService.GetEnabledFlashcardModelKeys(settings).FirstOrDefault()
                    ?? BundledModelHostService.GetRecommendedFlashcardModelKeyForCurrentMachine();
            }

            settings.FlashcardFlipDelayMilliseconds = AppSettings.NormalizeFlashcardFlipDelayMilliseconds(
                settings.FlashcardFlipDelayMilliseconds);

            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var opts = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(settings, opts);
            File.WriteAllText(GetSettingsFilePath(), json);
        }
        catch
        {
            // Ignore persistence errors.
        }
    }
}
