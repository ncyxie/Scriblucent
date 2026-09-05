using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Scriblucent.Localization;
using WeCantSpell.Hunspell;

namespace Scriblucent.Services;

/// <summary>
/// Manages Hunspell dictionary files for the note editor's custom spell checker: each language
/// gets its own self-contained directory, downloads land in a staging directory first and are
/// validated before an atomic directory swap commits them (with rollback on failure), transient
/// network errors get retried, and a small metadata file (ETag/Last-Modified) enables checking
/// the LibreOffice source for updates without re-downloading. Unlike a general-purpose binary
/// payload, a dictionary is just two plain text files, so "does WordList.CreateFromFiles load
/// it" is the whole validation story - no native-library/PE-header checks or archive extraction
/// needed.
/// </summary>
internal static class SpellCheckDictionaryService
{
    private sealed record LanguageInfo(
        string Key,
        string DisplayName,
        string LocalizedNameResourceKey,
        string DicUrl,
        string AffUrl);

    private sealed record DictionaryMetadata(
        string? DicETag,
        string? DicLastModified,
        string? AffETag,
        string? AffLastModified,
        DateTimeOffset DownloadedAt);

    public readonly record struct SpellCheckLanguageOption(string Key, string DisplayName);

    private const string MetadataFileName = "dictionary-metadata.json";
    private const int MaxDownloadAttempts = 3;

    // A starting set covering this app's own localization (English/Lithuanian) plus the most
    // common European languages - "satisfy international users" broadly without an unbounded
    // list. Trivial to extend: every entry is just a LibreOffice/dictionaries repo path, verified
    // reachable at plan time.
    private static readonly LanguageInfo[] SupportedLanguages =
    [
        new("en_US", "English (US)", "SpellCheckLanguageNameEnUS",
            "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/en/en_US.dic",
            "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/en/en_US.aff"),
        new("en_GB", "English (UK)", "SpellCheckLanguageNameEnGB",
            "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/en/en_GB.dic",
            "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/en/en_GB.aff"),
        new("lt_LT", "Lietuvių", "SpellCheckLanguageNameLtLT",
            "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/lt_LT/lt.dic",
            "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/lt_LT/lt.aff"),
        new("de_DE", "Deutsch", "SpellCheckLanguageNameDeDE",
            "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/de/de_DE_frami.dic",
            "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/de/de_DE_frami.aff"),
        new("fr_FR", "Français", "SpellCheckLanguageNameFrFR",
            "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/fr_FR/dictionaries/fr.dic",
            "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/fr_FR/dictionaries/fr.aff"),
        new("es_ES", "Español", "SpellCheckLanguageNameEsES",
            "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/es/es_ES.dic",
            "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/es/es_ES.aff"),
        new("it_IT", "Italiano", "SpellCheckLanguageNameItIT",
            "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/it_IT/it_IT.dic",
            "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/it_IT/it_IT.aff"),
        new("pt_PT", "Português", "SpellCheckLanguageNamePtPT",
            "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/pt_PT/pt_PT.dic",
            "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/pt_PT/pt_PT.aff"),
        new("ru_RU", "Русский", "SpellCheckLanguageNameRuRU",
            "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/ru_RU/ru_RU.dic",
            "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/ru_RU/ru_RU.aff"),
        new("pl_PL", "Polski", "SpellCheckLanguageNamePlPL",
            "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/pl_PL/pl_PL.dic",
            "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/pl_PL/pl_PL.aff"),
        new("nl_NL", "Nederlands", "SpellCheckLanguageNameNlNL",
            "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/nl_NL/nl_NL.dic",
            "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/nl_NL/nl_NL.aff"),
    ];

    private static readonly HttpClient DownloadClient = new()
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    private static readonly Dictionary<string, WordList> LoadedDictionaries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LanguagesCurrentlyDownloading = new(StringComparer.OrdinalIgnoreCase);

    static SpellCheckDictionaryService()
    {
        DownloadClient.DefaultRequestHeaders.UserAgent.ParseAdd("Scriblucent/1.0 (+https://github.com/ncyxie/Scriblucent)");
    }

    public static IReadOnlyList<SpellCheckLanguageOption> GetSupportedLanguages()
        => SupportedLanguages.Select(language => new SpellCheckLanguageOption(language.Key, language.DisplayName)).ToArray();

    public static string GetLanguageDisplayName(string key)
        => FindLanguage(key)?.DisplayName ?? key;

