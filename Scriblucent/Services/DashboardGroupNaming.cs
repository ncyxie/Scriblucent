using System.Globalization;
using Scriblucent.Localization;
using Scriblucent.Models;

namespace Scriblucent.Services;

internal static class DashboardGroupNaming
{
    internal const int Version = 1;

    internal static string NextName<T>(IEnumerable<T> groups) where T : IDashboardGroupMetadata
    {
        var numbers = groups.Where(g => !IsLegacyName(g)).Select(g => Number(g.Name)).Where(n => n > 0).ToHashSet();
        var maximum = numbers.DefaultIfEmpty(0).Max();
        int next = maximum < int.MaxValue ? maximum + 1 : 1;
        while (numbers.Contains(next)) next++;
        return Format(next);
    }

    // Upgrade only the exact old generated label (including numeric-only GUID prefixes).
    // The version prevents a future "Group 1234" matching its GUID from being renamed again.
    internal static bool Normalize<T>(IEnumerable<T> groups) where T : IDashboardGroupMetadata
    {
        var ordered = groups.OrderBy(g => g.SortOrder ?? int.MaxValue).ToArray();
        var pending = ordered.Where(g => IsLegacyName(g) || string.IsNullOrWhiteSpace(g.Name)).ToHashSet();
        var used = ordered.Where(g => !pending.Contains(g)).Select(g => Number(g.Name)).Where(n => n > 0).ToHashSet();
        bool changed = false;
        int number = 1;
        foreach (var group in ordered)
        {
            if (!pending.Contains(group)) continue;
            while (used.Contains(number)) number++;
            group.Name = Format(number);
            group.NameVersion = Version;
            used.Add(number);
            number++;
            changed = true;
        }
        return changed;
    }

    private static bool IsLegacyName(IDashboardGroupMetadata group)
    {
        if (group.NameVersion >= Version) return false;
        var code = group.GroupId.ToString("N")[..4].ToUpperInvariant();
        return group.Name == "Group " + code || group.Name == "Grupė " + code;
    }

    private static int Number(string? name)
    {
        var value = (name ?? string.Empty).Trim();
        foreach (var prefix in new[] { "Group ", "Grupė " })
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                return number;
        return 0;
    }

    private static string Format(int number) => string.Format(CultureInfo.CurrentCulture,
        LocalizationService.GetString("GroupTitleFormat"), number);
}
