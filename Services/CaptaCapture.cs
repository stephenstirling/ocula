using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Ocula.Services;

/// <summary>
/// What Capta (the sibling screenshot app) records about a capture. Capta writes it into each
/// PNG it saves as one tEXt chunk with the keyword "Capta", holding JSON:
/// {"version":1,"software":"Capta","mode":"Window","capturedAt":"…","sourceApp":"Settings",
///  "windowTitle":"Network — Settings","width":1280,"height":816,"toneMappedFromHdr":false,
///  "ocrText":"Network · Wi-Fi …"}. Non-ASCII text is \u-escaped, as tEXt is Latin-1.
/// </summary>
public sealed record CaptaCapture(string Mode, string? SourceApp, string? WindowTitle, DateTimeOffset? CapturedAt, string? Text)
{
    public const string PngKeyword = "Capta";

    /// <summary>
    /// The largest Capta chunk read. The text is capped at <see cref="CaptureTextIndex.MaxTextChars"/> characters,
    /// which JSON escapes can stretch about sixfold, so a real chunk is well inside this.
    /// </summary>
    public const int MaxChunkBytes = 1024 * 1024;

    /// <summary>
    /// Stored in the library's capture column: mode, source app and window title on separate
    /// lines, so a search for any of them finds the capture.
    /// </summary>
    public string Summary => string.Join('\n', new[] { Mode, SourceApp, WindowTitle }.Select(s => s ?? string.Empty));

    /// <summary>The capture details in a PNG, or null if it isn't a Capta capture (or isn't a PNG).</summary>
    public static CaptaCapture? Read(string path)
    {
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return Read(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static CaptaCapture? Read(Stream png)
    {
        Span<byte> signature = stackalloc byte[8];
        if (png.ReadAtLeast(signature, 8, throwOnEndOfStream: false) < 8 ||
            !signature.SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return null;

        Span<byte> header = stackalloc byte[8];
        while (png.ReadAtLeast(header, 8, throwOnEndOfStream: false) == 8)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header[4..]);
            if (length > int.MaxValue) return null;

            if (type == "tEXt")
            {
                // Read only the keyword (at most 79 characters and a zero byte) before deciding whether the
                // chunk is Capta's; any other tEXt chunk, however large, is skipped without being read.
                var keywordBytes = (int)Math.Min(length, 80);
                Span<byte> start = stackalloc byte[80];
                if (png.ReadAtLeast(start[..keywordBytes], keywordBytes, throwOnEndOfStream: false) < keywordBytes) return null;
                var zero = start[..keywordBytes].IndexOf((byte)0);
                var isCapta = zero > 0 && Encoding.Latin1.GetString(start[..zero]) == PngKeyword;

                if (!isCapta)
                {
                    png.Seek(length - keywordBytes + 4L, SeekOrigin.Current); // rest of the data, and the CRC
                    continue;
                }
                if (length > MaxChunkBytes) return null; // far more than any real capture's details: not Capta's
                var rest = new byte[length - zero - 1];
                start[(zero + 1)..keywordBytes].CopyTo(rest);
                var already = keywordBytes - zero - 1;
                if (png.ReadAtLeast(rest.AsSpan(already), rest.Length - already, throwOnEndOfStream: false) < rest.Length - already) return null;
                return Parse(Encoding.Latin1.GetString(rest));
            }
            else if (type == "IEND")
            {
                return null;
            }
            else
            {
                png.Seek(length + 4L, SeekOrigin.Current); // data and CRC
            }
        }
        return null;
    }

    /// <summary>
    /// Parses Capta's JSON; null if it isn't valid or wasn't written by Capta. Every text field is untrusted
    /// (it came off a screen, or out of a file someone sent), so each is cleaned and capped here, once, and
    /// nothing downstream has to wonder: see <see cref="CaptureTextIndex"/>.
    /// </summary>
    public static CaptaCapture? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? Get(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

            if (Get("software") is { } software && !software.Equals("Capta", StringComparison.OrdinalIgnoreCase)) return null;
            DateTimeOffset? capturedAt = root.TryGetProperty("capturedAt", out var at) && at.ValueKind == JsonValueKind.String && at.TryGetDateTimeOffset(out var when) ? when : null;
            return new CaptaCapture(
                CaptureTextIndex.CleanLine(Get("mode"), 32) ?? "Capture",
                CaptureTextIndex.CleanLine(Get("sourceApp")),
                CaptureTextIndex.CleanLine(Get("windowTitle")),
                capturedAt,
                CaptureTextIndex.CleanText(Get("ocrText")));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
