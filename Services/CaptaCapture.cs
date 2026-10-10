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

            if (type == "tEXt" && length <= 16 * 1024 * 1024)
            {
                var data = new byte[length];
                if (png.ReadAtLeast(data, data.Length, throwOnEndOfStream: false) < data.Length) return null;
                var zero = Array.IndexOf(data, (byte)0);
                if (zero > 0 && Encoding.Latin1.GetString(data, 0, zero) == PngKeyword)
                    return Parse(Encoding.Latin1.GetString(data, zero + 1, data.Length - zero - 1));
                png.Seek(4, SeekOrigin.Current); // CRC
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

    /// <summary>Parses Capta's JSON; null if it isn't valid or wasn't written by Capta.</summary>
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
            return new CaptaCapture(Get("mode") ?? "Capture", Get("sourceApp"), Get("windowTitle"), capturedAt, Get("ocrText"));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
