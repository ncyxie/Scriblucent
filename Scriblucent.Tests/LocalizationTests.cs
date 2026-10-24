using System.Collections;
using System.Globalization;
using System.Resources;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using Scriblucent.Localization;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class LocalizationTests
{
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
