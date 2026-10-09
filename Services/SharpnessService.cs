using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Ocula.Services;

/// <summary>
/// Scores how sharp a photo is, from 0 (very blurry) to 100 (crisp).
///
/// Method: the photo is decoded at about 1,000 px and converted to brightness. A Laplacian
/// filter (which responds to fine edges and goes quiet on blur) is run over it, and its
/// variance measured in each cell of a 10×10 grid. The score comes from the sharpest cells
/// (the 90th percentile), not the average, so a portrait with a deliberately blurred
/// background still scores well when the subject is in focus.
///
/// The thresholds are a starting point; very noisy high-ISO photos can score a little high.
/// </summary>
public static class SharpnessService
{
    public const int SoftThreshold = 35;

    private const int DecodeSize = 1024;
    private const int Grid = 10;
    private const double ReferenceVariance = 1500; // variance that maps to a score of 100

    public static async Task<int?> MeasureAsync(string path, byte[]? fallbackThumbnail = null)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            return await MeasureStreamAsync(stream);
        }
        catch
        {
            // No Windows decoder for this file (e.g. some RAW formats without the Raw Image
            // Extension): use the cached thumbnail instead. Less precise, but still useful.
            if (fallbackThumbnail == null) return null;
            try
            {
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(fallbackThumbnail.AsBuffer());
                stream.Seek(0);
                return await MeasureStreamAsync(stream);
            }
            catch
            {
                return null;
            }
        }
    }

    private static async Task<int> MeasureStreamAsync(IRandomAccessStream stream)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var scale = Math.Min(1.0, DecodeSize / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var w = Math.Max(16, (int)(decoder.PixelWidth * scale));
        var h = Math.Max(16, (int)(decoder.PixelHeight * scale));

        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            new BitmapTransform { ScaledWidth = (uint)w, ScaledHeight = (uint)h, InterpolationMode = BitmapInterpolationMode.Fant },
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var bgra = data.DetachPixelData();

        return Score(bgra, w, h);
    }

    private static int Score(byte[] bgra, int w, int h)
    {
        // Brightness (Rec. 709 weights).
        var luma = new float[w * h];
        for (var i = 0; i < luma.Length; i++)
            luma[i] = 0.0722f * bgra[i * 4] + 0.7152f * bgra[i * 4 + 1] + 0.2126f * bgra[i * 4 + 2];

        // Laplacian variance per grid cell.
        var cellW = w / Grid;
        var cellH = h / Grid;
        var variances = new List<double>(Grid * Grid);
        for (var gy = 0; gy < Grid; gy++)
        {
            for (var gx = 0; gx < Grid; gx++)
            {
                double sum = 0, sumSq = 0;
                var n = 0;
                var y0 = Math.Max(1, gy * cellH);
                var y1 = Math.Min(h - 1, (gy + 1) * cellH);
                var x0 = Math.Max(1, gx * cellW);
                var x1 = Math.Min(w - 1, (gx + 1) * cellW);
                for (var y = y0; y < y1; y++)
                {
                    var row = y * w;
                    for (var x = x0; x < x1; x++)
                    {
                        var i = row + x;
                        double lap = 4 * luma[i] - luma[i - 1] - luma[i + 1] - luma[i - w] - luma[i + w];
                        sum += lap;
                        sumSq += lap * lap;
                        n++;
                    }
                }
                if (n > 0) variances.Add(sumSq / n - (sum / n) * (sum / n));
            }
        }
        if (variances.Count == 0) return 0;

        variances.Sort();
        var sharpest = variances[(int)(variances.Count * 0.9)];
        var score = 100 * Math.Log10(1 + sharpest) / Math.Log10(1 + ReferenceVariance);
        return (int)Math.Round(Math.Clamp(score, 0, 100));
    }
}
