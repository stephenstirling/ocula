using System;
using System.Linq;

namespace Ocula.Services;

/// <summary>
/// Where a face's five landmarks go in SFace's standard 112 x 112 crop, and the arithmetic that moves
/// a detected face there (a rotation, one uniform scale and a shift; no skew). Pure, so unit-tested.
/// The template is OpenCV's FaceRecognizerSF (face_recognize.cpp).
/// </summary>
public static class FaceAlignment
{
    public const int Size = 112;

    /// <summary>Right eye, left eye, nose tip, right mouth corner, left mouth corner, as seen in the photo.</summary>
    public static readonly (float X, float Y)[] Reference =
    {
        (38.2946f, 51.6963f), (73.5318f, 51.5014f), (56.0252f, 71.7366f), (41.5493f, 92.3655f), (70.7299f, 92.2041f)
    };

    /// <summary>
    /// The map from photo coordinates to crop coordinates: x' = A x - B y + Tx, y' = B x + A y + Ty.
    /// A and B together hold the rotation and scale (scale = sqrt(A*A + B*B)).
    /// </summary>
    public readonly record struct Transform(float A, float B, float Tx, float Ty)
    {
        public float Scale => MathF.Sqrt(A * A + B * B);

        /// <summary>Photo to crop.</summary>
        public (float X, float Y) Apply(float x, float y) => (A * x - B * y + Tx, B * x + A * y + Ty);

        /// <summary>Crop to photo: where a crop pixel came from, for sampling.</summary>
        public (float X, float Y) Invert(float u, float v)
        {
            var norm = Math.Max(A * A + B * B, 1e-9f);
            float dx = u - Tx, dy = v - Ty;
            return ((A * dx + B * dy) / norm, (-B * dx + A * dy) / norm);
        }
    }

    /// <summary>The least-squares similarity transform taking the five detected landmarks onto <see cref="Reference"/>.</summary>
    public static Transform Solve((float X, float Y)[] landmarks)
    {
        if (landmarks.Length != Reference.Length) throw new ArgumentException("Expected five landmarks.", nameof(landmarks));

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
        return new Transform(a, b, rx - (a * mx - b * my), ry - (b * mx + a * my));
    }
}
