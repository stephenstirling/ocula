using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Ocula.Services;

public enum SmartTier
{
    /// <summary>Smaller, faster models (about 155 MB).</summary>
    Compact,
    /// <summary>Full-precision models, slightly more accurate (about 610 MB).</summary>
    Accurate
}

/// <summary>
/// The recognition model files and where they live. Ocula uses OpenAI's CLIP (ViT-B/32, MIT
/// licence) as exported to ONNX by the Xenova project on Hugging Face. Nothing is bundled:
/// the files are downloaded once, when the user turns Smart recognition on.
/// </summary>
public static class SmartModels
{
    // Pinned to one revision so the files can't change under the hashes below
    // (https://huggingface.co/Xenova/clip-vit-base-patch32/tree/d15189d7028b43f1d3e65039190477f6af591c2a).
    private const string Repo = "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/d15189d7028b43f1d3e65039190477f6af591c2a/";

    /// <summary>
    /// A file to download. When Size and Sha256 are given, a download that doesn't match both is
    /// rejected, so a changed, truncated or tampered file is never loaded.
    /// </summary>
    public sealed record ModelFile(string LocalName, string Url, long Size = 0, string? Sha256 = null);

    public static string Folder(SmartTier tier) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ocula", "Models", "clip-b32-" + tier.ToString().ToLowerInvariant());

    /// <summary>
    /// Bump when a change to ClipModel or ClipTokenizer changes the vectors they produce, so stored
    /// photo embeddings and the cached label vectors are made again. v2: the reference-matching
    /// tokenizer padding and image preprocessing (dbfe037).
    /// </summary>
    public const string EncodingVersion = "v2";

    /// <summary>The key stored with each photo's embedding; photos analysed under an older one are analysed again.</summary>
    public static string ModelId(SmartTier tier) => "clip-b32-" + tier.ToString().ToLowerInvariant() + "-" + EncodingVersion;

    public static string SizeText(SmartTier tier) => tier == SmartTier.Compact ? "about 155 MB" : "about 610 MB";

    // Sizes and SHA-256 hashes as published by Hugging Face for the pinned revision.
    public static IReadOnlyList<ModelFile> Files(SmartTier tier) => tier == SmartTier.Compact
        ? new[]
        {
            new ModelFile("vision.onnx", Repo + "onnx/vision_model_quantized.onnx", 89_117_001,
                          "583fd1110a514667812fee7d684952aaf82a99b959760c8d7dca7e0ab9839299"),
            new ModelFile("text.onnx", Repo + "onnx/text_model_quantized.onnx", 64_504_507,
                          "73baab855d406190da9faa498cfedf65f15cf309f4cc7385b7b032e6d08e5c3a"),
            Vocab, Merges
        }
        : new[]
        {
            new ModelFile("vision.onnx", Repo + "onnx/vision_model.onnx", 351_685_709,
                          "fd6e1402a588279d1723c7534d4bcba5bc0b14b47dfab0e46f8c47b8270d7d40"),
            new ModelFile("text.onnx", Repo + "onnx/text_model.onnx", 254_058_553,
                          "3f6571f5bad13a97c469c1622e1cfc4d9aef78b79fdbfcff804ca357bfada8cc"),
            Vocab, Merges
        };

    private static readonly ModelFile Vocab = new("vocab.json", Repo + "vocab.json", 862_328,
        "5047b556ce86ccaf6aa22b3ffccfc52d391ea4accdab9c2f2407da5b742d4363");
    private static readonly ModelFile Merges = new("merges.txt", Repo + "merges.txt", 524_619,
        "9fd691f7c8039210e0fced15865466c65820d09b63988b0174bfe25de299051a");

    /// <summary>Every file is present with the expected size (hashes are checked when downloading).</summary>
    public static bool IsInstalled(SmartTier tier)
    {
        foreach (var file in Files(tier))
        {
            var path = Path.Combine(Folder(tier), file.LocalName);
            if (!File.Exists(path)) return false;
            if (file.Size > 0 && new FileInfo(path).Length != file.Size) return false;
        }
        return true;
    }

    public static string PathOf(SmartTier tier, string localName) => Path.Combine(Folder(tier), localName);

    /// <summary>
    /// Downloads every file for the tier, reporting 0..1 overall progress. Each file is written
    /// to a ".part" file first and renamed when complete, so an interrupted download is never
    /// mistaken for a finished one.
    /// </summary>
    public static Task DownloadAsync(SmartTier tier, IProgress<double>? progress, CancellationToken cancel) =>
        DownloadFilesAsync(Folder(tier), Files(tier), progress, cancel);

    /// <summary>Shared by every model download (recognition and faces).</summary>
    public static async Task DownloadFilesAsync(string folder, IReadOnlyList<ModelFile> files,
                                                IProgress<double>? progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(folder);

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Ocula/1.0");

        // First ask each file's size so progress covers the whole download, not one file.
        var sizes = new long[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            if (files[i].Size > 0) { sizes[i] = files[i].Size; continue; }
            using var head = new HttpRequestMessage(HttpMethod.Head, files[i].Url);
            using var response = await http.SendAsync(head, cancel);
            response.EnsureSuccessStatusCode();
            sizes[i] = response.Content.Headers.ContentLength ?? 0;
        }

        var total = Math.Max(1, sizes.Sum());
        long done = 0;

        for (var i = 0; i < files.Count; i++)
        {
            var target = Path.Combine(folder, files[i].LocalName);
            if (File.Exists(target) && sizes[i] > 0 && new FileInfo(target).Length == sizes[i])
            {
                done += sizes[i];
                progress?.Report((double)done / total);
                continue;
            }

            var part = target + ".part";
            using (var response = await http.GetAsync(files[i].Url, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancel);
                await using var output = File.Create(part);
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancel)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                    done += read;
                    progress?.Report(Math.Min(1.0, (double)done / total));
                }
            }

            if (sizes[i] > 0 && new FileInfo(part).Length != sizes[i])
            {
                File.Delete(part);
                throw new IOException($"{files[i].LocalName} downloaded incompletely. Please try again.");
            }
            if (files[i].Sha256 is { } expected && !string.Equals(await Sha256Async(part, cancel), expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                throw new IOException($"{files[i].LocalName} didn't match the expected file. Please try again.");
            }
            File.Move(part, target, overwrite: true);
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancel)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancel));
    }

    public static void Delete(SmartTier tier)
    {
        try
        {
            if (Directory.Exists(Folder(tier))) Directory.Delete(Folder(tier), recursive: true);
        }
        catch (IOException)
        {
            // Files still in use; they'll go next time.
        }
    }
}
