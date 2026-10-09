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
/// It never decides who anyone is: grouping and names are in PeopleService and the user's hands.
/// </summary>
public sealed class FaceService : IDisposable
{
    private const int MinFacePixels = 40;      // smaller faces give unreliable measurements
    private const float ScoreThreshold = 0.8f;
    private const float NmsThreshold = 0.3f;
    private const int MaxFacesPerPhoto = 20;
    private const int AlignedSize = 112;

    // Where the five landmarks sit in a standard 112x112 face (right eye, left eye, nose, right and left mouth corner, as seen in the photo).
    private static readonly (float X, float Y)[] Reference =
    {
        (38.2946f, 51.6963f), (73.5318f, 51.5014f), (56.0252f, 71.7366f), (41.5493f, 92.3655f), (70.7299f, 92.2041f)
    };

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
                var options = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2)
                };
                _detector = new InferenceSession(FaceModels.PathOf("yunet.onnx"), options);
                _recogniser = new InferenceSession(FaceModels.PathOf("sface.onnx"), options);
                return true;
            });
        }
        catch
        {
            Unload();
            return false;
        }
        finally
        {
            _loadLock.Release();
        }
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

    // ================= Detection (YuNet) =================

    private sealed record Detection(float X, float Y, float Width, float Height, float Score, (float X, float Y)[] Landmarks);

    private static List<Detection> Detect(InferenceSession session, Image<Rgb24> image)
    {
        // The model wants a fixed square. Keep the picture's shape and pad the rest with black.
        var inputName = session.InputMetadata.Keys.First();
        var dims = session.InputMetadata[inputName].Dimensions;
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
                    // YuNet expects BGR, 0 to 255, no further scaling.
                    tensor[0, 0, y, x] = row[x].B;
                    tensor[0, 1, y, x] = row[x].G;
                    tensor[0, 2, y, x] = row[x].R;
                }
            }
        });

        using var outputs = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
        var byName = outputs.ToDictionary(o => o.Name, o => o.AsTensor<float>());

        var all = new List<Detection>();
        foreach (var stride in new[] { 8, 16, 32 })
        {
            if (!byName.TryGetValue($"cls_{stride}", out var cls) || !byName.TryGetValue($"obj_{stride}", out var obj) ||
                !byName.TryGetValue($"bbox_{stride}", out var bbox) || !byName.TryGetValue($"kps_{stride}", out var kps))
                continue;

            var cols = side / stride;
            var count = cls.Dimensions[1];
            for (var n = 0; n < count; n++)
            {
                var score = MathF.Sqrt(Math.Clamp(cls[0, n, 0], 0f, 1f) * Math.Clamp(obj[0, n, 0], 0f, 1f));
                if (score < ScoreThreshold) continue;

                var row = n / cols;
                var col = n % cols;
                var cx = (col + bbox[0, n, 0]) * stride;
                var cy = (row + bbox[0, n, 1]) * stride;
                var bw = MathF.Exp(bbox[0, n, 2]) * stride;
                var bh = MathF.Exp(bbox[0, n, 3]) * stride;

                var landmarks = new (float X, float Y)[5];
                for (var k = 0; k < 5; k++)
                    landmarks[k] = (((kps[0, n, 2 * k] + col) * stride) / scale, ((kps[0, n, 2 * k + 1] + row) * stride) / scale);

                all.Add(new Detection((cx - bw / 2) / scale, (cy - bh / 2) / scale, bw / scale, bh / scale, score, landmarks));
            }
        }
        return NonMaxSuppression(all);
    }

    private static List<Detection> NonMaxSuppression(List<Detection> detections)
    {
        var kept = new List<Detection>();
        foreach (var d in detections.OrderByDescending(d => d.Score))
            if (kept.All(k => IoU(k, d) < NmsThreshold)) kept.Add(d);
        return kept;
    }

    private static float IoU(Detection a, Detection b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        var y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        var inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        var union = a.Width * a.Height + b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }

    // ================= Alignment =================

    /// <summary>Returns a 112x112 face as BGR floats [3][112*112], rotated and scaled to the standard pose.</summary>
    private static float[][] Align(Image<Rgb24> image, (float X, float Y)[] landmarks)
    {
        // Best similarity transform (rotation + uniform scale + shift) mapping the landmarks onto the reference.
        float mx = landmarks.Average(p => p.X), my = landmarks.Average(p => p.Y);
        float rx = Reference.Average(p => p.X), ry = Reference.Average(p => p.Y);
        float den = 0, a = 0, b = 0;
        for (var i = 0; i < 5; i++)
        {
            float px = landmarks[i].X - mx, py = landmarks[i].Y - my;
            float qx = Reference[i].X - rx, qy = Reference[i].Y - ry;
            den += px * px + py * py;
            a += px * qx + py * qy;
            b += px * qy - py * qx;
        }
        if (den < 1e-6f) den = 1e-6f;
        a /= den;
        b /= den;
        var tx = rx - (a * mx - b * my);
        var ty = ry - (b * mx + a * my);

        // For each output pixel, find where it came from in the photo (the inverse transform) and sample it.
        var norm = a * a + b * b;
        if (norm < 1e-9f) norm = 1e-9f;
        var channels = new[] { new float[AlignedSize * AlignedSize], new float[AlignedSize * AlignedSize], new float[AlignedSize * AlignedSize] };
        image.ProcessPixelRows(accessor =>
        {
            for (var v = 0; v < AlignedSize; v++)
            {
                for (var u = 0; u < AlignedSize; u++)
                {
                    float dx = u - tx, dy = v - ty;
                    var sx = (a * dx + b * dy) / norm;
                    var sy = (-b * dx + a * dy) / norm;
                    var (r, g, bl) = Bilinear(accessor, sx, sy);
                    var index = v * AlignedSize + u;
                    channels[0][index] = bl; // SFace expects BGR, 0 to 255
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
        var image = new Image<Rgb24>(AlignedSize, AlignedSize);
        for (var y = 0; y < AlignedSize; y++)
            for (var x = 0; x < AlignedSize; x++)
            {
                var i = y * AlignedSize + x;
                image[x, y] = new Rgb24((byte)Math.Clamp(bgr[2][i], 0, 255), (byte)Math.Clamp(bgr[1][i], 0, 255), (byte)Math.Clamp(bgr[0][i], 0, 255));
            }
        return image;
    }

    // ================= Recognition (SFace) =================

    private static float[] Embed(InferenceSession session, float[][] bgr)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, AlignedSize, AlignedSize });
        for (var c = 0; c < 3; c++)
            for (var i = 0; i < AlignedSize * AlignedSize; i++)
                tensor[0, c, i / AlignedSize, i % AlignedSize] = bgr[c][i];

        var inputName = session.InputMetadata.Keys.First();
        using var outputs = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
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
