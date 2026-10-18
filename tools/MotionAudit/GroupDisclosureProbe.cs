using System.Collections;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Services;
using Scriblucent.ViewModels;

internal static partial class Program
{
    private static Button FindGroupDisclosure(DashboardGroupHeader header)
        => ControlTree(header).OfType<Button>().Single(button => button.Command == DashboardGroupHeader.ToggleExpansionCommand);
    private static void ProbeGroupDisclosure(Grid root, ObservableCollection<object> groups, DashboardGroupHeader initial,
        string dashboard, string theme, FrameworkElement scene, Window window)
    {
        var model = initial.DataContext;
        var (state, items) = model switch
        {
            NoteGroupViewModel g => (g.Disclosure, (IList)g.Notes),
            FlashcardSetGroupViewModel g => (g.Disclosure, (IList)g.Sets),
            MindMapGroupViewModel g => (g.Disclosure, (IList)g.MindMaps),
            QuizGroupViewModel g => (g.Disclosure, (IList)g.Quizzes),
            _ => throw new InvalidOperationException("Unknown group fixture.")
        };
        var original = items.Cast<object>().ToArray();
        while (items.Count < 24) items.Add(GroupFixture(dashboard).Items[0]);
        Pump(80); window.UpdateLayout();
        DashboardGroupHeader Header() => ControlTree(root).OfType<DashboardGroupHeader>().Single(h => ReferenceEquals(h.DataContext, model));
        Border Face() => ControlTree(root).OfType<Border>().Single(b => ReferenceEquals(b.Tag, model) && CardSurfaceMotionBehavior.GetProfile(b) == CardSurfaceMotionProfile.GroupSlide);
        DashboardGroupRevealHost Body() => ControlTree(Face()).OfType<DashboardGroupRevealHost>().Single();
        Button Toggle() => FindGroupDisclosure(Header());
        void Click() => DashboardGroupHeader.ToggleExpansionCommand.Execute(null, Toggle());
        void CheckChevron()
        {
            var toggle = Toggle();
            var glyph = (FrameworkElement)toggle.Template.FindName("DisclosureChevron", toggle);
            Require(toggle.RenderTransform.Value.IsIdentity && glyph.RenderTransform is RotateTransform rotate
                && Math.Abs(rotate.Angle - (Header().IsExpanded ? 0 : -90)) < .01,
                $"Disclosure pose: expanded={Header().IsExpanded}, bound={SectionChevronMotionBehavior.GetIsExpanded(toggle)}, root={toggle.RenderTransform.Value}, glyph={glyph.RenderTransform.GetType().Name}/{(glyph.RenderTransform as RotateTransform)?.Angle}, loaded={toggle.IsLoaded}/{glyph.IsLoaded}.");
        }
        void Settle()
        {
            Pump(40); window.UpdateLayout();
            for (var i = 0; i < 80 && (!double.IsNaN(Body().Extent) || NativeSurfaceMotion.IsRunning(Body(), MotionChannel.Layout)); i++) Pump(20);
            window.UpdateLayout(); Pump(40);
            Require(double.IsNaN(Body().Extent) && !NativeSurfaceMotion.IsRunning(Body(), MotionChannel.Layout), "Group disclosure did not settle.");
            CheckChevron();
        }
        Require(Body().NaturalExtent > 320 && Body().AnimateLargeExtent, "Group disclosure fixture did not cover a large body.");
        foreach (var mode in new[] { MotionPreference.Automatic, MotionPreference.Reduced, MotionPreference.Off })
        {
            MotionPolicy.Configure(mode, false);
            var otherStates = ControlTree(root).OfType<DashboardGroupHeader>().Where(h => !ReferenceEquals(h.DataContext, model)).Select(h => h.IsExpanded).ToArray();
            Click(); Pump(40);
            if (mode == MotionPreference.Automatic && NativeSurfaceMotion.IsRunning(Body(), MotionChannel.Layout))
            {
                Require(!Body().IsEnabled && !Body().IsHitTestVisible,
                    $"Collapsing content still accepted input: expanded={state.IsExpanded}, header={Header().IsExpanded}, visible={Body().Visibility}, enabled={Body().IsEnabled}, hit={Body().IsHitTestVisible}.");
                Require(((FrameworkElement)Body().Child).ActualHeight > 320, "Collapse resized its card body instead of its layout slot.");
            }
            Settle();
            Require(!state.IsExpanded && !Header().IsExpanded && Body().Visibility == Visibility.Collapsed && Body().DesiredSize.Height == 0,
                "Individual collapse did not hide only the card body.");
            Require(Header().IsVisible && Face().AllowDrop && Header().ItemCount == items.Count,
                "Collapsed group lost its header, count or drop target.");
            Require(Math.Abs(Face().ActualHeight - Header().ActualHeight - 24) < .1, "Collapsed group retained phantom body spacing.");
            Require(Toggle().ToolTip as string == LocalizationService.GetString("ExpandGroup"), "Disclosure tooltip did not follow its state.");
            Require(ControlTree(root).OfType<DashboardGroupHeader>().Where(h => !ReferenceEquals(h.DataContext, model)).Select(h => h.IsExpanded).SequenceEqual(otherStates),
                "Collapsing a group changed another group's state.");
            if (mode == MotionPreference.Automatic) SaveControlCapture(scene, $"disclosure-{dashboard}-{theme}-collapsed");

            var owner = ControlTree(root).OfType<ItemsControl>().Single(c => ReferenceEquals(c.ItemsSource, groups));
            owner.Items.Filter = item => !ReferenceEquals(item, model); Pump(40);
            owner.Items.Filter = null; Pump(60); window.UpdateLayout();
            CheckChevron();
            Require(!Header().IsExpanded && Body().Visibility == Visibility.Collapsed, "Filtering/recreating the header reset a collapsed group.");
            var index = groups.IndexOf(model); groups.Move(index, (index + 1) % groups.Count); Pump(40);
            Require(!Header().IsExpanded, "Reordering expanded a collapsed group.");
            items.Add(GroupFixture(dashboard).Items[0]); Pump(40);
            Require(Header().ItemCount == items.Count && Body().Visibility == Visibility.Collapsed, "Hidden additions lost their count or forced expansion.");

            MainWindow.ExpandDroppedGroup(model); Settle();
            Require(state.IsExpanded && Header().IsExpanded && Body().Visibility == Visibility.Visible && Body().IsEnabled && Body().IsHitTestVisible,
                "Accepted drop did not reopen a usable group.");
            Require(Toggle().ToolTip as string == LocalizationService.GetString("CollapseGroup"), "Expanded disclosure tooltip is incorrect.");
            if (mode == MotionPreference.Automatic)
            {
                for (var i = 0; i < 8; i++) { Click(); Pump(20); }
                Settle();
                Require(state.IsExpanded && Body().Visibility == Visibility.Visible && Body().Child.Opacity == 1 && !Body().ClipToBounds,
                    "Rapid disclosure reversals stranded cards, opacity or clipping.");
            }
            else Require(!NativeSurfaceMotion.IsRunning(Body(), MotionChannel.Layout), "Reduced/Off retained group layout motion.");
        }
        MotionPolicy.Configure(MotionPreference.Off, false);
        items.Clear(); foreach (var item in original) items.Add(item); Settle();
        Require(Header().ItemCount == original.Length, "Disclosure fixture failed to restore its original group count.");
        Console.WriteLine($"PASS disclosure {dashboard}/{theme}: large group, one-group isolation, filtering, reordering, hidden additions, drop reopening, rapid reversal and Automatic/Reduced/Off.");
    }
}
