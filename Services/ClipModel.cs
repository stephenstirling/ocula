using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Ocula.Services;

/// <summary>
/// The CLIP models themselves: loads the image and text ONNX files and the tokenizer from a
/// model folder and turns a picture or a sentence into a normalised 512-number embedding.
/// Knows nothing about settings or the library, so it can be tested on its own.
/// </summary>
public sealed class ClipModel : IDisposable
{
    // The tensor names in Xenova's ONNX export of clip-vit-base-patch32 (both the full and the
    // quantized files). Checked when loading, so a different export fails clearly.
    public const string ImageInput = "pixel_values";   // float32 [batch, 3, 224, 224], RGB
    public const string ImageOutput = "image_embeds";  // float32 [batch, 512]
    public const string TextInput = "input_ids";       // int64 [batch, 77]
    public const string TextOutput = "text_embeds";    // float32 [batch, 512]

    // Image preprocessing from the model's preprocessor_config.json: shortest edge to 224 with
    // bicubic resampling, centre crop to 224 x 224, scale to 0..1, then normalise per channel.
    public const int ImageSize = 224;
    private static readonly float[] Mean = { 0.48145466f, 0.4578275f, 0.40821073f };
    private static readonly float[] Std = { 0.26862954f, 0.26130258f, 0.27577711f };

    private readonly InferenceSession _vision;
    private readonly InferenceSession _text;

    public ClipTokenizer Tokenizer { get; }

    private ClipModel(InferenceSession vision, InferenceSession text, ClipTokenizer tokenizer)
    {
        _vision = vision;
        _text = text;
        Tokenizer = tokenizer;
    }

    /// <summary>Loads vision.onnx, text.onnx, vocab.json and merges.txt from a model folder.</summary>
    public static ClipModel Load(string folder, int threads)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, threads)
        };
        InferenceSession? vision = null, text = null;
        try
        {
            vision = new InferenceSession(Path.Combine(folder, "vision.onnx"), options);
            text = new InferenceSession(Path.Combine(folder, "text.onnx"), options);
            Require(vision, "vision.onnx", ImageInput, ImageOutput);
            Require(text, "text.onnx", TextInput, TextOutput);
            var tokenizer = new ClipTokenizer(Path.Combine(folder, "vocab.json"), Path.Combine(folder, "merges.txt"));
            return new ClipModel(vision, text, tokenizer);
        }
        catch
        {
            vision?.Dispose();
            text?.Dispose();
            throw;
        }
    }

    private static void Require(InferenceSession session, string file, string input, string output)
    {
        if (!session.InputMetadata.ContainsKey(input) || !session.OutputMetadata.ContainsKey(output))
            throw new InvalidDataException(
                $"{file} isn't the expected CLIP export: needs input '{input}' and output '{output}', " +
                $"has inputs [{string.Join(", ", session.InputMetadata.Keys)}] and outputs [{string.Join(", ", session.OutputMetadata.Keys)}].");
    }

    /// <summary>Turns a JPEG/PNG (a thumbnail is plenty) into a normalised embedding.</summary>
    public float[] EncodeImage(byte[] imageBytes)
    {
        using var image = Image.Load<Rgb24>(imageBytes);
        return EncodePixels(Preprocess(image));
    }

    /// <summary>Resizes, centre-crops and normalises a picture into the [1, 3, 224, 224] tensor CLIP reads.</summary>
    public static DenseTensor<float> Preprocess(Image<Rgb24> image)
    {
        // Same arithmetic as Hugging Face's CLIPImageProcessor: the short side becomes 224 and the
        // long side is rounded down, then the centre square is cut with the offset rounded down.
        // (ImageSharp's ResizeMode.Crop rounds differently, which shifted the picture by a pixel.)
        var landscape = image.Width >= image.Height;
        var shortSide = Math.Min(image.Width, image.Height);
        var longSide = Math.Max(image.Width, image.Height);
        var scaledLong = Math.Max(ImageSize, (int)((double)ImageSize * longSide / shortSide));
        var width = landscape ? scaledLong : ImageSize;
        var height = landscape ? ImageSize : scaledLong;
        image.Mutate(x => x
            .Resize(width, height, KnownResamplers.Bicubic)
            .Crop(new Rectangle((width - ImageSize) / 2, (height - ImageSize) / 2, ImageSize, ImageSize)));

        var tensor = new DenseTensor<float>(new[] { 1, 3, ImageSize, ImageSize });
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    tensor[0, 0, y, x] = (p.R / 255f - Mean[0]) / Std[0];
                    tensor[0, 1, y, x] = (p.G / 255f - Mean[1]) / Std[1];
                    tensor[0, 2, y, x] = (p.B / 255f - Mean[2]) / Std[2];
                }
            }
        });
        return tensor;
    }

    /// <summary>Runs the image model on an already-preprocessed tensor.</summary>
    public float[] EncodePixels(DenseTensor<float> pixels) =>
        Run(_vision, NamedOnnxValue.CreateFromTensor(ImageInput, pixels), ImageOutput);

    public float[] EncodeText(string text) => EncodeTokens(Tokenizer.Encode(text));

    /// <summary>Runs the text model on token ids (normally the tokenizer's 77).</summary>
    public float[] EncodeTokens(long[] ids)
    {
        var tensor = new DenseTensor<long>(ids, new[] { 1, ids.Length });
        return Run(_text, NamedOnnxValue.CreateFromTensor(TextInput, tensor), TextOutput);
    }

    private static float[] Run(InferenceSession session, NamedOnnxValue input, string outputName)
    {
        using var results = session.Run(new[] { input }, new[] { outputName });
        var vector = results.First().AsTensor<float>().ToArray();

        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm > 0) for (var i = 0; i < vector.Length; i++) vector[i] /= norm;
        return vector;
    }

    /// <summary>Input and output names, types and shapes, for diagnostics.</summary>
    public IEnumerable<string> Describe()
    {
        foreach (var (file, session) in new[] { ("vision.onnx", _vision), ("text.onnx", _text) })
        {
            foreach (var (name, m) in session.InputMetadata)
                yield return $"{file} input  {name} {m.ElementType.Name} [{string.Join(",", m.Dimensions)}]";
            foreach (var (name, m) in session.OutputMetadata)
                yield return $"{file} output {name} {m.ElementType.Name} [{string.Join(",", m.Dimensions)}]";
        }
    }

    public void Dispose()
    {
        _vision.Dispose();
        _text.Dispose();
    }
}
