using Scriblucent.Models;

namespace Scriblucent.Icons;

/// <summary>
/// Curated vector icons for mind-map nodes. Stored on <c>MindMapNode.Icons</c> as
/// <see cref="AppIconKind"/> names. Legacy emoji strings are migrated on load.
/// </summary>
internal static class MindMapNodeIconCatalog
{
    // Same semantic set as the old emoji picker, in the same order.
    public static readonly AppIconKind[] PickerKinds =
    [
        AppIconKind.Pin,           // 📌
        AppIconKind.Star,          // ⭐
        AppIconKind.Lightbulb,     // 💡
        AppIconKind.Target,        // 🎯
        AppIconKind.Notes,         // 📝
        AppIconKind.Bell,          // 🔔
        AppIconKind.Heart,         // ❤️
        AppIconKind.Flame,         // 🔥
        AppIconKind.AiGenerated,   // ✨
        AppIconKind.Book,          // 📚
        AppIconKind.GraduationCap, // 🎓
        AppIconKind.Briefcase,     // 💼
        AppIconKind.Trophy,        // 🏆
        AppIconKind.Color,         // 🎨
        AppIconKind.Rocket,        // 🚀
        AppIconKind.Monitor,       // 💻
        AppIconKind.Chart,         // 📊
        AppIconKind.Search,        // 🔍
        AppIconKind.Settings,      // ⚙️
        AppIconKind.Link,          // 🔗
        AppIconKind.FolderOpen,    // 📁
        AppIconKind.MapPin,        // 📍
        AppIconKind.Checkmark,     // ✔️
        AppIconKind.PartyPopper    // 🎉
    ];

    private static readonly Dictionary<string, AppIconKind> EmojiToKind = new(StringComparer.Ordinal)
    {
        ["📌"] = AppIconKind.Pin,
        ["⭐"] = AppIconKind.Star,
        ["⭐️"] = AppIconKind.Star,
        ["💡"] = AppIconKind.Lightbulb,
        ["\U0001F4A1"] = AppIconKind.Lightbulb,
        ["🎯"] = AppIconKind.Target,
        ["📝"] = AppIconKind.Notes,
        ["🔔"] = AppIconKind.Bell,
        ["\U0001F514"] = AppIconKind.Bell,
        ["❤️"] = AppIconKind.Heart,
        ["❤"] = AppIconKind.Heart,
        ["🔥"] = AppIconKind.Flame,
        ["✨"] = AppIconKind.AiGenerated,
        ["📚"] = AppIconKind.Book,
        ["🎓"] = AppIconKind.GraduationCap,
        ["💼"] = AppIconKind.Briefcase,
        ["🏆"] = AppIconKind.Trophy,
        ["🎨"] = AppIconKind.Color,
        ["🚀"] = AppIconKind.Rocket,
        ["💻"] = AppIconKind.Monitor,
        ["📊"] = AppIconKind.Chart,
        ["🔍"] = AppIconKind.Search,
        ["⚙️"] = AppIconKind.Settings,
        ["⚙"] = AppIconKind.Settings,
        ["🔗"] = AppIconKind.Link,
        ["📁"] = AppIconKind.FolderOpen,
        ["\U0001F4C1"] = AppIconKind.FolderOpen,
        ["📍"] = AppIconKind.MapPin,
        ["✔️"] = AppIconKind.Checkmark,
        ["✅"] = AppIconKind.Checkmark,
        ["✔"] = AppIconKind.Checkmark,
        ["🎉"] = AppIconKind.PartyPopper,
        ["❓"] = AppIconKind.Question,
        ["❌"] = AppIconKind.Close,
        ["⚠️"] = AppIconKind.Warning,
        ["⚠"] = AppIconKind.Warning,
        ["ℹ️"] = AppIconKind.Info,
        ["ℹ"] = AppIconKind.Info,
        ["\U0001F6A9"] = AppIconKind.Flag,
        ["\U0001F4CE"] = AppIconKind.Paperclip,
        ["\U0001F4C5"] = AppIconKind.Schedule,
        ["⏰"] = AppIconKind.Schedule,
        ["⏳"] = AppIconKind.Hourglass,
        ["✏️"] = AppIconKind.Edit,
        ["✏"] = AppIconKind.Edit,
        ["\U0001F512"] = AppIconKind.Lock,
        ["\U0001F516"] = AppIconKind.Bookmark,
        ["\U0001F6D1"] = AppIconKind.Ban
    };

    public static string ToStorageId(AppIconKind kind) => kind.ToString();

    public static bool TryParseKind(string? value, out AppIconKind kind)
    {
        kind = AppIconKind.None;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (Enum.TryParse(trimmed, ignoreCase: true, out kind) && kind != AppIconKind.None)
            return true;

        if (EmojiToKind.TryGetValue(trimmed, out kind))
            return true;

        // Variation-selector / ZWJ normalized fallbacks for common emoji forms.
        var compact = trimmed.Replace("\uFE0F", string.Empty, StringComparison.Ordinal);
        return EmojiToKind.TryGetValue(compact, out kind);
    }

    public static List<string> NormalizeStoredIcons(IEnumerable<string>? icons)
    {
        var result = new List<string>();
        if (icons is null)
            return result;

        foreach (var raw in icons)
        {
            if (!TryParseKind(raw, out var kind))
                continue;

            var id = ToStorageId(kind);
            if (!result.Contains(id, StringComparer.Ordinal))
                result.Add(id);
        }

        return result;
    }

    public static void NormalizeTreeIcons(MindMapNode node)
    {
        node.Icons = NormalizeStoredIcons(node.Icons);
        foreach (var child in node.Children)
            NormalizeTreeIcons(child);
    }
}
