using System.Globalization;
using System.Text;
using Ocula.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using Windows.Storage;
using Windows.Storage.FileProperties;

// Measures smart search and tag suggestions on a labelled folder of photos. See README.md.

var options = Options.Parse(args);
if (options == null)
{
    Console.Error.WriteLine("""
        SmartEval --root <photo folder> [--labels <labels.csv>] [--tier compact|accurate|both] [--models <folder>] [--tags] [--dump <file.csv>]
        """);
    return 1;
}

var labelled = Labels.Read(options.Labels);
var byFile = labelled.ToDictionary(p => p.File);
var culture = CultureInfo.InvariantCulture;
using var dump = options.Dump != null ? new StreamWriter(options.Dump, false, Encoding.UTF8) : null;
dump?.WriteLine("tier,kind,prompt,file,positives,ambiguous,score");

Console.WriteLine($"# Smart recognition evaluation, {DateTime.Now:yyyy-MM-dd}");
Console.WriteLine();
Console.WriteLine($"{labelled.Count} labelled photos. Positives: " +
    string.Join(", ", Prompts.Subjects.Select(s => $"{s} {labelled.Count(p => p.Is(s))}")) + ".");

foreach (var tier in options.Tiers)
{
    var folder = Path.Combine(options.ModelsRoot, tier);
    using var model = ClipModel.Load(folder, Environment.ProcessorCount);
    var images = Embeddings.Load(model, tier, options.Root, labelled);
    var library = labelled.Select(p => (p.File, images[p.File])).ToList();

    if (dump != null)
    {
        foreach (var (kind, prompt) in Prompts.All())
        {
            var text = model.EncodeText(prompt);
            foreach (var photo in labelled)
                dump.WriteLine(string.Join(',', tier, kind, Csv(prompt), Csv(photo.File), photo.Positives, photo.Ambiguous,
                    SmartScoring.Dot(text, images[photo.File]).ToString("R", culture)));
        }
    }

    Console.WriteLine();
    Console.WriteLine($"## {tier}");
    Console.WriteLine();
    Console.WriteLine("### Search");
    Console.WriteLine();
    Console.WriteLine("Shown: photos the search returns. Right / wrong / missed: labelled positives shown, other photos " +
                      "shown, positives not shown (photos labelled ambiguous for the subject aren't counted either way).");
    Console.WriteLine();
    Console.WriteLine("| Query | Before: shown | right / wrong / missed | top 3 | After: shown | right / wrong / missed | top 3 |");
    Console.WriteLine("|---|--:|:-:|---|--:|:-:|---|");
    var neutral = SmartScoring.Average(SmartScoring.NeutralPrompts.Select(model.EncodeText).ToList());
    foreach (var (query, subject) in Prompts.Queries)
    {
        var before = Before.Search(library, model.EncodeText(query));
        var after = SmartScoring.Search(library, model.EncodeText(SmartScoring.SearchPrompt(query)), neutral);
        Console.WriteLine($"| {query} | {Describe(before, subject)} | {Describe(after, subject)} |");
    }

    var vectors = SmartLabels.All.Select(l => model.EncodeText($"a photo of {l}")).ToArray();
    var beforeTags = labelled.ToDictionary(p => p.File, p => Before.SuggestTags(images[p.File], SmartLabels.All, vectors));
    var afterTags = labelled.ToDictionary(p => p.File,
        p => SmartScoring.SuggestTags(images[p.File], SmartLabels.All, vectors, SmartLabels.Groups).Select(s => s.Label).ToList());

    Console.WriteLine();
    Console.WriteLine("### Tag suggestions");
    Console.WriteLine();
    Console.WriteLine("Expected tag: a suggestion names the photo's labelled subject (for example \"dog\", or \"sunset\" for a sunset).");
    Console.WriteLine();
    Console.WriteLine("| | Photos with no tags | Tags per photo | Expected tag suggested |");
    Console.WriteLine("|---|--:|--:|--:|");
    foreach (var (name, tags) in new[] { ("Before", beforeTags), ("After", afterTags) })
    {
        var expected = labelled.SelectMany(p => p.Subjects.Select(s => Prompts.ExpectedTags[s].Overlaps(tags[p.File]))).ToList();
        Console.WriteLine($"| {name} | {tags.Count(t => t.Value.Count == 0)} of {tags.Count} | {tags.Average(t => t.Value.Count):F1} | " +
                          $"{expected.Count(e => e)} of {expected.Count} |");
    }
    if (options.ShowTags)
    {
        Console.WriteLine();
        Console.WriteLine("| Photo | Before | After |");
        Console.WriteLine("|---|---|---|");
        foreach (var photo in labelled)
            Console.WriteLine($"| {photo.File} | {string.Join(", ", beforeTags[photo.File])} | {string.Join(", ", afterTags[photo.File])} |");
    }
}
return 0;

