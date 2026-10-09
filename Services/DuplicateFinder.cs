using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using CoenM.ImageHash.HashAlgorithms;
using Ocula.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.PixelFormats;

namespace Ocula.Services;

/// <summary>
/// What Ocula remembers about a photo's look: 16 hashes (2 kinds x 8 orientations) and a 16x16
/// grayscale thumbnail used to double-check a match. 384 bytes when stored.
/// </summary>
public sealed class PhotoFingerprint
{
    public const int TinySize = 16;
    public ulong[] Hashes { get; init; } = new ulong[DuplicateFinder.Orientations * 2];
    public byte[] Tiny { get; init; } = new byte[TinySize * TinySize];

    public const int ByteLength = DuplicateFinder.Orientations * 2 * sizeof(ulong) + TinySize * TinySize;

    public byte[] ToBytes()
    {
        var bytes = new byte[ByteLength];
        Buffer.BlockCopy(Hashes, 0, bytes, 0, Hashes.Length * sizeof(ulong));
        Buffer.BlockCopy(Tiny, 0, bytes, Hashes.Length * sizeof(ulong), Tiny.Length);
        return bytes;
    }

    public static PhotoFingerprint? FromBytes(byte[] bytes)
    {
        if (bytes.Length != ByteLength) return null; // from an older version: recomputed on the next scan
        var hashes = new ulong[DuplicateFinder.Orientations * 2];
        Buffer.BlockCopy(bytes, 0, hashes, 0, hashes.Length * sizeof(ulong));
        var tiny = new byte[TinySize * TinySize];
        Buffer.BlockCopy(bytes, hashes.Length * sizeof(ulong), tiny, 0, tiny.Length);
        return new PhotoFingerprint { Hashes = hashes, Tiny = tiny };
    }
}

public sealed record HashedImage(ImageItem Item, PhotoFingerprint Fingerprint, long Size);

/// <summary>
/// How strict a match is: how many of the 64 bits may differ for each kind of hash, and how closely
/// the two 16x16 grayscale pictures must correlate (1 = identical, 0 = unrelated).
/// </summary>
public readonly record struct MatchLimits(int Perceptual, int Difference, double Correlation);

/// <summary>
/// Finds duplicate and visually similar photos using two kinds of perceptual hash from the open-source
/// CoenM.ImageHash library (MIT): a perceptual hash (pHash, tolerant of edits and re-saves) and a
/// difference hash (dHash, sensitive to local structure). Two photos only match when BOTH agree, which
/// cuts false matches between different photos that merely share a layout.
///
/// Each photo is hashed in its 8 orientations (4 rotations, with and without a mirror image), so a
/// copy that was rotated or flipped still matches. Fingerprints are made from the small thumbnails
/// Ocula already caches, so even RAW files are checked quickly, and they are stored in the library
/// (128 bytes per photo) so a second scan is almost instant.
///
/// Hash layout (16 numbers): [0..7] pHash per orientation, [8..15] dHash per orientation.
/// Orientation 0 is the photo as it is.
///
/// A pair that passes both hash tests gets a third, pixel-level check: the 16x16 grayscale
/// versions are compared by correlation (brightness-independent). That rejects look-alikes with
/// a different layout, which lets the "Similar" level use loose hash limits and still catch
/// lightly cropped copies.
/// </summary>
public static class DuplicateFinder
{
    public const int Orientations = 8;

    private static readonly PerceptualHash Perceptual = new();
    private static readonly DifferenceHash Difference = new();

    private static readonly (RotateMode Rotate, FlipMode Flip)[] Transforms =
    {
        (RotateMode.None, FlipMode.None),
        (RotateMode.Rotate90, FlipMode.None),
        (RotateMode.Rotate180, FlipMode.None),
        (RotateMode.Rotate270, FlipMode.None),
        (RotateMode.None, FlipMode.Horizontal),
        (RotateMode.Rotate90, FlipMode.Horizontal),
        (RotateMode.Rotate180, FlipMode.Horizontal),
        (RotateMode.Rotate270, FlipMode.Horizontal)
    };

