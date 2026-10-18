using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Scriblucent.Animations;
using Scriblucent.Models;
using Scriblucent.Services;

internal static partial class Program
{
    private static void ProbeWrappedNoteSave()
    {
        AppSettingsService.SettingsDirectoryOverride = Path.Combine(_output, "settings");
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null,
            new AppSettings { EnableAutoSave = false, EnableAiFeatures = false });
        LoadNativeControlResources("Light"); Scriblucent.ThemeManager.SetTheme("Light"); MotionPolicy.Configure(MotionPreference.Automatic, false);
        var bytes = File.ReadAllBytes(Path.Combine(Directory.GetCurrentDirectory(), "Scriblucent.Tests", "TestData", "Images", "pixel.png"));
        var checks = new List<string>(); var failures = new List<string>();
        foreach (var mode in new[] { NoteImageWrapMode.Square, NoteImageWrapMode.TopAndBottom })
        {
            var document = new NoteDocument { Title = "Wrapped save " + mode,
                Content = string.Join("\n\n", Enumerable.Repeat(string.Join(" ", Enumerable.Repeat("Text flows beside or below this picture without changing the authored paragraph.", 8)), 6)),
                Images = [new NoteImageAttachment { Data = Convert.ToBase64String(bytes), Width = 200, Height = 140, Left = 36, Top = 100, LockAspectRatio = false, WrapMode = mode }] };
            var editor = new Scriblucent.NoteEditorWindow { Width = 980, Height = 760, ShowInTaskbar = false };
            try
            {
                editor.LoadFromDocument(document);
                var images = CompletionField<FloatingImageCoordinator>(editor, "_floatingImages");
                images.MediaDirectory = Path.Combine(_output, "media");
                editor.Show(); Pump(1500);
                var wrap = CompletionField<NoteWrapLayoutEngine>(editor, "_wrapLayout");
                Require(images.GetWrapSessions().Count == 1 && wrap.CaptureWrapState() is not null, mode + " fixture did not actually displace text.");
                for (int pass = 0; pass < 2; pass++)
                {
                    Require(RemainingCall(editor, "SaveCurrentDocument") is true, "Wrapped note save failed.");
                    Require(!editor.WorkspaceDirty, "Save did not initially acknowledge the note.");
                    long savedVersion = CompletionField<long>(editor, "_editorChangeVersion");
                    // Both idle debounce work and an unchanged geometry rebuild are cosmetic.
                    if (pass == 1) wrap.Reapply();
                    bool dirtyDuringSettle = false;
                    for (int tick = 0; tick < 20; tick++) { Pump(100); dirtyDuringSettle |= editor.WorkspaceDirty; }
                    long settledVersion = CompletionField<long>(editor, "_editorChangeVersion");
                    if (dirtyDuringSettle) failures.Add($"{mode}/save {pass + 1}: idle note became edited, version {savedVersion} -> {settledVersion}.");
                    else checks.Add($"{mode}/save {pass + 1}: clean through delayed wrap/layout work, version {savedVersion} -> {settledVersion}.");
                }
                // A fix must not swallow real typing, field edits, formatting or image changes.
                var content = (RichTextBox)editor.FindName("ContentTextBox");
                content.CaretPosition = content.Document.ContentEnd;
                content.CaretPosition.InsertTextInRun(" Authored after save."); Pump(100);
                Require(editor.WorkspaceDirty, "A genuine text edit was suppressed.");
                Require(RemainingCall(editor, "SaveCurrentDocument") is true, "Second authored save failed.");
                ((TextBox)editor.FindName("TitleTextBox")).Text += " immediate title edit"; Pump(600);
                Require(editor.WorkspaceDirty, "A delayed baseline swallowed an immediate title edit.");
                Require(RemainingCall(editor, "SaveCurrentDocument") is true, "Title save failed.");
                images.SetWrapMode(document.Images[0].Id, mode == NoteImageWrapMode.Square ? NoteImageWrapMode.TopAndBottom : NoteImageWrapMode.Square); Pump(150);
                Require(editor.WorkspaceDirty, "An authored wrap-mode change was suppressed.");
                checks.Add(mode + ": real text, immediate title and image wrap-mode edits remain dirty.");
                foreach (var edit in new[] { "title", "text", "image" })
                {
                    RemainingCall(editor, "PerformAutoSave");
                    Require(!editor.WorkspaceDirty, mode + ": autosave did not acknowledge its captured revision immediately.");
                    var savedTitle = document.Title;
                    if (edit == "title") ((TextBox)editor.FindName("TitleTextBox")).Text += " after autosave";
                    else if (edit == "text") content.Document.ContentEnd.InsertTextInRun(" Typed immediately after autosave.");
                    else images.SetWrapMode(document.Images[0].Id, document.Images[0].WrapMode == NoteImageWrapMode.Square ? NoteImageWrapMode.TopAndBottom : NoteImageWrapMode.Square);
                    Require(editor.WorkspaceDirty, mode + ": immediate " + edit + " edit was not marked dirty.");
                    Pump(650);
                    Require(editor.WorkspaceDirty, mode + ": autosave swallowed a subsequent " + edit + " edit.");
                    Require(document.Title == savedTitle, "Unsaved title leaked into the captured document.");
                }
                RemainingCall(editor, "PerformAutoSave"); wrap.Reapply(); Pump(650);
                Require(!editor.WorkspaceDirty, mode + ": unchanged layout became dirty after autosave.");
                checks.Add(mode + ": autosave acknowledges only the saved revision; later title/text/image edits stay dirty and cosmetic reflow stays clean.");
            }
            finally { editor.DisposeWorkspaceEditor(); Pump(100); }
        }
        File.WriteAllLines(Path.Combine(_output, "wrapped-save-checks.txt"), checks.Concat(failures));
        foreach (var check in checks.Concat(failures)) Console.WriteLine(check);
        Require(failures.Count == 0, "Wrapped image layout re-marked a saved note as edited.");
    }
}
