using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Resources;

namespace Scriblucent.Localization;

public sealed class LocalizationProvider : INotifyPropertyChanged
{
    public static LocalizationProvider Instance { get; } = new();

    private LocalizationProvider()
    {
    }

    private static readonly ResourceManager ResourceManager = new("Scriblucent.Resources.Strings", Assembly.GetExecutingAssembly());

    public string this[string key] => ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
}

public static class LocalizationService
{
    public const string English = "en";
    public const string Lithuanian = "lt";

    public static event EventHandler? CultureChanged;

    public static string GetString(string key)
    {
        return LocalizationProvider.Instance[key];
    }

    public static string FormatAiGeneratedTooltip(
        string generatedWithFormatKey,
        string modelDisplayName,
        bool includeReviewDisclaimer = true)
    {
        var generated = string.Format(GetString(generatedWithFormatKey), modelDisplayName.Trim());
        if (!includeReviewDisclaimer)
            return generated;

        return generated + Environment.NewLine + GetString("AiReviewDisclaimer");
    }

    public static string NormalizeLanguage(string? language)
    {
        return string.Equals(language, Lithuanian, StringComparison.OrdinalIgnoreCase)
            ? Lithuanian
            : English;
    }

    public static void SetCulture(string? language)
    {
        var normalized = NormalizeLanguage(language);
        var culture = new CultureInfo(normalized);

        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        LocalizationProvider.Instance.Refresh();
        CultureChanged?.Invoke(null, EventArgs.Empty);
    }
}
