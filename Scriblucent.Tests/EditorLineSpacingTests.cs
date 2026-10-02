using System.Windows.Media;
using Scriblucent.Services;

namespace Scriblucent.Tests;

public class EditorLineSpacingTests
{
    [Fact]
    public void Menu_steps_multiply_the_font_line_calibri_draws() => RunSta(() =>
    {
        var family = new FontFamily("Calibri");
        var single = EditorLineSpacing.SingleSpacing(family);
        Assert.InRange(single, 1.20, 1.24);

        const double size = 14;
        var singleLine = size * single;
        Assert.Equal(singleLine, EditorLineSpacing.LineHeight(singleLine, 1.0), 3);
        Assert.Equal(singleLine * 1.15, EditorLineSpacing.LineHeight(singleLine, 1.15), 3);
        Assert.Equal(singleLine * 1.5, EditorLineSpacing.LineHeight(singleLine, 1.5), 3);
        Assert.Equal(singleLine * 2, EditorLineSpacing.LineHeight(singleLine, 2.0), 3);
        Assert.True(EditorLineSpacing.LineHeight(singleLine, 1.15) > singleLine + 1);
    });

    [Fact]
    public void Each_font_keeps_its_own_single_line() => RunSta(() =>
    {
        var calibri = EditorLineSpacing.SingleSpacing(new FontFamily("Calibri"));
        var arial = EditorLineSpacing.SingleSpacing(new FontFamily("Arial"));
        var segoe = EditorLineSpacing.SingleSpacing(new FontFamily("Segoe UI"));
        Assert.NotEqual(calibri, arial);
        Assert.True(segoe > calibri);
        Assert.Equal(20 * arial * 1.15, EditorLineSpacing.LineHeight(20 * arial, 1.15), 3);
    });

    [Fact]
    public void An_older_point_size_step_still_reads_as_that_step() => RunSta(() =>
    {
        var single = 14 * EditorLineSpacing.SingleSpacing(new FontFamily("Calibri"));

        Assert.Equal(1.15, EditorLineSpacing.ReadMultiplier(14 * 1.15, 14, single));
        Assert.True(EditorLineSpacing.IsLegacyPointSizeStep(14 * 1.15, 14, single, out var legacy));
        Assert.Equal(1.15, legacy);

        Assert.Equal(1.5, EditorLineSpacing.ReadMultiplier(single * 1.5, 14, single));
        Assert.False(EditorLineSpacing.IsLegacyPointSizeStep(single * 1.5, 14, single, out _));
        Assert.Equal(1.0, EditorLineSpacing.ReadMultiplier(single, 14, single));
    });

    private static void RunSta(Action scenario)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { scenario(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA line-spacing scenario timed out");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
