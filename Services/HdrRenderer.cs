using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Ocula.Services;

/// <summary>What the current monitor can show. Brightness values are in nits.</summary>
public sealed record DisplayHdr(bool IsHdr, float SdrWhiteNits, float PeakNits)
{
    /// <summary>How many times brighter than normal white the screen can go.</summary>
    public float Headroom => Math.Max(1f, PeakNits / SdrWhiteNits);

    public static readonly DisplayHdr Standard = new(false, 80f, 80f);
}

/// <summary>
/// A decoded HDR preview: RGBA half-floats in scRGB, where 1.0 means 80 nits. This is the
/// format Windows composes HDR content in, so the pixels go to the screen as they are.
/// </summary>
public sealed record HdrImage(Half[] Pixels, int Width, int Height, int Orientation);

/// <summary>
/// Renders HDR photos for an HDR display. All maths runs on the CPU over a preview-sized
/// image (decoded directly at that size), which keeps it quick without needing custom shaders.
/// </summary>
public static class HdrRenderer
{
    public static bool CanRender(string path, string? kind) => kind switch
    {
        HdrDetector.GainMap => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jfif",
        HdrDetector.PQ or HdrDetector.HLG => true,
        _ => false
    };

    public static async Task<HdrImage?> RenderAsync(string path, string? kind, int maxSize, DisplayHdr display)
    {
        if (!display.IsHdr || !CanRender(path, kind)) return null;
        return kind == HdrDetector.GainMap
            ? await RenderGainMapAsync(path, maxSize, display)
            : await RenderTransferAsync(path, kind!, maxSize, display);
    }

    // ================= Gain maps (Ultra HDR, Adobe) =================

