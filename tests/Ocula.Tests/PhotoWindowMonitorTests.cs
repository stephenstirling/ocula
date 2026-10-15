using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

/// <summary>
/// Spot rectangles on realistic monitors: 1920 x 1080 and a 3440 x 1440 ultrawide, with the
/// taskbar at the bottom or on the left, and two monitors with different sizes and scaling side by
/// side. Everything is in physical pixels of the monitor concerned (what the windows are moved in),
/// and each monitor's work area (its bounds minus the taskbar) is what the spots must stay inside.
/// </summary>
public class PhotoWindowMonitorTests
{
    /// <summary>A monitor: where it is, and the taskbar on it (a strip on one edge, in physical pixels).</summary>
    public sealed record Monitor(string Name, SpotRect Bounds, string TaskbarEdge, int TaskbarSize)
    {
        public SpotRect WorkArea => TaskbarEdge switch
        {
            "bottom" => Bounds with { Height = Bounds.Height - TaskbarSize },
            "top" => new(Bounds.X, Bounds.Y + TaskbarSize, Bounds.Width, Bounds.Height - TaskbarSize),
            "left" => new(Bounds.X + TaskbarSize, Bounds.Y, Bounds.Width - TaskbarSize, Bounds.Height),
            "right" => Bounds with { Width = Bounds.Width - TaskbarSize },
            _ => Bounds,
        };

        public SpotRect Taskbar => TaskbarEdge switch
        {
            "bottom" => new(Bounds.X, Bounds.Bottom - TaskbarSize, Bounds.Width, TaskbarSize),
            "top" => new(Bounds.X, Bounds.Y, Bounds.Width, TaskbarSize),
            "left" => new(Bounds.X, Bounds.Y, TaskbarSize, Bounds.Height),
            "right" => new(Bounds.Right - TaskbarSize, Bounds.Y, TaskbarSize, Bounds.Height),
            _ => new(0, 0, 0, 0),
        };

        public override string ToString() => Name;
    }

    private static readonly Monitor FullHdBottom = new("1920x1080, taskbar bottom", new(0, 0, 1920, 1080), "bottom", 48);
    private static readonly Monitor FullHdLeft = new("1920x1080, taskbar left", new(0, 0, 1920, 1080), "left", 48);
    private static readonly Monitor UltrawideBottom = new("3440x1440, taskbar bottom", new(0, 0, 3440, 1440), "bottom", 48);
    private static readonly Monitor UltrawideLeft = new("3440x1440, taskbar left", new(0, 0, 3440, 1440), "left", 48);

    // Beside the ultrawide, at 150% scaling: its taskbar is 72 physical pixels high.
    private static readonly Monitor SecondaryAt150 = new("2560x1440 at 150%, to the right", new(3440, 0, 2560, 1440), "bottom", 72);

    public static TheoryData<Monitor> Monitors => new() { FullHdBottom, FullHdLeft, UltrawideBottom, UltrawideLeft, SecondaryAt150 };

    public static IEnumerable<SnapLayout> AllLayouts => Enum.GetValues<SnapLayout>();
    private static readonly SnapLayout[] Tiling =
        { SnapLayout.Columns, SnapLayout.LargeAndRest, SnapLayout.TopHeavyRows, SnapLayout.BottomHeavyRows };

    [Fact]
    public void The_test_monitors_have_the_expected_work_areas()
    {
        Assert.Equal(new SpotRect(0, 0, 1920, 1032), FullHdBottom.WorkArea);
        Assert.Equal(new SpotRect(48, 0, 1872, 1080), FullHdLeft.WorkArea);
        Assert.Equal(new SpotRect(0, 0, 3440, 1392), UltrawideBottom.WorkArea);   // this PC's monitor
        Assert.Equal(new SpotRect(48, 0, 3392, 1440), UltrawideLeft.WorkArea);
    }

    [Theory]
    [MemberData(nameof(Monitors))]
    public void Spots_for_two_to_six_windows_tile_the_work_area_and_never_touch_the_taskbar(Monitor monitor)
    {
        foreach (var layout in Tiling)
        {
            for (var spots = PhotoWindowLayout.MinSpots; spots <= PhotoWindowLayout.MaxSpots; spots++)
            {
                var rects = PhotoWindowLayout.Spots(layout, spots, monitor.WorkArea);
                var label = $"{monitor}, {layout}, {spots} spots";

                Assert.True(spots == rects.Count, $"{label}: {rects.Count} rectangles");
                Assert.All(rects, r => Assert.True(monitor.WorkArea.Contains(r), $"{label}: {r} leaves the work area"));
                Assert.All(rects, r => Assert.False(r.Overlaps(monitor.Taskbar), $"{label}: {r} covers the taskbar"));
                for (var i = 0; i < rects.Count; i++)
                    for (var j = i + 1; j < rects.Count; j++)
                        Assert.False(rects[i].Overlaps(rects[j]), $"{label}: {rects[i]} overlaps {rects[j]}");
                Assert.True((long)monitor.WorkArea.Width * monitor.WorkArea.Height == rects.Sum(r => (long)r.Width * r.Height),
                            $"{label}: leaves gaps");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Monitors))]
    public void Cascade_stays_inside_the_work_area_for_two_to_six_windows(Monitor monitor)
    {
        for (var spots = PhotoWindowLayout.MinSpots; spots <= PhotoWindowLayout.MaxSpots; spots++)
            Assert.All(PhotoWindowLayout.Spots(SnapLayout.Cascade, spots, monitor.WorkArea),
                       r => Assert.True(monitor.WorkArea.Contains(r) && !r.Overlaps(monitor.Taskbar), $"{monitor}: {r}"));
    }

