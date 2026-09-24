using System.Text;
using System.Text.RegularExpressions;

namespace Scriblucent.Services.AI;

internal static class StudyAnswerList
{
    public static bool IsExplicitList(string answer)
        => TrySplit(answer, out _);

    public static bool TrySplit(string answer, out IReadOnlyList<string> items)
    {
        items = [];
        var normalized = Regex.Replace(answer.Trim(), @"\s+", " ");
        var commaCount = normalized.Count(character => character is ',' or '，');
        var strongSeparatorCount = normalized.Count(character => character is ';' or '；' or '、');
        if (strongSeparatorCount == 0 && commaCount < 2)
            return false;

        var parsed = Regex.Split(normalized, @"\s*[,;，；、]\s*")
            .Select(item => item.Trim().TrimEnd('.', ':', '!', '?', '？', '؟'))
            .Where(item => item.Length > 0)
            .ToList();
        if (parsed.Count is < 2 or > 4
            || parsed.Any(item => !IsUsefulComponent(item))
            || parsed.Select(Normalize).Distinct(StringComparer.Ordinal).Count() != parsed.Count)
        {
            return false;
        }

        items = parsed;
        return true;
    }

    public static bool IsUsefulComponent(string value)
        => value.Length is >= 2 and <= 80
           && Regex.IsMatch(value, @"[\p{L}\p{N}]");

    private static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}\s]", " ");
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }
}
