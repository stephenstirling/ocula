using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

/// <summary>The maths of People's groups: similarity, joining a group, the group's average, and clustering in order.</summary>
public class FaceGroupingTests
{
    private static float[] Unit(params float[] v)
    {
        var length = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / length).ToArray();
    }

    /// <summary>A unit vector with the given cosine similarity to the first axis, the rest on the second axis.</summary>
    private static float[] AtCosine(float cosine, int dimensions = 4) =>
        Enumerable.Range(0, dimensions).Select(i => i == 0 ? cosine : i == 1 ? MathF.Sqrt(1 - cosine * cosine) : 0f).ToArray();

    [Fact]
    public void The_default_threshold_is_0_45_and_stricter_than_SFaces_own_0_363()
    {
        Assert.Equal(0.45f, FaceGrouping.DefaultJoinThreshold);
        Assert.True(FaceGrouping.DefaultJoinThreshold > 0.363f);
    }

    [Fact]
    public void Similarity_is_one_for_the_same_face_zero_for_unrelated_and_minus_one_for_opposite()
    {
        var a = Unit(1, 2, 3, 4);

        Assert.Equal(1f, FaceGrouping.Similarity(a, a), 5);
        Assert.Equal(0f, FaceGrouping.Similarity(Unit(1, 0, 0, 0), Unit(0, 1, 0, 0)), 5);
        Assert.Equal(-1f, FaceGrouping.Similarity(a, a.Select(x => -x).ToArray()), 5);
        Assert.Equal(0.6f, FaceGrouping.Similarity(AtCosine(1), AtCosine(0.6f)), 5);
    }

    [Fact]
    public void A_face_joins_the_closest_group_above_the_threshold()
    {
        var groups = new Dictionary<long, (float[] Centroid, int Count)>
        {
            [1] = (Unit(0, 1, 0, 0), 5),
            [2] = (AtCosine(0.9f), 3),
            [3] = (AtCosine(0.6f), 8),
        };

        Assert.Equal(2, FaceGrouping.BestGroup(AtCosine(1), groups, 0.45f));
    }

    [Fact]
    public void A_face_needs_to_score_strictly_above_the_threshold()
    {
        var groups = new Dictionary<long, (float[] Centroid, int Count)> { [7] = (AtCosine(0.5f), 1) };

        Assert.Equal(0, FaceGrouping.BestGroup(AtCosine(1), groups, 0.5f));     // exactly at it: starts a new group
        Assert.Equal(7, FaceGrouping.BestGroup(AtCosine(1), groups, 0.49f));
    }

    [Fact]
    public void With_no_close_group_or_no_groups_the_answer_is_zero()
    {
        Assert.Equal(0, FaceGrouping.BestGroup(Unit(1, 0, 0, 0), new Dictionary<long, (float[], int)>(), 0.45f));

        var far = new Dictionary<long, (float[] Centroid, int Count)> { [1] = (Unit(0, 1, 0, 0), 4) };
        Assert.Equal(0, FaceGrouping.BestGroup(Unit(1, 0, 0, 0), far, 0.45f));
    }

    [Fact]
    public void A_group_that_ties_goes_to_the_first_one_seen()
    {
        var shared = AtCosine(0.8f);
        var groups = new Dictionary<long, (float[] Centroid, int Count)> { [4] = (shared, 1), [9] = (shared.ToArray(), 1) };

        Assert.Equal(4, FaceGrouping.BestGroup(AtCosine(1), groups, 0.45f));
    }

    [Fact]
    public void Adding_a_face_averages_by_count_and_keeps_length_one()
    {
        var centroid = Unit(1, 0, 0, 0);
        var face = Unit(0, 1, 0, 0);

        // One face already in the group, one joining: the midpoint, scaled back to length 1.
        var two = FaceGrouping.AddFace(centroid, 1, face);
        Assert.Equal(MathF.Sqrt(0.5f), two[0], 5);
        Assert.Equal(MathF.Sqrt(0.5f), two[1], 5);

        // Three faces already agree on the first axis: one outlier moves the average only a little.
        var four = FaceGrouping.AddFace(centroid, 3, face);
        Assert.Equal(3 / MathF.Sqrt(10), four[0], 5);
        Assert.Equal(1 / MathF.Sqrt(10), four[1], 5);
        Assert.Equal(1f, MathF.Sqrt(four.Sum(x => x * x)), 5);
    }

    [Fact]
    public void Adding_a_face_to_an_empty_group_gives_that_face()
    {
        var face = Unit(1, 2, 3, 4);

        var result = FaceGrouping.AddFace(new float[4], 0, face);

        for (var i = 0; i < face.Length; i++) Assert.Equal(face[i], result[i], 5);
    }

    // ---------- Clustering in order, like the scanner ----------

    /// <summary>Faces of three "people" on three axes, with a deterministic wobble.</summary>
    private static (float[][] Faces, int[] Truth) ThreePeople(int perPerson, float wobble)
    {
        var rng = new Random(11);
        var faces = new List<float[]>();
        var truth = new List<int>();
        for (var i = 0; i < perPerson; i++)
            for (var person = 0; person < 3; person++)
            {
                var v = new float[6];
                v[person] = 1;
                for (var d = 0; d < v.Length; d++) v[d] += (float)(rng.NextDouble() - 0.5) * wobble;
                faces.Add(Unit(v));
                truth.Add(person);
            }
        return (faces.ToArray(), truth.ToArray());
    }

    [Fact]
    public void Three_distinct_people_give_three_groups_whatever_the_order()
    {
        var (faces, truth) = ThreePeople(perPerson: 12, wobble: 0.3f);

        foreach (var seed in new[] { 1, 2, 3 })
        {
            // A different, fixed shuffle for each seed.
            var order = Enumerable.Range(0, faces.Length).OrderBy(i => (i * 7919 + seed * 104729) % 1009).ToArray();
            var labels = FaceGrouping.Cluster(order.Select(i => faces[i]).ToList(), FaceGrouping.DefaultJoinThreshold);

            Assert.Equal(3, labels.Distinct().Count());
            // Every group holds exactly one real person.
            foreach (var group in labels.Select((l, k) => (l, person: truth[order[k]])).GroupBy(x => x.l))
                Assert.Single(group.Select(x => x.person).Distinct());
        }
    }

    [Fact]
    public void Group_numbers_follow_first_appearance()
    {
        var a = Unit(1, 0, 0, 0);
        var b = Unit(0, 1, 0, 0);

        var labels = FaceGrouping.Cluster(new[] { a, b, a, b, a }, 0.45f);

        Assert.Equal(new[] { 0, 1, 0, 1, 0 }, labels);
    }

    [Fact]
    public void A_higher_threshold_never_gives_fewer_groups()
    {
        var (faces, _) = ThreePeople(perPerson: 10, wobble: 1.2f);   // noisy: some faces are hard to place

        var counts = new[] { 0.2f, 0.35f, 0.45f, 0.6f, 0.8f }
            .Select(t => FaceGrouping.Cluster(faces, t).Distinct().Count()).ToArray();

        Assert.Equal(counts.OrderBy(c => c), counts);
        Assert.True(counts[^1] > counts[0], "the strictest threshold should split more than the loosest");
    }

    [Fact]
    public void Two_similar_people_merge_below_their_similarity_and_split_above_it()
    {
        // Two people whose faces look alike (cosine about 0.55 between them, with a little wobble inside each).
        var rng = new Random(5);
        float[] Person(float[] centre) => Unit(centre.Select(x => x + (float)(rng.NextDouble() - 0.5) * 0.05f).ToArray());
        var a = AtCosine(1, 3);
        var b = AtCosine(0.55f, 3);
        var faces = Enumerable.Range(0, 8).SelectMany(_ => new[] { Person(a), Person(b) }).ToList();

        Assert.Equal(1, FaceGrouping.Cluster(faces, 0.45f).Distinct().Count());   // 0.45 is below 0.55: they merge
        Assert.Equal(2, FaceGrouping.Cluster(faces, 0.65f).Distinct().Count());   // above it: they stay apart
    }

    [Fact]
    public void An_empty_list_gives_no_labels()
    {
        Assert.Empty(FaceGrouping.Cluster(Array.Empty<float[]>(), 0.45f));
    }
}
