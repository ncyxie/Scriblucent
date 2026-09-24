using Scriblucent.Models;

namespace Scriblucent.Services.AI;

internal sealed class AiModelManifest
{
    public static readonly AiModelManifest Instance = new();

    internal const string BasicModelKey = "Qwen3.5-0.8B";
    internal const string CompactModelKey = "Qwen3.5-2B";
    internal const string RecommendedModelKey = "Qwen3.5-4B";
    internal const string AdvancedModelKey = "Qwen3.5-9B";
    private const string DefaultModelKey = BasicModelKey;

    private readonly AiModelInfo[] _models =
    [
        new AiModelInfo(
            Key: BasicModelKey,
            FileName: "Qwen3.5-0.8B-Q4_K_M.gguf",
            DisplayName: BasicModelKey,
            DescriptionResourceKey: "FlashcardModel08BDescription",
            DefaultDescription: "Basic",
            DownloadStatusKey: "ConvertToFlashcardsStatusDownloadingModel08B",
            MinimumBytes: 500L * 1024L * 1024L,
            MinimumSupportedMemoryBytes: 0,
            MinimumRecommendedMemoryBytes: 0,
            ExpectedSha256: "bd258782e35f7f458f8aced1adc053e6e92e89bc735ba3be89d38a06121dc517",
            [
                "https://huggingface.co/unsloth/Qwen3.5-0.8B-GGUF/resolve/main/Qwen3.5-0.8B-Q4_K_M.gguf",
                "https://huggingface.co/unsloth/Qwen3.5-0.8B-GGUF/resolve/main/Qwen3.5-0.8B-Q4_K_M.gguf?download=true",
                "https://hf-mirror.com/unsloth/Qwen3.5-0.8B-GGUF/resolve/main/Qwen3.5-0.8B-Q4_K_M.gguf?download=true"
            ]),
        new AiModelInfo(
            Key: CompactModelKey,
            FileName: "Qwen3.5-2B-Q5_K_M.gguf",
            DisplayName: CompactModelKey,
            DescriptionResourceKey: "FlashcardModel2BDescription",
            DefaultDescription: "Compact",
            DownloadStatusKey: "ConvertToFlashcardsStatusDownloadingModel2B",
            // Hugging Face LFS size for Qwen3.5-2B-Q5_K_M.gguf.
            MinimumBytes: 1435238656,
            MinimumSupportedMemoryBytes: 0,
            MinimumRecommendedMemoryBytes: 4L * 1024L * 1024L * 1024L,
            ExpectedSha256: "1885b3a9195f8cc09da9a7a7a75afdc1e8d5cbf9fc4a499c3961dddea37098ac",
            [
                "https://huggingface.co/unsloth/Qwen3.5-2B-GGUF/resolve/main/Qwen3.5-2B-Q5_K_M.gguf",
                "https://huggingface.co/unsloth/Qwen3.5-2B-GGUF/resolve/main/Qwen3.5-2B-Q5_K_M.gguf?download=true",
                "https://hf-mirror.com/unsloth/Qwen3.5-2B-GGUF/resolve/main/Qwen3.5-2B-Q5_K_M.gguf?download=true"
            ]),
        new AiModelInfo(
            Key: RecommendedModelKey,
            FileName: "Qwen3.5-4B-Q4_K_M.gguf",
            DisplayName: RecommendedModelKey,
            DescriptionResourceKey: "FlashcardModel4BDescription",
            DefaultDescription: "Recommended",
            DownloadStatusKey: "ConvertToFlashcardsStatusDownloadingModel4B",
            // Hugging Face LFS size for Qwen3.5-4B-Q4_K_M.gguf.
            MinimumBytes: 2740937888,
            MinimumSupportedMemoryBytes: 0,
            MinimumRecommendedMemoryBytes: 8L * 1024L * 1024L * 1024L,
            ExpectedSha256: "00fe7986ff5f6b463e62455821146049db6f9313603938a70800d1fb69ef11a4",
            [
                "https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/main/Qwen3.5-4B-Q4_K_M.gguf",
                "https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/main/Qwen3.5-4B-Q4_K_M.gguf?download=true",
                "https://hf-mirror.com/unsloth/Qwen3.5-4B-GGUF/resolve/main/Qwen3.5-4B-Q4_K_M.gguf?download=true"
            ]),
        new AiModelInfo(
            Key: AdvancedModelKey,
            FileName: "Qwen3.5-9B-Q4_K_M.gguf",
            DisplayName: AdvancedModelKey,
            DescriptionResourceKey: "FlashcardModel9BDescription",
            DefaultDescription: "Advanced",
            DownloadStatusKey: "ConvertToFlashcardsStatusDownloadingModel9B",
            // Hugging Face LFS size for Qwen3.5-9B-Q4_K_M.gguf.
            MinimumBytes: 5680522464,
            MinimumSupportedMemoryBytes: 8L * 1024L * 1024L * 1024L,
            MinimumRecommendedMemoryBytes: 12L * 1024L * 1024L * 1024L,
            ExpectedSha256: "03b74727a860a56338e042c4420bb3f04b2fec5734175f4cb9fa853daf52b7e8",
            [
                "https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/main/Qwen3.5-9B-Q4_K_M.gguf",
                "https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/main/Qwen3.5-9B-Q4_K_M.gguf?download=true",
                "https://hf-mirror.com/unsloth/Qwen3.5-9B-GGUF/resolve/main/Qwen3.5-9B-Q4_K_M.gguf?download=true"
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
        var memoryBytes = BundledModelHostService.GetTotalPhysicalMemoryBytesForCurrentMachine();
        var selectedModel = string.IsNullOrWhiteSpace(selectedKey) ? null : FindByKey(selectedKey);

        if (selectedModel is null
            || !enabledKeys.Any(key => string.Equals(key, selectedKey, StringComparison.OrdinalIgnoreCase))
            || !IsModelSupportedWithMemory(selectedModel.Key, memoryBytes))
        {
            selectedKey = enabledKeys
                .FirstOrDefault(key => IsModelSupportedWithMemory(key, memoryBytes))
                ?? Default.Key;
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

    public void MergeMissingCatalogTools(AppSettings settings)
    {
        settings.AiTools ??= new List<AiToolSettingsItem>();
        var existing = new HashSet<string>(
            settings.AiTools
                .Where(item => !string.IsNullOrWhiteSpace(item.Key))
                .Select(item => item.Key),
            StringComparer.OrdinalIgnoreCase);

        foreach (var model in _models)
        {
            if (existing.Contains(model.Key))
                continue;

            settings.AiTools.Add(new AiToolSettingsItem
            {
                Key = model.Key,
                IsEnabled = true
            });
        }
    }

    public IReadOnlyList<string> GetEnabledModelKeys(AppSettings? settings = null)
    {
        var source = settings ?? AppSettingsService.Load();
        MergeMissingCatalogTools(source);
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
        if (totalPhysicalMemoryBytes >= 8L * 1024L * 1024L * 1024L)
            return FindByKey(RecommendedModelKey) ?? Default;

        if (totalPhysicalMemoryBytes >= 4L * 1024L * 1024L * 1024L)
            return FindByKey(CompactModelKey) ?? Default;

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

    public long GetModelMinimumSupportedMemoryBytes(string key)
    {
        return FindByKey(key)?.MinimumSupportedMemoryBytes ?? 0;
    }

    public bool IsModelSupportedWithMemory(string key, long totalPhysicalMemoryBytes)
    {
        var minimumBytes = GetModelMinimumSupportedMemoryBytes(key);
        return totalPhysicalMemoryBytes <= 0 || totalPhysicalMemoryBytes >= minimumBytes;
    }
}
