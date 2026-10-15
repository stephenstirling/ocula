using System;
using System.Collections.Generic;

namespace Ocula.Services;

/// <summary>
/// The arithmetic behind People's groups, kept apart from the database and the window so it can be
/// unit-tested and run on its own to compare thresholds (tests/Ocula.Tests/FaceGroupingTests.cs).
///
/// A group is the average of its faces' embeddings, scaled to length 1 (its "centroid"). A new face
/// joins the group whose centroid it is closest to (cosine similarity, which for unit-length vectors
/// is a dot product) if that is above the threshold; otherwise it starts a new group.
/// </summary>
public static class FaceGrouping
{
    /// <summary>Cosine similarity a face needs to join a group. SFace's own "same person" cut-off is 0.363.</summary>
    public const float DefaultJoinThreshold = 0.45f;

    /// <summary>Cosine similarity of two embeddings (their dot product when both have length 1).</summary>
    public static float Similarity(float[] a, float[] b)
    {
        var sum = 0f;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++) sum += a[i] * b[i];
        return sum;
    }

    /// <summary>
    /// The id of the group a face joins, or 0 when none is close enough. A face must score
    /// above the threshold; ties go to the group seen first.
    /// </summary>
    public static long BestGroup(float[] embedding, IReadOnlyDictionary<long, (float[] Centroid, int Count)> groups, float threshold)
    {
        long bestId = 0;
        var best = threshold;
        foreach (var (id, group) in groups)
        {
            var score = Similarity(embedding, group.Centroid);
            if (score > best)
            {
                best = score;
                bestId = id;
            }
        }
        return bestId;
    }

    /// <summary>The group's new centroid once a face joins: the old average, weighted by how many faces made it, plus the new face, scaled to length 1.</summary>
    public static float[] AddFace(float[] centroid, int count, float[] embedding)
    {
        var merged = new float[embedding.Length];
        for (var i = 0; i < merged.Length; i++) merged[i] = centroid[i] * count + embedding[i];
        var norm = 0f;
        foreach (var v in merged) norm += v * v;
        norm = MathF.Sqrt(norm);
        if (norm > 0) for (var i = 0; i < merged.Length; i++) merged[i] /= norm;
        return merged;
    }

    /// <summary>
    /// Groups a list of embeddings in order, the way People does as it scans photos. Returns each
    /// embedding's group number (0, 1, 2 and so on, in order of first appearance).
    /// </summary>
    public static int[] Cluster(IReadOnlyList<float[]> embeddings, float threshold)
    {
        var groups = new Dictionary<long, (float[] Centroid, int Count)>();
        var labels = new int[embeddings.Count];
        for (var i = 0; i < embeddings.Count; i++)
        {
            var id = BestGroup(embeddings[i], groups, threshold);
            if (id == 0)
            {
                id = groups.Count + 1;
                groups[id] = (embeddings[i], 0);
            }
            var (centroid, count) = groups[id];
            groups[id] = (AddFace(centroid, count, embeddings[i]), count + 1);
            labels[i] = (int)id - 1;
        }
        return labels;
    }
}
