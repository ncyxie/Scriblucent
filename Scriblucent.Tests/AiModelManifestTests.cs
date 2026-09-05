using Scriblucent.Services.AI;

namespace Scriblucent.Tests;

public sealed class AiModelManifestTests
{
    [Fact]
    public void Models_ContainsThreeQwenModels()
    {
        var manifest = AiModelManifest.Instance;

        Assert.Equal(3, manifest.Models.Count);
        Assert.Contains(manifest.Models, m => m.Key == "Qwen3.5-0.8B");
        Assert.Contains(manifest.Models, m => m.Key == "Qwen3.5-2B");
        Assert.Contains(manifest.Models, m => m.Key == "Qwen3.5-4B");
    }

    [Fact]
    public void Default_IsQwen08B()
    {
        Assert.Equal("Qwen3.5-0.8B", AiModelManifest.Instance.Default.Key);
    }

    [Fact]
    public void FindByKey_ReturnsModelForValidKey()
    {
        var model = AiModelManifest.Instance.FindByKey("Qwen3.5-2B");

        Assert.NotNull(model);
        Assert.Equal("Qwen3.5-2B", model.Key);
        Assert.Equal("Qwen3.5-2B-Q4_K_M.gguf", model.FileName);
    }

    [Fact]
    public void FindByKey_IsCaseInsensitive()
    {
        var model = AiModelManifest.Instance.FindByKey("qwen3.5-0.8b");

        Assert.NotNull(model);
        Assert.Equal("Qwen3.5-0.8B", model.Key);
    }

    [Fact]
    public void FindByKey_ReturnsNullForUnknownKey()
    {
        Assert.Null(AiModelManifest.Instance.FindByKey("NonExistent"));
    }

    [Fact]
    public void AllModels_HaveValidHttpsUrls()
    {
        foreach (var model in AiModelManifest.Instance.Models)
        {
            Assert.False(string.IsNullOrWhiteSpace(model.FileName), $"Model {model.Key} has empty FileName");
            Assert.True(model.Urls.Length > 0, $"Model {model.Key} has no URLs");

            foreach (var url in model.Urls)
            {
                Assert.StartsWith("https://", url, StringComparison.OrdinalIgnoreCase);
                Assert.True(
                    url.Contains("huggingface.co", StringComparison.OrdinalIgnoreCase)
                    || url.Contains("hf-mirror.com", StringComparison.OrdinalIgnoreCase),
                    $"Model {model.Key} has URL from unexpected domain: {url}");
            }
        }
    }

    [Fact]
    public void AllModels_HavePositiveMinimumBytes()
    {
        foreach (var model in AiModelManifest.Instance.Models)
        {
            Assert.True(model.MinimumBytes > 0, $"Model {model.Key} has non-positive MinimumBytes");
        }
    }

    [Fact]
    public void AllModels_HaveNonEmptyDisplayNames()
    {
        foreach (var model in AiModelManifest.Instance.Models)
        {
            Assert.False(string.IsNullOrWhiteSpace(model.DisplayName), $"Model {model.Key} has empty DisplayName");
        }
    }

    [Fact]
    public void AllModels_HaveGgufFileExtension()
    {
        foreach (var model in AiModelManifest.Instance.Models)
        {
            Assert.EndsWith(".gguf", model.FileName, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void GetModelDisplayName_ReturnsDisplayNameForKnownKey()
    {
        var name = AiModelManifest.Instance.GetModelDisplayName("Qwen3.5-4B");

        Assert.Equal("Qwen3.5-4B", name);
    }

    [Fact]
    public void GetModelDisplayName_FallsBackToDefaultForUnknownKey()
    {
        var name = AiModelManifest.Instance.GetModelDisplayName("Unknown");

        Assert.Equal(AiModelManifest.Instance.Default.DisplayName, name);
    }

    [Fact]
    public void GetModelRequiredMemoryBytes_ReturnsZeroFor08B()
    {
        var mem = AiModelManifest.Instance.GetModelRequiredMemoryBytes("Qwen3.5-0.8B");

        Assert.Equal(0, mem);
    }

    [Fact]
    public void GetModelRequiredMemoryBytes_Returns6GBFor2B()
    {
        var mem = AiModelManifest.Instance.GetModelRequiredMemoryBytes("Qwen3.5-2B");

        Assert.Equal(6L * 1024L * 1024L * 1024L, mem);
    }

    [Fact]
    public void GetMachineRecommendedModel_LowMemory_Returns08B()
    {
        var model = AiModelManifest.Instance.GetMachineRecommendedModel(4L * 1024L * 1024L * 1024L);

        Assert.Equal("Qwen3.5-0.8B", model.Key);
    }

    [Fact]
    public void GetMachineRecommendedModel_MediumMemory_Returns2B()
    {
        var model = AiModelManifest.Instance.GetMachineRecommendedModel(8L * 1024L * 1024L * 1024L);

        Assert.Equal("Qwen3.5-2B", model.Key);
    }

    [Fact]
    public void GetMachineRecommendedModel_HighMemory_Returns4B()
    {
        var model = AiModelManifest.Instance.GetMachineRecommendedModel(16L * 1024L * 1024L * 1024L);

        Assert.Equal("Qwen3.5-4B", model.Key);
    }

    [Fact]
    public void BuildDefaultToolSettings_ReturnsOnePerModel()
    {
        var settings = AiModelManifest.Instance.BuildDefaultToolSettings();

        Assert.Equal(AiModelManifest.Instance.Models.Count, settings.Count);
        Assert.All(settings, s => Assert.True(s.IsEnabled));
    }

    [Fact]
    public void GetSupportedTools_ReturnsOnePerModel()
    {
        var tools = AiModelManifest.Instance.GetSupportedTools();

        Assert.Equal(AiModelManifest.Instance.Models.Count, tools.Count);
    }
}
