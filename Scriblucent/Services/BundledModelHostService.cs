using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using Scriblucent.Models;
using Scriblucent.Services.AI;

namespace Scriblucent.Services;

public sealed class BundledModelHostService
{
    public sealed record FlashcardProgress(
        string StatusKey,
        int? Percent = null,
        int? GeneratedChars = null,
        int? ChunkIndex = null,
        int? ChunkCount = null);
    public sealed record FlashcardToolInfo(string Key, string DisplayName);

    private readonly SemaphoreSlim _sync = new(1, 1);
    private static readonly HttpClient DownloadClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(60)
    };

    public static BundledModelHostService Instance { get; } = new();

    private const string CacheFolderName = "AiCache";

    static BundledModelHostService()
    {
        DownloadClient.DefaultRequestHeaders.UserAgent.ParseAdd("Scriblucent/1.0 (+https://github.com/ncyxie/Scriblucent)");
        Scriblucent.Services.StartupProbe.Mark("modelType");
    }

    private static AiModelInfo GetSelectedModelInfo()
    {
        var settings = AppSettingsService.Load();
        return AiModelManifest.Instance.GetSelectedModel(settings);
    }

    public static IReadOnlyList<FlashcardToolInfo> GetSupportedFlashcardTools()
    {
        return AiModelManifest.Instance.GetSupportedTools();
    }

    public static List<AiToolSettingsItem> BuildDefaultAiToolSettings()
    {
        return AiModelManifest.Instance.BuildDefaultToolSettings();
    }

    public static IReadOnlyList<string> GetEnabledFlashcardModelKeys(AppSettings? settings = null)
    {
        return AiModelManifest.Instance.GetEnabledModelKeys(settings);
    }

    public static string GetRecommendedFlashcardModelKeyForCurrentMachine()
    {
        return AiModelManifest.Instance.GetMachineRecommendedModel().Key;
    }

    public static string GetRecommendedFlashcardModelKey(long totalPhysicalMemoryBytes)
    {
        return AiModelManifest.Instance.GetMachineRecommendedModel(totalPhysicalMemoryBytes).Key;
    }

    public static string GetFlashcardModelDisplayLabel(string key, bool includeWarningPrefix = false, bool isCompatible = true)
    {
        return AiModelManifest.Instance.GetModelDisplayLabel(key, includeWarningPrefix, isCompatible);
    }

    public static string GetFlashcardModelDisplayName(string key)
    {
        return AiModelManifest.Instance.GetModelDisplayName(key);
    }

    public static string GetFlashcardModelDescription(string key)
    {
        return AiModelManifest.Instance.GetModelDescription(key);
    }

    public static long GetFlashcardModelRequiredMemoryBytes(string key)
    {
        return AiModelManifest.Instance.GetModelRequiredMemoryBytes(key);
    }

    public static bool IsFlashcardModelCompatibleWithMemory(string key, long totalPhysicalMemoryBytes)
    {
        return totalPhysicalMemoryBytes >= GetFlashcardModelRequiredMemoryBytes(key);
    }

    public static long GetTotalPhysicalMemoryBytesForCurrentMachine()
    {
        try
        {
            var memoryStatus = new MEMORYSTATUSEX();
            memoryStatus.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
            if (GlobalMemoryStatusEx(ref memoryStatus))
                return (long)memoryStatus.ullTotalPhys;
        }
        catch
        {
        }

        return 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public string GetSelectedModelDisplayName()
    {
        return GetSelectedModelInfo().DisplayName;
    }

    internal AiChunkingModelProfile GetSelectedChunkingProfile(string? modelKey = null)
    {
        var selectedKey = modelKey ?? GetSelectedModelInfo().Key;
        if (selectedKey.Contains("0.8B", StringComparison.OrdinalIgnoreCase))
            return AiChunkingModelProfile.Small;

        if (selectedKey.Contains("2B", StringComparison.OrdinalIgnoreCase))
            return AiChunkingModelProfile.Medium;

        return AiChunkingModelProfile.Large;
    }

    private static async Task EnsureModelAssetAsync(
        string modelsAssetsDir,
        string modelsCacheDir,
        AiModelInfo selectedModel,
        IProgress<FlashcardProgress>? progress,
        CancellationToken cancellationToken)
    {
        var cacheModelPath = Path.Combine(modelsCacheDir, selectedModel.FileName);
        if (IsModelFileValid(cacheModelPath, selectedModel))
            return;

        var bundledModelPath = Path.Combine(modelsAssetsDir, selectedModel.FileName);
        if (IsModelFileValid(bundledModelPath, selectedModel))
        {
            CopyToCache(bundledModelPath, cacheModelPath);
            if (IsModelFileValid(cacheModelPath, selectedModel))
                return;
        }

        progress?.Report(new FlashcardProgress(selectedModel.DownloadStatusKey, 0));
        await DownloadModelAsync(cacheModelPath, selectedModel, progress, cancellationToken);
    }

    private static async Task DownloadModelAsync(
        string modelPath,
        AiModelInfo model,
        IProgress<FlashcardProgress>? progress,
        CancellationToken cancellationToken)
    {
        var urls = ResolveModelDownloadUrls(model.Urls);

        Exception? lastError = null;
        foreach (var modelUrl in urls)
        {
            var tempPath = $"{modelPath}.{Guid.NewGuid():N}.download";
            try
            {
                await DownloadFileWithRetryAsync(modelUrl, tempPath, model.DownloadStatusKey, progress, cancellationToken);
                ValidateDownloadedModelFile(tempPath, model);
                File.Move(tempPath, modelPath, overwrite: true);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                TryDeleteFile(tempPath);
            }
        }
        var attempted = string.Join(", ", urls);
        throw new InvalidOperationException(
            $"Failed to download bundled Qwen model automatically. Attempted URLs: {attempted}. Last error: {lastError?.Message}",
            lastError);
    }

    private static IReadOnlyList<string> ResolveModelDownloadUrls(IEnumerable<string> modelUrls)
    {
        var safeDefaults = modelUrls
            .Where(IsSafeModelDownloadUrl)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (safeDefaults.Length == 0)
            throw new InvalidOperationException("No allowlisted model download URLs are configured for the selected model.");

        var overrideUrl = Environment.GetEnvironmentVariable("SCRIBLUCENT_QWEN_MODEL_URL");
        if (string.IsNullOrWhiteSpace(overrideUrl))
            return safeDefaults;

#if DEBUG
        if (Uri.TryCreate(overrideUrl, UriKind.Absolute, out var debugUri)
            && (string.Equals(debugUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.Equals(debugUri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(debugUri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)))
        {
            return [overrideUrl];
        }

        throw new InvalidOperationException("SCRIBLUCENT_QWEN_MODEL_URL must be an HTTPS URL (or localhost) in Debug builds.");
#else
        return safeDefaults;
#endif
    }

    private static bool IsSafeModelDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

#if DEBUG
        // In DEBUG, SCRIBLUCENT_QWEN_MODEL_URL may be http://localhost for local testing (see ResolveModelDownloadUrls)
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
            return true;
#endif

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        var host = uri.Host;
        // Allow huggingface.co and any subdomain (cdn-lfs.huggingface.co, cas-bridge.xethub.huggingface.co)
        // but not evil-huggingface.co (must be exact or dot-prefix)
        if (string.Equals(host, "huggingface.co", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(host, "hf-mirror.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".hf-mirror.com", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static async Task DownloadFileWithRetryAsync(
        string url,
        string destinationPath,
        string statusKey,
        IProgress<FlashcardProgress>? progress,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await DownloadFileAsync(url, destinationPath, statusKey, progress, cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < 3)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException($"Failed to download file from '{url}'.", lastError);
    }

    private static bool IsRedirectStatusCode(System.Net.HttpStatusCode statusCode)
    {
        return statusCode == System.Net.HttpStatusCode.MovedPermanently // 301
            || statusCode == System.Net.HttpStatusCode.Found // 302
            || statusCode == System.Net.HttpStatusCode.SeeOther // 303
            || statusCode == System.Net.HttpStatusCode.TemporaryRedirect // 307
            || (int)statusCode == 308; // PermanentRedirect
    }

    private static async Task DownloadFileAsync(
        string url,
        string destinationPath,
        string statusKey,
        IProgress<FlashcardProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Manual redirect handling with allowlist re-validation on every hop.
        // Default HttpClient would follow 302 from huggingface.co -> attacker.com silently.
        var currentUrl = url;
        for (var redirectCount = 0; redirectCount < 5; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

            using var response = await DownloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (IsRedirectStatusCode(response.StatusCode))
            {
                var location = response.Headers.Location;
                if (location is null)
                    throw new InvalidOperationException($"Redirect from '{currentUrl}' missing Location header.");

                var nextUrl = location.IsAbsoluteUri
                    ? location.ToString()
                    : new Uri(new Uri(currentUrl), location).ToString();

                if (!IsSafeModelDownloadUrl(nextUrl))
                    throw new InvalidOperationException($"Redirect to non-allowlisted URL blocked: '{nextUrl}' (from '{currentUrl}').");

                currentUrl = nextUrl;
                continue;
            }

            response.EnsureSuccessStatusCode();

            // Defense-in-depth: final URL must still be allowlisted (initial url was pre-validated in ResolveModelDownloadUrls, but double-check)
            if (!IsSafeModelDownloadUrl(currentUrl))
                throw new InvalidOperationException($"Download URL not allowlisted: '{currentUrl}'.");

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var totalBytes = response.Content.Headers.ContentLength;
            var buffer = new byte[81920];
            long readBytes = 0;
            var lastPercent = -1;

            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                readBytes += read;

                if (totalBytes.HasValue && totalBytes.Value > 0)
                {
                    var percent = (int)Math.Clamp((readBytes * 100L) / totalBytes.Value, 0, 100);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Report(new FlashcardProgress(statusKey, percent));
                    }
                }
            }

            if (lastPercent < 100)
                progress?.Report(new FlashcardProgress(statusKey, 100));

            return;
        }

        throw new InvalidOperationException($"Too many redirects for '{url}'.");
    }


    private static string GetCacheRootDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Scriblucent",
            CacheFolderName);
    }

    public static string GetModelsDirectoryPath()
    {
        return Path.Combine(GetCacheRootDirectory(), "models");
    }

    public static string? GetModelFilePath(string key)
    {
        var model = AiModelManifest.Instance.FindByKey(key);
        if (model is null)
            return null;

        return Path.Combine(GetModelsDirectoryPath(), model.FileName);
    }

    public static bool IsModelDownloaded(string key)
    {
        var path = GetModelFilePath(key);
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    public static void DeleteModelArtifacts(string key)
    {
        var model = AiModelManifest.Instance.FindByKey(key);
        if (model is null)
            return;

        var cachePath = Path.Combine(GetModelsDirectoryPath(), model.FileName);
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Models", model.FileName);

        TryDeleteFile(cachePath);
        TryDeleteFile(bundledPath);

        try
        {
            var modelsDir = GetModelsDirectoryPath();
            if (Directory.Exists(modelsDir))
            {
                foreach (var tempFile in Directory.EnumerateFiles(modelsDir, $"{model.FileName}.*.download", SearchOption.TopDirectoryOnly))
                    TryDeleteFile(tempFile);
            }
        }
        catch
        {
        }
    }

    public async Task EnsureModelAvailableAsync(
        string key,
        IProgress<FlashcardProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Model key is required.", nameof(key));

        var selectedModel = AiModelManifest.Instance.FindByKey(key);
        if (selectedModel is null)
            throw new InvalidOperationException($"Unsupported model key '{key}'.");

        await _sync.WaitAsync(cancellationToken);
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var modelsAssetsDir = Path.Combine(baseDir, "Assets", "Models");
            var modelsCacheDir = GetModelsDirectoryPath();

            Directory.CreateDirectory(modelsAssetsDir);
            Directory.CreateDirectory(modelsCacheDir);

            await EnsureModelAssetAsync(modelsAssetsDir, modelsCacheDir, selectedModel, progress, cancellationToken);
        }
        finally
        {
            _sync.Release();
        }
    }

    private static void CopyToCache(string sourcePath, string cachePath)
    {
        try
        {
            var cacheDir = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(cacheDir))
                Directory.CreateDirectory(cacheDir);

            File.Copy(sourcePath, cachePath, overwrite: true);
        }
        catch
        {
        }
    }

    private static void ValidateDownloadedModelFile(string filePath, AiModelInfo? model = null)
    {
        var fileInfo = new FileInfo(filePath);
        var minBytes = model?.MinimumBytes ?? (1024L * 1024L);
        if (!fileInfo.Exists || fileInfo.Length < minBytes)
            throw new InvalidDataException("Downloaded Qwen model file is invalid or too small.");

        using var stream = File.OpenRead(filePath);
        Span<byte> header = stackalloc byte[4];
        var bytesRead = stream.Read(header);
        if (bytesRead < 4 || header[0] != (byte)'G' || header[1] != (byte)'G' || header[2] != (byte)'U' || header[3] != (byte)'F')
            throw new InvalidDataException("Downloaded file is not a valid GGUF model.");

        if (!string.IsNullOrWhiteSpace(model?.ExpectedSha256))
        {
            stream.Position = 0;
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(stream);
            var hex = Convert.ToHexString(hash);
            if (!string.Equals(hex, model.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Model file integrity check failed. Expected SHA-256: {model.ExpectedSha256}, Got: {hex}");
        }
    }

    private static bool IsModelFileValid(string filePath, AiModelInfo? model = null)
    {
        try
        {
            ValidateDownloadedModelFile(filePath, model);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

}
