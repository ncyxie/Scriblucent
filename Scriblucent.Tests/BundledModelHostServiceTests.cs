using Scriblucent.Services;

namespace Scriblucent.Tests;

public sealed class BundledModelHostServiceTests
{
    [Theory]
    [InlineData("https://huggingface.co/unsloth/Qwen3.5-0.8B-GGUF/resolve/main/Qwen3.5-0.8B-Q4_K_M.gguf")]
    [InlineData("https://hf-mirror.com/unsloth/Qwen3.5-0.8B-GGUF/resolve/main/Qwen3.5-0.8B-Q4_K_M.gguf")]
    public void ConfiguredModelDownloadUrl_AllowsConfiguredSources(string url)
    {
        Assert.True(BundledModelHostService.IsSafeConfiguredModelDownloadUrl(url));
    }

    [Fact]
    public void ConfiguredModelDownloadUrl_RejectsHuggingFaceCdn()
    {
        const string cdnUrl = "https://us.aws.cdn.hf.co/xet-bridge-us/model.gguf";

        Assert.False(BundledModelHostService.IsSafeConfiguredModelDownloadUrl(cdnUrl));
    }

    [Fact]
    public void ModelRedirectUrl_AllowsHuggingFaceCdn()
    {
        const string cdnUrl = "https://us.aws.cdn.hf.co/xet-bridge-us/model.gguf";

        Assert.True(BundledModelHostService.IsSafeModelRedirectUrl(cdnUrl));
    }

    [Theory]
    [InlineData("https://evil-huggingface.co/model.gguf")]
    [InlineData("https://hf.co.attacker.example/model.gguf")]
    [InlineData("http://us.aws.cdn.hf.co/model.gguf")]
    public void ModelRedirectUrl_RejectsUntrustedOrInsecureHost(string url)
    {
        Assert.False(BundledModelHostService.IsSafeModelRedirectUrl(url));
    }
}