string Describe(List<SmartMatch> shown, string? subject)
{
    var photos = shown.Select(m => byFile[m.Path]).ToList();
    var right = subject == null ? 0 : photos.Count(p => p.Is(subject));
    var wrong = subject == null ? photos.Count : photos.Count(p => !p.Is(subject) && !p.Maybe(subject));
    var missed = subject == null ? 0 : labelled.Count(p => p.Is(subject)) - right;
    var top = string.Join("<br>", shown.Take(3).Select(m => Path.GetFileNameWithoutExtension(m.Path)));
    return $"{shown.Count} | {right} / {wrong} / {missed} | {top}";
}

static string Csv(string s) => s.Contains(',') ? $"\"{s}\"" : s;

/// <summary>The rules before this evaluation, kept to measure against.</summary>
static class Before
{
    /// <summary>The raw query; everything within 0.07 of the best score and above 0.19.</summary>
    public static List<SmartMatch> Search(List<(string Path, float[] Vector)> library, float[] query)
    {
        var scored = library.Select(item => new SmartMatch(item.Path, SmartScoring.Dot(query, item.Vector))).ToList();
        var cutoff = Math.Max(0.19f, scored.Max(s => s.Score) - 0.07f);
        return scored.Where(s => s.Score >= cutoff).OrderByDescending(s => s.Score).ToList();
    }

    /// <summary>Softmax over all labels, each label on its own, 10% or more, at most 6.</summary>
    public static List<string> SuggestTags(float[] image, IReadOnlyList<string> labels, float[][] vectors)
    {
        var logits = vectors.Select(v => SmartScoring.LogitScale * SmartScoring.Dot(image, v)).ToArray();
        var top = logits.Max();
        var exp = logits.Select(l => MathF.Exp(l - top)).ToArray();
        var sum = exp.Sum();
        return exp.Select((e, i) => (Label: labels[i], P: e / sum)).Where(x => x.P >= 0.10f)
                  .OrderByDescending(x => x.P).Take(6).Select(x => x.Label).ToList();
    }
}

sealed record Options(string Root, string Labels, string ModelsRoot, string[] Tiers, string? Dump, bool ShowTags)
{
    public static Options? Parse(string[] args)
    {
        string? root = null, labels = null, models = null, dump = null, tier = "both";
        var showTags = args.Contains("--tags");
        args = args.Where(a => a != "--tags").ToArray();
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--root": root = args[i + 1]; break;
                case "--labels": labels = args[i + 1]; break;
                case "--models": models = args[i + 1]; break;
                case "--tier": tier = args[i + 1]; break;
                case "--dump": dump = args[i + 1]; break;
                default: return null;
            }
        }
        models ??= DefaultModelsRoot();
        if (root == null || models == null) return null;
        var tiers = tier == "both" ? new[] { "clip-b32-compact", "clip-b32-accurate" } : new[] { $"clip-b32-{tier}" };
        return new Options(root, labels ?? Path.Combine(root, "labels.csv"), models, tiers, dump, showTags);
    }

    /// <summary>Where the packaged app keeps downloaded models (the same lookup as the tests).</summary>
    private static string? DefaultModelsRoot()
    {
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (!Directory.Exists(packages)) return null;
        return Directory.GetDirectories(packages, "StephenStirling.Ocula_*")
            .Select(p => Path.Combine(p, "LocalCache", "Local", "Ocula", "Models"))
            .FirstOrDefault(Directory.Exists);
    }
}

/// <summary>One row of labels.csv: a photo and the subjects in it.</summary>
sealed record Photo(string File, string Positives, string Ambiguous)
{
    public IEnumerable<string> Subjects => Positives.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    public bool Is(string subject) => Positives.Split(' ').Contains(subject);
    public bool Maybe(string subject) => Ambiguous.Split(' ').Contains(subject);
}

