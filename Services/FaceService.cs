using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Ocula.Services;

/// <summary>One face found in a photo. Box is in pixels of the image that was analysed.</summary>
public sealed record FaceResult(int X, int Y, int Width, int Height, float Score, float[] Embedding, byte[] Thumbnail);

/// <summary>
/// Finds faces and measures them, entirely on this PC:
///   1. YuNet detects faces and five landmarks (eyes, nose, mouth corners).
///   2. The face is rotated, scaled and cropped to a standard 112x112 pose using those landmarks.
///   3. SFace turns that crop into 128 numbers; similar faces give similar numbers.
/// It never decides who anyone is: grouping and names are in FaceGrouping, FaceIndexer and the user's hands.
/// The decoding and alignment arithmetic is in YuNetDecoder and FaceAlignment (unit-tested).
/// </summary>
public sealed class FaceService : IDisposable
{
    // The tensors of the OpenCV Zoo models (checked against the real files by FaceModelTests).
    public const string DetectorInput = "input";    // float32 [1, 3, 640, 640], BGR, 0 to 255
    public const string RecogniserInput = "data";   // float32 [1, 3, 112, 112], RGB, 0 to 255
    public const string RecogniserOutput = "fc1";   // float32 [1, 128]

    /// <summary>Smaller faces give unreliable measurements.</summary>
    public int MinFacePixels { get; init; } = 40;
    public float ScoreThreshold { get; init; } = 0.8f;
    public float NmsThreshold { get; init; } = 0.3f;
    public int MaxFacesPerPhoto { get; init; } = 20;

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private InferenceSession? _detector;
    private InferenceSession? _recogniser;

