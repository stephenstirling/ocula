using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Ocula.Services;

/// <summary>
/// Cuts the Just Jared frame and logo off a downloaded photo (see <see cref="JustJaredTrimPlan"/>). A JPEG can't be cropped
/// to arbitrary pixels without being saved again, so the result is a new JPEG at quality 95. The file keeps its name.
/// </summary>
public static class JustJaredTrim
{
    /// <summary>Returns true when the file was changed. Any problem leaves the original untouched and returns false.</summary>
    public static bool TrimFile(string path)
    {
        string? temp = null;
        try
        {
            using var image = Image.Load<Rgba32>(path, out var format);
            var box = JustJaredTrimPlan.Plan(image.Width, image.Height, (x, y) =>
            {
                var p = image[x, y];
                return (p.R, p.G, p.B);
            });
            if (box.IsNone) return false;

            image.Mutate(c => c.Crop(new Rectangle(box.Left, box.Top, image.Width - box.Left - box.Right, image.Height - box.Top - box.Bottom)));

            temp = path + ".trim.tmp";
            using (var stream = File.Create(temp))
            {
                if (format.Name == "JPEG") image.SaveAsJpeg(stream, new JpegEncoder { Quality = 95 });
                else image.Save(stream, format);
            }
            File.Move(temp, path, overwrite: true);
            temp = null;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (temp != null) { try { File.Delete(temp); } catch (Exception) { } }
        }
    }
}
