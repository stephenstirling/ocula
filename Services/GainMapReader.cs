using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ocula.Services;

/// <summary>
/// Gain map settings, as defined by Adobe's gain map specification (also used by Android's
/// Ultra HDR). Values are per colour channel (R, G, B); log2 values where the spec says so.
/// </summary>
public sealed record GainMapParams(
    float[] Min, float[] Max, float[] Gamma, float[] OffsetSdr, float[] OffsetHdr,
    float CapacityMin, float CapacityMax, bool BaseIsHdr);

/// <summary>
/// Finds the gain map image inside a JPEG and reads its settings.
///
/// A gain-map JPEG is a normal JPEG followed by a second, smaller JPEG (the gain map). Where
/// the second image starts is recorded either in an MPF index (the CIPA "multi-picture"
/// format; used by Ultra HDR) or in an XMP "container" directory (used by Adobe). The
/// settings are XMP attributes in the hdrgm namespace, normally on the gain map image.
/// </summary>
public static class GainMapReader
{
    public static (byte[] GainMap, GainMapParams Params)? Read(byte[] file)
    {
        var gainMap = FindViaMpf(file) ?? FindViaContainer(file);
        if (gainMap == null) return null;

        var settings = ParseParams(gainMap) ?? ParseParams(file);
        return settings == null ? null : (gainMap, settings);
    }

    private static byte[]? FindViaMpf(byte[] f)
    {
        if (f.Length < 4 || f[0] != 0xFF || f[1] != 0xD8) return null;
        var i = 2;
        while (i + 4 < f.Length && f[i] == 0xFF)
        {
            var marker = f[i + 1];
            if (marker == 0xDA) break; // start of image data: no more metadata segments
            var length = (f[i + 2] << 8) | f[i + 3];

            // APP2 segment starting "MPF\0", followed by a TIFF-style header and index.
            if (marker == 0xE2 && length > 8 && f[i + 4] == 'M' && f[i + 5] == 'P' && f[i + 6] == 'F' && f[i + 7] == 0)
            {
                var tiff = i + 8;
                var little = f[tiff] == 'I';
                int U16(int o) => little ? f[o] | (f[o + 1] << 8) : (f[o] << 8) | f[o + 1];
                long U32(int o) => little
                    ? (uint)(f[o] | (f[o + 1] << 8) | (f[o + 2] << 16) | (f[o + 3] << 24))
                    : (uint)((f[o] << 24) | (f[o + 1] << 16) | (f[o + 2] << 8) | f[o + 3]);

                var ifd = tiff + (int)U32(tiff + 4);
                if (ifd + 2 > f.Length) return null;
                var count = U16(ifd);
                for (var e = 0; e < count; e++)
                {
                    var entry = ifd + 2 + e * 12;
                    if (entry + 12 > f.Length || U16(entry) != 0xB002) continue; // MP Entry

                    var images = (int)(U32(entry + 4) / 16);
                    var table = tiff + (int)U32(entry + 8);
                    for (var k = 1; k < images; k++) // entry 0 is the main image
                    {
                        var me = table + k * 16;
                        if (me + 16 > f.Length) break;
                        var size = U32(me + 4);
                        var start = tiff + U32(me + 8); // offsets are relative to the TIFF header
                        if (start > 0 && start + size <= f.Length && f[start] == 0xFF && f[start + 1] == 0xD8)
                            return f.AsSpan((int)start, (int)size).ToArray();
                    }
                }
            }
            i += 2 + length;
        }
        return null;
    }

    private static byte[]? FindViaContainer(byte[] f)
    {
        // The XMP container lists items in file order; the gain map's Item:Length is its size,
        // and it sits at the very end of the file.
        var xmp = Encoding.Latin1.GetString(f, 0, Math.Min(f.Length, 256 * 1024));
        var item = Regex.Match(xmp, @"<Container:Item\b[^>]*Item:Semantic=""GainMap""[^>]*>");
        if (!item.Success) return null;
        var length = Regex.Match(item.Value, @"Item:Length=""(\d+)""");
        if (!length.Success || !int.TryParse(length.Groups[1].Value, out var size) || size <= 0 || size > f.Length) return null;

        var start = f.Length - size;
        return f[start] == 0xFF && f[start + 1] == 0xD8 ? f.AsSpan(start, size).ToArray() : null;
    }

    private static GainMapParams? ParseParams(byte[] jpeg)
    {
        var xmp = Encoding.Latin1.GetString(jpeg, 0, Math.Min(jpeg.Length, 256 * 1024));
        if (!xmp.Contains("hdrgm:", StringComparison.Ordinal)) return null;

        var max = Values(xmp, "GainMapMax");
        if (max == null) return null; // the one required value

        float[] Or(float[]? v, float d) => v ?? new[] { d };
        var capacityMax = Values(xmp, "HDRCapacityMax")?[0] ?? max.Max();
        var baseIsHdr = Regex.IsMatch(xmp, @"hdrgm:BaseRenditionIsHDR=""True""", RegexOptions.IgnoreCase);

        return new GainMapParams(
            Min: PerChannel(Or(Values(xmp, "GainMapMin"), 0f)),
            Max: PerChannel(max),
            Gamma: PerChannel(Or(Values(xmp, "Gamma"), 1f)),
            OffsetSdr: PerChannel(Or(Values(xmp, "OffsetSDR"), 1f / 64)),
            OffsetHdr: PerChannel(Or(Values(xmp, "OffsetHDR"), 1f / 64)),
            CapacityMin: Values(xmp, "HDRCapacityMin")?[0] ?? 0f,
            CapacityMax: capacityMax,
            BaseIsHdr: baseIsHdr);
    }

    /// <summary>Reads hdrgm:Name as an attribute, an element, or a per-channel rdf:Seq.</summary>
    private static float[]? Values(string xmp, string name)
    {
        var attribute = Regex.Match(xmp, $@"hdrgm:{name}=""([^""]+)""");
        if (attribute.Success) return Parse(attribute.Groups[1].Value);

        var element = Regex.Match(xmp, $@"<hdrgm:{name}>(.*?)</hdrgm:{name}>", RegexOptions.Singleline);
        if (!element.Success) return null;
        var items = Regex.Matches(element.Groups[1].Value, @"<rdf:li>([^<]+)</rdf:li>");
        return items.Count > 0
            ? items.Select(m => Parse(m.Groups[1].Value)?[0] ?? 0f).ToArray()
            : Parse(element.Groups[1].Value);
    }

    private static float[]? Parse(string text) =>
        float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? new[] { v } : null;

    private static float[] PerChannel(float[] v) => v.Length >= 3 ? v[..3] : new[] { v[0], v[0], v[0] };
}
