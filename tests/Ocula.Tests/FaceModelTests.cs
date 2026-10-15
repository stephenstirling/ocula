using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Ocula.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace Ocula.Tests;

/// <summary>The model files People downloads: where from, and what the real YuNet and SFace do.</summary>
public class FaceModelTests
{
    private readonly ITestOutputHelper _output;

    public FaceModelTests(ITestOutputHelper output) => _output = output;

    private static readonly string Photos = Path.Combine(AppContext.BaseDirectory, "TestData");

    // ---------- Download list (no model files needed) ----------

    [Fact]
    public void Both_models_are_pinned_to_a_revision_with_a_size_and_a_SHA256()
    {
        Assert.Equal(new[] { "yunet.onnx", "sface.onnx" }, FaceModels.Files.Select(f => f.LocalName));
        foreach (var file in FaceModels.Files)
        {
            Assert.Matches(@"^https://huggingface\.co/opencv/[a-z_]+/resolve/[0-9a-f]{40}/", file.Url);   // not "main"
            Assert.True(file.Size > 0);
            Assert.Matches("^[0-9a-f]{64}$", file.Sha256 ?? "");
        }
    }

    [Fact]
    public void The_published_sizes_are_recorded()
    {
        Assert.Equal(232_589, FaceModels.Files.Single(f => f.LocalName == "yunet.onnx").Size);
        Assert.Equal(38_696_353, FaceModels.Files.Single(f => f.LocalName == "sface.onnx").Size);
    }

    // ---------- Needs the downloaded models ----------

    private static string? ModelsFolder()
    {
        if (Environment.GetEnvironmentVariable("OCULA_FACE_MODELS_DIR") is { Length: > 0 } dir) return dir;
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        return Directory.Exists(packages)
            ? Directory.GetDirectories(packages, "StephenStirling.Ocula_*")
                .Select(p => Path.Combine(p, "LocalCache", "Local", "Ocula", "Models", "faces")).FirstOrDefault(Directory.Exists)
            : null;
    }

    private static string FolderOrSkip()
    {
        var folder = ModelsFolder();
        if (folder == null || !File.Exists(Path.Combine(folder, "sface.onnx")))
            Assert.Skip("The People models aren't downloaded (turn on People in Ocula's Settings, or set OCULA_FACE_MODELS_DIR).");
        return folder!;
    }

    private static FaceService LoadOrSkip()
    {
        var service = new FaceService();
        service.Load(FolderOrSkip(), threads: 2);
        return service;
    }

    [Fact]
    public void The_downloaded_files_are_the_expected_ones()
    {
        var folder = FolderOrSkip();
        foreach (var file in FaceModels.Files)
        {
            var path = Path.Combine(folder, file.LocalName);
            Assert.Equal(file.Size, new FileInfo(path).Length);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            Assert.Equal(file.Sha256, hash);
        }
    }

    [Fact]
    public void The_models_have_the_tensors_the_code_uses()
    {
        var folder = FolderOrSkip();

        using var yunet = new InferenceSession(Path.Combine(folder, "yunet.onnx"));
        Assert.Equal(new[] { 1, 3, 640, 640 }, yunet.InputMetadata[FaceService.DetectorInput].Dimensions);
        foreach (var (stride, cells) in new[] { (8, 6400), (16, 1600), (32, 400) })
        {
            Assert.Equal(new[] { 1, cells, 1 }, yunet.OutputMetadata[$"cls_{stride}"].Dimensions);
            Assert.Equal(new[] { 1, cells, 1 }, yunet.OutputMetadata[$"obj_{stride}"].Dimensions);
            Assert.Equal(new[] { 1, cells, 4 }, yunet.OutputMetadata[$"bbox_{stride}"].Dimensions);
            Assert.Equal(new[] { 1, cells, 10 }, yunet.OutputMetadata[$"kps_{stride}"].Dimensions);
            Assert.Equal(cells, (640 / stride) * (640 / stride));
        }

        using var sface = new InferenceSession(Path.Combine(folder, "sface.onnx"));
        Assert.Equal(new[] { 1, 3, 112, 112 }, sface.InputMetadata[FaceService.RecogniserInput].Dimensions);
        Assert.Equal(new[] { 1, 128 }, sface.OutputMetadata[FaceService.RecogniserOutput].Dimensions);
    }

    [Fact]
    public void A_picture_with_no_face_gives_no_faces()
    {
        using var service = LoadOrSkip();

        var faces = service.Analyse(File.ReadAllBytes(Path.Combine(Photos, "image", "synthetic.png")));

        Assert.Empty(faces);
    }

