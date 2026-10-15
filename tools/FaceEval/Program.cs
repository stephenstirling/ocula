// FaceEval: measures People on real photos with the app's own face code.
//   detect: finds faces in the given photos and writes copies with the boxes drawn on.
//   group:  groups the largest face of each labelled photo at a range of thresholds and scores the result.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Ocula.Services;
using Windows.Storage;
using Windows.Storage.FileProperties;

var options = Options.Parse(args);
if (options.Mode is not ("detect" or "group"))
{
    Console.WriteLine("""
        FaceEval detect --out DIR [--models DIR] FILE...
        FaceEval group  --labels labels.csv --root DIR --out DIR [--models DIR] [--min-face PX] [--orders N]
        See tools/FaceEval/README.md.
        """);
    return 1;
}

var modelFolder = options.Models ?? FindModels();
if (modelFolder == null || !File.Exists(Path.Combine(modelFolder, "yunet.onnx")))
{
    Console.WriteLine("The People models aren't downloaded. Turn on People in Ocula's Settings, or pass --models.");
    return 1;
}
Directory.CreateDirectory(options.Out);

using var faces = new FaceService { ScoreThreshold = options.Mode == "detect" ? 0.5f : 0.8f, MinFacePixels = options.MinFace };
faces.Load(modelFolder, Environment.ProcessorCount / 2);
Console.WriteLine($"Models: {modelFolder}");

return options.Mode == "detect" ? Detect(faces, options) : Group(faces, options);

// ======================================================================================

static int Detect(FaceService faces, Options o)
{
    var csv = new StringBuilder("photo,thumbnail,found,passes_app_filters,faces\n");
    var index = 0;
    foreach (var path in o.Files)
    {
        index++;
        var bytes = Thumbnail(path);
        using var image = new Bitmap(new MemoryStream(bytes));
        var found = faces.DetectOnly(bytes).OrderByDescending(f => f.Score).ToList();
        var passing = found.Where(f => f.Score >= 0.8f && Math.Min(f.Width, f.Height) >= faces.MinFacePixels).ToList();

        using (var g = Graphics.FromImage(image))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var font = new Font("Segoe UI", Math.Max(10, image.Width / 70f), FontStyle.Bold, GraphicsUnit.Pixel);
            foreach (var f in found)
            {
                var kept = passing.Contains(f);
                using var pen = new Pen(kept ? Color.LimeGreen : Color.Orange, Math.Max(2, image.Width / 400f));
                g.DrawRectangle(pen, f.X, f.Y, f.Width, f.Height);
                foreach (var (lx, ly) in f.Landmarks) g.FillEllipse(kept ? Brushes.Red : Brushes.Yellow, lx - 2.5f, ly - 2.5f, 5, 5);
                g.DrawString($"{f.Score:0.00} {Math.Min(f.Width, f.Height):0}px", font, kept ? Brushes.LimeGreen : Brushes.Orange, f.X, Math.Max(0, f.Y - font.Height - 1));
            }
        }
        var name = $"{index:00} {Path.GetFileNameWithoutExtension(path)}.jpg";
        image.Save(Path.Combine(o.Out, name), ImageFormat.Jpeg);

        var describe = string.Join("; ", found.Select(f => $"{f.Score:0.00}/{Math.Min(f.Width, f.Height):0}px{(passing.Contains(f) ? "" : " (filtered)")}"));
        Console.WriteLine($"{index,2}. {Path.GetFileName(path),-34} found {found.Count,2}, kept {passing.Count,2}  {describe}");
        csv.AppendLine($"\"{path}\",\"{name}\",{found.Count},{passing.Count},\"{describe}\"");
    }
    File.WriteAllText(Path.Combine(o.Out, "detections.csv"), csv.ToString());
    Console.WriteLine($"Annotated copies are in {o.Out} (green = kept by the app, orange = found but filtered out).");
    return 0;
}

