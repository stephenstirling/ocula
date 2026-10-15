using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

/// <summary>
/// The similarity transform that moves a detected face's five landmarks onto SFace's standard 112 x 112
/// template (the one in OpenCV's FaceRecognizerSF).
/// </summary>
public class FaceAlignmentTests
{
    private static readonly (float X, float Y)[] Template = FaceAlignment.Reference;

    /// <summary>The template as it would appear after rotating, scaling and shifting.</summary>
    private static (float X, float Y)[] Moved(double degrees, float scale, float dx, float dy)
    {
        var r = degrees * Math.PI / 180;
        var (cos, sin) = ((float)Math.Cos(r), (float)Math.Sin(r));
        return Template.Select(p => (scale * (cos * p.X - sin * p.Y) + dx, scale * (sin * p.X + cos * p.Y) + dy)).ToArray();
    }

    [Fact]
    public void The_template_is_OpenCVs_five_points()
    {
        Assert.Equal(112, FaceAlignment.Size);
        Assert.Equal(new[]
        {
            (38.2946f, 51.6963f), (73.5318f, 51.5014f), (56.0252f, 71.7366f), (41.5493f, 92.3655f), (70.7299f, 92.2041f)
        }, Template);
    }

    [Fact]
    public void A_face_already_in_the_template_pose_needs_no_change()
    {
        var t = FaceAlignment.Solve(Template);

        Assert.Equal(1f, t.A, 4);
        Assert.Equal(0f, t.B, 4);
        Assert.Equal(0f, t.Tx, 3);
        Assert.Equal(0f, t.Ty, 3);
        Assert.Equal(1f, t.Scale, 4);
    }

    [Theory]
    [InlineData(0, 1f, 0f, 0f)]
    [InlineData(0, 3.5f, 400f, 250f)]       // a bigger face further along the picture
    [InlineData(30, 1f, 0f, 0f)]            // tilted head
    [InlineData(-45, 2f, 100f, -20f)]
    [InlineData(180, 0.5f, 60f, 60f)]       // upside down, small
    public void A_moved_template_maps_back_onto_the_template(double degrees, float scale, float dx, float dy)
    {
        var detected = Moved(degrees, scale, dx, dy);

        var t = FaceAlignment.Solve(detected);

        Assert.Equal(1f / scale, t.Scale, 3);
        for (var i = 0; i < 5; i++)
        {
            var (x, y) = t.Apply(detected[i].X, detected[i].Y);
            Assert.Equal(Template[i].X, x, 0.05);
            Assert.Equal(Template[i].Y, y, 0.05);
        }
    }

    [Fact]
    public void Invert_undoes_Apply()
    {
        var t = FaceAlignment.Solve(Moved(20, 1.8f, 300, 120));

        var (u, v) = t.Apply(412.5f, 190.25f);
        var (x, y) = t.Invert(u, v);

        Assert.Equal(412.5f, x, 0.01);
        Assert.Equal(190.25f, y, 0.01);
    }

    [Fact]
    public void Noisy_landmarks_still_give_a_close_fit_without_stretching()
    {
        // A pixel of jitter on each landmark of a 2x face: the fit should stay near scale 0.5 and put the eyes level.
        var detected = Moved(10, 2f, 50, 80).Select((p, i) => (X: p.X + (i % 2 == 0 ? 1 : -1), Y: p.Y + (i % 3 == 0 ? 1f : -1f))).ToArray();

        var t = FaceAlignment.Solve(detected);
        var rightEye = t.Apply(detected[0].X, detected[0].Y);
        var leftEye = t.Apply(detected[1].X, detected[1].Y);

        Assert.InRange(t.Scale, 0.47f, 0.53f);
        Assert.InRange(Math.Abs(rightEye.Y - leftEye.Y), 0, 1.5);
    }

    [Fact]
    public void Five_landmarks_are_required()
    {
        Assert.Throws<ArgumentException>(() => FaceAlignment.Solve(new (float, float)[4]));
    }

    [Fact]
    public void All_landmarks_in_one_place_does_not_divide_by_zero()
    {
        var t = FaceAlignment.Solve(Enumerable.Repeat((10f, 10f), 5).ToArray());

        Assert.False(float.IsNaN(t.A) || float.IsNaN(t.B) || float.IsNaN(t.Tx) || float.IsNaN(t.Ty));
    }
}
