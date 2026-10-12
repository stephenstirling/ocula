using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

/// <summary>
/// Where photo windows go on a monitor (PhotoWindowLayout): spots tile the work area exactly,
/// fewer windows re-flow into wider spots, and new windows take the lowest free spot.
/// </summary>
public class PhotoWindowLayoutTests
{
    // A 1920 x 1080 monitor with a 48 px taskbar, and a 3440 x 1440 ultrawide with the taskbar on a second monitor.
    public static TheoryData<int, int, int, int> Areas => new()
    {
        { 0, 0, 1920, 1032 },
        { -3440, 0, 3440, 1440 },
    };

    private static readonly SnapLayout[] Tiling =
        { SnapLayout.Columns, SnapLayout.LargeAndRest, SnapLayout.TopHeavyRows, SnapLayout.BottomHeavyRows };

    [Theory]
    [MemberData(nameof(Areas))]
    public void Every_tiling_layout_fills_the_work_area_exactly_for_one_to_six_windows(int x, int y, int width, int height)
    {
        var area = new SpotRect(x, y, width, height);
        foreach (var layout in Tiling)
        {
            for (var count = 1; count <= PhotoWindowLayout.MaxSpots; count++)
            {
                var spots = PhotoWindowLayout.Spots(layout, count, area);

                Assert.Equal(count, spots.Count);
                Assert.All(spots, s => Assert.True(area.Contains(s), $"{layout} x{count}: {s} is outside {area}"));
                Assert.All(spots, s => Assert.True(s.Width > 0 && s.Height > 0));
                for (var i = 0; i < spots.Count; i++)
                    for (var j = i + 1; j < spots.Count; j++)
                        Assert.False(spots[i].Overlaps(spots[j]), $"{layout} x{count}: {spots[i]} overlaps {spots[j]}");
                Assert.Equal((long)width * height, spots.Sum(s => (long)s.Width * s.Height));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Areas))]
    public void Columns_are_equal_width_left_to_right(int x, int y, int width, int height)
    {
        var area = new SpotRect(x, y, width, height);
        for (var count = PhotoWindowLayout.MinSpots; count <= PhotoWindowLayout.MaxSpots; count++)
        {
            var spots = PhotoWindowLayout.Spots(SnapLayout.Columns, count, area);
            Assert.True(spots.Max(s => s.Width) - spots.Min(s => s.Width) <= 1);
            Assert.All(spots, s => Assert.Equal(height, s.Height));
            Assert.Equal(spots.OrderBy(s => s.X), spots);
        }
    }

    [Fact]
    public void One_large_plus_four_puts_a_half_width_window_left_and_a_two_by_two_grid_right()
    {
        var spots = PhotoWindowLayout.Spots(SnapLayout.LargeAndRest, 5, new SpotRect(0, 0, 1920, 1040));

        Assert.Equal(new SpotRect(0, 0, 960, 1040), spots[0]);
        Assert.Equal(new SpotRect(960, 0, 480, 520), spots[1]);
        Assert.Equal(new SpotRect(1440, 0, 480, 520), spots[2]);
        Assert.Equal(new SpotRect(960, 520, 480, 520), spots[3]);
        Assert.Equal(new SpotRect(1440, 520, 480, 520), spots[4]);
    }

    [Fact]
    public void Three_plus_two_has_three_on_top_and_two_below()
    {
        var spots = PhotoWindowLayout.Spots(SnapLayout.TopHeavyRows, 5, new SpotRect(0, 0, 1920, 1040));

        Assert.Equal(3, spots.Count(s => s.Y == 0));
        Assert.Equal(2, spots.Count(s => s.Y == 520));
        Assert.Equal(640, spots[0].Width);
        Assert.Equal(960, spots[3].Width);
    }

    [Fact]
    public void Two_plus_three_has_two_on_top_and_three_below()
    {
        var spots = PhotoWindowLayout.Spots(SnapLayout.BottomHeavyRows, 5, new SpotRect(0, 0, 1920, 1040));

        Assert.Equal(2, spots.Count(s => s.Y == 0));
        Assert.Equal(3, spots.Count(s => s.Y == 520));
    }

    [Theory]
    [InlineData(SnapLayout.Columns, 5, "5 columns")]
    [InlineData(SnapLayout.Columns, 3, "3 columns")]
    [InlineData(SnapLayout.LargeAndRest, 5, "1 large + 4")]
    [InlineData(SnapLayout.TopHeavyRows, 5, "3 + 2")]
    [InlineData(SnapLayout.BottomHeavyRows, 5, "2 + 3")]
    [InlineData(SnapLayout.TopHeavyRows, 6, "3 + 3")]
    [InlineData(SnapLayout.Cascade, 5, "Cascade")]
    public void Layout_names_follow_the_number_of_spots(SnapLayout layout, int spots, string name) =>
        Assert.Equal(name, PhotoWindowLayout.Name(layout, spots));

    [Theory]
    [MemberData(nameof(Areas))]
    public void Cascade_windows_step_down_and_right_and_stay_on_the_monitor(int x, int y, int width, int height)
    {
        var area = new SpotRect(x, y, width, height);
        var spots = PhotoWindowLayout.Spots(SnapLayout.Cascade, 6, area);

        Assert.Equal(6, spots.Count);
        Assert.All(spots, s => Assert.True(area.Contains(s)));
        Assert.True(spots[1].X > spots[0].X && spots[1].Y > spots[0].Y);
        Assert.All(spots, s => Assert.Equal(spots[0].Width, s.Width));
    }

    [Fact]
    public void Three_windows_in_five_columns_reflow_into_three_wider_columns_in_spot_order()
    {
        var area = new SpotRect(0, 0, 1920, 1040);

        // Windows hold spots 4, 0 and 2 (opened in that order).
        var placed = PhotoWindowLayout.Place(SnapLayout.Columns, new[] { 4, 0, 2 }, area);

        Assert.Equal(new SpotRect(1280, 0, 640, 1040), placed[0]); // spot 4: rightmost
        Assert.Equal(new SpotRect(0, 0, 640, 1040), placed[1]);    // spot 0: leftmost
        Assert.Equal(new SpotRect(640, 0, 640, 1040), placed[2]);  // spot 2: middle
    }

    [Fact]
    public void New_windows_take_the_lowest_free_spot_and_none_when_full()
    {
        Assert.Equal(0, PhotoWindowLayout.NextFreeSpot(Array.Empty<int>(), 5));
        Assert.Equal(1, PhotoWindowLayout.NextFreeSpot(new[] { 0, 2, 3 }, 5));
        Assert.Equal(4, PhotoWindowLayout.NextFreeSpot(new[] { 3, 2, 1, 0 }, 5));
        Assert.Null(PhotoWindowLayout.NextFreeSpot(new[] { 0, 1, 2, 3, 4 }, 5));
        Assert.Null(PhotoWindowLayout.NextFreeSpot(new[] { 0, 1 }, 2));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(5, 5)]
    [InlineData(9, 6)]
    public void Spot_count_stays_between_two_and_six(int asked, int expected) =>
        Assert.Equal(expected, PhotoWindowLayout.ClampSpots(asked));

    [Fact]
    public void Centre_is_two_thirds_size_in_the_middle()
    {
        var centre = PhotoWindowLayout.Centre(new SpotRect(100, 0, 1800, 900));
        Assert.Equal(new SpotRect(400, 150, 1200, 600), centre);
    }
}
