using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Scriblucent.Localization;
using Scriblucent.Models;

namespace Scriblucent.Services;

public sealed class BundledModelHostService
{
    public sealed record FlashcardProgress(
        string StatusKey,
        int? Percent = null,
        int? GeneratedChars = null,
        int? ChunkIndex = null,
        int? ChunkCount = null);
    public sealed record RuntimeUpdateResult(
        string CurrentVersion,
        string LatestVersion,
        bool IsUpdateAvailable,
        bool WasUpdated);
    public sealed record FlashcardToolInfo(string Key, string DisplayName);

    private sealed record ModelInfo(
        string Key,
        string FileName,
        string DisplayName,
        string DescriptionResourceKey,
        string DefaultDescription,
        string DownloadStatusKey,
        long MinimumBytes,
        long MinimumRecommendedMemoryBytes,
        string[] Urls);

    private sealed record RuntimeReleaseInfo(
        string TagName,
        string HtmlUrl,
        DateTimeOffset? PublishedAt,
        IReadOnlyList<string> Urls);

    private sealed record RuntimeVersionMetadata(
        string TagName,
        string? ReleaseUrl,
        DateTimeOffset? PublishedAt,
        DateTimeOffset InstalledAt);

    private readonly SemaphoreSlim _sync = new(1, 1);
    private static readonly HttpClient DownloadClient = new()
    {
        Timeout = TimeSpan.FromMinutes(60)
    };

    public static BundledModelHostService Instance { get; } = new();

    private const int MaxOutputChars = 20000;
    private const int MaxCapturedStdoutChars = 250_000;
    private const int MaxCapturedStderrChars = 120_000;
    private static readonly TimeSpan PromptFileRetention = TimeSpan.FromHours(24);
    private static readonly TimeSpan InferenceTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InferenceInactivityTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan InferenceNoOutputTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan InferenceOutputStallTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan InferenceLoopCheckInterval = TimeSpan.FromSeconds(2);
    private const int MinOutputCharsForLoopDetection = 1600;
    private const int MinContextTokens = 4096;
    private const int MaxContextTokens = 16384;
    private const int ContextSafetyTokens = 512;
    private const int ContextTokenGranularity = 1024;

    private const string RuntimeExeName = "llama-completion.exe";
    private const string RuntimeArchivePrefix = "scriblucent-model-runtime";
    private const string RuntimeVersionMetadataFileName = "runtime-release.json";
    private const string DefaultModelKey = "Qwen3.5-0.8B";
    private static readonly string[] RuntimeReleaseApiUrls =
    [
        "https://api.github.com/repos/ggml-org/llama.cpp/releases/latest",
        "https://api.github.com/repos/ggerganov/llama.cpp/releases/latest"
    ];

    private static readonly ModelInfo[] SupportedModels =
    [
        new(
            "Qwen3.5-0.8B",
            "Qwen3.5-0.8B-Q4_K_M.gguf",
            "Qwen3.5-0.8B",
            "FlashcardModel08BDescription",
            "faster, light",
            "ConvertToFlashcardsStatusDownloadingModel08B",
            100L * 1024L * 1024L,
            0,
            [
                "https://huggingface.co/unsloth/Qwen3.5-0.8B-GGUF/resolve/main/Qwen3.5-0.8B-Q4_K_M.gguf",
                "https://huggingface.co/unsloth/Qwen3.5-0.8B-GGUF/resolve/main/Qwen3.5-0.8B-Q4_K_M.gguf?download=true",
                "https://hf-mirror.com/unsloth/Qwen3.5-0.8B-GGUF/resolve/main/Qwen3.5-0.8B-Q4_K_M.gguf?download=true"
            ]),
        new(
            "Qwen3.5-2B",
            "Qwen3.5-2B-Q4_K_M.gguf",
            "Qwen3.5-2B",
            "FlashcardModel2BDescription",
            "balanced, better quality",
            "ConvertToFlashcardsStatusDownloadingModel2B",
            300L * 1024L * 1024L,
            6L * 1024L * 1024L * 1024L,
            [
                "https://huggingface.co/unsloth/Qwen3.5-2B-GGUF/resolve/main/Qwen3.5-2B-Q4_K_M.gguf",
                "https://huggingface.co/unsloth/Qwen3.5-2B-GGUF/resolve/main/Qwen3.5-2B-Q4_K_M.gguf?download=true",
                "https://hf-mirror.com/unsloth/Qwen3.5-2B-GGUF/resolve/main/Qwen3.5-2B-Q4_K_M.gguf?download=true"
            ]),
        new(
            "Qwen3.5-4B",
            "Qwen3.5-4B-Q4_K_M.gguf",
            "Qwen3.5-4B",
            "FlashcardModel4BDescription",
            "more quality, slower",
            "ConvertToFlashcardsStatusDownloadingModel4B",
            600L * 1024L * 1024L,
            12L * 1024L * 1024L * 1024L,
            [
                "https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/main/Qwen3.5-4B-Q4_K_M.gguf",
                "https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/main/Qwen3.5-4B-Q4_K_M.gguf?download=true",
                "https://hf-mirror.com/unsloth/Qwen3.5-4B-GGUF/resolve/main/Qwen3.5-4B-Q4_K_M.gguf?download=true"
            ])
    ];

    private const string CacheFolderName = "AiCache";

    static BundledModelHostService()
    {
        DownloadClient.DefaultRequestHeaders.UserAgent.ParseAdd("Scriblucent/1.0 (+https://github.com/ncyxie/Scriblucent)");
    }

    public void Stop() { }

