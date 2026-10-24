using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Models;
using Scriblucent.Services;
using WeCantSpell.Hunspell;

internal static partial class Program
{
    private static void ProbeSpellCheck()
    {
        AppSettingsService.SettingsDirectoryOverride = Path.Combine(_output, "settings");
        var settings = new AppSettings { EnableAutoSave = false, EnableAiFeatures = false, EnableSpellCheck = false };
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, settings);
        LoadNativeControlResources("Light"); ThemeManager.SetTheme("Light"); MotionPolicy.Configure(MotionPreference.Off, false);
        var dictionary = WordList.CreateFromWords(["This", "is", "a", "and", "beside", "correct", "word", "text"]);
        var editor = new NoteEditorWindow { Width = 980, Height = 760, ShowInTaskbar = false };
        var results = new List<string>();
        var failures = new List<string>();
        var document = new NoteDocument { Title = "Proofing regression checks", Content = "This is correct text." };
        object? Call(string method, params object[] args) => typeof(NoteEditorWindow)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, args);
        T Field<T>(string name) => (T)typeof(NoteEditorWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
        try
        {
            editor.LoadFromDocument(document); editor.Show(); Pump(250);
            var box = (RichTextBox)editor.FindName("ContentTextBox");
            void SetText(params Block[] blocks)
            {
                Call("ClearSpellCheckDecorations");
                box.Document.Blocks.Clear(); foreach (var block in blocks) box.Document.Blocks.Add(block);
            }
            void Apply() => Call("ApplySpellCheckDecorations", SpellCheckService.FindMisspelledWordSpans((string)Call("GetDocumentSearchText")!, dictionary));
            void Check(string name)
            {
                var text = (string)Call("GetDocumentSearchText")!;
                var expected = SpellCheckService.FindMisspelledWordSpans(text, dictionary);
                var errors = new List<string>();
                foreach (var range in Field<List<TextRange>>("_spellCheckDecoratedRanges"))
                {
                    var word = (string)Call("GetSpellCheckWord", range)!;
                    if (!expected.Any(s => text.Substring(s.Start, s.Length) == word)) errors.Add("misplaced range: " + word);
                }
                for (var i = 0; i < text.Length; i++)
                {
                    if (text[i] == '\n') continue;
                    var start = (TextPointer?)Call("GetTextPointerAtTextOffset", i);
                    var end = (TextPointer?)Call("GetTextPointerAtTextOffset", i + 1);
                    if (start is null || end is null) continue;
                    // TextRange.GetPropertyValue can report mixed parent/child formatting
                    // at a Hyperlink edge. Inspect the text-bearing Run that actually draws it.
                    var leaf = start;
                    while (leaf is not null && leaf.CompareTo(end) < 0
                        && leaf.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text)
                        leaf = leaf.GetNextContextPosition(LogicalDirection.Forward);
                    var actual = leaf?.Parent is Inline inline
                        && inline.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Baseline) == true;
                    if (actual != expected.Any(s => i >= s.Start && i < s.Start + s.Length)) errors.Add($"wrong mark at {i} ({text[i]})");
                }
                var line = name + ": " + (errors.Count == 0 ? "passed" : string.Join(", ", errors.Take(12)));
                results.Add(line); Console.WriteLine(line); if (errors.Count > 0) failures.Add(line);
            }
            SetText(new Paragraph(new Run("This mispelled wrod and baaad text.")));
            Apply(); Check("Multiple misspellings in one run");
            SaveControlCapture(box, "proofing-Light");
            Apply(); Check("Repeated scan");
            SetText(new Paragraph { Inlines = { new Run("This "), new Bold(new Run("mis")), new Italic(new Run("pelled")),
                new Run(" wrod and "), new Run("baaad") { TextDecorations = TextDecorations.Underline }, new Run(" text.") } });
            Apply(); Check("Words spanning styled runs");
            var underlineOffset = ((string)Call("GetDocumentSearchText")!).IndexOf("baaad", StringComparison.Ordinal);
            var underlinedWord = new TextRange((TextPointer)Call("GetTextPointerAtTextOffset", underlineOffset + 1)!,
                (TextPointer)Call("GetTextPointerAtTextOffset", underlineOffset + 4)!);
            Require(underlinedWord.GetPropertyValue(Inline.TextDecorationsProperty)
                is TextDecorationCollection decorations && decorations.Any(d => d.Location == TextDecorationLocation.Underline),
                "Proofing removed authored underline formatting.");
            SetText(new Paragraph(new Run("mispelled")), new Paragraph(new Run("wrod")),
                new System.Windows.Documents.List(new ListItem(new Paragraph(new Run("baaad")))));
            Apply(); Check("Paragraph and list boundaries");
            var table = new Table(); var group = new TableRowGroup(); var row = new TableRow();
            row.Cells.Add(new TableCell(new Paragraph(new Run("mispelled"))));
            row.Cells.Add(new TableCell(new Paragraph(new Run("wrod")))); group.Rows.Add(row); table.RowGroups.Add(group);
            SetText(new Paragraph { Inlines = { new Run("baaad"), new LineBreak(), new Run("mispelled") } }, table);
            Apply(); Check("Line breaks and table cells");
            SetText(new Paragraph(new Run("mispelled")));
            Apply();
            var marked = box.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>())
                .First(r => r.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Baseline) == true);
            box.Document.Blocks.Add(new Paragraph(new Run("correct") { TextDecorations = marked.TextDecorations.Clone() }));
            Apply(); Check("Inherited or pasted spelling marks outside tracked ranges");
            SetText(new Paragraph { Inlines = { new Hyperlink(new Run("mispelled")) { NavigateUri = new Uri("https://example.com") },
                new Run(" and baaad") } });
            Apply();
            Call("ApplyHyperlinkCtrlHintDecorations", true); Check("Ctrl link hints preserve spelling marks");
            Call("ApplyHyperlinkCtrlHintDecorations", false); Check("Releasing Ctrl preserves spelling marks");
            var link = box.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Hyperlink>()).Single();
            new TextRange(link.ContentStart, link.ContentEnd).Text = "correct";
            Apply(); Check("Corrected link text loses its spelling marks");
            SetText(new Paragraph(new Run("mispelled")));
            Apply();
            box.CaretPosition = box.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
            box.CaretPosition.InsertTextInRun(" correct");
            Apply(); Check("Typing at a misspelled word boundary");
            Call("ClearSpellCheckDecorations");
            box.Undo();
            Apply(); Check("Undo after a proofing pass");
            SetText(new Paragraph(new Run("This mispelled wrod and baaad text.")));
            Apply();
            Require(Call("SaveCurrentDocument") is true, "Proofed note did not save.");
            using (var stream = new MemoryStream(Convert.FromBase64String(document.Content)))
            {
                var saved = new FlowDocument(); new TextRange(saved.ContentStart, saved.ContentEnd).Load(stream, DataFormats.XamlPackage);
                for (var p = saved.ContentStart; p is not null && p.CompareTo(saved.ContentEnd) < 0; p = p.GetNextContextPosition(LogicalDirection.Forward))
                    if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                        Require(p.Parent is not Inline inline || inline.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Baseline) != true, "Spelling marks leaked into saved content.");
            }
            Apply(); Check("Save and restore proofing");
            Require(!editor.WorkspaceDirty, "Proofing dirtied an unchanged saved note.");

            var cache = (Dictionary<string, WordList>)typeof(SpellCheckDictionaryService)
                .GetField("LoadedDictionaries", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            cache["audit-proofing"] = dictionary;
            settings.SpellCheckLanguageKey = "audit-proofing";
            settings.EnableSpellCheck = true;
            editor.ApplySpellCheckPreference(true);
            SetText(new Paragraph(new Run("This mispelled wrod and baaad text.")));
            Drain(); Check("Debounced background scan");
            SetText(new Paragraph(new Run(string.Concat(Enumerable.Repeat("correct ", 30000)) + "mispelled")));
            var firstPass = (Task)Call("RunSpellCheckPassAsync")!;
            Call("RunSpellCheckPassAsync");
            Require(Field<bool>("_spellCheckPassPending"), "A scan request was lost while the checker was busy.");
            SetText(new Paragraph(new Run("This baaad text and mispelled wrod.")));
            Call("SpellCheckDebounceTimer_Tick", editor, EventArgs.Empty);
            Drain(); firstPass.GetAwaiter().GetResult(); Check("Rapid edits and overlapping scan requests");
            box.Selection.Select(box.Document.ContentStart, box.Document.ContentEnd);
            box.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, new TextDecorationCollection());
            Drain(); Check("Formatting changes restore proofing");
            Require(Call("SaveCurrentDocument") is true, "Background-proofed note did not save.");
            Drain(); Require(!editor.WorkspaceDirty, "Background proofing dirtied a saved note.");
            editor.LoadFromDocument(document); Drain(); Check("Saved note reopening");
            Require(!editor.WorkspaceDirty, "Opening and proofing a saved note dirtied it.");
            ThemeManager.SetTheme("Dark");
            SetText(Enumerable.Range(0, 35).Select(_ => (Block)new Paragraph(new Run("This mispelled wrod and baaad text."))).ToArray());
            Drain(); Check("Dark theme");
            box.ScrollToVerticalOffset(220); Pump(80); var beforeProofing = box.VerticalOffset;
            Apply(); Pump(80);
            Require(Math.Abs(box.VerticalOffset - beforeProofing) < 1, "Proofing moved the scrolled editor viewport.");
            SaveControlCapture(box, "proofing-Dark-scrolled");
            SetText(new Paragraph(new Run("This baaad text and mispelled wrod."))); Drain();
            settings.SpellCheckCustomWords.Add("mispelled");
            Call("IgnoreMisspelledWordForSession", "baaad");
            editor.RefreshSpellCheckLanguages(); Drain();
            Require(Field<List<TextRange>>("_spellCheckDecoratedRanges").Select(r => (string)Call("GetSpellCheckWord", r)!).SequenceEqual(["wrod"]),
                "Ignore or custom dictionary words were not reflected in the next scan.");
            SetText(new Paragraph(new Run(string.Concat(Enumerable.Repeat("correct ", 30000)) + "wrod")));
            var disabledPass = (Task)Call("RunSpellCheckPassAsync")!;
            editor.ApplySpellCheckPreference(false); Drain(); disabledPass.GetAwaiter().GetResult();
            Require(Field<List<TextRange>>("_spellCheckDecoratedRanges").Count == 0
                && Field<System.Windows.Threading.DispatcherTimer>("_spellCheckDebounceTimer").IsEnabled == false,
                "Disabling proofing allowed a pending scan to restore marks.");
            results.Add("Ignore/custom words and disabling during a scan: passed");

            void Drain()
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                do
                {
                    Pump(30);
                    if (!Field<bool>("_isSpellCheckScanning")
                        && !Field<System.Windows.Threading.DispatcherTimer>("_spellCheckDebounceTimer").IsEnabled) return;
                } while (clock.ElapsedMilliseconds < 8000);
                throw new TimeoutException("Spell checking did not settle.");
            }
            File.WriteAllLines(Path.Combine(_output, "spell-checks.txt"), results);
            Require(failures.Count == 0, "Spelling marks were missing or applied to the wrong text.");
        }
        finally { editor.DisposeWorkspaceEditor(); Pump(50); }
    }
}