    /// <summary>Makes the fingerprint for a photo's thumbnail.</summary>
    public static PhotoFingerprint MakeFingerprint(byte[] imageBytes)
    {
        using var image = Image.Load<Rgba32>(imageBytes);
        var hashes = new ulong[Orientations * 2];
        for (var k = 0; k < Orientations; k++)
        {
            var (rotate, flip) = Transforms[k];
            if (k == 0)
            {
                hashes[0] = Perceptual.Hash(image);
                hashes[Orientations] = Difference.Hash(image);
            }
            else
            {
                using var variant = image.Clone(ctx => ctx.RotateFlip(rotate, flip));
                hashes[k] = Perceptual.Hash(variant);
                hashes[Orientations + k] = Difference.Hash(variant);
            }
        }

        var tiny = new byte[PhotoFingerprint.TinySize * PhotoFingerprint.TinySize];
        using (var small = image.Clone(ctx => ctx.Resize(PhotoFingerprint.TinySize, PhotoFingerprint.TinySize, KnownResamplers.Triangle)))
        {
            for (var y = 0; y < PhotoFingerprint.TinySize; y++)
                for (var x = 0; x < PhotoFingerprint.TinySize; x++)
                {
                    var p = small[x, y];
                    tiny[y * PhotoFingerprint.TinySize + x] = (byte)Math.Round(0.299 * p.R + 0.587 * p.G + 0.114 * p.B);
                }
        }
        return new PhotoFingerprint { Hashes = hashes, Tiny = tiny };
    }

    /// <summary>
    /// How strict each sensitivity level is. These numbers are starting points, not measured values
    /// on real libraries: tune them. (On synthetic test images, "Similar" found copies cropped by
    /// 5-8% and had no false matches, but real photos with similar layouts will correlate more.)
    /// </summary>
    public static MatchLimits Limits(string sensitivity) => sensitivity switch
    {
        "Exact copies" => new MatchLimits(2, 4, 0.97),
        "Very similar" => new MatchLimits(6, 12, 0.93),
        _ => new MatchLimits(20, 30, 0.75) // "Similar": also lightly cropped copies and colour edits
    };

    /// <summary>
    /// Smallest pHash distance at which both hashes and the pixel check agree, or null if the photos
    /// don't match. When anyOrientation is on, rotated and mirrored copies of A are tried as well.
    /// </summary>
    public static int? Distance(PhotoFingerprint a, PhotoFingerprint b, MatchLimits limits, bool anyOrientation)
    {
        var passes = false;
        int? best = null;
        var count = anyOrientation ? Orientations : 1;
        for (var k = 0; k < count; k++)
        {
            var p = BitOperations.PopCount(a.Hashes[k] ^ b.Hashes[0]);
            if (p > limits.Perceptual) continue;
            if (BitOperations.PopCount(a.Hashes[Orientations + k] ^ b.Hashes[Orientations]) > limits.Difference) continue;
            passes = true;
            if (best == null || p < best) best = p;
        }
        if (!passes) return null;

        // Pixel check. The best orientation is searched again on the tiny pictures, so this does not
        // depend on the hash orientations and the tiny pictures being numbered the same way.
        return Correlation(a.Tiny, b.Tiny, anyOrientation) >= limits.Correlation ? best : null;
    }

    private static readonly int N = PhotoFingerprint.TinySize;

