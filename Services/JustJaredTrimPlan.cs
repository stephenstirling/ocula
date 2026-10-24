using System;

namespace Ocula.Services;

/// <summary>How many pixels to cut from each edge.</summary>
public readonly record struct TrimBox(int Left, int Top, int Right, int Bottom)
{
    public bool IsNone => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;
}

/// <summary>
/// Works out what to cut from a Just Jared photo: the thin yellow frame the site bakes into the picture, and the
/// "JUST JARED" watermark in the bottom-right corner. Pure arithmetic over pixels, so it can be tested without images.
/// Measured on the live site (October 2026): the frame is 3 pixels wide on every edge; the logo's yellow lettering sits
/// 30 to 90 pixels above the bottom edge, and some photos (a sonogram, for one) have the frame but no logo.
/// </summary>
public static class JustJaredTrimPlan
{
    public const int FramePixels = 3;
    public const int FrameCut = FramePixels + 1;   // one more pixel, because the frame's inner edge is blended
    public const int LogoMargin = 16;              // cut this far above the highest lettering pixel, to take the round badge beside it too
    public const int MinLogoPixels = 30;           // fewer yellow pixels than this in the corner is not a logo

    // The frame and the lettering are the same yellow-green; colour is judged loosely so JPEG blur doesn't matter.
    public static bool IsFrameYellow(byte r, byte g, byte b) => r > 200 && g > 200 && b < 100;
    public static bool IsLogoYellow(byte r, byte g, byte b) => r > 190 && g > 170 && b < 90;

    /// <param name="pixel">Reads the red, green and blue of the pixel at (x, y).</param>
    public static TrimBox Plan(int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        if (width < 100 || height < 100) return default;

        // Is there a frame? Sample the middle of each edge: at least 9 in 10 samples of the outer 3 pixels must be yellow.
        int frameSamples = 0, frameHits = 0;
        for (var i = 0; i < 40; i++)
        {
            var x = width * (i + 1) / 41;
            var y = height * (i + 1) / 41;
            for (var d = 0; d < FramePixels; d++)
            {
                foreach (var (px, py) in new[] { (x, d), (x, height - 1 - d), (d, y), (width - 1 - d, y) })
                {
                    var (r, g, b) = pixel(px, py);
                    frameSamples++;
                    if (IsFrameYellow(r, g, b)) frameHits++;
                }
            }
        }
        var hasFrame = frameHits * 10 >= frameSamples * 9;
        var edge = hasFrame ? FrameCut : 0;

        // Is there a logo? Look for logo-coloured pixels in the bottom-right corner, inside the frame.
        var top = Math.Max(0, height - 130);
        int logoTop = int.MaxValue, count = 0;
        for (var y = top; y < height - FrameCut; y++)
        {
            for (var x = width * 2 / 5; x < width - FrameCut; x++)
            {
                var (r, g, b) = pixel(x, y);
                if (!IsLogoYellow(r, g, b)) continue;
                count++;
                if (y < logoTop) logoTop = y;
            }
        }

        var bottom = edge;
        if (count >= MinLogoPixels)
            bottom = Math.Max(edge, height - logoTop + LogoMargin);

        var box = new TrimBox(edge, edge, edge, bottom);
        // Never take away most of a picture: if the cut is implausibly large, leave it alone.
        if (width - box.Left - box.Right < width / 2 || height - box.Top - box.Bottom < height * 7 / 10) return default;
        return box;
    }
}