    [Theory]
    [MemberData(nameof(Monitors))]
    public void Columns_run_left_to_right_from_the_edge_of_the_work_area(Monitor monitor)
    {
        for (var spots = PhotoWindowLayout.MinSpots; spots <= PhotoWindowLayout.MaxSpots; spots++)
        {
            var rects = PhotoWindowLayout.Spots(SnapLayout.Columns, spots, monitor.WorkArea);

            // Spot 1 starts where the work area does: right of a left-hand taskbar, not under it.
            Assert.Equal(monitor.WorkArea.X, rects[0].X);
            Assert.Equal(monitor.WorkArea.Right, rects[^1].Right);
            Assert.Equal(rects.OrderBy(r => r.X), rects);
            Assert.InRange(rects.Max(r => r.Width) - rects.Min(r => r.Width), 0, 1);
            Assert.All(rects, r => Assert.Equal(monitor.WorkArea.Height, r.Height));
            for (var i = 1; i < rects.Count; i++) Assert.Equal(rects[i - 1].Right, rects[i].X); // no gap between neighbours
        }
    }

    [Theory]
    [MemberData(nameof(Monitors))]
    public void Fewer_windows_than_spots_keep_their_spot_rectangles_and_do_not_re_flow(Monitor monitor)
    {
        foreach (var layout in AllLayouts)
        {
            for (var spots = PhotoWindowLayout.MinSpots; spots <= PhotoWindowLayout.MaxSpots; spots++)
            {
                var all = PhotoWindowLayout.Spots(layout, spots, monitor.WorkArea);

                for (var open = 1; open < spots; open++)
                {
                    // The first `open` spots taken, then the last `open` spots taken.
                    var first = Enumerable.Range(0, open).ToList();
                    var last = Enumerable.Range(spots - open, open).ToList();

                    Assert.Equal(first.Select(s => all[s]), PhotoWindowLayout.Place(layout, spots, first, monitor.WorkArea));
                    Assert.Equal(last.Select(s => all[s]), PhotoWindowLayout.Place(layout, spots, last, monitor.WorkArea));
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Monitors))]
    public void A_lone_window_in_a_middle_spot_is_one_spot_wide_not_the_whole_screen(Monitor monitor)
    {
        for (var spots = 3; spots <= PhotoWindowLayout.MaxSpots; spots++)
        {
            var middle = spots / 2;
            var placed = PhotoWindowLayout.Place(SnapLayout.Columns, spots, new[] { middle }, monitor.WorkArea).Single();

            Assert.InRange(placed.Width, monitor.WorkArea.Width / spots, monitor.WorkArea.Width / spots + 1);
            Assert.True(placed.X > monitor.WorkArea.X && placed.Right < monitor.WorkArea.Right);
        }
    }

    [Fact]
    public void Each_monitor_is_laid_out_in_its_own_physical_pixels()
    {
        // The ultrawide and the 150% monitor beside it: spots on one never leak onto the other, and
        // each monitor's columns add up to that monitor's own width.
        foreach (var layout in AllLayouts)
        {
            for (var spots = PhotoWindowLayout.MinSpots; spots <= PhotoWindowLayout.MaxSpots; spots++)
            {
                var onUltrawide = PhotoWindowLayout.Spots(layout, spots, UltrawideBottom.WorkArea);
                var onSecondary = PhotoWindowLayout.Spots(layout, spots, SecondaryAt150.WorkArea);

                Assert.All(onUltrawide, r => Assert.True(UltrawideBottom.Bounds.Contains(r) && !r.Overlaps(SecondaryAt150.Bounds)));
                Assert.All(onSecondary, r => Assert.True(SecondaryAt150.Bounds.Contains(r) && !r.Overlaps(UltrawideBottom.Bounds) && !r.Overlaps(SecondaryAt150.Taskbar)));
            }

            var columns = PhotoWindowLayout.Spots(SnapLayout.Columns, 5, SecondaryAt150.WorkArea);
            Assert.Equal(2560, columns.Sum(c => c.Width));
            Assert.Equal(SecondaryAt150.WorkArea.Height, columns[0].Height); // 1440 - 72 for the taskbar
        }
    }

    [Theory]
    [MemberData(nameof(Monitors))]
    public void The_centred_window_is_inside_the_work_area(Monitor monitor)
    {
        var centred = PhotoWindowLayout.Centre(monitor.WorkArea);

        Assert.True(monitor.WorkArea.Contains(centred));
        Assert.False(centred.Overlaps(monitor.Taskbar));
        Assert.InRange(centred.X - monitor.WorkArea.X - (monitor.WorkArea.Right - centred.Right), -1, 1); // centred left to right
    }

    [Theory]
    [InlineData(8, 0, 8, 8)]    // 100% scaling: about 7-8 px of invisible border
    [InlineData(12, 0, 12, 12)] // 150%
    [InlineData(0, 0, 0, 0)]    // a window with no border
    public void Windows_grow_by_their_invisible_border_so_the_visible_frames_meet(int left, int top, int right, int bottom)
    {
        var borders = new WindowFrame.Insets(left, top, right, bottom);
        var spots = PhotoWindowLayout.Spots(SnapLayout.Columns, 4, UltrawideBottom.WorkArea);

        var windows = spots.Select(s => WindowFrame.Expand(s, borders)).ToList();

        for (var i = 0; i < spots.Count; i++)
        {
            // Take the border back off and the visible frame is exactly the spot.
            Assert.Equal(spots[i], new SpotRect(windows[i].X + left, windows[i].Y + top,
                                                windows[i].Width - left - right, windows[i].Height - top - bottom));
        }
    }
}