static class Labels
{
    public static List<Photo> Read(string path) => File.ReadLines(path)
        .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("file,"))
        .Select(l => l.Split(','))
        .Select(p => new Photo(p[0], p.ElementAtOrDefault(1) ?? "", p.ElementAtOrDefault(2) ?? ""))
        .ToList();
}

static class Prompts
{
    public static readonly string[] Subjects = { "sunset", "dog", "table", "snow", "food" };

    /// <summary>Suggestions that count as naming each subject.</summary>
    public static readonly Dictionary<string, HashSet<string>> ExpectedTags = new()
    {
        ["sunset"] = new() { "sunset", "sunrise", "golden hour" },
        ["dog"] = new() { "dog" },
        ["table"] = new() { "restaurant", "family", "breakfast", "food", "picnic", "birthday party", "barbecue" },
        ["snow"] = new() { "snow", "skiing", "snowboarding" },
        ["food"] = new() { "food", "pizza", "burger", "sushi", "salad", "cake", "dessert", "breakfast", "fruit", "vegetables", "barbecue", "picnic" },
    };

    /// <summary>Test queries and the subject each one should find (null: nothing in the set should match).</summary>
    public static readonly (string Query, string? Subject)[] Queries =
    {
        ("sunset over water", "sunset"), ("dog", "dog"), ("people at a table", "table"), ("snow", "snow"), ("food", "food"),
        ("cat", null), ("fireworks", null), ("elephant", null), ("christmas tree", null),
    };

    public static IEnumerable<(string Kind, string Prompt)> All()
    {
        foreach (var (q, _) in Queries) yield return ("query", q);
        foreach (var (q, _) in Queries) yield return ("query-photo", $"a photo of {q}");
        foreach (var n in new[] { "a photo", "a photo of something", "a picture", "an image", "" }) yield return ("neutral", n);
        foreach (var l in SmartLabels.All) yield return ("label", $"a photo of {l}");
    }
}

/// <summary>
/// Image embeddings, cached in the photo folder. Photos are prepared like the app does: Windows's
/// 448-pixel thumbnail (upright, as Windows follows the EXIF orientation), as JPEG.
/// </summary>
static class Embeddings
{
    public static Dictionary<string, float[]> Load(ClipModel model, string tier, string root, List<Photo> photos)
    {
        var cachePath = Path.Combine(root, $"embeddings-{tier}.bin");
        var cache = Read(cachePath);
        var changed = false;
        foreach (var photo in photos)
        {
            var file = new FileInfo(Path.Combine(root, photo.File));
            var key = $"{photo.File}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            if (cache.ContainsKey(key)) continue;
            cache[key] = model.EncodeImage(Thumbnail(file.FullName));
            changed = true;
        }
        if (changed) Write(cachePath, cache);
        return cache.ToDictionary(kv => kv.Key.Split('|')[0], kv => kv.Value);
    }

    /// <summary>The same request as ThumbnailService.GetBytesAsync(path, 448), re-encoded as JPEG like the app does.</summary>
    private static byte[] Thumbnail(string path)
    {
        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        using var thumb = file.GetThumbnailAsync(ThumbnailMode.SingleItem, 448, ThumbnailOptions.ResizeThumbnail)
                              .AsTask().GetAwaiter().GetResult();
        using var stream = thumb.AsStreamForRead();
        using var image = Image.Load<Rgb24>(stream);
        using var jpeg = new MemoryStream();
        image.Save(jpeg, new JpegEncoder { Quality = 82 });
        return jpeg.ToArray();
    }

    private static Dictionary<string, float[]> Read(string path)
    {
        var result = new Dictionary<string, float[]>();
        if (!File.Exists(path)) return result;
        using var reader = new BinaryReader(File.OpenRead(path));
        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var key = reader.ReadString();
            var vector = new float[reader.ReadInt32()];
            for (var j = 0; j < vector.Length; j++) vector[j] = reader.ReadSingle();
            result[key] = vector;
        }
        return result;
    }

    private static void Write(string path, Dictionary<string, float[]> cache)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(cache.Count);
        foreach (var (key, vector) in cache)
        {
            writer.Write(key);
            writer.Write(vector.Length);
            foreach (var f in vector) writer.Write(f);
        }
    }
}