static int Group(FaceService faces, Options o)
{
    // Ground truth: one line per photo, "path,person". The photo's largest face is taken to be that person.
    var labels = File.ReadAllLines(o.Labels!).Skip(1).Where(l => l.Trim().Length > 0)
        .Select(ParseLabel).Select(p => (Path: Path.Combine(o.Root!, p.Path), p.Person)).ToList();

    var cachePath = Path.Combine(o.Out, "embeddings.json");
    var cache = File.Exists(cachePath) ? JsonSerializer.Deserialize<Dictionary<string, CachedFace?>>(File.ReadAllText(cachePath)) ?? new() : new();

    var items = new List<(string Path, string Person, float[] Embedding, int Size, float Score)>();
    var skipped = 0;
    foreach (var (path, person) in labels)
    {
        var key = $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{o.MinFace}";
        if (!cache.TryGetValue(key, out var face))
        {
            var found = faces.Analyse(Thumbnail(path)).OrderByDescending(f => (long)f.Width * f.Height).FirstOrDefault();
            face = found == null ? null : new CachedFace(found.Embedding, Math.Min(found.Width, found.Height), found.Score);
            cache[key] = face;
        }
        if (face == null) { skipped++; continue; }
        items.Add((path, person, face.Embedding, face.Size, face.Score));
    }
    File.WriteAllText(cachePath, JsonSerializer.Serialize(cache));

    var people = items.Select(i => i.Person).Distinct().ToList();
    Console.WriteLine($"{labels.Count} labelled photos, {skipped} without a usable face, {items.Count} faces of {people.Count} people.");
    foreach (var g in items.GroupBy(i => i.Person).OrderByDescending(g => g.Count()))
        Console.WriteLine($"   {g.Key,-28} {g.Count(),3} faces");

    // How far apart are same-person and different-person similarities?
    var same = new List<float>();
    var different = new List<float>();
    for (var i = 0; i < items.Count; i++)
        for (var j = i + 1; j < items.Count; j++)
            (items[i].Person == items[j].Person ? same : different).Add(FaceGrouping.Similarity(items[i].Embedding, items[j].Embedding));
    same.Sort();
    different.Sort();
    float P(List<float> v, double q) => v.Count == 0 ? float.NaN : v[(int)Math.Clamp(q * (v.Count - 1), 0, v.Count - 1)];
    Console.WriteLine($"\nSimilarity, same person ({same.Count} pairs):      5%={P(same, .05):0.00}  25%={P(same, .25):0.00}  median={P(same, .5):0.00}  75%={P(same, .75):0.00}");
    Console.WriteLine($"Similarity, different people ({different.Count} pairs): median={P(different, .5):0.00}  95%={P(different, .95):0.00}  99%={P(different, .99):0.00}  99.9%={P(different, .999):0.00}  max={different.LastOrDefault():0.00}");

    // The best single cut-off on pairs, ignoring grouping: where would "same person" be decided best?
    Console.WriteLine("\nPair cut-off (is this pair the same person?):");
    foreach (var t in new[] { 0.30f, 0.35f, 0.363f, 0.40f, 0.45f, 0.50f, 0.55f })
    {
        var tp = same.Count(s => s >= t);
        var fp = different.Count(d => d >= t);
        var prec = tp + fp == 0 ? 1.0 : tp / (double)(tp + fp);
        var rec = same.Count == 0 ? 0 : tp / (double)same.Count;
        Console.WriteLine($"   {t,5:0.000}: precision {prec:P1}, recall {rec:P1}, false matches {fp}");
    }

    // Grouping the way People does it (in order, centroid average), at each threshold, over several orders.
    var labelOf = items.Select(i => people.IndexOf(i.Person)).ToArray();
    var embeddings = items.Select(i => i.Embedding).ToList();
    var orders = new List<int[]> { Enumerable.Range(0, items.Count).ToArray() };
    var rng = new Random(7);
    for (var k = 1; k < o.Orders; k++) orders.Add(Enumerable.Range(0, items.Count).OrderBy(_ => rng.Next()).ToArray());

    Console.WriteLine($"\nGrouping at each threshold ({people.Count} real people; {orders.Count} photo orders; mean, with min to max for F1):");
    Console.WriteLine("  thresh | groups | single-face groups | purity | pair precision | pair recall | pair F1 (min-max)");
    foreach (var t in Enumerable.Range(0, 9).Select(i => 0.35f + i * 0.025f).Append(0.363f).OrderBy(x => x))
    {
        var rows = orders.Select(order =>
        {
            var clusters = FaceGrouping.Cluster(order.Select(i => embeddings[i]).ToList(), t);
            var truth = order.Select(i => labelOf[i]).ToArray();
            return Score(clusters, truth);
        }).ToList();
        Console.WriteLine($"   {t,5:0.000} | {rows.Average(r => r.Groups),6:0.0} | {rows.Average(r => r.Singletons),18:0.0} | {rows.Average(r => r.Purity),6:P0} | {rows.Average(r => r.Precision),14:P1} | {rows.Average(r => r.Recall),11:P1} | {rows.Average(r => r.F1):0.000} ({rows.Min(r => r.F1):0.000}-{rows.Max(r => r.F1):0.000})");
    }
    return 0;
}