    public async Task<string> CompleteAsync(
        string prompt,
        int nPredict,
        double temperature,
        IProgress<FlashcardProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return string.Empty;

        await _sync.WaitAsync(cancellationToken);
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var runtimeAssetsDir = Path.Combine(baseDir, "Assets", "ModelRuntime");
            var modelsAssetsDir = Path.Combine(baseDir, "Assets", "Models");
            var runtimeCacheDir = Path.Combine(GetCacheRootDirectory(), "runtime");
            var modelsCacheDir = Path.Combine(GetCacheRootDirectory(), "models");
            Directory.CreateDirectory(runtimeAssetsDir);
            Directory.CreateDirectory(modelsAssetsDir);
            Directory.CreateDirectory(runtimeCacheDir);
            Directory.CreateDirectory(modelsCacheDir);

            progress?.Report(new FlashcardProgress("ConvertToFlashcardsStatusPreparingAssets"));
            var selectedModel = GetSelectedModelInfo();
            await EnsureBundledAssetsAsync(runtimeAssetsDir, modelsAssetsDir, runtimeCacheDir, modelsCacheDir, selectedModel, progress, cancellationToken);

            var cliPath = Path.Combine(runtimeCacheDir, RuntimeExeName);
            var modelPath = Path.Combine(modelsCacheDir, selectedModel.FileName);

            if (!File.Exists(cliPath))
                throw new FileNotFoundException("Bundled model runtime was not found.", cliPath);
            if (!File.Exists(modelPath))
                throw new FileNotFoundException($"Bundled Qwen model file '{selectedModel.DisplayName}' was not found.", modelPath);

            var promptDir = Path.Combine(GetCacheRootDirectory(), "temp");
            Directory.CreateDirectory(promptDir);
            CleanupOldPromptFiles(promptDir);
            var promptFilePath = Path.Combine(promptDir, $"prompt-{Guid.NewGuid():N}.txt");
            try
            {
                progress?.Report(new FlashcardProgress("ConvertToFlashcardsStatusProcessing"));
                await File.WriteAllTextAsync(promptFilePath, prompt, Encoding.UTF8, cancellationToken);

                var threadCount = Math.Max(1, Environment.ProcessorCount - 1);
                var temperatureText = temperature.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var contextTokens = CalculateContextTokens(prompt, nPredict);

                // Qwen instruct GGUF models follow format instructions more reliably through their chat template.
                var commandArgs = new[]
                {
                    "-m", modelPath,
                    "-f", promptFilePath,
                    "-n", nPredict.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--temp", temperatureText,
                    "--top-p", "0.95",
                    "--top-k", "40",
                    "--presence-penalty", "0.0",
                    "--repeat-penalty", "1.08",
                    "-c", contextTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-t", threadCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--no-display-prompt",
                    "-st",
                    "--simple-io"
                };

                var args = string.Join(" ", commandArgs.Select(QuoteArgumentForLog));

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = cliPath,
                        WorkingDirectory = runtimeCacheDir,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    }
                };

                foreach (var arg in commandArgs)
                    process.StartInfo.ArgumentList.Add(arg);

                if (!process.Start())
                    throw new InvalidOperationException("Failed to start bundled model runtime process.");

                try
                {
                    try { process.StandardInput.Close(); } catch { }

                    var outputBuilder = new StringBuilder();
                    var errorBuilder = new StringBuilder();
                    var streamLock = new object();
                    long lastActivityTicks = DateTime.UtcNow.Ticks;
                    long lastOutputTicks = DateTime.UtcNow.Ticks;
                    var lastCpuTime = TimeSpan.Zero;
                    int generatedChars = 0;
                    var lastCharReportAt = DateTime.UtcNow;
                    var lastLoopCheckAt = DateTime.UtcNow;
                    var stoppedAfterOutputLimit = false;
                    var stoppedAfterRepetitiveOutput = false;

                    var outputTask = PumpStreamAsync(process.StandardOutput.BaseStream, outputBuilder, streamLock, chunkLen =>
                    {
                        Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);
                        Interlocked.Exchange(ref lastOutputTicks, DateTime.UtcNow.Ticks);
                        var chars = Interlocked.Add(ref generatedChars, chunkLen);
                        if ((DateTime.UtcNow - lastCharReportAt) >= TimeSpan.FromMilliseconds(800))
                        {
                            lastCharReportAt = DateTime.UtcNow;
                            progress?.Report(new FlashcardProgress("ConvertToFlashcardsStatusProcessing", null, chars));
                        }
                    }, MaxCapturedStdoutChars, cancellationToken);

