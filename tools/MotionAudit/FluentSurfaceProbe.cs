using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;

internal static partial class Program
{
    private static void ProbeFluentSurfaces()
    {
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, new AppSettings { EnableAutoSave = false });
        var checks = new List<string>();
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var sources = Directory.EnumerateFiles("Scriblucent", "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .Select(path => (Path: path, Source: XDocument.Load(path)))
            .Where(item => item.Source.Root?.Name == wpf + "Window" &&
                item.Source.Descendants().Any(node => node.Name.LocalName == nameof(FeatureWindowShell))).ToArray();
        Require(sources.Length == 13, "Feature-window coverage changed; update the audit deliberately.");
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var language in new[] { "en", "lt" })
        {
            LoadNativeControlResources(theme); LocalizationService.SetCulture(language);
            MotionPolicy.Configure(MotionPreference.Off, false);
            foreach (var item in sources)
            {
                var root = item.Source.Root!;
                var body = root.Elements().First(node => !node.Name.LocalName.Contains('.'));
                var wrapper = new XElement(wpf + "Grid");
                if (root.Element(wpf + "Window.Resources") is { } resources)
                    wrapper.Add(new XElement(wpf + "Grid.Resources", resources.Elements()));
                wrapper.Add(new XElement(body));
                StripFixtureEvents(wrapper);
                var content = Fragment(wrapper, root);
                var window = new Window
                {
                    Content = content, Width = (double?)root.Attribute("Width") ?? 920,
                    Height = (double?)root.Attribute("Height") ?? 720,
                    ShowInTaskbar = false, ShowActivated = false,
                    Title = Path.GetFileNameWithoutExtension(item.Path)
                };
                window.SetResourceReference(Control.BackgroundProperty, "WorkspaceBackdrop");
                if (root.Attribute(x + "Name") is { } name) window.Name = name.Value;
                ShellWindowChrome.TryAttach(window, ShellChromeKind.Client);
                try
                {
                    window.Show(); Pump(60); window.UpdateLayout();
                    CheckWorkspaceCaption(window); CheckOwnedChrome(window);
                    var shell = ControlTree(window).OfType<FeatureWindowShell>().Single();
                    Require(ReferenceEquals(shell.Background, window.FindResource("WorkspaceBackdrop")), "Canvas mismatch: " + item.Path);
                    var frame = (SurfaceMaterialBorder)shell.Template.FindName("StageFace", shell);
                    Require(frame.CornerRadius == new CornerRadius(18) && SurfaceMaterial.GetKind(frame) == SurfaceMaterialKind.Workspace &&
                        ReferenceEquals(frame.Background, window.FindResource("WindowBackground")), "Workspace frame differs: " + item.Path);
                    Require(shell.Template.FindName("WorkspaceWash", shell) is null && shell.Template.FindName("StageShadow", shell) is null,
                        "A legacy wash or inset shadow remains: " + item.Path);
                    SaveControlCapture((FrameworkElement)window.Content, $"shell-{Path.GetFileNameWithoutExtension(item.Path)}-{theme}-{language}");
                    checks.Add($"{theme}/{language}: {Path.GetFileName(item.Path)} production markup loads, owned styles, shared canvas/caption; shared rounded frame and static perimeter light; no decorative wash.");
                }
                finally { window.Close(); Pump(30); }
            }
        }
        Require(typeof(AppSettingsService).GetField("_pendingJson", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) is null,
            "A fixture queued a settings write.");
        Require(!RenderTransitionClock.IsAnyRunning, "Feature fixtures left idle motion work.");
        File.WriteAllLines(Path.Combine(_output, "feature-shell-checks.txt"), checks);
        foreach (var check in checks) Console.WriteLine(check);
    }

    // Production layout/resources, without application handlers or editor services/user-data operations.
    private static void StripFixtureEvents(XElement wrapper)
    {
        foreach (var setter in wrapper.Descendants().Where(node => node.Name.LocalName == "EventSetter").ToArray()) setter.Remove();
        foreach (var node in wrapper.DescendantsAndSelf())
        {
            // Loose fixture loading cannot reproduce BAML's unshared resource factory semantics.
            node.Attribute(XName.Get("Shared", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Remove();
            var ns = node.Name.NamespaceName;
            var type = ns.StartsWith("clr-namespace:")
                ? typeof(FeatureWindowShell).Assembly.GetType(ns[14..].Split(';')[0] + "." + node.Name.LocalName)
                : typeof(Control).Assembly.GetType("System.Windows.Controls." + node.Name.LocalName)
                  ?? typeof(Control).Assembly.GetType("System.Windows.Controls.Primitives." + node.Name.LocalName);
            foreach (var attribute in node.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration &&
                attribute.Name.NamespaceName.Length == 0 && type?.GetEvent(attribute.Name.LocalName) is not null).ToArray()) attribute.Remove();
        }
    }
}
