using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

/// <summary>
/// YuNet's decoding, against the formulas in OpenCV's FaceDetectorYN (face_detect.cpp): score is
/// sqrt(clamp(cls) * clamp(obj)), the box centre is (cell + offset) * stride, the size is
/// exp(raw) * stride, and landmarks are (raw + cell) * stride.
/// </summary>
public class YuNetDecoderTests
{
    private const int Side = 640;

    /// <summary>An empty grid for a stride: every cell scores zero.</summary>
    private static YuNetDecoder.GridOutput EmptyGrid(int stride)
    {
        var cells = (Side / stride) * (Side / stride);
        return new YuNetDecoder.GridOutput(new float[cells], new float[cells], new float[cells * 4], new float[cells * 10]);
    }

    private static Dictionary<int, YuNetDecoder.GridOutput> EmptyGrids() =>
        YuNetDecoder.Strides.ToDictionary(s => s, EmptyGrid);

    private static void Put(YuNetDecoder.GridOutput grid, int stride, int row, int column, float cls, float obj,
                            float[] bbox, float[]? kps = null)
    {
        var n = row * (Side / stride) + column;
        grid.Cls[n] = cls;
        grid.Obj[n] = obj;
        Array.Copy(bbox, 0, grid.Bbox, n * 4, 4);
        if (kps != null) Array.Copy(kps, 0, grid.Kps, n * 10, 10);
    }

    [Fact]
    public void The_strides_are_8_16_and_32()
    {
        Assert.Equal(new[] { 8, 16, 32 }, YuNetDecoder.Strides);
    }

    [Fact]
    public void A_cell_decodes_to_the_box_in_OpenCVs_formulas()
    {
        var grids = EmptyGrids();
        // Stride 8, row 10, column 20: centre = ((20 + 0.25) * 8, (10 + 0.5) * 8) = (162, 84); size = exp(ln 4) * 8 by exp(ln 5) * 8 = 32 x 40.
        Put(grids[8], 8, row: 10, column: 20, cls: 0.9f, obj: 0.9f, bbox: new[] { 0.25f, 0.5f, MathF.Log(4), MathF.Log(5) });

        var face = Assert.Single(YuNetDecoder.Decode(Side, scale: 1f, grids, scoreThreshold: 0.5f));

        Assert.Equal(162 - 16, face.X, 3);
        Assert.Equal(84 - 20, face.Y, 3);
        Assert.Equal(32, face.Width, 3);
        Assert.Equal(40, face.Height, 3);
        Assert.Equal(0.9f, face.Score, 5);
    }

    [Fact]
    public void Results_are_scaled_back_to_the_picture_the_model_was_shrunk_from()
    {
        var grids = EmptyGrids();
        Put(grids[8], 8, row: 10, column: 20, cls: 1, obj: 1, bbox: new[] { 0.25f, 0.5f, MathF.Log(4), MathF.Log(5) });

        // The picture was shrunk to a quarter (scale 0.25) to fit the model: everything comes back four times bigger.
        var face = Assert.Single(YuNetDecoder.Decode(Side, scale: 0.25f, grids, 0.5f));

        Assert.Equal((162 - 16) * 4, face.X, 2);
        Assert.Equal((84 - 20) * 4, face.Y, 2);
        Assert.Equal(32 * 4, face.Width, 2);
        Assert.Equal(40 * 4, face.Height, 2);
    }

    [Fact]
    public void Score_is_the_geometric_mean_of_class_and_objectness()
    {
        var grids = EmptyGrids();
        Put(grids[8], 8, 0, 0, cls: 0.64f, obj: 1f, bbox: new[] { 0f, 0f, 0f, 0f });   // sqrt(0.64) = 0.8
        Put(grids[8], 8, 0, 1, cls: 0.5f, obj: 0.5f, bbox: new[] { 0f, 0f, 0f, 0f });    // 0.5

        var faces = YuNetDecoder.Decode(Side, 1f, grids, scoreThreshold: 0f).OrderByDescending(f => f.Score).ToList();

        Assert.Equal(0.8f, faces[0].Score, 5);
        Assert.Equal(0.5f, faces[1].Score, 5);
    }

    [Fact]
    public void Class_and_objectness_are_clamped_to_zero_to_one()
    {
        var grids = EmptyGrids();
        Put(grids[8], 8, 0, 0, cls: 3f, obj: 2f, bbox: new[] { 0f, 0f, 0f, 0f });     // clamps to 1 * 1
        Put(grids[8], 8, 0, 1, cls: -1f, obj: 5f, bbox: new[] { 0f, 0f, 0f, 0f });    // clamps to 0 * 1

        var faces = YuNetDecoder.Decode(Side, 1f, grids, scoreThreshold: 0.001f);

        Assert.Equal(1f, Assert.Single(faces).Score, 5);
    }

