using Microsoft.ML.OnnxRuntime.Tensors;
using Ocula.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Ocula.Tests;

/// <summary>
/// Checks against the real CLIP models. They're downloaded by Ocula (Settings > Smart recognition),
/// so these tests look in the packaged app's data folder, or in OCULA_MODELS_DIR, and are skipped
/// when the models aren't there.
/// </summary>
public class ClipModelTests
{
    private readonly ITestOutputHelper _output;

    public ClipModelTests(ITestOutputHelper output) => _output = output;

    public static TheoryData<string> Tiers => new() { "clip-b32-compact", "clip-b32-accurate" };

    /// <summary>
    /// How close two embeddings of practically the same input must be. The Compact (int8 quantized)
    /// models are noisier: a +/-1 level change to every pixel already moves the image embedding to
    /// about 0.993 (Accurate: 0.9996), so they get a looser bound.
    /// </summary>
    private static float SameInput(string tier) => tier.EndsWith("compact") ? 0.98f : 0.999f;

    private static string? ModelsRoot()
    {
        if (Environment.GetEnvironmentVariable("OCULA_MODELS_DIR") is { Length: > 0 } dir) return dir;
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (!Directory.Exists(packages)) return null;
        return Directory.GetDirectories(packages, "StephenStirling.Ocula_*")
            .Select(p => Path.Combine(p, "LocalCache", "Local", "Ocula", "Models"))
            .FirstOrDefault(Directory.Exists);
    }

    private static ClipModel LoadOrSkip(string tier)
    {
        var folder = ModelsRoot() is { } root ? Path.Combine(root, tier) : null;
        if (folder == null || !File.Exists(Path.Combine(folder, "vision.onnx")))
            Assert.Skip($"{tier} isn't downloaded (turn on Smart recognition in Ocula, or set OCULA_MODELS_DIR).");
        return ClipModel.Load(folder!, Environment.ProcessorCount / 2);
    }

    [Theory]
    [MemberData(nameof(Tiers))]
    public void Models_have_the_expected_inputs_and_outputs(string tier)
    {
        using var model = LoadOrSkip(tier);
        foreach (var line in model.Describe()) _output.WriteLine(line);

        var lines = model.Describe().ToList();
        Assert.Contains("vision.onnx input  pixel_values Single [-1,-1,-1,-1]", lines);
        Assert.Contains("vision.onnx output image_embeds Single [-1,512]", lines);
        Assert.Contains("text.onnx input  input_ids Int64 [-1,-1]", lines);
        Assert.Contains("text.onnx output text_embeds Single [-1,512]", lines);
    }

    [Theory]
    [MemberData(nameof(Tiers))]
    public void Padding_does_not_change_the_text_embedding(string tier)
    {
        using var model = LoadOrSkip(tier);
        var ids = model.Tokenizer.Encode("a photo of a cat");
        var used = ClipTokenizer.UsedLength(ids);

        var withEndPadding = model.EncodeTokens(ids);
        var withZeroPadding = model.EncodeTokens(ids.Select((id, i) => i < used ? id : 0L).ToArray());
        var unpadded = model.EncodeTokens(ids.Take(used).ToArray());

        _output.WriteLine($"cosine end-padding vs zero-padding: {Dot(withEndPadding, withZeroPadding):F6}");
        _output.WriteLine($"cosine end-padding vs unpadded:     {Dot(withEndPadding, unpadded):F6}");
        // Ocula always sends 77 ids; how they're padded must not matter. (Sending no padding at all
        // is a different input, and the quantized model gives a very slightly different result.)
        Assert.True(Dot(withEndPadding, withZeroPadding) > SameInput(tier));
        Assert.True(Dot(withEndPadding, unpadded) > SameInput(tier));
    }

    [Theory]
    [MemberData(nameof(Tiers))]
    public void Image_preprocessing_matches_the_Pillow_reference(string tier)
    {
        using var model = LoadOrSkip(tier);
        var dir = Path.Combine(AppContext.BaseDirectory, "TestData", "image");

        using var image = Image.Load<Rgb24>(Path.Combine(dir, "synthetic.png"));
        var ours = ClipModel.Preprocess(image);
        var reference = FromRgb(File.ReadAllBytes(Path.Combine(dir, "reference_224.rgb")));

        // Pixel differences, back in 0..255 units.
        double total = 0, worst = 0;
        float[] std = { 0.26862954f, 0.26130258f, 0.27577711f };
        for (var c = 0; c < 3; c++)
        for (var y = 0; y < ClipModel.ImageSize; y++)
        for (var x = 0; x < ClipModel.ImageSize; x++)
        {
            var diff = Math.Abs(ours[0, c, y, x] - reference[0, c, y, x]) * std[c] * 255;
            total += diff;
            worst = Math.Max(worst, diff);
        }
        var mean = total / (3 * ClipModel.ImageSize * ClipModel.ImageSize);
        var cosine = Dot(model.EncodePixels(ours), model.EncodePixels(reference));

        _output.WriteLine($"pixel difference vs Pillow: mean {mean:F2}, max {worst:F0} (0..255)");
        _output.WriteLine($"embedding cosine vs Pillow-preprocessed: {cosine:F5}");
        Assert.True(mean < 0.5, $"mean pixel difference {mean:F2}");
        Assert.True(worst <= 8, $"max pixel difference {worst:F0}");
        Assert.True(cosine > SameInput(tier), $"embedding cosine {cosine:F5}");
    }

    /// <summary>Raw 224x224 RGB bytes, normalised the way CLIP expects.</summary>
    private static DenseTensor<float> FromRgb(byte[] rgb)
    {
        float[] mean = { 0.48145466f, 0.4578275f, 0.40821073f };
        float[] std = { 0.26862954f, 0.26130258f, 0.27577711f };
        var size = ClipModel.ImageSize;
        var tensor = new DenseTensor<float>(new[] { 1, 3, size, size });
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        for (var c = 0; c < 3; c++)
            tensor[0, c, y, x] = (rgb[(y * size + x) * 3 + c] / 255f - mean[c]) / std[c];
        return tensor;
    }

    private static float Dot(float[] a, float[] b) => a.Zip(b, (x, y) => x * y).Sum();
}
