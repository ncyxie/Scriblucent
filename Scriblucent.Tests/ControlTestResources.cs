using System.Windows;
using System.Windows.Markup;
using System.Xml.Linq;

namespace Scriblucent.Tests;

internal static class ControlTestResources
{
    internal static ResourceDictionary Load(string name)
    {
        var uri = new Uri($"pack://application:,,,/Scriblucent;component/Themes/{name}.xaml");
        if (name != "Controls/Chrome") return new ResourceDictionary { Source = uri };

        // Chrome's deferred bindings normally resolve Loc in App.Resources. These
        // isolated STA windows have no Application; provide that resource in the
        // parsing scope without replacing the production binding or template.
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !Directory.Exists(Path.Combine(folder.FullName, "Scriblucent"))) folder = folder.Parent;
        var document = XDocument.Load(Path.Combine(folder!.FullName, "Scriblucent", "Themes", name + ".xaml"));
        var root = document.Root!;
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        root.SetAttributeValue(XNamespace.Xmlns + "localization", "clr-namespace:Scriblucent.Localization");
        var localization = new XElement(xaml + "Static", new XAttribute(xaml + "Key", "Loc"),
            new XAttribute("Member", "localization:LocalizationProvider.Instance"));
        root.Element(presentation + "ResourceDictionary.MergedDictionaries")!.AddAfterSelf(localization);
        var text = System.Text.RegularExpressions.Regex.Replace(root.ToString(),
            "clr-namespace:(Scriblucent[^\";]*)", "clr-namespace:$1;assembly=Scriblucent");
        return (ResourceDictionary)XamlReader.Parse(text, new ParserContext { BaseUri = uri });
    }
}
