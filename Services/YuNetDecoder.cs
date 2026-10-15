using System;
using System.Collections.Generic;
using System.Linq;

namespace Ocula.Services;

/// <summary>One face YuNet found: its box and five landmarks in pixels of the picture analysed.</summary>
public sealed record FaceDetection(float X, float Y, float Width, float Height, float Score, (float X, float Y)[] Landmarks);

/// <summary>
/// Turns YuNet's raw output tensors into faces. Pure arithmetic, so it is unit-tested
/// (tests/Ocula.Tests/YuNetDecoderTests.cs). It follows OpenCV's FaceDetectorYN, which the model
/// was published for (opencv/modules/objdetect/src/face_detect.cpp):
///   - the model has three grids, strides 8, 16 and 32, with outputs cls_N [1, cells, 1],
///     obj_N [1, cells, 1], bbox_N [1, cells, 4] and kps_N [1, cells, 10];
///   - score = sqrt(clamp(cls, 0, 1) * clamp(obj, 0, 1));
///   - box centre = (column + bbox[0], row + bbox[1]) * stride; size = exp(bbox[2], bbox[3]) * stride;
///   - landmark k = (kps[2k] + column, kps[2k + 1] + row) * stride, in the order right eye, left eye,
///     nose tip, right mouth corner, left mouth corner (right and left as seen in the picture).
/// </summary>
public static class YuNetDecoder
{
    public static readonly int[] Strides = { 8, 16, 32 };

    /// <summary>One grid's flattened outputs: Cls and Obj have one value per cell, Bbox four, Kps ten.</summary>
    public readonly record struct GridOutput(float[] Cls, float[] Obj, float[] Bbox, float[] Kps);

    /// <summary>
    /// Faces at or above <paramref name="scoreThreshold"/> from every grid, before non-maximum
    /// suppression. <paramref name="side"/> is the model's square input; <paramref name="scale"/> is
    /// how much the picture was shrunk to fit it, so results come back in the picture's own pixels.
    /// </summary>
    public static List<FaceDetection> Decode(int side, float scale, IReadOnlyDictionary<int, GridOutput> grids, float scoreThreshold)
    {
        var all = new List<FaceDetection>();
        foreach (var stride in Strides)
        {
            if (!grids.TryGetValue(stride, out var grid)) continue;

            var columns = side / stride;
            var cells = grid.Cls.Length;
            for (var n = 0; n < cells; n++)
            {
                var score = MathF.Sqrt(Math.Clamp(grid.Cls[n], 0f, 1f) * Math.Clamp(grid.Obj[n], 0f, 1f));
                if (score < scoreThreshold) continue;

                var row = n / columns;
                var column = n % columns;
                var cx = (column + grid.Bbox[n * 4]) * stride;
                var cy = (row + grid.Bbox[n * 4 + 1]) * stride;
                var w = MathF.Exp(grid.Bbox[n * 4 + 2]) * stride;
                var h = MathF.Exp(grid.Bbox[n * 4 + 3]) * stride;

                var landmarks = new (float X, float Y)[5];
                for (var k = 0; k < 5; k++)
                    landmarks[k] = ((grid.Kps[n * 10 + 2 * k] + column) * stride / scale,
                                    (grid.Kps[n * 10 + 2 * k + 1] + row) * stride / scale);

                all.Add(new FaceDetection((cx - w / 2) / scale, (cy - h / 2) / scale, w / scale, h / scale, score, landmarks));
            }
        }
        return all;
    }

    /// <summary>Keeps the best face of each overlapping group: highest score first, dropping any that overlap a kept one by <paramref name="iouThreshold"/> or more.</summary>
    public static List<FaceDetection> NonMaxSuppression(IEnumerable<FaceDetection> detections, float iouThreshold)
    {
        var kept = new List<FaceDetection>();
        foreach (var d in detections.OrderByDescending(d => d.Score))
            if (kept.All(k => IoU(k, d) < iouThreshold)) kept.Add(d);
        return kept;
    }

    /// <summary>Intersection over union of two boxes: 0 for none, 1 for identical.</summary>
    public static float IoU(FaceDetection a, FaceDetection b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        var y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        var inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        var union = a.Width * a.Height + b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }
}