    [Fact]
    public void Cells_below_the_score_threshold_are_dropped_and_the_threshold_itself_is_kept()
    {
        var grids = EmptyGrids();
        Put(grids[8], 8, 0, 0, cls: 0.81f, obj: 0.81f, bbox: new[] { 0f, 0f, 0f, 0f });   // score 0.81
        Put(grids[8], 8, 0, 1, cls: 0.79f, obj: 0.79f, bbox: new[] { 0f, 0f, 0f, 0f });   // score 0.79
        Put(grids[8], 8, 0, 2, cls: 0.8f, obj: 0.8f, bbox: new[] { 0f, 0f, 0f, 0f });     // exactly the threshold (OpenCV keeps it: it skips only score < threshold)

        var faces = YuNetDecoder.Decode(Side, 1f, grids, scoreThreshold: 0.8f);

        Assert.Equal(2, faces.Count);
        Assert.DoesNotContain(faces, f => f.Score < 0.8f);
    }

    [Theory]
    [InlineData(8, 80)]
    [InlineData(16, 40)]
    [InlineData(32, 20)]
    public void Each_grid_has_side_over_stride_columns(int stride, int columns)
    {
        Assert.Equal(columns, Side / stride);
        var grids = EmptyGrids();
        // The last cell of the first row, and the first cell of the second row, must not be confused.
        Put(grids[stride], stride, row: 1, column: 0, cls: 1, obj: 1, bbox: new[] { 0f, 0f, 0f, 0f });

        var face = Assert.Single(YuNetDecoder.Decode(Side, 1f, grids, 0.5f));

        // Cell (row 1, column 0): centre = (0, stride), size = stride x stride.
        Assert.Equal(-stride / 2f, face.X, 3);
        Assert.Equal(stride - stride / 2f, face.Y, 3);
        Assert.Equal(stride, face.Width, 3);
    }

    [Fact]
    public void Landmarks_are_cell_plus_offset_times_stride_in_the_order_the_model_gives_them()
    {
        var grids = EmptyGrids();
        var kps = new float[10];
        for (var k = 0; k < 5; k++) { kps[2 * k] = k; kps[2 * k + 1] = 10 + k; }   // offsets 0..4 across, 10..14 down
        Put(grids[16], 16, row: 3, column: 5, cls: 1, obj: 1, bbox: new[] { 0f, 0f, 0f, 0f }, kps);

        var face = Assert.Single(YuNetDecoder.Decode(Side, scale: 0.5f, grids, 0.5f));

        Assert.Equal(5, face.Landmarks.Length);
        for (var k = 0; k < 5; k++)
        {
            Assert.Equal((k + 5) * 16 / 0.5f, face.Landmarks[k].X, 3);
            Assert.Equal((10 + k + 3) * 16 / 0.5f, face.Landmarks[k].Y, 3);
        }
    }

    [Fact]
    public void A_missing_grid_is_skipped()
    {
        var grids = EmptyGrids();
        Put(grids[8], 8, 0, 0, 1, 1, new[] { 0f, 0f, 0f, 0f });
        grids.Remove(16);

        Assert.Single(YuNetDecoder.Decode(Side, 1f, grids, 0.5f));
    }

    // ---------- Non-maximum suppression ----------

    private static FaceDetection Box(float x, float y, float w, float h, float score) =>
        new(x, y, w, h, score, new (float, float)[5]);

    [Fact]
    public void IoU_is_one_for_identical_boxes_zero_for_apart_and_a_third_for_half_overlap()
    {
        var a = Box(0, 0, 10, 10, 1);

        Assert.Equal(1f, YuNetDecoder.IoU(a, Box(0, 0, 10, 10, 1)), 5);
        Assert.Equal(0f, YuNetDecoder.IoU(a, Box(20, 20, 10, 10, 1)), 5);
        Assert.Equal(0f, YuNetDecoder.IoU(a, Box(10, 0, 10, 10, 1)), 5);                  // touching edges only
        Assert.Equal(1f / 3f, YuNetDecoder.IoU(a, Box(5, 0, 10, 10, 1)), 5);               // 50 of 150
    }

    [Fact]
    public void Suppression_keeps_the_best_of_overlapping_boxes_and_all_separate_ones()
    {
        var kept = YuNetDecoder.NonMaxSuppression(new[]
        {
            Box(0, 0, 100, 100, 0.85f),
            Box(5, 5, 100, 100, 0.95f),     // overlaps the first strongly: this one wins
            Box(300, 300, 100, 100, 0.82f), // elsewhere: kept
        }, iouThreshold: 0.3f);

        Assert.Equal(2, kept.Count);
        Assert.Equal(0.95f, kept[0].Score);
        Assert.Equal(0.82f, kept[1].Score);
    }

    [Fact]
    public void Boxes_overlapping_less_than_the_threshold_are_both_kept()
    {
        // Overlap of 10 x 100 = 1000 of 19000 (IoU about 0.05) is well under 0.3.
        var kept = YuNetDecoder.NonMaxSuppression(new[] { Box(0, 0, 100, 100, 0.9f), Box(90, 0, 100, 100, 0.8f) }, 0.3f);

        Assert.Equal(2, kept.Count);
    }
}