    private static async Task<HdrImage?> RenderGainMapAsync(string path, int maxSize, DisplayHdr display)
    {
        var file = await File.ReadAllBytesAsync(path);
        if (GainMapReader.Read(file) is not { } found || found.Params.BaseIsHdr) return null;
        var p = found.Params;

        var (base8, w, h, orientation) = await DecodeAsync(file, maxSize, BitmapPixelFormat.Rgba8);
        var (map8, _, _, _) = await DecodeAsync(found.GainMap, 0, BitmapPixelFormat.Rgba8, (w, h)); // stretched to match

        // How much of the gain map to apply: all of it if the screen has at least the photo's
        // intended headroom, none on a standard screen, and proportionally in between.
        var headroomLog2 = MathF.Log2(display.Headroom);
        var weight = p.CapacityMax <= p.CapacityMin
            ? (headroomLog2 >= p.CapacityMax ? 1f : 0f)
            : Math.Clamp((headroomLog2 - p.CapacityMin) / (p.CapacityMax - p.CapacityMin), 0f, 1f);

        // Lookup tables: sRGB → linear, and gain-map byte → brightness multiplier per channel.
        var linear = new float[256];
        for (var i = 0; i < 256; i++) linear[i] = SrgbToLinear(i / 255f);
        var gain = new float[3][];
        for (var c = 0; c < 3; c++)
        {
            gain[c] = new float[256];
            for (var i = 0; i < 256; i++)
            {
                var g = MathF.Pow(i / 255f, 1f / p.Gamma[c]);
                var log2Boost = p.Min[c] + (p.Max[c] - p.Min[c]) * g;
                gain[c][i] = MathF.Pow(2f, log2Boost * weight);
            }
        }

        // SDR white should look exactly as bright as Windows' own SDR white.
        var scale = display.SdrWhiteNits / 80f;
        var pixels = new Half[w * h * 4];
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                for (var c = 0; c < 3; c++)
                {
                    var hdr = (linear[base8[i + c]] + p.OffsetSdr[c]) * gain[c][map8[i + c]] - p.OffsetHdr[c];
                    pixels[i + c] = (Half)(Math.Max(0f, hdr) * scale);
                }
                pixels[i + 3] = (Half)1f;
            }
        });
        return new HdrImage(pixels, w, h, orientation);
    }

    // ================= PQ and HLG (AVIF, HEIC, PNG) =================

    private static async Task<HdrImage?> RenderTransferAsync(string path, string kind, int maxSize, DisplayHdr display)
    {
        // Windows' decoders (with the AV1 / HEIF extensions for AVIF and HEIC) hand back the
        // encoded 10/12-bit signal, widened to 16 bits.
        var file = await File.ReadAllBytesAsync(path);
        var (raw, w, h, orientation) = await DecodeAsync(file, maxSize, BitmapPixelFormat.Rgba16);
        var signal = MemoryMarshal.Cast<byte, ushort>(raw).ToArray();

        var curve = kind == HdrDetector.PQ ? PqLut.Value : HlgLut.Value;
        var peak = display.PeakNits;
        var knee = peak * 0.75f;
        var pixels = new Half[w * h * 4];

        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                float r = curve[signal[i]], g = curve[signal[i + 1]], b = curve[signal[i + 2]];

                if (kind == HdrDetector.HLG)
                {
                    // HLG stores scene light; the reference display (1,000 nits, system gamma 1.2)
                    // brightens it based on overall luminance.
                    var ys = 0.2627f * r + 0.6780f * g + 0.0593f * b;
                    var ootf = 1000f * MathF.Pow(Math.Max(ys, 1e-6f), 0.2f);
                    r *= ootf; g *= ootf; b *= ootf;
                }

                // BT.2020 colours → the BT.709 primaries scRGB uses (values outside 0..1 are fine).
                var r709 = 1.6605f * r - 0.5876f * g - 0.0728f * b;
                var g709 = -0.1246f * r + 1.1329f * g - 0.0083f * b;
                var b709 = -0.0182f * r - 0.1006f * g + 1.1187f * b;

                // Gently roll off anything brighter than the screen can show, instead of clipping.
                var lum = 0.2126f * r709 + 0.7152f * g709 + 0.0722f * b709;
                if (lum > knee && peak > knee)
                {
                    var mapped = knee + (peak - knee) * (1f - MathF.Exp(-(lum - knee) / (peak - knee)));
                    var s = mapped / lum;
                    r709 *= s; g709 *= s; b709 *= s;
                }

                pixels[i] = (Half)(r709 / 80f);
                pixels[i + 1] = (Half)(g709 / 80f);
                pixels[i + 2] = (Half)(b709 / 80f);
                pixels[i + 3] = (Half)1f;
            }
        });
        return new HdrImage(pixels, w, h, orientation);
    }

    /// <summary>PQ (SMPTE ST 2084): 16-bit signal → absolute brightness in nits.</summary>
    private static readonly Lazy<float[]> PqLut = new(() =>
    {
        const float m1 = 0.1593017578125f, m2 = 78.84375f, c1 = 0.8359375f, c2 = 18.8515625f, c3 = 18.6875f;
        var lut = new float[65536];
        for (var i = 0; i < lut.Length; i++)
        {
            var np = MathF.Pow(i / 65535f, 1f / m2);
            lut[i] = 10000f * MathF.Pow(Math.Max(np - c1, 0f) / (c2 - c3 * np), 1f / m1);
        }
        return lut;
    });

    /// <summary>HLG (ARIB STD-B67): 16-bit signal → relative scene light, 0..1.</summary>
    private static readonly Lazy<float[]> HlgLut = new(() =>
    {
        const float a = 0.17883277f, b = 0.28466892f, c = 0.55991073f;
        var lut = new float[65536];
        for (var i = 0; i < lut.Length; i++)
        {
            var e = i / 65535f;
            lut[i] = e <= 0.5f ? e * e / 3f : (MathF.Exp((e - c) / a) + b) / 12f;
        }
        return lut;
    });

    private static float SrgbToLinear(float v) =>
        v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    /// <summary>Decodes a photo at preview size as 8-bit RGBA, with its EXIF orientation (for colour management).</summary>
    public static async Task<(byte[] Pixels, int Width, int Height, int Orientation)> DecodePreviewAsync(string path, int maxSize) =>
        await DecodeAsync(await File.ReadAllBytesAsync(path), maxSize, BitmapPixelFormat.Rgba8);

    /// <summary>
    /// Decodes straight to the preview size (JPEG decoders can do this cheaply), without
    /// colour management or rotation. The EXIF orientation is returned so it can be applied
    /// when drawing, keeping the photo and its gain map aligned.
    /// </summary>
    private static async Task<(byte[] Pixels, int Width, int Height, int Orientation)> DecodeAsync(
        byte[] bytes, int maxSize, BitmapPixelFormat format, (int W, int H)? exactSize = null)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);

        int w, h;
        if (exactSize is { } size) (w, h) = size;
        else
        {
            var scale = Math.Min(1.0, maxSize / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            w = Math.Max(1, (int)Math.Round(decoder.PixelWidth * scale));
            h = Math.Max(1, (int)Math.Round(decoder.PixelHeight * scale));
        }

        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)w,
            ScaledHeight = (uint)h,
            InterpolationMode = BitmapInterpolationMode.Linear
        };
        var data = await decoder.GetPixelDataAsync(format, BitmapAlphaMode.Straight, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);

        var orientation = 1;
        try
        {
            var props = await decoder.BitmapProperties.GetPropertiesAsync(new[] { "System.Photo.Orientation" });
            if (props.TryGetValue("System.Photo.Orientation", out var value) && value.Value is ushort o) orientation = o;
        }
        catch
        {
            // No orientation recorded: leave as is.
        }
        return (data.DetachPixelData(), w, h, orientation);
    }
}
