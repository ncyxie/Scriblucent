using Scriblucent.Models;
using Scriblucent.Services.AI;

namespace Scriblucent.Tests;

public sealed class AiModelManifestTests
{
    [Fact]
    public void Models_ContainsFourQwenModels()
    {
        var manifest = AiModelManifest.Instance;

        Assert.Equal(4, manifest.Models.Count);
        Assert.Contains(manifest.Models, m => m.Key == "Qwen3.5-0.8B");
        Assert.Contains(manifest.Models, m => m.Key == "Qwen3.5-2B");
        Assert.Contains(manifest.Models, m => m.Key == "Qwen3.5-4B");
        Assert.Contains(manifest.Models, m => m.Key == "Qwen3.5-9B");
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
        Assert.Equal("Qwen3.5-2B-Q5_K_M.gguf", model.FileName);
        Assert.Equal("1885b3a9195f8cc09da9a7a7a75afdc1e8d5cbf9fc4a499c3961dddea37098ac", model.ExpectedSha256);
        Assert.Equal(1435238656, model.MinimumBytes);
    }

    [Fact]
    public void FourB_UsesStandardQ4KM()
    {
        var model = AiModelManifest.Instance.FindByKey("Qwen3.5-4B");

        Assert.NotNull(model);
        Assert.Equal("Qwen3.5-4B-Q4_K_M.gguf", model.FileName);
        Assert.Equal("00fe7986ff5f6b463e62455821146049db6f9313603938a70800d1fb69ef11a4", model.ExpectedSha256);
        Assert.Equal(2740937888, model.MinimumBytes);
    }

    [Fact]
    public void NineB_UsesStandardQ4KM()
    {
        var model = AiModelManifest.Instance.FindByKey("Qwen3.5-9B");

        Assert.NotNull(model);
        Assert.Equal("Qwen3.5-9B-Q4_K_M.gguf", model.FileName);
        Assert.Equal("03b74727a860a56338e042c4420bb3f04b2fec5734175f4cb9fa853daf52b7e8", model.ExpectedSha256);
        Assert.Equal(5680522464, model.MinimumBytes);
    }

    [Fact]
    public void AllModels_UseNonUnslothDynamicFiles()
    {
        foreach (var model in AiModelManifest.Instance.Models)
        {
            Assert.DoesNotContain("UD-", model.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.All(model.Urls, url => Assert.DoesNotContain("UD-", url, StringComparison.OrdinalIgnoreCase));
        }
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
    public void GetModelRequiredMemoryBytes_Returns4GBFor2B()
    {
        var mem = AiModelManifest.Instance.GetModelRequiredMemoryBytes("Qwen3.5-2B");

        Assert.Equal(4L * 1024L * 1024L * 1024L, mem);
    }

    [Fact]
    public void GetMachineRecommendedModel_LowMemory_Returns08B()
    {
        var model = AiModelManifest.Instance.GetMachineRecommendedModel(3L * 1024L * 1024L * 1024L);

        Assert.Equal("Qwen3.5-0.8B", model.Key);
    }

    [Fact]
    public void GetMachineRecommendedModel_MediumMemory_Returns2B()
    {
        var model = AiModelManifest.Instance.GetMachineRecommendedModel(6L * 1024L * 1024L * 1024L);

        Assert.Equal("Qwen3.5-2B", model.Key);
    }

    [Fact]
    public void GetMachineRecommendedModel_HighMemory_Returns4B()
    {
        var model = AiModelManifest.Instance.GetMachineRecommendedModel(8L * 1024L * 1024L * 1024L);

        Assert.Equal("Qwen3.5-4B", model.Key);
    }

    [Fact]
    public void GetMachineRecommendedModel_VeryHighMemory_StillReturns4B()
    {
        var model = AiModelManifest.Instance.GetMachineRecommendedModel(64L * 1024L * 1024L * 1024L);

        Assert.Equal("Qwen3.5-4B", model.Key);
    }

    [Theory]
        [InlineData(7, false)]
        [InlineData(8, true)]
        [InlineData(12, true)]
    public void IsModelSupportedWithMemory_Enforces9BMinimum(int memoryGb, bool expected)
    {
        var actual = AiModelManifest.Instance.IsModelSupportedWithMemory(
            "Qwen3.5-9B",
            memoryGb * 1024L * 1024L * 1024L);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void NineB_UsesSeparateWarningAndHardMemoryThresholds()
    {
        var model = Assert.IsType<AiModelInfo>(AiModelManifest.Instance.FindByKey("Qwen3.5-9B"));

        Assert.Equal(8L * 1024L * 1024L * 1024L, model.MinimumSupportedMemoryBytes);
        Assert.Equal(12L * 1024L * 1024L * 1024L, model.MinimumRecommendedMemoryBytes);
    }

    [Fact]
    public void GetEnabledModelKeys_IncludesCatalogModelsMissingFromSavedSettings()
    {
        var settings = new AppSettings
        {
            AiTools =
            [
                new AiToolSettingsItem { Key = "Qwen3.5-0.8B", IsEnabled = true },
                new AiToolSettingsItem { Key = "Qwen3.5-2B", IsEnabled = true },
                new AiToolSettingsItem { Key = "Qwen3.5-4B", IsEnabled = true }
            ]
        };

        var keys = AiModelManifest.Instance.GetEnabledModelKeys(settings);

        Assert.Contains("Qwen3.5-9B", keys);
        Assert.Contains(settings.AiTools, item => item.Key == "Qwen3.5-9B" && item.IsEnabled);
    }

    [Fact]
    public void GetEnabledModelKeys_RespectsExplicitlyDisabledCatalogModel()
    {
        var settings = new AppSettings
        {
            AiTools =
            [
                new AiToolSettingsItem { Key = "Qwen3.5-0.8B", IsEnabled = true },
                new AiToolSettingsItem { Key = "Qwen3.5-2B", IsEnabled = true },
                new AiToolSettingsItem { Key = "Qwen3.5-4B", IsEnabled = true },
                new AiToolSettingsItem { Key = "Qwen3.5-9B", IsEnabled = false }
            ]
        };

        var keys = AiModelManifest.Instance.GetEnabledModelKeys(settings);

        Assert.DoesNotContain("Qwen3.5-9B", keys);
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
