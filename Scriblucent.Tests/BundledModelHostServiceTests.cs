using System.Security.Cryptography;
using Scriblucent.Services;
using Scriblucent.Services.AI;

namespace Scriblucent.Tests;

public sealed class BundledModelHostServiceTests
{
    [Fact]
    public async Task ValidateDownloadedModelFileAsync_VerifiesCompleteFileAndRejectsChanges()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scriblucent-model-check-{Guid.NewGuid():N}.gguf");
        var bytes = new byte[4096];
        "GGUF"u8.CopyTo(bytes);
        bytes[^1] = 42;
        var model = AiModelManifest.Instance.Default with
        {
            MinimumBytes = bytes.Length,
            ExpectedSha256 = Convert.ToHexString(SHA256.HashData(bytes))
        };

        try
        {
            await File.WriteAllBytesAsync(path, bytes);
            await BundledModelHostService.ValidateDownloadedModelFileAsync(path, model);

            bytes[^1]++;
            await File.WriteAllBytesAsync(path, bytes);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                BundledModelHostService.ValidateDownloadedModelFileAsync(path, model));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ValidateDownloadedModelFileAsync_HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BundledModelHostService.ValidateDownloadedModelFileAsync(
                "unused.gguf", AiModelManifest.Instance.Default, cancellation.Token));
    }

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