    /// <summary>"{native name} ({name translated into the app's current UI language})" - e.g.
    /// "Deutsch (German)" in an English UI, "Deutsch (Vokiečių)" in a Lithuanian UI. Falls back to
    /// just the native name when the two would read identically (the dictionary's own language
    /// happens to match the app's current UI language).</summary>
    public static string GetFullLanguageLabel(string key)
    {
        var language = FindLanguage(key);
        if (language is null)
            return key;

        var localizedName = LocalizationService.GetString(language.LocalizedNameResourceKey);
        return string.Equals(localizedName, language.DisplayName, StringComparison.Ordinal)
            ? language.DisplayName
            : $"{language.DisplayName} ({localizedName})";
    }

    private static LanguageInfo? FindLanguage(string key)
        => SupportedLanguages.FirstOrDefault(language => string.Equals(language.Key, key, StringComparison.OrdinalIgnoreCase));

    public static string GetDictionariesRootDirectoryPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scriblucent", "dictionaries");

    /// <summary>Each language is a single self-contained directory (dic + aff + metadata) -
    /// lets install/update be an atomic directory swap instead of juggling two independently-
    /// moved files that could end up mismatched.</summary>
    private static string GetLanguageDirectoryPath(string key)
        => Path.Combine(GetDictionariesRootDirectoryPath(), key);

    private static string GetDicFilePath(string key) => Path.Combine(GetLanguageDirectoryPath(key), $"{key}.dic");
    private static string GetAffFilePath(string key) => Path.Combine(GetLanguageDirectoryPath(key), $"{key}.aff");

    public static bool IsDictionaryDownloaded(string key)
        => File.Exists(GetDicFilePath(key)) && File.Exists(GetAffFilePath(key));

    public static void DeleteDictionary(string key)
    {
        LoadedDictionaries.Remove(key);
        TryDeleteDirectory(GetLanguageDirectoryPath(key));
    }

