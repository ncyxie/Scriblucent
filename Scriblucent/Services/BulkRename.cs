using System.Globalization;

namespace Scriblucent.Services;

internal enum BulkRenameMode { RenameTo, Replace, Affixes, Numbered }
internal sealed record RenameTarget(Guid Id, string Name);
internal sealed record RenameChange(Guid Id, string Before, string After);
internal sealed record BulkRenameRule(BulkRenameMode Mode, string Find, string Replacement, string Prefix, string Suffix, int Start = 1);

internal static class BulkRename
{
    internal static IReadOnlyList<RenameChange> Preview(IReadOnlyList<RenameTarget> targets, BulkRenameRule rule, CancellationToken cancellationToken = default)
    {
        if (rule.Mode == BulkRenameMode.Replace && string.IsNullOrEmpty(rule.Find)) throw new ArgumentException("RenameFindRequired");
        if (rule.Mode == BulkRenameMode.Numbered && (rule.Start < 1 || (long)rule.Start + Math.Max(0, targets.Count - 1) > int.MaxValue)) throw new ArgumentException("RenameStartInvalid");
        var result = new List<RenameChange>(targets.Count);
        for (int i = 0; i < targets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = targets[i];
            var name = rule.Mode switch
            {
                BulkRenameMode.RenameTo => rule.Replacement,
                BulkRenameMode.Replace => target.Name.Replace(rule.Find, rule.Replacement, StringComparison.OrdinalIgnoreCase),
                BulkRenameMode.Affixes => rule.Prefix + target.Name + rule.Suffix,
                _ => rule.Replacement.Trim() + " " + ((long)rule.Start + i).ToString(CultureInfo.CurrentCulture)
            };
            name = name.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (name.Length == 0) throw new ArgumentException("RenameEmptyName");
            result.Add(new(target.Id, target.Name, name));
        }
        return result;
    }
}