    private static byte[] Headshot() => File.ReadAllBytes(Path.Combine(Photos, "face", "headshot-cc0.jpg"));

    [Fact]
    public void A_headshot_gives_one_face_with_landmarks_in_the_right_places()
    {
        using var service = LoadOrSkip();

        var detections = service.DetectOnly(Headshot());
        var face = Assert.Single(detections.Where(d => d.Score >= 0.8f));

        _output.WriteLine($"score {face.Score:0.00}, box {face.X:0},{face.Y:0} {face.Width:0}x{face.Height:0}");
        Assert.InRange(face.Score, 0.85f, 1f);
        Assert.InRange(Math.Min(face.Width, face.Height), 100, 457);
        Assert.True(face.X >= -5 && face.Y >= -5 && face.X + face.Width <= 462 && face.Y + face.Height <= 645, "the box is inside the 457 x 640 photo");

        var (rightEye, leftEye, nose, rightMouth, leftMouth) = (face.Landmarks[0], face.Landmarks[1], face.Landmarks[2], face.Landmarks[3], face.Landmarks[4]);
        Assert.True(rightEye.X < leftEye.X, "the first eye is the one on the viewer's left");
        Assert.True(rightMouth.X < leftMouth.X, "the first mouth corner is the one on the viewer's left");
        Assert.True(nose.Y > Math.Max(rightEye.Y, leftEye.Y), "the nose is below the eyes");
        Assert.True(Math.Min(rightMouth.Y, leftMouth.Y) > nose.Y, "the mouth is below the nose");
        Assert.All(face.Landmarks, p => Assert.True(p.X >= face.X && p.X <= face.X + face.Width && p.Y >= face.Y && p.Y <= face.Y + face.Height, "landmarks are inside the box"));
    }

    [Fact]
    public void The_embedding_is_128_numbers_of_length_one_and_the_same_every_time()
    {
        using var service = LoadOrSkip();

        var first = Assert.Single(service.Analyse(Headshot()));
        var second = Assert.Single(service.Analyse(Headshot()));

        Assert.Equal(128, first.Embedding.Length);
        Assert.Equal(1f, MathF.Sqrt(first.Embedding.Sum(x => x * x)), 4);
        Assert.Equal(1f, FaceGrouping.Similarity(first.Embedding, second.Embedding), 4);
        Assert.NotEmpty(first.Thumbnail);
    }

    [Fact]
    public void The_same_face_at_another_size_or_mirrored_still_matches_far_above_the_group_threshold()
    {
        using var service = LoadOrSkip();
        var original = Assert.Single(service.Analyse(Headshot()));

        byte[] Transform(Action<IImageProcessingContext> change)
        {
            using var image = Image.Load<Rgb24>(Headshot());
            image.Mutate(change);
            using var stream = new MemoryStream();
            image.Save(stream, new JpegEncoder { Quality = 90 });
            return stream.ToArray();
        }

        var half = Assert.Single(service.Analyse(Transform(x => x.Resize(229, 320))));
        var mirrored = Assert.Single(service.Analyse(Transform(x => x.Flip(FlipMode.Horizontal))));

        var smaller = FaceGrouping.Similarity(original.Embedding, half.Embedding);
        var flipped = FaceGrouping.Similarity(original.Embedding, mirrored.Embedding);
        _output.WriteLine($"half size: {smaller:0.00}; mirrored: {flipped:0.00}");
        Assert.True(smaller > 0.8f, $"half size {smaller:0.00}");
        Assert.True(flipped > 0.6f, $"mirrored {flipped:0.00}");
    }

    [Fact]
    public void Feeding_the_face_in_the_wrong_colour_order_gives_a_different_embedding()
    {
        // The reason Embed swaps to RGB (see FaceService.Embed): this isn't a no-op, the model really
        // notices. A blue-tinted copy of the headshot stands in for "channels swapped".
        using var service = LoadOrSkip();
        var original = Assert.Single(service.Analyse(Headshot()));

        using var image = Image.Load<Rgb24>(Headshot());
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
                foreach (ref var p in accessor.GetRowSpan(y)) p = new Rgb24(p.B, p.G, p.R);
        });
        using var stream = new MemoryStream();
        image.Save(stream, new JpegEncoder { Quality = 95 });
        var swapped = service.Analyse(stream.ToArray());

        // Swapped colours may or may not be detected as a face; if it is, it must not match as the same face.
        if (swapped.Count == 1)
            Assert.True(FaceGrouping.Similarity(original.Embedding, swapped[0].Embedding) < 0.95f);
    }
}
