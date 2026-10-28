using System.Collections;
using System.Globalization;
using System.Resources;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class LocalizationTests
{
    [Fact]
    public void New_settings_follow_system_and_existing_explicit_choices_are_preserved()
    {
        var defaults = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.Equal(LocalizationService.SystemLanguage, defaults.Language);
        Assert.Equal(ThemeManager.SystemTheme, defaults.Theme);
        Assert.Equal(AppSettings.UnitSystemSystem, defaults.MeasurementUnitSystem);
        foreach (var (language, theme, units) in new[] { ("en", "Light", "Imperial"), ("lt", "Dark", "Metric"), ("System", "System", "System") })
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings { Language = language, Theme = theme, MeasurementUnitSystem = units }))!;
            Assert.Equal(language, settings.Language);
            Assert.Equal(theme, settings.Theme);
            Assert.Equal(units, settings.MeasurementUnitSystem);
        }
    }

    [Theory]
    [InlineData("metric", "Metric")]
    [InlineData("Imperial", "Imperial")]
    public void Explicit_measurement_choices_override_Windows(string preference, string expected)
        => Assert.Equal(expected, MeasurementUnitService.Resolve(preference));

    [Fact]
    public void System_measurements_ignore_app_language_and_match_editor_and_template_labels() => Sta(() =>
    {
        var original = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture,
            CultureInfo.DefaultThreadCurrentCulture, CultureInfo.DefaultThreadCurrentUICulture);
        try
        {
            var system = MeasurementUnitService.Resolve(AppSettings.UnitSystemSystem);
            using var regionalSettings = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\International");
            if (regionalSettings?.GetValue("iMeasure") is string measure && measure is "0" or "1")
                Assert.Equal(measure == "0" ? AppSettings.UnitSystemMetric : AppSettings.UnitSystemImperial, system);
            var expected = system == AppSettings.UnitSystemMetric ? "1.27 cm" : "0.5 in";
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            foreach (var language in new[] { "en", "lt" })
            {
                LocalizationService.SetCulture(language);
                Assert.Equal(system, MeasurementUnitService.Resolve(AppSettings.UnitSystemSystem));
                Assert.Equal(expected, typeof(Views.SettingsPanel).GetMethod("FormatTemplateIndent", flags)!
                    .Invoke(null, new object[] { 48d, AppSettings.UnitSystemSystem }));
                Assert.Equal(expected, typeof(NoteEditorWindow).GetMethod("FormatIndentSizeLabel", flags)!
                    .Invoke(null, new object[] { 48d, system }));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original.Item1; CultureInfo.CurrentUICulture = original.Item2;
            CultureInfo.DefaultThreadCurrentCulture = original.Item3; CultureInfo.DefaultThreadCurrentUICulture = original.Item4;
            LocalizationProvider.Instance.Refresh();
        }
    });

    [Theory]
    [InlineData("System", "lt-LT", "lt")]
    [InlineData("system", "en-GB", "en")]
    [InlineData(null, "lt-LT", "lt")]
    [InlineData("System", "de-DE", "en")]
    [InlineData("lt", "en-US", "lt")]
    [InlineData("en", "lt-LT", "en")]
    [InlineData("lt-LT", "en-US", "lt")]
    public void Language_preferences_resolve_supported_Windows_languages_and_fallback(string? preference, string windowsLanguage, string expected)
        => Assert.Equal(expected, LocalizationService.ResolveLanguage(preference, CultureInfo.GetCultureInfo(windowsLanguage)));

    [Fact]
    public void Switching_back_to_system_does_not_use_the_last_app_language() => Sta(() =>
    {
        var original = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture,
            CultureInfo.DefaultThreadCurrentCulture, CultureInfo.DefaultThreadCurrentUICulture);
        try
        {
            LocalizationService.SetCulture("lt");
            LocalizationService.SetCulture(LocalizationService.SystemLanguage);
            var systemLanguage = CultureInfo.CurrentUICulture.Name;
            LocalizationService.SetCulture(systemLanguage == "lt" ? "en" : "lt");
            LocalizationService.SetCulture(LocalizationService.SystemLanguage);
            Assert.Equal(systemLanguage, CultureInfo.CurrentUICulture.Name);
            Assert.Equal(systemLanguage, CultureInfo.DefaultThreadCurrentUICulture!.Name);
            Assert.Equal(systemLanguage, CultureInfo.CurrentCulture.Name);
        }
        finally
        {
            CultureInfo.CurrentCulture = original.Item1; CultureInfo.CurrentUICulture = original.Item2;
            CultureInfo.DefaultThreadCurrentCulture = original.Item3; CultureInfo.DefaultThreadCurrentUICulture = original.Item4;
            LocalizationProvider.Instance.Refresh();
        }
    });

    [Fact]
    public void Lithuanian_catalog_has_every_English_entry_without_fallback()
    {
        var manager = new ResourceManager("Scriblucent.Resources.Strings", typeof(LocalizationService).Assembly);
        Dictionary<string, string> Catalog(string culture) => manager
            .GetResourceSet(CultureInfo.GetCultureInfo(culture), true, false)!
            .Cast<DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!);
        var english = Catalog("");
        var lithuanian = Catalog("lt");
        Assert.Equal(english.Keys.Order(), lithuanian.Keys.Order());
        foreach (var key in english.Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(lithuanian[key]), key);
            Assert.Equal(CompositeFormat(english[key]), CompositeFormat(lithuanian[key]));
        }
    }

    private static string[] CompositeFormat(string value) => System.Text.RegularExpressions.Regex
        .Matches(value, @"(?<!\{)\{(\d+)(?:[^{}]*)\}(?!\})")
        .Select(match => match.Groups[1].Value).Distinct().Order().ToArray();

    [Fact]
    public void Quiz_action_labels_follow_language_changes() => Sta(() =>
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !Directory.Exists(Path.Combine(folder.FullName, "Scriblucent"))) folder = folder.Parent;
        Assert.NotNull(folder);
        var source = XDocument.Load(Path.Combine(folder.FullName, "Scriblucent/Views/QuizModeWindow.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var original = CultureInfo.CurrentUICulture.Name;
        try
        {
            foreach (var (name, english, lithuanian) in new[]
            {
                ("ShowHintButton", "Show hint", "Rodyti užuominą"),
                ("RetryAllButton", "Retry all", "Kartoti visus"),
                ("RetryWrongButton", "Retry incorrect", "Kartoti klaidingus")
            })
            {
                var button = source.Descendants().Single(node => (string?)node.Attribute(xaml + "Name") == name);
                var labelSource = new XElement(button.Descendants().Single(node => node.Name.LocalName == "TextBlock"));
                labelSource.SetAttributeValue(XNamespace.Xmlns + "x", xaml.NamespaceName);
                labelSource.SetAttributeValue(XNamespace.Xmlns + "localization", "clr-namespace:Scriblucent.Localization;assembly=Scriblucent");
                var label = (TextBlock)XamlReader.Parse(labelSource.ToString().Replace("{StaticResource Loc}", "{x:Static localization:LocalizationProvider.Instance}"));
                foreach (var (culture, expected) in new[] { ("en", english), ("lt", lithuanian), ("en", english) })
                {
                    LocalizationService.SetCulture(culture);
                    label.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
                    Assert.Equal(expected, label.Text);
                }
            }
        }
        finally { LocalizationService.SetCulture(original); }
    });

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
