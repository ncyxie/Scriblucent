using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using Scriblucent.Services;
using Scriblucent.Services.AI;

namespace Scriblucent.Tests;

public sealed class BundledModelHostServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingUserModel_WorksWithReadOnlyInstallation(bool assetsExist)
    {
        using var storage = new ModelStorageFixture();
        if (assetsExist) Directory.CreateDirectory(storage.BundledDirectory);
        Directory.CreateDirectory(storage.UserModelsDirectory);
        await File.WriteAllBytesAsync(Path.Combine(storage.UserModelsDirectory, storage.Model.FileName), storage.Bytes);
        storage.ProtectInstallation();

        if (!assetsExist)
            Assert.Throws<UnauthorizedAccessException>(() => Directory.CreateDirectory(storage.BundledDirectory));
        await BundledModelHostService.EnsureModelAssetAsync(storage.BundledDirectory, storage.UserModelsDirectory,
            storage.Model, null, CancellationToken.None);

        Assert.Equal(assetsExist, Directory.Exists(storage.BundledDirectory));
        Assert.Empty(Directory.GetFiles(storage.ApplicationDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task BundledModel_IsReadWithoutWritingInstallation()
    {
        using var storage = new ModelStorageFixture();
        Directory.CreateDirectory(storage.BundledDirectory);
        var bundled = Path.Combine(storage.BundledDirectory, storage.Model.FileName);
        await File.WriteAllBytesAsync(bundled, storage.Bytes);
        storage.ProtectInstallation();
        Assert.Throws<UnauthorizedAccessException>(() => File.OpenWrite(bundled));

        await BundledModelHostService.EnsureModelAssetAsync(storage.BundledDirectory, storage.UserModelsDirectory,
            storage.Model, null, CancellationToken.None);

        Assert.Equal(storage.Bytes, await File.ReadAllBytesAsync(Path.Combine(storage.UserModelsDirectory, storage.Model.FileName)));
        Assert.Equal(storage.Bytes, await File.ReadAllBytesAsync(bundled));
    }

    [Fact]
    public async Task FirstDownload_PreparesOnlyUserStorageWithReadOnlyInstallation()
    {
        using var storage = new ModelStorageFixture();
        storage.ProtectInstallation();
        Assert.Throws<UnauthorizedAccessException>(() => Directory.CreateDirectory(storage.BundledDirectory));
        using var cancellation = new CancellationTokenSource();
        var progress = new StoragePreparationProgress(storage.UserModelsDirectory, cancellation);

        // Stop before any HTTP request: this regression is the storage preparation
        // which used to fail before download or cached-model verification could run.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BundledModelHostService.EnsureModelAssetAsync(
            storage.BundledDirectory, storage.UserModelsDirectory, storage.Model, progress, cancellation.Token));

        Assert.True(progress.Prepared);
        Assert.False(Directory.Exists(storage.BundledDirectory));
        Assert.Empty(Directory.GetFileSystemEntries(storage.ApplicationDirectory));
    }

    private sealed class StoragePreparationProgress(string directory, CancellationTokenSource cancellation)
        : IProgress<BundledModelHostService.FlashcardProgress>
    {
        public bool Prepared { get; private set; }
        public void Report(BundledModelHostService.FlashcardProgress value)
        {
            Assert.Equal(BundledModelHostService.ModelVerificationStatusKey, value.StatusKey);
            var staging = Path.Combine(directory, "storage-check.download");
            File.WriteAllText(staging, "Writable model staging"); File.Delete(staging);
            Prepared = true; cancellation.Cancel();
        }
    }

    private sealed class ModelStorageFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"scriblucent-model-storage-{Guid.NewGuid():N}");
        private byte[]? _originalSecurity;
        public string ApplicationDirectory => Path.Combine(_root, "ProgramFiles", "Scriblucent");
        public string BundledDirectory => Path.Combine(ApplicationDirectory, "Assets", "Models");
        public string UserModelsDirectory => Path.Combine(_root, "UserData", "Scriblucent", "Models");
        public byte[] Bytes { get; } = new byte[4096];
        public AiModelInfo Model { get; }
        public ModelStorageFixture()
        {
            Directory.CreateDirectory(ApplicationDirectory);
            "GGUF"u8.CopyTo(Bytes);
            Model = AiModelManifest.Instance.Default with
            {
                MinimumBytes = Bytes.Length, ExpectedSha256 = Convert.ToHexString(SHA256.HashData(Bytes)),
                Urls = []
            };
        }
        public void ProtectInstallation()
        {
            var directory = new DirectoryInfo(ApplicationDirectory);
            _originalSecurity = directory.GetAccessControl().GetSecurityDescriptorBinaryForm();
            var locked = directory.GetAccessControl();
            using var identity = WindowsIdentity.GetCurrent();
            locked.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.Write | FileSystemRights.Delete,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny));
            directory.SetAccessControl(locked);
        }
        public void Dispose()
        {
            if (_originalSecurity is not null)
            {
                var restored = new DirectorySecurity();
                restored.SetSecurityDescriptorBinaryForm(_originalSecurity, AccessControlSections.Access);
                new DirectoryInfo(ApplicationDirectory).SetAccessControl(restored);
            }
            Directory.Delete(_root, recursive: true);
        }
    }

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
