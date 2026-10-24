using System;
using System.IO;
using Ocula.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Ocula.Tests;

public class JustJaredTrimTests
{
    private static readonly (byte, byte, byte) Yellow = (236, 252, 6);
    private static readonly (byte, byte, byte) Grey = (120, 120, 120);

    // A grey photo, optionally with the 3-pixel yellow frame and a yellow "logo" block near the bottom-right corner.
    private static Func<int, int, (byte R, byte G, byte B)> Photo(int w, int h, bool frame, (int X, int Y, int W, int H)? logo)
    {
        return (x, y) =>
        {
            if (frame && (x < 3 || y < 3 || x >= w - 3 || y >= h - 3)) return Yellow;
            if (logo is { } l && x >= l.X && x < l.X + l.W && y >= l.Y && y < l.Y + l.H && (x + y) % 2 == 0) return Yellow;
            return Grey;
        };
    }

    [Fact]
    public void A_frame_and_a_logo_are_both_cut_with_a_margin_above_the_logo()
    {
        // Measured on a real portrait photo (776 x 1222): the lettering ran from y 1133 to 1192.
        var box = JustJaredTrimPlan.Plan(776, 1222, Photo(776, 1222, true, (609, 1133, 110, 60)));
        Assert.Equal(new TrimBox(4, 4, 4, 1222 - 1133 + JustJaredTrimPlan.LogoMargin), box);
    }

    [Fact]
    public void A_frame_without_a_logo_is_cut_evenly()
    {
        var box = JustJaredTrimPlan.Plan(610, 552, Photo(610, 552, true, null));
        Assert.Equal(new TrimBox(4, 4, 4, 4), box);
    }

    [Fact]
    public void A_landscape_photo_with_a_logo_nearer_the_bottom_is_cut_to_just_above_it()
    {
        // Measured: lettering 33 to 88 pixels above the bottom of a 1222 x 827 photo, so it starts at y 739.
        var box = JustJaredTrimPlan.Plan(1222, 827, Photo(1222, 827, true, (1055, 739, 112, 56)));
        Assert.Equal(new TrimBox(4, 4, 4, 827 - 739 + JustJaredTrimPlan.LogoMargin), box);
    }

    [Fact]
    public void A_photo_with_neither_is_left_alone()
    {
        Assert.True(JustJaredTrimPlan.Plan(800, 600, Photo(800, 600, false, null)).IsNone);
    }

    [Fact]
    public void A_logo_without_a_frame_still_cuts_the_bottom_only()
    {
        var box = JustJaredTrimPlan.Plan(800, 600, Photo(800, 600, false, (600, 500, 100, 50)));
        Assert.Equal(new TrimBox(0, 0, 0, 600 - 500 + JustJaredTrimPlan.LogoMargin), box);
    }

    [Fact]
    public void A_few_stray_yellow_pixels_in_the_corner_are_not_a_logo()
    {
        var box = JustJaredTrimPlan.Plan(800, 600, (x, y) => x is >= 700 and < 705 && y is >= 550 and < 553 ? Yellow : Grey);
        Assert.True(box.IsNone);
    }

    [Fact]
    public void A_tiny_image_is_left_alone()
    {
        Assert.True(JustJaredTrimPlan.Plan(50, 50, Photo(50, 50, true, null)).IsNone);
    }

    [Fact]
    public void A_cut_that_would_remove_most_of_the_picture_is_refused()
    {
        // A short picture with a "logo" in its lower corner would lose more than 30% of its height.
        Assert.True(JustJaredTrimPlan.Plan(800, 150, Photo(800, 150, false, (600, 40, 100, 50))).IsNone);
    }

    [Fact]
    public void TrimFile_cuts_a_png_and_keeps_the_rest()
    {
        var path = Path.Combine(Path.GetTempPath(), "ocula-trim-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var image = new Image<Rgba32>(200, 200))
            {
                var photo = Photo(200, 200, true, null);
                for (var y = 0; y < 200; y++)
                    for (var x = 0; x < 200; x++)
                    {
                        var (r, g, b) = photo(x, y);
                        image[x, y] = new Rgba32(r, g, b);
                    }
                image.SaveAsPng(path);
            }

            Assert.True(JustJaredTrim.TrimFile(path));
            using var trimmed = Image.Load<Rgba32>(path);
            Assert.Equal(192, trimmed.Width);
            Assert.Equal(192, trimmed.Height);
            Assert.False(JustJaredTrim.TrimFile(path)); // nothing left to cut the second time
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TrimFile_leaves_a_file_that_is_not_a_picture_alone()
    {
        var path = Path.Combine(Path.GetTempPath(), "ocula-trim-" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            File.WriteAllText(path, "not a picture");
            Assert.False(JustJaredTrim.TrimFile(path));
            Assert.Equal("not a picture", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