    /// <summary>Best Pearson correlation between two 16x16 pictures, over the 8 orientations of the first.</summary>
    public static double Correlation(byte[] a, byte[] b, bool anyOrientation)
    {
        var meanB = b.Average(v => (double)v);
        var devB = b.Select(v => v - meanB).ToArray();
        var normB = Math.Sqrt(devB.Sum(v => v * v));
        if (normB == 0) return 0;

        var best = double.MinValue;
        var current = a;
        for (var k = 0; k < (anyOrientation ? Orientations : 1); k++)
        {
            if (k == 4) current = Mirror(a);
            var candidate = k % 4 == 0 ? current : Rotate(current);
            current = candidate;
            var meanA = candidate.Average(v => (double)v);
            double dot = 0, sumA = 0;
            for (var i = 0; i < candidate.Length; i++)
            {
                var da = candidate[i] - meanA;
                dot += da * devB[i];
                sumA += da * da;
            }
            var normA = Math.Sqrt(sumA);
            if (normA > 0) best = Math.Max(best, dot / (normA * normB));
        }
        return best == double.MinValue ? 0 : best;
    }

    private static byte[] Rotate(byte[] p)
    {
        var r = new byte[p.Length];
        for (var y = 0; y < N; y++)
            for (var x = 0; x < N; x++)
                r[x * N + (N - 1 - y)] = p[y * N + x];
        return r;
    }

    private static byte[] Mirror(byte[] p)
    {
        var r = new byte[p.Length];
        for (var y = 0; y < N; y++)
            for (var x = 0; x < N; x++)
                r[y * N + (N - 1 - x)] = p[y * N + x];
        return r;
    }

    /// <summary>
    /// Groups matching photos. The best copy of each group (the one Ocula would keep) is its anchor,
    /// and every other photo must match that anchor, so "A looks like B and B looks like C" can no
    /// longer pull an unrelated A and C into one group. Largest groups come first.
    /// </summary>
    public static List<List<HashedImage>> Group(IReadOnlyList<HashedImage> images, MatchLimits limits, bool anyOrientation)
    {
        // Best copies first, so each group is anchored on the photo that would be kept.
        var ordered = images
            .OrderByDescending(i => i.Item.Rating)
            .ThenByDescending(i => i.Item.RawPath != null)
            .ThenByDescending(i => i.Size)
            .ToList();

        var groups = new List<List<HashedImage>>();
        foreach (var image in ordered)
        {
            List<HashedImage>? home = null;
            var closest = int.MaxValue;
            foreach (var group in groups)
            {
                var distance = Distance(image.Fingerprint, group[0].Fingerprint, limits, anyOrientation);
                if (distance != null && distance < closest) { closest = distance.Value; home = group; }
            }
            if (home != null) home.Add(image);
            else groups.Add(new List<HashedImage> { image });
        }

        return groups
            .Where(g => g.Count > 1)
            .OrderByDescending(g => g.Count)
            .ThenByDescending(g => g.Sum(i => i.Size))
            .ToList();
    }

    /// <summary>
    /// Picks the copy to keep: highest rating, then a RAW+JPEG pair over a lone file,
    /// then the largest file (usually the highest resolution or least compressed).
    /// </summary>
    public static HashedImage Best(IEnumerable<HashedImage> group) =>
        group.OrderByDescending(i => i.Item.Rating)
             .ThenByDescending(i => i.Item.RawPath != null)
             .ThenByDescending(i => i.Size)
             .First();

    /// <summary>All files that make up an item: the image(s) plus any XMP sidecar.</summary>
    public static IEnumerable<string> FilesOf(ImageItem item)
    {
        foreach (var path in item.AllPaths)
        {
            yield return path;
            var sidecar = XmpSidecar.SidecarPath(path);
            if (!MetadataStore.Embeds(path) && File.Exists(sidecar)) yield return sidecar;
        }
    }

    /// <summary>Moves files to the Recycle Bin, so a mistake can be undone from there.</summary>
    public static bool MoveToRecycleBin(IEnumerable<string> paths, IntPtr owner)
    {
        var list = paths.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToList();
        if (list.Count == 0) return true;

        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = FO_DELETE,
            pFrom = string.Join('\0', list) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