                    var errorTask = PumpStreamAsync(process.StandardError.BaseStream, errorBuilder, streamLock, _ =>
                        Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks), MaxCapturedStderrChars, cancellationToken);

                    var startedAt = DateTime.UtcNow;

                    while (!process.HasExited)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (TryGetTotalProcessorTime(process, out var cpuTime) && cpuTime > lastCpuTime)
                        {
                            lastCpuTime = cpuTime;
                            Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);
                        }

                        if (DateTime.UtcNow - startedAt > InferenceTimeout)
                        {
                            TryKillProcess(process);
                            var logPath = WriteInferenceDiagnosticLog(args, outputBuilder.ToString(), errorBuilder.ToString(), generatedChars, null);
                            throw new TimeoutException($"AI generation timed out. Diagnostic log: {logPath}");
                        }

                        if (generatedChars == 0 && DateTime.UtcNow - startedAt > InferenceNoOutputTimeout)
                        {
                            TryKillProcess(process);
                            var logPath = WriteInferenceDiagnosticLog(args, outputBuilder.ToString(), errorBuilder.ToString(), generatedChars, null);
                            throw new TimeoutException($"AI generation produced no output. Diagnostic log: {logPath}");
                        }

                        var lastOutput = new DateTime(Interlocked.Read(ref lastOutputTicks), DateTimeKind.Utc);
                        if (generatedChars > 0 && DateTime.UtcNow - lastOutput > InferenceOutputStallTimeout)
                        {
                            TryKillProcess(process);
                            var logPath = WriteInferenceDiagnosticLog(args, outputBuilder.ToString(), errorBuilder.ToString(), generatedChars, null);
                            throw new TimeoutException($"AI generation stopped producing output. Diagnostic log: {logPath}");
                        }

                        if (generatedChars >= MaxOutputChars)
                        {
                            stoppedAfterOutputLimit = true;
                            TryKillProcess(process);
                        }

                        if (generatedChars >= MinOutputCharsForLoopDetection
                            && DateTime.UtcNow - lastLoopCheckAt >= InferenceLoopCheckInterval)
                        {
                            lastLoopCheckAt = DateTime.UtcNow;
                            string outputSnapshot;
                            lock (streamLock)
                                outputSnapshot = outputBuilder.ToString();

                            if (LooksLikeRepetitiveGeneration(outputSnapshot))
                            {
                                stoppedAfterRepetitiveOutput = true;
                                TryKillProcess(process);
                            }
                        }

                        var lastActivity = new DateTime(Interlocked.Read(ref lastActivityTicks), DateTimeKind.Utc);
                        if (DateTime.UtcNow - lastActivity > InferenceInactivityTimeout)
                        {
                            TryKillProcess(process);
                            var logPath = WriteInferenceDiagnosticLog(args, outputBuilder.ToString(), errorBuilder.ToString(), generatedChars, null);
                            throw new TimeoutException($"AI generation stalled. Diagnostic log: {logPath}");
                        }

                        await Task.Delay(250, cancellationToken);
                    }

                    await process.WaitForExitAsync(cancellationToken);
                    await Task.WhenAll(outputTask, errorTask);

                    var output = outputBuilder.ToString();
                    var error = errorBuilder.ToString();

                    var normalizedOutput = NormalizeOutput(output);

                    if (process.ExitCode != 0)
                    {
                        if (!stoppedAfterOutputLimit
                            && !stoppedAfterRepetitiveOutput
                            && !(process.ExitCode == 130 && normalizedOutput.Contains("q:", StringComparison.OrdinalIgnoreCase)))
                        {
                            var logPath = WriteInferenceDiagnosticLog(args, output, error, generatedChars, process.ExitCode);
                            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                                ? $"Bundled model runtime returned non-zero exit code. Diagnostic: {logPath}"
                                : $"{error.Trim()}\nDiagnostic: {logPath}");
                        }
                    }

                    progress?.Report(new FlashcardProgress("ConvertToFlashcardsStatusFinalizing"));
                    return normalizedOutput;
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(true); } catch { }
                    }
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(promptFilePath))
                        File.Delete(promptFilePath);
                }
                catch { }
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    private static int CalculateContextTokens(string prompt, int nPredict)
    {
        var estimatedPromptTokens = EstimateTokenCount(prompt);
        var requestedTokens = estimatedPromptTokens + Math.Max(0, nPredict) + ContextSafetyTokens;
        var roundedTokens = RoundUp(requestedTokens, ContextTokenGranularity);
        return Math.Clamp(roundedTokens, MinContextTokens, MaxContextTokens);
    }

    private static int EstimateTokenCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var charEstimate = (int)Math.Ceiling(text.Length / 3.25);
        var wordCount = Regex.Matches(text, @"[\p{L}\p{N}]+").Count;
        var symbolCount = Regex.Matches(text, @"[^\s\p{L}\p{N}]").Count;
        var wordEstimate = (int)Math.Ceiling(wordCount * 1.45 + symbolCount * 0.45);

        return Math.Max(charEstimate, wordEstimate);
    }

    private static int RoundUp(int value, int granularity)
    {
        if (granularity <= 0)
            return value;

        return ((value + granularity - 1) / granularity) * granularity;
    }

    private static async Task PumpStreamAsync(
        Stream source,
        StringBuilder target,
        object streamLock,
        Action<int> onActivity,
        int maxChars,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            onActivity(read);
            var chunk = Encoding.UTF8.GetString(buffer, 0, read);

            lock (streamLock)
            {
                if (target.Length >= maxChars)
                    continue;

                var remaining = maxChars - target.Length;
                if (chunk.Length <= remaining)
                {
                    target.Append(chunk);
                }
                else
                {
                    target.Append(chunk.AsSpan(0, remaining));
                }
            }
        }
    }

    private static bool LooksLikeRepetitiveGeneration(string output)
    {
        if (string.IsNullOrWhiteSpace(output) || output.Length < MinOutputCharsForLoopDetection)
            return false;

        var tail = output.Length > 5000 ? output[^5000..] : output;
        var normalizedTail = Regex.Replace(tail, @"\s+", " ").Trim();

        if (Regex.IsMatch(normalizedTail, @"(.{30,300})\1{2,}", RegexOptions.Singleline))
            return true;

        var lines = tail
            .Replace("\r", "", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => Regex.Replace(line, @"\s+", " ").Trim())
            .Where(line => !IsRepeatedFormatLine(line))
            .Where(line => line.Length >= 6)
            .TakeLast(24)
            .ToList();

        if (lines.Count >= 10)
        {
            var groupedLines = lines.GroupBy(line => line, StringComparer.OrdinalIgnoreCase).ToList();
            var maxLineRepeat = groupedLines.Max(group => group.Count());
            var uniqueLineShare = groupedLines.Count / (double)lines.Count;

            if (maxLineRepeat >= 5 || uniqueLineShare <= 0.25)
                return true;
        }

        var tokens = Regex.Matches(normalizedTail.ToLowerInvariant(), @"[\p{L}\p{N}]{3,}")
            .Select(match => match.Value)
            .ToList();

        return HasRepeatedTokenNgrams(tokens);
    }

    private static bool IsRepeatedFormatLine(string line)
    {
        return Regex.IsMatch(
            line,
            @"^(?:type\s*:\s*(?:single|multi|truefalse)|answer\s*:\s*(?:true|false|teisinga|klaidinga))$",
            RegexOptions.IgnoreCase);
    }

    private static bool HasRepeatedTokenNgrams(IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 80)
            return false;

        var tailTokens = tokens.Count > 120
            ? tokens.Skip(tokens.Count - 120).ToList()
            : tokens;

        for (var size = 4; size <= 10; size++)
        {
            if (tailTokens.Count < size * 4)
                continue;

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var total = tailTokens.Count - size + 1;

            for (var i = 0; i <= tailTokens.Count - size; i++)
            {
                var key = string.Join('\u001F', tailTokens.Skip(i).Take(size));
                counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
            }

            var maxRepeat = counts.Values.Max();
            var repeatedShare = maxRepeat * size / (double)tailTokens.Count;
            if (maxRepeat >= 4 && repeatedShare >= 0.35)
                return true;
        }

        return false;
    }

    private static string WriteInferenceDiagnosticLog(string args, string output, string error, int generatedChars, int? exitCode)
    {
        try
        {
            var diagnosticsDir = Path.Combine(GetCacheRootDirectory(), "diagnostics");
            Directory.CreateDirectory(diagnosticsDir);
            var filePath = Path.Combine(diagnosticsDir, $"inference-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");

            var body = new StringBuilder();
            body.AppendLine($"TimestampUtc: {DateTime.UtcNow:O}");
            body.AppendLine($"ExitCode: {(exitCode.HasValue ? exitCode.Value.ToString() : "n/a")}");
            body.AppendLine($"GeneratedChars: {generatedChars}");
            body.AppendLine($"Args: {args}");
            body.AppendLine();
            body.AppendLine("--- STDERR ---");
            body.AppendLine(error);
            body.AppendLine();
            body.AppendLine("--- STDOUT (preview) ---");
            body.AppendLine(output.Length > 4000 ? output[..4000] : output);

            File.WriteAllText(filePath, body.ToString(), Encoding.UTF8);
            return filePath;
        }
        catch
        {
            return "(failed to write diagnostic log)";
        }
    }

    private static void TryKillProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); } catch { }
    }

    private static bool TryGetTotalProcessorTime(Process process, out TimeSpan totalProcessorTime)
    {
        try
        {
            totalProcessorTime = process.TotalProcessorTime;
            return true;
        }
        catch
        {
            totalProcessorTime = TimeSpan.Zero;
            return false;
        }
    }

    private static ModelInfo GetSelectedModelInfo()
    {
        var settings = AppSettingsService.Load();
        var enabledKeys = GetEnabledFlashcardModelKeys(settings);
        var selectedKey = settings.FlashcardModelKey;

        if (string.IsNullOrWhiteSpace(selectedKey)
            || !enabledKeys.Any(key => string.Equals(key, selectedKey, StringComparison.OrdinalIgnoreCase)))
        {
            selectedKey = enabledKeys[0];
        }

        return SupportedModels.FirstOrDefault(m => string.Equals(m.Key, selectedKey, StringComparison.OrdinalIgnoreCase))
            ?? SupportedModels[0];
    }

    public static IReadOnlyList<FlashcardToolInfo> GetSupportedFlashcardTools()
    {
        return SupportedModels
            .Select(model => new FlashcardToolInfo(model.Key, model.DisplayName))
            .ToArray();
    }

    public static List<AiToolSettingsItem> BuildDefaultAiToolSettings()
    {
        return SupportedModels
            .Select(model => new AiToolSettingsItem
            {
                Key = model.Key,
                IsEnabled = true
            })
            .ToList();
    }

    public static IReadOnlyList<string> GetEnabledFlashcardModelKeys(AppSettings? settings = null)
    {
        var source = settings ?? AppSettingsService.Load();
        var supportedByKey = SupportedModels
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
            configured = BuildDefaultAiToolSettings().Select(item => item.Key).ToList();

        return configured;
    }

    public static string GetRecommendedFlashcardModelKeyForCurrentMachine()
    {
        return GetRecommendedFlashcardModelKey(GetTotalPhysicalMemoryBytesForCurrentMachine());
    }

    public static string GetRecommendedFlashcardModelKey(long totalPhysicalMemoryBytes)
    {
        if (totalPhysicalMemoryBytes >= 12L * 1024L * 1024L * 1024L)
            return "Qwen3.5-4B";

        if (totalPhysicalMemoryBytes >= 6L * 1024L * 1024L * 1024L)
            return "Qwen3.5-2B";

        return DefaultModelKey;
    }

    public static string GetFlashcardModelDisplayLabel(string key, bool includeWarningPrefix = false, bool isCompatible = true)
    {
        var model = SupportedModels.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? SupportedModels[0];

        var label = $"{model.DisplayName} - {GetFlashcardModelDescription(key)}";
        if (includeWarningPrefix && !isCompatible)
            label = $"⚠️ {label}";

        return label;
    }

    public static string GetFlashcardModelDisplayName(string key)
    {
        return SupportedModels.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase))?.DisplayName
            ?? SupportedModels[0].DisplayName;
    }

    public static string GetFlashcardModelDescription(string key)
    {
        var model = SupportedModels.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? SupportedModels[0];

        var localized = LocalizationService.GetString(model.DescriptionResourceKey);
        if (!string.IsNullOrWhiteSpace(localized) && !string.Equals(localized, model.DescriptionResourceKey, StringComparison.Ordinal))
            return localized;

        return model.DefaultDescription;
    }

    public static long GetFlashcardModelRequiredMemoryBytes(string key)
    {
        return SupportedModels.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase))?.MinimumRecommendedMemoryBytes
            ?? 0;
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

    internal AiChunkingModelProfile GetSelectedChunkingProfile()
    {
        var selectedKey = GetSelectedModelInfo().Key;
        if (selectedKey.Contains("0.8B", StringComparison.OrdinalIgnoreCase))
            return AiChunkingModelProfile.Small;

        if (selectedKey.Contains("2B", StringComparison.OrdinalIgnoreCase))
            return AiChunkingModelProfile.Medium;

        return AiChunkingModelProfile.Large;
    }

    private static async Task EnsureBundledAssetsAsync(
        string runtimeAssetsDir,
        string modelsAssetsDir,
        string runtimeCacheDir,
        string modelsCacheDir,
        ModelInfo selectedModel,
        IProgress<FlashcardProgress>? progress,
        CancellationToken cancellationToken)
    {
        await EnsureRuntimeAssetsAsync(runtimeAssetsDir, runtimeCacheDir, progress, cancellationToken);
        await EnsureModelAssetAsync(modelsAssetsDir, modelsCacheDir, selectedModel, progress, cancellationToken);
        progress?.Report(new FlashcardProgress("ConvertToFlashcardsStatusAssetsReady"));
    }

    private static async Task EnsureRuntimeAssetsAsync(
        string runtimeAssetsDir,
        string runtimeCacheDir,
        IProgress<FlashcardProgress>? progress,
        CancellationToken cancellationToken)
    {
        var cacheRuntimePath = Path.Combine(runtimeCacheDir, RuntimeExeName);
        if (IsRuntimePayloadAvailable(cacheRuntimePath)
            && await IsRuntimeRunnableAsync(cacheRuntimePath, cancellationToken))
        {
            return;
        }

        var bundledRuntimePath = Path.Combine(runtimeAssetsDir, RuntimeExeName);
        if (!IsRuntimePayloadAvailable(bundledRuntimePath))
        {
            await TryDownloadRuntimePayloadToDirectoryAsync(runtimeCacheDir, progress, cancellationToken);

            if (IsRuntimePayloadAvailable(cacheRuntimePath)
                && await IsRuntimeRunnableAsync(cacheRuntimePath, cancellationToken))
            {
                return;
            }

            throw new FileNotFoundException("Bundled llama.cpp runtime is missing and runtime download failed.", Path.Combine(runtimeAssetsDir, RuntimeExeName));
        }

        await InstallRuntimePayloadAsync(bundledRuntimePath, runtimeCacheDir, null, cancellationToken);

        if (!await IsRuntimeRunnableAsync(cacheRuntimePath, cancellationToken))
            throw new InvalidOperationException("Bundled llama runtime is present but could not be started from cache.");
    }

    private static async Task TryDownloadRuntimePayloadToDirectoryAsync(
        string runtimeCacheDir,
        IProgress<FlashcardProgress>? progress,
        CancellationToken cancellationToken)
    {
        var release = await ResolveRuntimeDownloadReleaseAsync(cancellationToken);
        if (release is null || release.Urls.Count == 0)
            throw new InvalidOperationException("No compatible llama.cpp runtime download URLs were resolved for this architecture.");

        progress?.Report(new FlashcardProgress("ConvertToFlashcardsStatusDownloadingRuntime", 0));

        await DownloadRuntimePayloadToDirectoryAsync(runtimeCacheDir, release.Urls, release, progress, cancellationToken);
    }

    private static async Task DownloadRuntimePayloadToDirectoryAsync(
        string runtimeCacheDir,
        IReadOnlyList<string> runtimeUrls,
        RuntimeReleaseInfo? release,
        IProgress<FlashcardProgress>? progress,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (var runtimeUrl in runtimeUrls)
        {
            var tempWorkDir = Path.Combine(Path.GetTempPath(), "Scriblucent", "runtime-download", Guid.NewGuid().ToString("N"));
            var archivePath = Path.Combine(tempWorkDir, $"{RuntimeArchivePrefix}.zip");
            var extractDir = Path.Combine(tempWorkDir, "extract");
            try
            {
                Directory.CreateDirectory(tempWorkDir);
                await DownloadFileWithRetryAsync(runtimeUrl, archivePath, "ConvertToFlashcardsStatusDownloadingRuntime", progress, cancellationToken);

                Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(archivePath, extractDir, overwriteFiles: true);

                var extractedRuntimePath = Directory
                    .EnumerateFiles(extractDir, "*.exe", SearchOption.AllDirectories)
                    .Where(path => string.Equals(Path.GetFileName(path), RuntimeExeName, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();

                if (string.IsNullOrWhiteSpace(extractedRuntimePath))
                    throw new FileNotFoundException("Downloaded runtime archive does not contain llama-completion executable.", RuntimeExeName);

                await InstallRuntimePayloadAsync(extractedRuntimePath, runtimeCacheDir, release, cancellationToken);

                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
            finally
            {
                TryDeleteDirectory(tempWorkDir);
            }
        }

        if (lastError is not null)
        {
            var attempted = string.Join(", ", runtimeUrls);
            throw new InvalidOperationException(
                $"Failed to download runtime payload automatically. Attempted URLs: {attempted}. Last error: {lastError.Message}",
                lastError);
        }
    }

    private static async Task<IReadOnlyList<string>> ResolveRuntimeDownloadUrlsAsync(CancellationToken cancellationToken)
        => (await ResolveRuntimeDownloadReleaseAsync(cancellationToken))?.Urls ?? Array.Empty<string>();

    private static async Task<RuntimeReleaseInfo?> ResolveRuntimeDownloadReleaseAsync(CancellationToken cancellationToken)
    {
        var overrideUrl = Environment.GetEnvironmentVariable("SCRIBLUCENT_LLAMA_RUNTIME_URL");
        if (string.IsNullOrWhiteSpace(overrideUrl))
            overrideUrl = Environment.GetEnvironmentVariable("SCRIBLUCENT_OLLAMA_RUNTIME_URL");

        if (string.IsNullOrWhiteSpace(overrideUrl))
            return await ResolveRuntimeReleaseFromReleaseApiAsync(cancellationToken);

#if DEBUG
        if (Uri.TryCreate(overrideUrl, UriKind.Absolute, out var debugUri)
            && (string.Equals(debugUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.Equals(debugUri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(debugUri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)))
        {
            return new RuntimeReleaseInfo("debug-override", overrideUrl, null, [overrideUrl]);
        }

        throw new InvalidOperationException("SCRIBLUCENT_LLAMA_RUNTIME_URL must be an HTTPS URL (or localhost) in Debug builds.");
#else
        return await ResolveRuntimeReleaseFromReleaseApiAsync(cancellationToken);
#endif
    }

    private static async Task<IReadOnlyList<string>> ResolveRuntimeUrlsFromReleaseApiAsync(CancellationToken cancellationToken)
        => (await ResolveRuntimeReleaseFromReleaseApiAsync(cancellationToken))?.Urls ?? Array.Empty<string>();

    private static async Task<RuntimeReleaseInfo?> ResolveRuntimeReleaseFromReleaseApiAsync(CancellationToken cancellationToken)
    {
        var isArm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        var preferredArchTokens = isArm64
            ? new[] { "arm64", "aarch64" }
            : new[] { "x64", "amd64" };

        foreach (var apiUrl in RuntimeReleaseApiUrls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");

                using var response = await DownloadClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var tagName = document.RootElement.TryGetProperty("tag_name", out var tagProp)
                    ? tagProp.GetString()
                    : null;
                var htmlUrl = document.RootElement.TryGetProperty("html_url", out var htmlUrlProp)
                    ? htmlUrlProp.GetString()
                    : apiUrl;
                DateTimeOffset? publishedAt = null;
                if (document.RootElement.TryGetProperty("published_at", out var publishedProp)
                    && publishedProp.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(publishedProp.GetString(), out var parsedPublishedAt))
                {
                    publishedAt = parsedPublishedAt;
                }

                if (!document.RootElement.TryGetProperty("assets", out var assets)
                    || assets.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var urls = assets
                    .EnumerateArray()
                    .Select(a =>
                    {
                        var name = a.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                        var url = a.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() : null;
                        return (name, url);
                    })
                    .Where(x => !string.IsNullOrWhiteSpace(x.name) && !string.IsNullOrWhiteSpace(x.url))
                    .Where(x => x.name!.Contains("win", StringComparison.OrdinalIgnoreCase))
                    .Where(x => preferredArchTokens.Any(t => x.name!.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    .Where(x => x.name!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    .Where(x => !x.name!.Contains("cuda", StringComparison.OrdinalIgnoreCase)
                             && !x.name.Contains("vulkan", StringComparison.OrdinalIgnoreCase)
                             && !x.name.Contains("metal", StringComparison.OrdinalIgnoreCase)
                             && !x.name.Contains("opencl", StringComparison.OrdinalIgnoreCase)
                             && !x.name.Contains("sycl", StringComparison.OrdinalIgnoreCase)
                             && !x.name.Contains("rocm", StringComparison.OrdinalIgnoreCase)
                             && !x.name.Contains("hip", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.url!)
                    .Where(IsSafeRuntimeDownloadUrl)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (urls.Length > 0)
                {
                    return new RuntimeReleaseInfo(
                        string.IsNullOrWhiteSpace(tagName) ? "latest" : tagName,
                        string.IsNullOrWhiteSpace(htmlUrl) ? apiUrl : htmlUrl,
                        publishedAt,
                        urls);
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static bool IsSafeRuntimeDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        return string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRuntimeUpdateAvailable(string currentVersion, string latestVersion)
    {
        if (string.IsNullOrWhiteSpace(latestVersion))
            return false;

        if (string.Equals(currentVersion, latestVersion, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(currentVersion)
            || string.Equals(currentVersion, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TryCompareRuntimeVersions(currentVersion, latestVersion, out var comparison)
            ? comparison < 0
            : true;
    }

    private static bool TryCompareRuntimeVersions(string currentVersion, string latestVersion, out int comparison)
    {
        comparison = 0;

        if (TryExtractLlamaBuildNumber(currentVersion, out var currentBuild)
            && TryExtractLlamaBuildNumber(latestVersion, out var latestBuild))
        {
            comparison = currentBuild.CompareTo(latestBuild);
            return true;
        }

        if (TryParseNumericVersion(currentVersion, out var currentNumbers)
            && TryParseNumericVersion(latestVersion, out var latestNumbers))
        {
            for (var i = 0; i < Math.Max(currentNumbers.Length, latestNumbers.Length); i++)
            {
                var current = i < currentNumbers.Length ? currentNumbers[i] : 0;
                var latest = i < latestNumbers.Length ? latestNumbers[i] : 0;
                comparison = current.CompareTo(latest);
                if (comparison != 0)
                    return true;
            }

            comparison = 0;
            return true;
        }

        return false;
    }

    private static bool TryExtractLlamaBuildNumber(string value, out int buildNumber)
    {
        buildNumber = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var match = Regex.Match(value.Trim(), @"(?:^|[^\p{L}\p{N}])b(?<build>\d+)(?:$|[^\p{L}\p{N}])", RegexOptions.IgnoreCase);
        return match.Success
            && int.TryParse(match.Groups["build"].Value, out buildNumber);
    }

    private static bool TryParseNumericVersion(string value, out int[] numbers)
    {
        numbers = Array.Empty<int>();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim().TrimStart('v', 'V');
        var suffixIndex = normalized.IndexOfAny(['-', '+', ' ']);
        if (suffixIndex >= 0)
            normalized = normalized[..suffixIndex];

        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        var parsed = new List<int>();
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var number))
                return false;

            parsed.Add(number);
        }

        numbers = parsed.ToArray();
        return true;
    }

    private static RuntimeVersionMetadata? TryReadRuntimeVersionMetadata(string runtimeDir)
    {
        try
        {
            var path = Path.Combine(runtimeDir, RuntimeVersionMetadataFileName);
            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<RuntimeVersionMetadata>(json);
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteRuntimeVersionMetadataAsync(
        string runtimeDir,
        RuntimeReleaseInfo release,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(runtimeDir);
            var metadata = new RuntimeVersionMetadata(
                release.TagName,
                release.HtmlUrl,
                release.PublishedAt,
                DateTimeOffset.UtcNow);
            var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(runtimeDir, RuntimeVersionMetadataFileName), json, Encoding.UTF8, cancellationToken);
        }
        catch
        {
        }
    }

    private static async Task InstallRuntimePayloadAsync(
        string sourceRuntimeExePath,
        string runtimeCacheDir,
        RuntimeReleaseInfo? release,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cacheParentDir = Path.GetDirectoryName(runtimeCacheDir)
            ?? throw new InvalidOperationException("Invalid runtime cache directory.");
        Directory.CreateDirectory(cacheParentDir);
        CleanupOldRuntimeTransactionDirectories(cacheParentDir);

        var stagingDir = Path.Combine(cacheParentDir, $"runtime.staging-{Guid.NewGuid():N}");
        try
        {
            CopyRuntimePayload(sourceRuntimeExePath, Path.Combine(stagingDir, RuntimeExeName));
            var stagedRuntimePath = Path.Combine(stagingDir, RuntimeExeName);
            if (!IsRuntimePayloadAvailable(stagedRuntimePath))
                throw new InvalidDataException("Downloaded llama.cpp runtime payload is incomplete.");

            if (release is not null)
                await WriteRuntimeVersionMetadataAsync(stagingDir, release, cancellationToken);

            CommitRuntimePayload(stagingDir, runtimeCacheDir);
        }
        catch
        {
            TryDeleteDirectory(stagingDir);
            throw;
        }
    }

    private static void CommitRuntimePayload(string stagingDir, string runtimeCacheDir)
    {
        var cacheParentDir = Path.GetDirectoryName(runtimeCacheDir)
            ?? throw new InvalidOperationException("Invalid runtime cache directory.");
        var backupDir = Path.Combine(cacheParentDir, $"runtime.backup-{Guid.NewGuid():N}");
        var hasExistingRuntime = Directory.Exists(runtimeCacheDir);

        try
        {
            if (hasExistingRuntime)
                Directory.Move(runtimeCacheDir, backupDir);

            Directory.Move(stagingDir, runtimeCacheDir);
            TryDeleteDirectory(backupDir);
        }
        catch
        {
            if (!Directory.Exists(runtimeCacheDir) && Directory.Exists(backupDir))
            {
                try
                {
                    Directory.Move(backupDir, runtimeCacheDir);
                }
                catch
                {
                    // Preserve the backup directory if rollback cannot complete.
                }
            }

            throw;
        }
    }

    private static void CleanupOldRuntimeTransactionDirectories(string cacheParentDir)
    {
        try
        {
            var cutoffUtc = DateTime.UtcNow - TimeSpan.FromDays(1);
            foreach (var directory in Directory.EnumerateDirectories(cacheParentDir, "runtime.staging-*")
                         .Concat(Directory.EnumerateDirectories(cacheParentDir, "runtime.backup-*")))
            {
                try
                {
                    var info = new DirectoryInfo(directory);
                    var lastWriteUtc = info.LastWriteTimeUtc;
                    if (lastWriteUtc < cutoffUtc)
                        TryDeleteDirectory(directory);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static async Task EnsureModelAssetAsync(
        string modelsAssetsDir,
        string modelsCacheDir,
        ModelInfo selectedModel,
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
        ModelInfo model,
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

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        return string.Equals(uri.Host, "huggingface.co", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "hf-mirror.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string QuoteArgumentForLog(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";

        return value.Any(char.IsWhiteSpace) || value.Contains('"', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
    }

    private static async Task<FileStream> AcquireAssetsLockAsync(CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(GetCacheRootDirectory(), "assets.lock");
        Directory.CreateDirectory(GetCacheRootDirectory());

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(150, cancellationToken);
            }
        }
    }

    private static bool IsHexChar(char value)
    {
        return (value >= '0' && value <= '9')
            || (value >= 'a' && value <= 'f');
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

    private static async Task DownloadFileAsync(
        string url,
        string destinationPath,
        string statusKey,
        IProgress<FlashcardProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        using var response = await DownloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

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

    public static string GetRuntimeDirectoryPath()
    {
        return Path.Combine(GetCacheRootDirectory(), "runtime");
    }

    public static string GetRuntimeExecutablePath()
    {
        return Path.Combine(GetRuntimeDirectoryPath(), RuntimeExeName);
    }

    public static string? GetModelFilePath(string key)
    {
        var model = SupportedModels.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
        if (model is null)
            return null;

        return Path.Combine(GetModelsDirectoryPath(), model.FileName);
    }

    public static bool IsModelDownloaded(string key)
    {
        var path = GetModelFilePath(key);
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    public static bool IsRuntimeDownloaded()
    {
        return IsRuntimePayloadAvailable(GetRuntimeExecutablePath());
    }

    public static string GetRuntimeInstalledVersionDisplay()
    {
        var metadata = TryReadRuntimeVersionMetadata(GetRuntimeDirectoryPath());
        return string.IsNullOrWhiteSpace(metadata?.TagName) ? "unknown" : metadata.TagName;
    }

    public static void DeleteModelArtifacts(string key)
    {
        var model = SupportedModels.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
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

    public static void DeleteRuntimeArtifacts()
    {
        var runtimeDir = GetRuntimeDirectoryPath();
        TryDeleteDirectory(runtimeDir);
        var parentDir = Path.GetDirectoryName(runtimeDir);
        try
        {
            if (!string.IsNullOrWhiteSpace(parentDir) && Directory.Exists(parentDir))
            {
                foreach (var directory in Directory.EnumerateDirectories(parentDir, "runtime.staging-*")
                             .Concat(Directory.EnumerateDirectories(parentDir, "runtime.backup-*")))
                {
                    TryDeleteDirectory(directory);
                }
            }
        }
        catch
        {
        }

        TryDeleteDirectory(Path.Combine(AppContext.BaseDirectory, "Assets", "ModelRuntime"));
    }

    public async Task EnsureRuntimeAvailableAsync(
        IProgress<FlashcardProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var runtimeAssetsDir = Path.Combine(baseDir, "Assets", "ModelRuntime");
            var runtimeCacheDir = GetRuntimeDirectoryPath();

            Directory.CreateDirectory(runtimeAssetsDir);
            Directory.CreateDirectory(runtimeCacheDir);

            await EnsureRuntimeAssetsAsync(runtimeAssetsDir, runtimeCacheDir, progress, cancellationToken);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<RuntimeUpdateResult> CheckAndUpdateRuntimeAsync(
        IProgress<FlashcardProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            var runtimeCacheDir = GetRuntimeDirectoryPath();
            var runtimeExePath = Path.Combine(runtimeCacheDir, RuntimeExeName);
            if (!IsRuntimePayloadAvailable(runtimeExePath))
            {
                throw new InvalidOperationException("llama.cpp runtime is not downloaded yet.");
            }

            progress?.Report(new FlashcardProgress("AiToolsRuntimeUpdateChecking"));
            var latestRelease = await ResolveRuntimeDownloadReleaseAsync(cancellationToken)
                ?? throw new InvalidOperationException("No compatible llama.cpp runtime release was found.");

            var currentVersion = GetRuntimeInstalledVersionDisplay();
            var updateAvailable = IsRuntimeUpdateAvailable(currentVersion, latestRelease.TagName);
            if (!updateAvailable)
            {
                return new RuntimeUpdateResult(
                    currentVersion,
                    latestRelease.TagName,
                    IsUpdateAvailable: false,
                    WasUpdated: false);
            }

            progress?.Report(new FlashcardProgress("AiToolsRuntimeUpdateDownloading", 0));
            await DownloadRuntimePayloadToDirectoryAsync(runtimeCacheDir, latestRelease.Urls, latestRelease, progress, cancellationToken);

            if (!IsRuntimePayloadAvailable(runtimeExePath))
            {
                throw new InvalidOperationException("Updated llama.cpp runtime was downloaded, but the runtime files are incomplete.");
            }

            return new RuntimeUpdateResult(
                currentVersion,
                latestRelease.TagName,
                IsUpdateAvailable: true,
                WasUpdated: true);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task EnsureModelAvailableAsync(
        string key,
        IProgress<FlashcardProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Model key is required.", nameof(key));

        var selectedModel = SupportedModels.FirstOrDefault(model => string.Equals(model.Key, key, StringComparison.OrdinalIgnoreCase));
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

    private static bool IsRuntimeExecutableValid(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 4 * 1024;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRuntimePayloadAvailable(string runtimeExePath)
    {
        if (!IsRuntimeExecutableValid(runtimeExePath))
            return false;

        var runtimeDir = Path.GetDirectoryName(runtimeExePath);
        if (string.IsNullOrWhiteSpace(runtimeDir) || !Directory.Exists(runtimeDir))
            return false;

        var runtimeExe = new FileInfo(runtimeExePath);
        var companionLibraryPath = Path.Combine(
            runtimeDir,
            $"{Path.GetFileNameWithoutExtension(runtimeExePath)}-impl.dll");
        var hasCompanionLibrary = IsRuntimeLibraryValid(companionLibraryPath);
        var hasLauncherImplementation = runtimeExe.Length > 100 * 1024 || hasCompanionLibrary;
        var hasLlamaCore = IsRuntimeLibraryValid(Path.Combine(runtimeDir, "llama.dll"))
            && IsRuntimeLibraryValid(Path.Combine(runtimeDir, "llama-common.dll"));
        var hasGgmlCore = IsRuntimeLibraryValid(Path.Combine(runtimeDir, "ggml.dll"))
            && IsRuntimeLibraryValid(Path.Combine(runtimeDir, "ggml-base.dll"));
        var hasCpuBackend = Directory
            .GetFiles(runtimeDir, "ggml-cpu*.dll", SearchOption.TopDirectoryOnly)
            .Any(IsRuntimeLibraryValid);

        // New llama.cpp Windows packages use small launcher executables plus *-impl.dll.
        return hasLauncherImplementation
            && hasLlamaCore
            && hasGgmlCore
            && hasCpuBackend;
    }

    private static bool IsRuntimeLibraryValid(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 64 * 1024;
        }
        catch
        {
            return false;
        }
    }

    private static void CopyRuntimePayload(string sourceExePath, string destinationExePath)
    {
        var sourceDir = Path.GetDirectoryName(sourceExePath);
        var destinationDir = Path.GetDirectoryName(destinationExePath);
        if (string.IsNullOrWhiteSpace(sourceDir) || string.IsNullOrWhiteSpace(destinationDir))
            throw new InvalidOperationException("Invalid runtime source or destination path.");

        Directory.CreateDirectory(destinationDir);

        foreach (var sourcePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, sourcePath);
            var destinationPath = Path.Combine(destinationDir, relativePath);
            var destinationSubDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationSubDir))
                Directory.CreateDirectory(destinationSubDir);

            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
    }

    private static async Task<bool> IsRuntimeRunnableAsync(string runtimeExePath, CancellationToken cancellationToken)
    {
        try
        {
            var runtimeDir = Path.GetDirectoryName(runtimeExePath);
            if (string.IsNullOrWhiteSpace(runtimeDir))
                return false;

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = runtimeExePath,
                    Arguments = "--version",
                    WorkingDirectory = runtimeDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

            if (!process.Start())
                return false;

            var waitTask = process.WaitForExitAsync(cancellationToken);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            var completed = await Task.WhenAny(waitTask, timeoutTask);
            if (completed == timeoutTask)
            {
                try { process.Kill(true); } catch { }
                return false;
            }

            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            if (error.Contains(".dll", StringComparison.OrdinalIgnoreCase)
                && error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch
        {
            return false;
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

    private static void ValidateDownloadedModelFile(string filePath, ModelInfo? model = null)
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
    }

    private static bool IsModelFileValid(string filePath, ModelInfo? model = null)
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

    private static void CleanupOldPromptFiles(string promptDir)
    {
        try
        {
            var cutoffUtc = DateTime.UtcNow - PromptFileRetention;
            foreach (var file in Directory.EnumerateFiles(promptDir, "prompt-*.txt", SearchOption.TopDirectoryOnly))
            {
                DateTime lastWriteUtc;
                try
                {
                    lastWriteUtc = File.GetLastWriteTimeUtc(file);
                }
                catch
                {
                    continue;
                }

                if (lastWriteUtc < cutoffUtc)
                    TryDeleteFile(file);
            }
        }
        catch
        {
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

    private static string NormalizeOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return string.Empty;

        var text = output.Trim();
        if (text.Length > MaxOutputChars)
            text = text[..MaxOutputChars];

        return text;
    }
}
