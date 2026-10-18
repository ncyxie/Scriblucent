using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeCaptionLayout()
    {
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, new AppSettings { EnableAutoSave = false });
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var language in new[] { "en", "lt" })
        foreach (var mode in new[] { MotionPreference.Automatic, MotionPreference.Reduced, MotionPreference.Off })
        {
            LoadNativeControlResources(theme); Scriblucent.ThemeManager.SetTheme(theme);
            LocalizationService.SetCulture(language); MotionPolicy.Configure(mode, false);
            var suffix = $"{theme}-{language}-{mode}";
            var owner = new Window { Width = 760, Height = 660, Content = new Grid(), ShowInTaskbar = false };
            owner.Show(); Pump(20);
            var dialog = new InsertCaptionDialog("Figure", "A caption with room to breathe", CaptionNumbering.Arabic, ": ",
                CaptionPosition.Below, false, new[] { "Figure", "Diagram", "Custom label" }, new[] { "Figure" }, _ => 1, true) { Owner = owner };
            dialog.Show(); Pump(450); dialog.UpdateLayout();
            var shell = (OverlayDialogShell)dialog.Content;
            var field = (Border)dialog.FindName("CaptionFieldShell");
            var caption = (TextBox)dialog.FindName("CaptionTextBox");
            var combo = (ComboBox)dialog.FindName("LabelComboBox");
            Require(!caption.IsKeyboardFocusWithin && !combo.IsKeyboardFocusWithin, "Opening caption dialog focused an input before interaction.");
            Require(ControlTree(shell).OfType<TextBox>().All(t => !t.IsKeyboardFocused), "Opening dialog has an input focus ring.");
            var captionBounds = FinishingBounds(caption, field);
            Require(captionBounds.Left >= 9.5 && captionBounds.Top >= 7.5 && field.ActualHeight >= 63.5, $"Caption text has no proper inset/typing height: {captionBounds}; shell={field.RenderSize}, padding={field.Padding}.");
            Require(caption.Padding == new Thickness(), "Caption text double-counts shell padding.");
            Require(combo.Template.FindName("ComboBorder", combo) is SurfaceMaterialBorder face && SurfaceMaterial.GetKind(face) == SurfaceMaterialKind.Neutral,
                "Editable label still uses the old flat face.");
            var arrow = (ToggleButton)combo.Template.FindName("DropDownToggle", combo);
            var chevron = (FrameworkElement)arrow.Template.FindName("Chevron", arrow);
            var arrowCenter = chevron.TranslatePoint(new Point(chevron.ActualWidth / 2, chevron.ActualHeight / 2), combo);
            Require(Math.Abs(arrowCenter.X - (combo.ActualWidth - 16)) < .6, "Editable arrow lost its right-hand slot.");
            SaveControlCapture(shell, "caption-open-" + suffix);
            shell.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); Pump(80);
            Require(combo.IsKeyboardFocusWithin, "Neutral opening focus broke first Tab entry.");
            var edit = (TextBox)combo.Template.FindName("PART_EditableTextBox", combo);
            edit.Text = "A new label"; Pump(50);
            Require(dialog.ResultLabel == "A new label" && ((TextBlock)dialog.FindName("PreviewTextBlock")).Text.Contains("A new label"), "Editable label lost typing/preview behavior.");
            combo.IsDropDownOpen = true; Pump(180);
            var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
            Require(popup.IsOpen && ControlTree(popup.Child).OfType<ComboBoxItem>().Any(), "Label menu failed to realize items.");
            SaveControlCapture((FrameworkElement)PresentationSource.FromVisual(popup.Child)!.RootVisual, "caption-label-menu-" + suffix);
            combo.SelectedIndex = 1; combo.IsDropDownOpen = false; Pump(80);
            Require(dialog.ResultLabel == "Diagram", "Label choice no longer copies into editable text.");
            caption.Text = string.Join("\n", Enumerable.Range(1, 10).Select(i => "Caption line " + i + " remains inside its own typing area."));
            owner.Height = 480; owner.Width = 520; Pump(60);
            // Model opening the dialog against these smaller owner bounds using its production helper.
            OverlayDialogBoundsHelper.Apply(dialog); Pump(100); dialog.UpdateLayout();
            Require(dialog.ActualWidth <= 520 && dialog.ActualHeight <= 480, "Short-owner fixture did not actually constrain the dialog.");
            var viewer = (ScrollViewer)dialog.FindName("CaptionBodyScrollViewer");
            Require(viewer.ScrollableHeight > 0, "Long captions in a short owner cannot scroll.");
            var footer = (FrameworkElement)shell.Footer; var footerBounds = FinishingBounds(footer, shell);
            Require(footerBounds.Top > 0 && footerBounds.Bottom <= shell.ActualHeight + .1, "Caption footer was clipped by long content.");
            caption.Focus(); Pump(160); field.BringIntoView(); Pump(40); dialog.UpdateLayout();
            var innerScroll = (ScrollViewer)caption.Template.FindName("PART_ContentHost", caption);
            Require(caption.ActualHeight <= 96 && innerScroll.ScrollableHeight > 0, "Long caption text escaped its bounded text viewport.");
            innerScroll.ScrollToEnd(); Pump(60);
            Require(innerScroll.VerticalOffset > 0, "Caption typing viewport does not scroll.");
            SaveControlCapture(shell, "caption-long-short-owner-" + suffix);
            edit.Text = ""; RemainingCall(dialog, "Ok_Click", dialog, new RoutedEventArgs()); Pump(180);
            Require(combo.IsKeyboardFocusWithin && ((InlineValidationPresenter)dialog.FindName("LabelValidationMessage")).IsError,
                "Label validation lost native input focus.");
            dialog.Close(); owner.Close(); Pump(350);
            Require(!RenderTransitionClock.IsAnyRunning, "Caption dialog retained motion after unloading.");
            checks.Add(suffix + ": neutral opening, inset multiline text, first Tab, editable material/right-aligned arrow, label typing/selection/validation, bounded scrolling and pinned footer passed.");
            Console.WriteLine("Completed " + suffix);
        }
        Require(typeof(AppSettingsService).GetField("_pendingJson", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) is null,
            "Caption fixture queued preferences writes.");
        File.WriteAllLines(Path.Combine(_output, "checks.txt"), checks);
        File.WriteAllText(Path.Combine(_output, "README.md"), "# Caption dialog follow-up\n\nProduction compiled dialog, synthetic preferences and caption content. Captures use 100/125/150/200% raster scale, not physical-monitor DPI testing or presented-FPS measurements. No saved data is changed.\n\n" + string.Join("\n", checks.Select(c => "- " + c)));
    }
}