    public async Task<bool> LoadAsync()
    {
        await _loadLock.WaitAsync();
        try
        {
            if (_detector != null && _recogniser != null) return true;
            if (!FaceModels.IsInstalled) return false;
            return await Task.Run(() =>
            {
                Load(FaceModels.Folder, Math.Max(1, Environment.ProcessorCount / 2));
                return true;
            });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("People: couldn't load the face models", ex);
            Unload();
            return false;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>Loads yunet.onnx and sface.onnx from a folder, and checks they have the expected tensors.</summary>
    public void Load(string folder, int threads)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, threads)
        };
        try
        {
            _detector = new InferenceSession(Path.Combine(folder, "yunet.onnx"), options);
            _recogniser = new InferenceSession(Path.Combine(folder, "sface.onnx"), options);
            Require(_detector, "yunet.onnx", new[] { DetectorInput }, YuNetDecoder.Strides.SelectMany(s => new[] { $"cls_{s}", $"obj_{s}", $"bbox_{s}", $"kps_{s}" }));
            Require(_recogniser, "sface.onnx", new[] { RecogniserInput }, new[] { RecogniserOutput });
        }
        catch
        {
            Unload();
            throw;
        }
    }

    private static void Require(InferenceSession session, string file, IEnumerable<string> inputs, IEnumerable<string> outputs)
    {
        var missing = inputs.Where(n => !session.InputMetadata.ContainsKey(n))
                            .Concat(outputs.Where(n => !session.OutputMetadata.ContainsKey(n))).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException($"{file} isn't the expected OpenCV Zoo model: missing {string.Join(", ", missing)}.");
    }

    private void Unload()
    {
        _detector?.Dispose();
        _recogniser?.Dispose();
        _detector = null;
        _recogniser = null;
    }

    /// <summary>Finds and measures every usable face in a JPEG/PNG. Call from a background thread.</summary>
    public List<FaceResult> Analyse(byte[] imageBytes)
    {
        var detector = _detector ?? throw new InvalidOperationException("People isn't loaded.");
        var recogniser = _recogniser!;

        using var image = Image.Load<Rgb24>(imageBytes);
        var found = Detect(detector, image)
            .Where(f => Math.Min(f.Width, f.Height) >= MinFacePixels)
            .OrderByDescending(f => f.Score)
            .Take(MaxFacesPerPhoto)
            .ToList();

        var results = new List<FaceResult>();
        foreach (var face in found)
        {
            var aligned = Align(image, face.Landmarks);
            var embedding = Embed(recogniser, aligned);
            using var thumb = ToImage(aligned);
            using var stream = new MemoryStream();
            thumb.SaveAsJpeg(stream, new JpegEncoder { Quality = 85 });
            results.Add(new FaceResult((int)face.X, (int)face.Y, (int)face.Width, (int)face.Height,
                                       face.Score, embedding, stream.ToArray()));
        }
        return results;
    }

    /// <summary>Every face YuNet finds, before the size and count limits Analyse applies. For diagnostics.</summary>
    public IReadOnlyList<FaceDetection> DetectOnly(byte[] imageBytes)
    {
        var detector = _detector ?? throw new InvalidOperationException("People isn't loaded.");
        using var image = Image.Load<Rgb24>(imageBytes);
        return Detect(detector, image);
    }

    // ================= Detection (YuNet) =================

    private List<FaceDetection> Detect(InferenceSession session, Image<Rgb24> image)
    {
        // The model wants a fixed square. Keep the picture's shape and pad the rest with black.
        var dims = session.InputMetadata[DetectorInput].Dimensions;
        var side = dims.Length == 4 && dims[2] > 0 && dims[2] == dims[3] ? dims[2] : 640;

        var scale = side / (float)Math.Max(image.Width, image.Height);
        var w = Math.Max(1, (int)Math.Round(image.Width * scale));
        var h = Math.Max(1, (int)Math.Round(image.Height * scale));

        using var resized = image.Clone(x => x.Resize(w, h));
        var tensor = new DenseTensor<float>(new[] { 1, 3, side, side }); // zeros = black padding
        resized.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    // YuNet expects BGR, 0 to 255, no further scaling (OpenCV's blobFromImage with swapRB off).
                    tensor[0, 0, y, x] = row[x].B;
                    tensor[0, 1, y, x] = row[x].G;
                    tensor[0, 2, y, x] = row[x].R;
                }
            }
        });

        using var outputs = session.Run(new[] { NamedOnnxValue.CreateFromTensor(DetectorInput, tensor) });
        var byName = outputs.ToDictionary(o => o.Name, o => o.AsTensor<float>().ToArray());
        var grids = YuNetDecoder.Strides.ToDictionary(
            s => s,
            s => new YuNetDecoder.GridOutput(byName[$"cls_{s}"], byName[$"obj_{s}"], byName[$"bbox_{s}"], byName[$"kps_{s}"]));

        return YuNetDecoder.NonMaxSuppression(YuNetDecoder.Decode(side, scale, grids, ScoreThreshold), NmsThreshold);
    }

    // ================= Alignment =================

    /// <summary>Returns a 112x112 face as BGR floats [3][112*112], rotated and scaled to the standard pose.</summary>
    private static float[][] Align(Image<Rgb24> image, (float X, float Y)[] landmarks)
    {
        var transform = FaceAlignment.Solve(landmarks);
        const int size = FaceAlignment.Size;

        // For each output pixel, find where it came from in the photo (the inverse transform) and sample it.
        var channels = new[] { new float[size * size], new float[size * size], new float[size * size] };
        image.ProcessPixelRows(accessor =>
        {
            for (var v = 0; v < size; v++)
            {
                for (var u = 0; u < size; u++)
                {
                    var (sx, sy) = transform.Invert(u, v);
                    var (r, g, bl) = Bilinear(accessor, sx, sy);
                    var index = v * size + u;
                    channels[0][index] = bl;
                    channels[1][index] = g;
                    channels[2][index] = r;
                }
            }
        });
        return channels;
    }

    private static (float R, float G, float B) Bilinear(PixelAccessor<Rgb24> accessor, float x, float y)
    {
        x = Math.Clamp(x, 0, accessor.Width - 1.001f);
        y = Math.Clamp(y, 0, accessor.Height - 1.001f);
        int x0 = (int)x, y0 = (int)y;
        float fx = x - x0, fy = y - y0;
        // Row spans can't be captured by the local function, so copy out the four neighbours.
        var top = accessor.GetRowSpan(y0);
        var bottom = accessor.GetRowSpan(y0 + 1);
        Rgb24 tl = top[x0], tr = top[x0 + 1], bl = bottom[x0], br = bottom[x0 + 1];

        float Mix(Func<Rgb24, float> channel) =>
            channel(tl) * (1 - fx) * (1 - fy) + channel(tr) * fx * (1 - fy) +
            channel(bl) * (1 - fx) * fy + channel(br) * fx * fy;

        return (Mix(p => p.R), Mix(p => p.G), Mix(p => p.B));
    }

    private static Image<Rgb24> ToImage(float[][] bgr)
    {
        const int size = FaceAlignment.Size;
        var image = new Image<Rgb24>(size, size);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var i = y * size + x;
                image[x, y] = new Rgb24((byte)Math.Clamp(bgr[2][i], 0, 255), (byte)Math.Clamp(bgr[1][i], 0, 255), (byte)Math.Clamp(bgr[0][i], 0, 255));
            }
        return image;
    }

    // ================= Recognition (SFace) =================

    private static float[] Embed(InferenceSession session, float[][] bgr)
    {
        const int size = FaceAlignment.Size;
        var tensor = new DenseTensor<float>(new[] { 1, 3, size, size });

        // SFace reads RGB, 0 to 255: OpenCV's FaceRecognizerSF calls blobFromImage with swapRB on, which
        // turns its BGR crop into RGB. (Feeding BGR, as this once did, still runs but gives worse embeddings.)
        for (var c = 0; c < 3; c++)
        {
            var source = bgr[2 - c];
            for (var i = 0; i < size * size; i++)
                tensor[0, c, i / size, i % size] = source[i];
        }

        using var outputs = session.Run(new[] { NamedOnnxValue.CreateFromTensor(RecogniserInput, tensor) }, new[] { RecogniserOutput });
        var vector = outputs.First().AsTensor<float>().ToArray();

        var length = MathF.Sqrt(vector.Sum(v => v * v));
        if (length > 0) for (var i = 0; i < vector.Length; i++) vector[i] /= length;
        return vector;
    }

    public void Dispose()
    {
        Unload();
        _loadLock.Dispose();
    }
}
