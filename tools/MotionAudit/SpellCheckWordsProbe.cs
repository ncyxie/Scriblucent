using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.Views;
using WeCantSpell.Hunspell;

internal static partial class Program
{
    private static void ProbeSpellCheckWords()
    {
        AppSettingsService.SettingsDirectoryOverride = Path.Combine(_output, "settings");
        var settings = new AppSettings { EnableAutoSave = false, EnableAiFeatures = false, EnableSpellCheck = true,
            SpellCheckLanguageKey = "audit-proofing", SpellCheckCustomWords = ["Scriblucent", "mispelled"] };
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, settings);
        var cache = (Dictionary<string, WordList>)typeof(SpellCheckDictionaryService)
            .GetField("LoadedDictionaries", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        cache["audit-proofing"] = WordList.CreateFromWords(["correct", "and", "text"]);
        LoadNativeControlResources("Light"); ThemeManager.SetTheme("Light");
        LocalizationService.SetCulture("en"); MotionPolicy.Configure(MotionPreference.Off, false);
        var first = new NoteEditorWindow { Width = 1000, Height = 820, ShowInTaskbar = false };
        var second = new NoteEditorWindow { Width = 900, Height = 740, ShowInTaskbar = false };
        object? Call(object target, string method, params object[] args) => target.GetType()
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
        T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
        void Click(Window dialog, string name) => ((Button)dialog.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            foreach (var editor in new[] { first, second })
            {
                editor.LoadFromDocument(new NoteDocument { Title = "Dictionary checks", Content = "mispelled and baaad text" }); editor.Show();
            }
            Pump(700);
            var preview = new SpellCheckWordsDialog(settings.SpellCheckCustomWords) { Owner = first }; preview.Show(); Pump(80);
            var search = (TextBox)preview.FindName("SearchBox"); var input = (TextBox)preview.FindName("NewWordBox");
            var list = (ListBox)preview.FindName("WordsList");
            search.Text = "MIS"; Require(list.Items.Count == 1, "Custom-word search is not case insensitive.");
            input.Text = "two words"; Click(preview, "AddButton");
            Require(((InlineValidationPresenter)preview.FindName("Validation")).IsError, "Multiple-word dictionary input was accepted.");
            input.Text = "MISPELLED"; Click(preview, "AddButton"); Require(preview.Words.Count == 2, "A case-variant duplicate was added.");
            input.Text = "ąžuolas"; Click(preview, "AddButton"); Require(preview.Words.Contains("ąžuolas"), "A Lithuanian custom word could not be added.");
            list.SelectedItems.Clear(); list.SelectedItems.Add("mispelled"); list.SelectedItems.Add("Scriblucent"); Click(preview, "RemoveButton");
            Require(preview.Words.SequenceEqual(["ąžuolas"]), "Removing several custom words failed.");
            SaveControlCapture((FrameworkElement)preview.Content, "custom-words-Light");
            Call(preview, "Cancel_Click", preview, new RoutedEventArgs()); Pump(50);
            Require(settings.SpellCheckCustomWords.SequenceEqual(["Scriblucent", "mispelled"]), "Cancel changed the saved dictionary.");

            InDialog(dialog =>
            {
                var words = (ListBox)dialog.FindName("WordsList"); words.SelectedItem = "mispelled";
                Click(dialog, "RemoveButton"); Click(dialog, "SaveButton");
            }, () => SpellCheckWordsDialog.ManageCustomDictionary(first));
            Pump(250);
            Require(!settings.SpellCheckCustomWords.Contains("mispelled"), "Saved custom-word removal was not persisted.");
            foreach (var editor in new[] { first, second })
                Require(Field<List<TextRange>>(editor, "_spellCheckDecoratedRanges").Any(r => (string)Call(editor, "GetSpellCheckWord", r)! == "mispelled"),
                    "Removing a custom word did not refresh every open editor.");

            Call(first, "IgnoreMisspelledWordForSession", "baaad"); Pump(200);
            InDialog(dialog =>
            {
                Click(dialog, "ResetIgnoredButton"); Call(dialog, "Cancel_Click", dialog, new RoutedEventArgs());
            }, () => Call(first, "ManageIgnoredSpellCheckWords"));
            Require(Field<HashSet<string>>(first, "_sessionIgnoredWords").Contains("baaad"), "Cancel changed this note's ignored words.");
            InDialog(dialog =>
            {
                Require(((Grid)dialog.FindName("AddRow")).Visibility == Visibility.Collapsed, "Ignored-word manager offers permanent additions.");
                Click(dialog, "ResetIgnoredButton"); Click(dialog, "SaveButton");
            }, () => Call(first, "ManageIgnoredSpellCheckWords"));
            Pump(200);
            Require(Field<HashSet<string>>(first, "_sessionIgnoredWords").Count == 0, "Check all again did not clear this note's ignored words.");
            Require(Field<List<TextRange>>(first, "_spellCheckDecoratedRanges").Any(r => (string)Call(first, "GetSpellCheckWord", r)! == "baaad"), "Unignored word did not regain a spelling mark.");

            Call(first, "IgnoreMisspelledWordForSession", "baaad"); Pump(200);
            var box = (RichTextBox)first.FindName("ContentTextBox");
            var offset = ((string)Call(first, "GetDocumentSearchText")!).IndexOf("baaad", StringComparison.Ordinal) + 2;
            var pointer = (TextPointer)Call(first, "GetTextPointerAtTextOffset", offset)!;
            var bounds = pointer.GetCharacterRect(LogicalDirection.Forward);
            var menu = new ContextMenu();
            Call(first, "AddSpellCheckReversalMenu", menu, new Point(bounds.X + 1, bounds.Y + bounds.Height / 2));
            var unignore = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, LocalizationService.GetString("SpellCheckUnignoreWord")));
            unignore.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump(200);
            Require(!Field<HashSet<string>>(first, "_sessionIgnoredWords").Contains("baaad"), "Right-click stop-ignoring action failed.");
            Call(first, "ClearSpellCheckReversalMenu"); Require(menu.Items.Count == 0, "Word actions leaked into the next context menu.");
            Call(first, "AddWordToCustomDictionary", "mispelled"); Pump(200);
            foreach (var editor in new[] { first, second })
                Require(!Field<List<TextRange>>(editor, "_spellCheckDecoratedRanges").Any(r => (string)Call(editor, "GetSpellCheckWord", r)! == "mispelled"), "Adding a custom word did not refresh every open editor.");
            pointer = (TextPointer)Call(first, "GetTextPointerAtTextOffset", 3)!;
            bounds = pointer.GetCharacterRect(LogicalDirection.Forward);
            Call(first, "AddSpellCheckReversalMenu", menu, new Point(bounds.X + 1, bounds.Y + bounds.Height / 2));
            menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, LocalizationService.GetString("SpellCheckRemoveCustomWord")))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump(200);
            Require(!settings.SpellCheckCustomWords.Contains("mispelled"), "Right-click custom-word removal failed.");
            Call(first, "ClearSpellCheckReversalMenu");

            foreach (var theme in new[] { "Light", "Dark" })
            foreach (var language in new[] { "en", "lt" })
            {
                ThemeManager.SetTheme(theme); LocalizationService.SetCulture(language);
                var dialog = new SpellCheckWordsDialog(["Scriblucent", "ąžuolas", "ncyxie", "Mindmap"]) { Owner = first };
                dialog.Show(); Pump(80);
                var card = ControlTree((DependencyObject)dialog.Content).OfType<Border>().Single(b => b.Name == "DialogCard");
                Require(card.ActualWidth <= dialog.ActualWidth - 47 && card.ActualHeight <= dialog.ActualHeight - 47, "Word manager exceeds its owner.");
                SaveControlCapture((FrameworkElement)dialog.Content, $"custom-words-{theme}-{language}");
                Call(dialog, "Cancel_Click", dialog, new RoutedEventArgs()); Pump(30);
                var ignored = new SpellCheckWordsDialog([], ignored: true) { Owner = first }; ignored.Show(); Pump(50);
                Require(((TextBlock)ignored.FindName("EmptyText")).Visibility == Visibility.Visible, "Empty ignored-word list has no explanation.");
                SaveControlCapture((FrameworkElement)ignored.Content, $"ignored-words-empty-{theme}-{language}");
                Call(ignored, "Cancel_Click", ignored, new RoutedEventArgs()); Pump(30);
            }
            var narrowOwner = new Window { Width = 640, Height = 560, ShowInTaskbar = false, Content = new Grid() }; narrowOwner.Show();
            var manyWords = new SpellCheckWordsDialog(Enumerable.Range(0, 250).Select(i => "customword" + i)) { Owner = narrowOwner };
            manyWords.Show(); Pump(80);
            var narrowCard = ControlTree((DependencyObject)manyWords.Content).OfType<Border>().Single(b => b.Name == "DialogCard");
            Require(narrowCard.ActualHeight <= manyWords.ActualHeight - 47, "Word manager clips on a small owner.");
            var scrollingWords = (ListBox)manyWords.FindName("WordsList");
            Require(scrollingWords.ItemContainerGenerator.ContainerFromIndex(249) is null, "Off-screen dictionary words were all realized.");
            scrollingWords.ScrollIntoView(scrollingWords.Items[249]); Pump(80);
            Require(scrollingWords.ItemContainerGenerator.ContainerFromIndex(249) is not null, "Dictionary words cannot be scrolled to the end.");
            Call(manyWords, "Cancel_Click", manyWords, new RoutedEventArgs()); narrowOwner.Close(); Pump(30);
            AppSettingsService.Flush();
            var persisted = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(AppSettingsService.SettingsDirectoryOverride, "settings.json")));
            Require(persisted!.SpellCheckCustomWords.SequenceEqual(settings.SpellCheckCustomWords), "Custom dictionary changes were not written to settings.");
            Console.WriteLine("Custom/ignored words: search, validation, duplicate prevention, multi-remove, cancel/save, app-wide refresh, reset ignored words, right-click reversal, persistence and EN/LT layouts passed.");
        }
        finally { first.DisposeWorkspaceEditor(); second.DisposeWorkspaceEditor(); Pump(50); }

        void InDialog(Action<SpellCheckWordsDialog> action, Action open)
        {
            Exception? error = null; bool answered = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            timer.Tick += (_, _) =>
            {
                var dialog = _app.Windows.OfType<SpellCheckWordsDialog>().FirstOrDefault(d => d.IsVisible);
                if (dialog is null) return;
                timer.Stop(); answered = true;
                try { action(dialog); }
                catch (Exception ex) { error = ex; Call(dialog, "Cancel_Click", dialog, new RoutedEventArgs()); }
            };
            timer.Start(); try { open(); } finally { timer.Stop(); }
            Require(answered, "Word manager did not open."); if (error is not null) throw error;
        }
    }
}
