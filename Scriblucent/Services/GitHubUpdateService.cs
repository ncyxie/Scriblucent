using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scriblucent.Services;

public sealed class GitHubUpdateResult
{
    public required string CurrentVersion { get; init; }
    public GitHubReleaseInfo? LatestRelease { get; init; }
    public bool IsUpdateAvailable { get; init; }
    public bool HasComparableVersions { get; init; }
    public string ReleasesUrl { get; init; } = GitHubUpdateService.ReleasesUrl;
}

public sealed class GitHubReleaseInfo
{
    public required string Name { get; init; }
    public required string TagName { get; init; }
    public required string HtmlUrl { get; init; }
    public string? Body { get; init; }
    public bool IsPrerelease { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
}

public sealed class GitHubUpdateService
{
    public const string ReleasesUrl = "https://github.com/ncyxie/Scriblucent/releases";

    private const string ReleasesApiUrl = "https://api.github.com/repos/ncyxie/Scriblucent/releases?per_page=20";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;

    public GitHubUpdateService()
        : this(CreateHttpClient())
    {
    }

    internal GitHubUpdateService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<GitHubUpdateResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(ReleasesApiUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var releases = await JsonSerializer.DeserializeAsync<List<GitHubReleaseDto>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new List<GitHubReleaseDto>();

        var latestRelease = releases
            .Where(release => !release.Draft && !string.IsNullOrWhiteSpace(release.TagName) && !string.IsNullOrWhiteSpace(release.HtmlUrl))
            .Select(ToReleaseInfo)
            .OrderByDescending(release => ReleaseVersion.TryParse(release.TagName, out var version) ? version : null)
            .ThenByDescending(release => release.PublishedAt)
            .FirstOrDefault();

        var comparable = false;
        var updateAvailable = false;

        if (latestRelease is not null
            && ReleaseVersion.TryParse(AppVersionInfo.DisplayVersion, out var currentVersion)
            && currentVersion is not null
            && ReleaseVersion.TryParse(latestRelease.TagName, out var latestVersion)
            && latestVersion is not null)
        {
            comparable = true;
            updateAvailable = latestVersion.CompareTo(currentVersion) > 0;
        }

        return new GitHubUpdateResult
        {
            CurrentVersion = AppVersionInfo.DisplayVersion,
            LatestRelease = latestRelease,
            HasComparableVersions = comparable,
            IsUpdateAvailable = updateAvailable
        };
    }

    private static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient
        {
            Timeout = RequestTimeout
        };

        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Scriblucent", AppVersionInfo.DisplayVersion.TrimStart('v', 'V')));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        return httpClient;
    }

    private static GitHubReleaseInfo ToReleaseInfo(GitHubReleaseDto release)
    {
        return new GitHubReleaseInfo
        {
            Name = string.IsNullOrWhiteSpace(release.Name) ? release.TagName ?? string.Empty : release.Name,
            TagName = release.TagName ?? string.Empty,
            HtmlUrl = release.HtmlUrl ?? ReleasesUrl,
            Body = release.Body,
            IsPrerelease = release.Prerelease,
            PublishedAt = release.PublishedAt
        };
    }

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; init; }
    }

    private sealed class ReleaseVersion : IComparable<ReleaseVersion>
    {
        private readonly int[] _numbers;
        private readonly string[] _prereleaseParts;

        private ReleaseVersion(int[] numbers, string[] prereleaseParts)
        {
            _numbers = numbers;
            _prereleaseParts = prereleaseParts;
        }

        public static bool TryParse(string? value, out ReleaseVersion? version)
        {
            version = null;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            var normalized = value.Trim();
            if (normalized.StartsWith('v') || normalized.StartsWith('V'))
                normalized = normalized[1..];

            normalized = normalized.Replace('_', '-');
            var prereleaseIndex = normalized.IndexOf('-', StringComparison.Ordinal);
            var numericPart = prereleaseIndex >= 0 ? normalized[..prereleaseIndex] : normalized;
            var prereleasePart = prereleaseIndex >= 0 ? normalized[(prereleaseIndex + 1)..] : string.Empty;

            var numberTokens = numericPart.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (numberTokens.Length == 0 || numberTokens.Length > 3)
                return false;

            var numbers = new[] { 0, 0, 0 };
            for (var i = 0; i < numberTokens.Length; i++)
            {
                if (!int.TryParse(numberTokens[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                    return false;
            }

            var prereleaseParts = string.IsNullOrWhiteSpace(prereleasePart)
                ? Array.Empty<string>()
                : prereleasePart.Split(['.', '-'], StringSplitOptions.RemoveEmptyEntries);

            version = new ReleaseVersion(numbers, prereleaseParts);
            return true;
        }

        public int CompareTo(ReleaseVersion? other)
        {
            if (other is null)
                return 1;

            for (var i = 0; i < _numbers.Length; i++)
            {
                var numberComparison = _numbers[i].CompareTo(other._numbers[i]);
                if (numberComparison != 0)
                    return numberComparison;
            }

            if (_prereleaseParts.Length == 0 && other._prereleaseParts.Length > 0)
                return 1;

            if (_prereleaseParts.Length > 0 && other._prereleaseParts.Length == 0)
                return -1;

            var partCount = Math.Max(_prereleaseParts.Length, other._prereleaseParts.Length);
            for (var i = 0; i < partCount; i++)
            {
                if (i >= _prereleaseParts.Length)
                    return -1;

                if (i >= other._prereleaseParts.Length)
                    return 1;

                var leftIsNumber = int.TryParse(_prereleaseParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
                var rightIsNumber = int.TryParse(other._prereleaseParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);

                if (leftIsNumber && rightIsNumber)
                {
                    var numericComparison = leftNumber.CompareTo(rightNumber);
                    if (numericComparison != 0)
                        return numericComparison;

                    continue;
                }

                if (leftIsNumber != rightIsNumber)
                    return leftIsNumber ? -1 : 1;

                var textComparison = string.Compare(_prereleaseParts[i], other._prereleaseParts[i], StringComparison.OrdinalIgnoreCase);
                if (textComparison != 0)
                    return textComparison;
            }

            return 0;
        }
    }
}