/// <summary>One line of the labels file: a path (quoted if it contains commas) and a person.</summary>
static (string Path, string Person) ParseLabel(string line)
{
    line = line.Trim();
    if (line.StartsWith('"'))
    {
        var close = line.IndexOf('"', 1);
        return (line[1..close], line[(close + 1)..].TrimStart(',').Trim().Trim('"'));
    }
    var comma = line.IndexOf(',');
    return (line[..comma].Trim(), line[(comma + 1)..].Trim().Trim('"'));
}

static (double Groups, double Singletons, double Purity, double Precision, double Recall, double F1) Score(int[] clusters, int[] truth)
{
    var n = clusters.Length;
    long tp = 0, fp = 0, fn = 0;
    for (var i = 0; i < n; i++)
        for (var j = i + 1; j < n; j++)
        {
            var sameCluster = clusters[i] == clusters[j];
            var samePerson = truth[i] == truth[j];
            if (sameCluster && samePerson) tp++;
            else if (sameCluster) fp++;
            else if (samePerson) fn++;
        }
    var byCluster = clusters.Select((c, i) => (c, t: truth[i])).GroupBy(x => x.c).ToList();
    var purity = byCluster.Sum(g => g.GroupBy(x => x.t).Max(t => t.Count())) / (double)n;
    var precision = tp + fp == 0 ? 1 : tp / (double)(tp + fp);
    var recall = tp + fn == 0 ? 1 : tp / (double)(tp + fn);
    return (byCluster.Count, byCluster.Count(g => g.Count() == 1), purity, precision, recall, precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall));
}

/// <summary>The same request as ThumbnailService.GetBytesAsync(path, 960), re-encoded as JPEG like the app does.</summary>
static byte[] Thumbnail(string path)
{
    try
    {
        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        using var thumb = file.GetThumbnailAsync(ThumbnailMode.SingleItem, 960, ThumbnailOptions.ResizeThumbnail).AsTask().GetAwaiter().GetResult();
        using var stream = thumb.AsStreamForRead();
        using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(stream);
        using var jpeg = new MemoryStream();
        image.Save(jpeg, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 82 });
        return jpeg.ToArray();
    }
    catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
    {
        // Windows can't make thumbnails for some paths (network drives); the app then decodes the file
        // itself, like ThumbnailService.DecodeWithMagick.
        using var image = new ImageMagick.MagickImage(path);
        image.AutoOrient();
        image.Thumbnail(960, 960);
        image.Format = ImageMagick.MagickFormat.Jpeg;
        image.Quality = 82;
        return image.ToByteArray();
    }
}

static string? FindModels()
{
    var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
    return Directory.Exists(packages)
        ? Directory.GetDirectories(packages, "StephenStirling.Ocula_*")
            .Select(p => Path.Combine(p, "LocalCache", "Local", "Ocula", "Models", "faces")).FirstOrDefault(Directory.Exists)
        : null;
}

internal sealed record CachedFace(float[] Embedding, int Size, float Score);

internal sealed class Options
{
    public string Mode = "";
    public string Out = Path.Combine(Path.GetTempPath(), "ocula-faces");
    public string? Models, Labels, Root;
    public int MinFace = 40;
    public int Orders = 6;
    public List<string> Files = new();

    public static Options Parse(string[] args)
    {
        var o = new Options { Mode = args.FirstOrDefault() ?? "" };
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out": o.Out = args[++i]; break;
                case "--models": o.Models = args[++i]; break;
                case "--labels": o.Labels = args[++i]; break;
                case "--root": o.Root = args[++i]; break;
                case "--min-face": o.MinFace = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--orders": o.Orders = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default: o.Files.Add(args[i]); break;
            }
        }
        return o;
    }
}
