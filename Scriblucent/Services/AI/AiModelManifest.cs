using Scriblucent.Models;

namespace Scriblucent.Services.AI;

internal sealed class AiModelManifest
{
    public static readonly AiModelManifest Instance = new();

    private const string DefaultModelKey = "Qwen3.5-0.8B";

    private readonly AiModelInfo[] _models =
    [
        new AiModelInfo(
            Key: "Qwen3.5-0.8B",
            FileName: "Qwen3.5-0.8B-Q4_K_M.gguf",
            DisplayName: "Qwen3.5-0.8B",
            DescriptionResourceKey: "FlashcardModel08BDescription",
            DefaultDescription: "faster, light",
            DownloadStatusKey: "ConvertToFlashcardsStatusDownloadingModel08B",
            MinimumBytes: 500L * 1024L * 1024L,
            MinimumRecommendedMemoryBytes: 0,
            ExpectedSha256: "bd258782e35f7f458f8aced1adc053e6e92e89bc735ba3be89d38a06121dc517",
            [
                "https://huggingface.co/unsloth/Qwen3.5-0.8B-GGUF/resolve/main/Qwen3.5-0.8B-Q4_K_M.gguf",
                "https://huggingface.co/unsloth/Qwen3.5-0.8B-GGUF/resolve/main/Qwen3.5-0.8B-Q4_K_M.gguf?download=true",
                "https://hf-mirror.com/unsloth/Qwen3.5-0.8B-GGUF/resolve/main/Qwen3.5-0.8B-Q4_K_M.gguf?download=true"
            ]),
        new AiModelInfo(
            Key: "Qwen3.5-2B",
            FileName: "Qwen3.5-2B-Q4_K_M.gguf",
            DisplayName: "Qwen3.5-2B",
            DescriptionResourceKey: "FlashcardModel2BDescription",
            DefaultDescription: "balanced, better quality",
            DownloadStatusKey: "ConvertToFlashcardsStatusDownloadingModel2B",
            MinimumBytes: 1200L * 1024L * 1024L,
            MinimumRecommendedMemoryBytes: 6L * 1024L * 1024L * 1024L,
            ExpectedSha256: "aaf42c8b7c3cab2bf3d69c355048d4a0ee9973d48f16c731c0520ee914699223",
            [
                "https://huggingface.co/unsloth/Qwen3.5-2B-GGUF/resolve/main/Qwen3.5-2B-Q4_K_M.gguf",
                "https://huggingface.co/unsloth/Qwen3.5-2B-GGUF/resolve/main/Qwen3.5-2B-Q4_K_M.gguf?download=true",
                "https://hf-mirror.com/unsloth/Qwen3.5-2B-GGUF/resolve/main/Qwen3.5-2B-Q4_K_M.gguf?download=true"
            ]),
        new AiModelInfo(
            Key: "Qwen3.5-4B",
            FileName: "Qwen3.5-4B-Q4_K_M.gguf",
            DisplayName: "Qwen3.5-4B",
            DescriptionResourceKey: "FlashcardModel4BDescription",
            DefaultDescription: "more quality, slower",
            DownloadStatusKey: "ConvertToFlashcardsStatusDownloadingModel4B",
            MinimumBytes: 2600L * 1024L * 1024L,
            MinimumRecommendedMemoryBytes: 12L * 1024L * 1024L * 1024L,
            ExpectedSha256: "00fe7986ff5f6b463e62455821146049db6f9313603938a70800d1fb69ef11a4",
            [
                "https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/main/Qwen3.5-4B-Q4_K_M.gguf",
                "https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/main/Qwen3.5-4B-Q4_K_M.gguf?download=true",
                "https://hf-mirror.com/unsloth/Qwen3.5-4B-GGUF/resolve/main/Qwen3.5-4B-Q4_K_M.gguf?download=true"
            ])
    ];

    public IReadOnlyList<AiModelInfo> Models => _models;

    public AiModelInfo Default => _models[0];

    public AiModelInfo? FindByKey(string key)
    {
        return _models.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    public AiModelInfo GetSelectedModel(AppSettings? settings = null)
    {
        var source = settings ?? AppSettingsService.Load();
        var enabledKeys = GetEnabledModelKeys(source);
        var selectedKey = source.FlashcardModelKey;

        if (string.IsNullOrWhiteSpace(selectedKey)
            || !enabledKeys.Any(key => string.Equals(key, selectedKey, StringComparison.OrdinalIgnoreCase)))
        {
            selectedKey = enabledKeys[0];
        }

        return FindByKey(selectedKey) ?? Default;
    }

    public IReadOnlyList<BundledModelHostService.FlashcardToolInfo> GetSupportedTools()
    {
        return _models
            .Select(model => new BundledModelHostService.FlashcardToolInfo(model.Key, model.DisplayName))
            .ToArray();
    }

    public List<AiToolSettingsItem> BuildDefaultToolSettings()
    {
        return _models
            .Select(model => new AiToolSettingsItem
            {
                Key = model.Key,
                IsEnabled = true
            })
            .ToList();
    }

    public IReadOnlyList<string> GetEnabledModelKeys(AppSettings? settings = null)
    {
        var source = settings ?? AppSettingsService.Load();
        var supportedByKey = _models
            .ToDictionary(model => model.Key, StringComparer.OrdinalIgnoreCase);

        var configured = (source.AiTools ?? new List<AiToolSettingsItem>())
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .Where(item => !item.IsRemoved)
            .Where(item => item.IsEnabled)
            .Select(item => item.Key)
            .Where(key => supportedByKey.ContainsKey(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (configured.Count == 0)
            configured = BuildDefaultToolSettings().Select(item => item.Key).ToList();

        return configured;
    }

    public AiModelInfo GetMachineRecommendedModel()
    {
        return GetMachineRecommendedModel(BundledModelHostService.GetTotalPhysicalMemoryBytesForCurrentMachine());
    }

    public AiModelInfo GetMachineRecommendedModel(long totalPhysicalMemoryBytes)
    {
        if (totalPhysicalMemoryBytes >= 12L * 1024L * 1024L * 1024L)
            return FindByKey("Qwen3.5-4B") ?? Default;

        if (totalPhysicalMemoryBytes >= 6L * 1024L * 1024L * 1024L)
            return FindByKey("Qwen3.5-2B") ?? Default;

        return Default;
    }

    public string GetModelDisplayName(string key)
    {
        return FindByKey(key)?.DisplayName ?? Default.DisplayName;
    }

    public string GetModelDescription(string key)
    {
        var model = FindByKey(key) ?? Default;
        var localized = Localization.LocalizationService.GetString(model.DescriptionResourceKey);
        if (!string.IsNullOrWhiteSpace(localized) && !string.Equals(localized, model.DescriptionResourceKey, StringComparison.Ordinal))
            return localized;

        return model.DefaultDescription;
    }

    public string GetModelDisplayLabel(string key, bool includeWarningPrefix = false, bool isCompatible = true)
    {
        var model = FindByKey(key) ?? Default;
        var label = $"{model.DisplayName} - {GetModelDescription(key)}";
        if (includeWarningPrefix && !isCompatible)
            label = $"⚠️ {label}";

        return label;
    }

    public long GetModelRequiredMemoryBytes(string key)
    {
        return FindByKey(key)?.MinimumRecommendedMemoryBytes ?? 0;
    }
}
