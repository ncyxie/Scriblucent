using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Runtime.InteropServices;

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
    public const string SystemLanguage = "System";

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
        if (string.IsNullOrWhiteSpace(language) || string.Equals(language, SystemLanguage, StringComparison.OrdinalIgnoreCase))
            return SystemLanguage;

        return string.Equals(language, Lithuanian, StringComparison.OrdinalIgnoreCase)
            || language.StartsWith("lt-", StringComparison.OrdinalIgnoreCase)
            ? Lithuanian
            : English;
    }

    internal static string ResolveLanguage(string? language, CultureInfo systemUiCulture)
    {
        var preference = NormalizeLanguage(language);
        return preference == SystemLanguage
            ? (systemUiCulture.TwoLetterISOLanguageName == Lithuanian ? Lithuanian : English)
            : preference;
    }

    private static CultureInfo GetWindowsUiCulture()
    {
        // CurrentUICulture is changed by the app's explicit language choice, so it
        // cannot tell us which language Windows uses when switching back to System.
        try { return CultureInfo.GetCultureInfo(GetUserDefaultUILanguage()); }
        catch (CultureNotFoundException) { return CultureInfo.InstalledUICulture; }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern ushort GetUserDefaultUILanguage();

    public static void SetCulture(string? language)
    {
        var normalized = NormalizeLanguage(language);
        var culture = new CultureInfo(normalized == SystemLanguage
            ? ResolveLanguage(normalized, GetWindowsUiCulture()) : normalized);

        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        LocalizationProvider.Instance.Refresh();
        CultureChanged?.Invoke(null, EventArgs.Empty);
    }
}
