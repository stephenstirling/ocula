using System;
using System.IO;
using System.Text;

namespace Ocula.Services;

/// <summary>
/// Works out whether an image is HDR, and which kind. "HDR" covers a few different things,
/// so each gets its own code:
///
///   GainMap – a normal image plus a "gain map" that HDR screens use to show brighter
///             highlights: Google/Android Ultra HDR, iPhone HDR photos, Adobe and the
///             ISO 21496-1 standard. Used in JPEG, HEIC and AVIF.
///   PQ, HLG – the image itself is encoded in an HDR transfer curve (HDR10-style PQ or
///             broadcast HLG), found in AVIF, HEIC and PNG.
///   Float   – 32-bit floating-point pixels, typically an HDR merge saved as DNG or TIFF.
///   Capture – the camera's HDR mode merged several exposures, but saved a standard
///             (SDR) image. Detected from the EXIF CustomRendered tag that iPhones write.
///
/// Detection reads only the start and end of each file, where these markers live, so it
/// stays quick even for large files and network folders. It runs once per file during
/// indexing and the result is stored in the library.
/// </summary>
public static class HdrDetector
{
    public const string GainMap = "GainMap";
    public const string PQ = "PQ";
    public const string HLG = "HLG";
    public const string Float = "Float";
    public const string Capture = "Capture";

    private const int ChunkSize = 1024 * 1024;

    // Text markers that only appear in files carrying a gain map.
    private static readonly byte[][] GainMapMarkers =
    {
        Encoding.ASCII.GetBytes("http://ns.adobe.com/hdr-gain-map/1.0/"), // Ultra HDR / Adobe
        Encoding.ASCII.GetBytes("hdrgm:Version"),
        Encoding.ASCII.GetBytes("HDRGainMap"),                             // Apple (XMP namespace)
        Encoding.ASCII.GetBytes("urn:com:apple:photo:2020:aux:hdrgainmap"), // Apple (auxiliary image)
        Encoding.ASCII.GetBytes("urn:iso:std:iso:ts:21496:-1"),            // ISO 21496-1
    };

    private static readonly byte[] IsoColourBox = Encoding.ASCII.GetBytes("colrnclx"); // AVIF / HEIC
    private static readonly byte[] PngColourChunk = Encoding.ASCII.GetBytes("cICP");    // PNG
    private static readonly byte[] ToneMapItem = Encoding.ASCII.GetBytes("tmap");       // AVIF / HEIF gain map item

    public static string? Detect(string path, CameraInfo exif)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (exif.FloatingPoint) return Float;

            if (ext is ".jpg" or ".jpeg" or ".jfif" or ".heic" or ".heif" or ".avif" or ".png")
            {
                var (head, tail) = ReadHeadAndTail(path);

                foreach (var marker in GainMapMarkers)
                {
                    if (head.AsSpan().IndexOf(marker) >= 0 || tail.AsSpan().IndexOf(marker) >= 0)
                        return GainMap;
                }

                if (ext is ".heic" or ".heif" or ".avif")
                {
                    // Item types sit in the 'meta' box near the start, before the image data.
                    var meta = head.AsSpan(0, Math.Min(head.Length, 64 * 1024));
                    if (meta.IndexOf(ToneMapItem) >= 0) return GainMap;

                    // colr box: 'colr' 'nclx' primaries(2) transfer(2) matrix(2) range(1)
                    var at = head.AsSpan().IndexOf(IsoColourBox);
                    if (at >= 0 && at + 12 <= head.Length)
                    {
                        var transfer = (head[at + 10] << 8) | head[at + 11];
                        if (ToKind(transfer) is { } kind) return kind;
                    }
                }

                if (ext == ".png")
                {
                    // cICP chunk: 'cICP' primaries(1) transfer(1) matrix(1) range(1)
                    var at = head.AsSpan().IndexOf(PngColourChunk);
                    if (at >= 0 && at + 6 <= head.Length && ToKind(head[at + 5]) is { } kind) return kind;
                }
            }

            // iPhone "HDR" mode (2 = HDR without original, 3 = HDR with original kept).
            if (exif.CustomRendered is 2 or 3) return Capture;
        }
        catch
        {
            // Unreadable file: treat as standard.
        }
        return null;
    }

    public static string Title(string? kind) => kind switch
    {
        GainMap => "HDR photo · gain map",
        PQ => "HDR image · PQ",
        HLG => "HDR image · HLG",
        Float => "HDR merge · 32-bit",
        Capture => "Taken in HDR mode",
        _ => "Standard dynamic range"
    };

    public static string Description(string? kind) => kind switch
    {
        GainMap => "Includes a gain map (Ultra HDR, iPhone or ISO 21496-1). HDR screens can show brighter highlights; other screens show the standard version.",
        PQ => "Encoded with the PQ curve used by HDR10. Needs an HDR screen to look right.",
        HLG => "Encoded with the HLG curve. Shows reasonably on standard screens and brighter on HDR ones.",
        Float => "Floating-point pixels, usually an HDR merge from Lightroom or Photoshop.",
        Capture => "The camera merged several exposures, but saved a standard-range image.",
        _ => "No HDR information found in this file."
    };

    private static string? ToKind(int transferCharacteristics) => transferCharacteristics switch
    {
        16 => PQ,  // SMPTE ST 2084
        18 => HLG, // ARIB STD-B67
        _ => null
    };

    private static (byte[] Head, byte[] Tail) ReadHeadAndTail(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
        var length = stream.Length;

        var head = new byte[(int)Math.Min(length, ChunkSize)];
        stream.ReadExactly(head);

        if (length <= ChunkSize) return (head, Array.Empty<byte>());

        var tailSize = (int)Math.Min(length - head.Length, ChunkSize);
        var tail = new byte[tailSize];
        stream.Seek(-tailSize, SeekOrigin.End);
        stream.ReadExactly(tail);
        return (head, tail);
    }
}