    /// <summary>Downloads into a staging directory, validates the pair actually loads as a real
    /// Hunspell dictionary, then atomically swaps it into place - a failed or corrupt download
    /// never overwrites a working dictionary, and a validation failure never leaves a broken pair
    /// where a working one used to be.</summary>
    public static async Task DownloadDictionaryAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!LanguagesCurrentlyDownloading.Add(key))
            throw new InvalidOperationException($"A download for '{key}' is already in progress.");

        try
        {
            var language = FindLanguage(key) ?? throw new ArgumentException($"Unknown spell-check language '{key}'.", nameof(key));
            var root = GetDictionariesRootDirectoryPath();
            Directory.CreateDirectory(root);

            var stagingDir = Path.Combine(root, $"{key}.staging-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(stagingDir);
                var stagedDicPath = Path.Combine(stagingDir, $"{key}.dic");
                var stagedAffPath = Path.Combine(stagingDir, $"{key}.aff");

                var dicResult = await DownloadFileWithRetryAsync(language.DicUrl, stagedDicPath, cancellationToken);
                var affResult = await DownloadFileWithRetryAsync(language.AffUrl, stagedAffPath, cancellationToken);

                // Validate by actually loading it - the one meaningful check for a plain-text
                // dictionary pair (no PE header/native-library concept to inspect, unlike the
                // runtime executable).
                try
                {
                    await WordList.CreateFromFilesAsync(stagedDicPath, stagedAffPath, cancellationToken);
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException($"Downloaded dictionary for '{key}' failed to load.", ex);
                }

                var metadata = new DictionaryMetadata(dicResult.ETag, dicResult.LastModified, affResult.ETag, affResult.LastModified, DateTimeOffset.UtcNow);
                await WriteMetadataAsync(stagingDir, metadata, cancellationToken);

                CommitLanguagePayload(stagingDir, GetLanguageDirectoryPath(key));
            }
            catch
            {
                TryDeleteDirectory(stagingDir);
                throw;
            }

            // Force a reload next time this language is checked, in case a stale WordList was
            // already cached from a previous version of this dictionary.
            LoadedDictionaries.Remove(key);
        }
        finally
        {
            LanguagesCurrentlyDownloading.Remove(key);
        }
    }

    private static void CommitLanguagePayload(string stagingDir, string languageDir)
    {
        var root = Path.GetDirectoryName(languageDir) ?? throw new InvalidOperationException("Invalid dictionary directory.");
        var backupDir = Path.Combine(root, $"{Path.GetFileName(languageDir)}.backup-{Guid.NewGuid():N}");
        var hasExisting = Directory.Exists(languageDir);

        try
        {
            if (hasExisting)
                Directory.Move(languageDir, backupDir);

            Directory.Move(stagingDir, languageDir);
            TryDeleteDirectory(backupDir);
        }
        catch
        {
            // Roll back: if the new payload didn't make it into place, restore whatever was
            // there before rather than leaving the language with nothing installed.
            if (!Directory.Exists(languageDir) && Directory.Exists(backupDir))
            {
                try
                {
                    Directory.Move(backupDir, languageDir);
                }
                catch
                {
                    // Preserve the backup directory if rollback itself can't complete.
                }
            }

            throw;
        }
    }

    /// <summary>Checks every currently-downloaded language against the LibreOffice source (a
    /// conditional HEAD request compared against the ETag/Last-Modified captured at download time)
    /// and re-downloads any that changed - combines check-and-update into one action, since
    /// dictionaries change rarely enough that a separate "just tell me" step isn't worth a
    /// second click.</summary>
    public static async Task<(int Checked, int Updated)> CheckAndUpdateAllAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var downloadedKeys = SupportedLanguages
            .Select(language => language.Key)
            .Where(IsDictionaryDownloaded)
            .ToList();

        var updated = 0;
        foreach (var key in downloadedKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(GetLanguageDisplayName(key));

            if (await HasRemoteUpdateAsync(key, cancellationToken))
            {
                await DownloadDictionaryAsync(key, cancellationToken);
                updated++;
            }
        }

        return (downloadedKeys.Count, updated);
    }

    private static async Task<bool> HasRemoteUpdateAsync(string key, CancellationToken cancellationToken)
    {
        var language = FindLanguage(key);
        var metadata = TryReadMetadata(GetLanguageDirectoryPath(key));
        if (language is null || metadata is null)
            return false;

        var dicChanged = await HasResourceChangedAsync(language.DicUrl, metadata.DicETag, metadata.DicLastModified, cancellationToken);
        var affChanged = await HasResourceChangedAsync(language.AffUrl, metadata.AffETag, metadata.AffLastModified, cancellationToken);
        return dicChanged || affChanged;
    }

    private static async Task<bool> HasResourceChangedAsync(string url, string? knownETag, string? knownLastModified, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await DownloadClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return false;

            var currentETag = response.Headers.ETag?.Tag;
            var currentLastModified = response.Content.Headers.LastModified?.ToString("R");

            if (!string.IsNullOrEmpty(currentETag) && !string.IsNullOrEmpty(knownETag))
                return !string.Equals(currentETag, knownETag, StringComparison.Ordinal);

            if (!string.IsNullOrEmpty(currentLastModified) && !string.IsNullOrEmpty(knownLastModified))
                return !string.Equals(currentLastModified, knownLastModified, StringComparison.Ordinal);

            // Neither the server nor our stored metadata has a way to compare - assume unchanged
            // rather than force a redundant re-download every time.
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static DictionaryMetadata? TryReadMetadata(string languageDir)
    {
        try
        {
            var path = Path.Combine(languageDir, MetadataFileName);
            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<DictionaryMetadata>(json);
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteMetadataAsync(string languageDir, DictionaryMetadata metadata, CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(languageDir, MetadataFileName), json, Encoding.UTF8, cancellationToken);
        }
        catch
        {
            // Non-critical - a missing metadata file just means CheckAndUpdateAllAsync can't
            // compare this language later; it doesn't affect spell-checking itself.
        }
    }

    private sealed record DownloadResult(string? ETag, string? LastModified);

    private static async Task<DownloadResult> DownloadFileWithRetryAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
        {
            try
            {
                return await DownloadFileAsync(url, destinationPath, cancellationToken);
            }
            catch (Exception ex) when (attempt < MaxDownloadAttempts)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
            }
        }

        throw new InvalidOperationException($"Failed to download file from '{url}'.", lastError);
    }

    private static async Task<DownloadResult> DownloadFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        if (!IsAllowedDownloadHost(url))
            throw new InvalidOperationException($"Refusing to download from disallowed host: {url}");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await DownloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            await responseStream.CopyToAsync(fileStream, cancellationToken);
        }

        return new DownloadResult(response.Headers.ETag?.Tag, response.Content.Headers.LastModified?.ToString("R"));
    }

    private static bool IsAllowedDownloadHost(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static async Task<WordList?> GetOrLoadWordListAsync(string key, CancellationToken cancellationToken = default)
    {
        if (LoadedDictionaries.TryGetValue(key, out var cached))
            return cached;

        var dicPath = GetDicFilePath(key);
        var affPath = GetAffFilePath(key);
        if (!File.Exists(dicPath) || !File.Exists(affPath))
            return null;

        // The Hunspell parse of a full dictionary is heavy CPU (hundreds of ms) - run it fully
        // off the UI thread so the first spell-check pass of a session never hitches the editor.
        var wordList = await Task.Run(() => WordList.CreateFromFilesAsync(dicPath, affPath, cancellationToken));
        LoadedDictionaries[key] = wordList;
        return wordList;
    }
}
