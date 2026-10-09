using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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
    private const string Repo = "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/";

    public sealed record ModelFile(string LocalName, string Url);

    public static string Folder(SmartTier tier) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ocula", "Models", "clip-b32-" + tier.ToString().ToLowerInvariant());

    public static string ModelId(SmartTier tier) => "clip-b32-" + tier.ToString().ToLowerInvariant();

    public static string SizeText(SmartTier tier) => tier == SmartTier.Compact ? "about 155 MB" : "about 610 MB";

    public static IReadOnlyList<ModelFile> Files(SmartTier tier) => new[]
    {
        new ModelFile("vision.onnx", Repo + (tier == SmartTier.Compact ? "onnx/vision_model_quantized.onnx" : "onnx/vision_model.onnx")),
        new ModelFile("text.onnx",   Repo + (tier == SmartTier.Compact ? "onnx/text_model_quantized.onnx"   : "onnx/text_model.onnx")),
        new ModelFile("vocab.json",  Repo + "vocab.json"),
        new ModelFile("merges.txt",  Repo + "merges.txt")
    };

    public static bool IsInstalled(SmartTier tier)
    {
        foreach (var file in Files(tier))
            if (!File.Exists(Path.Combine(Folder(tier), file.LocalName))) return false;
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
            File.Move(part, target, overwrite: true);
        }
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
