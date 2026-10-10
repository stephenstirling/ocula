using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

/// <summary>
/// The rules that decide which photos a smart search shows and which tags are suggested.
/// Uses made-up vectors; tools/SmartEval measures the same code on real photos.
/// </summary>
public class SmartScoringTests
{
    [Theory]
    [InlineData("dog", "a photo of dog")]
    [InlineData("  sunset over water ", "a photo of sunset over water")]
    [InlineData("a photo of a dog", "a photo of a dog")]
    [InlineData("A picture of snow", "A picture of snow")]
    [InlineData("photos of food", "photos of food")]
    [InlineData("an image of a cat", "an image of a cat")]
    [InlineData("photographer at work", "a photo of photographer at work")]
    public void Search_prompt_describes_a_photo_once(string text, string expected) =>
        Assert.Equal(expected, SmartScoring.SearchPrompt(text));

    [Fact]
    public void Average_is_the_element_wise_mean()
    {
        var mean = SmartScoring.Average(new[] { new[] { 1f, 0f }, new[] { 0f, 1f }, new[] { 2f, 2f } });
        Assert.Equal(1f, mean[0], 5);
        Assert.Equal(1f, mean[1], 5);
    }

    // Search vectors: axis 0 is "the query", axis 1 is "any photo".
    private static readonly float[] Query = { 1f, 0f };
    private static readonly float[] Neutral = { 0f, 1f };

    [Fact]
    public void Search_keeps_photos_clearly_closer_to_the_query_than_to_neutral_best_first()
    {
        var library = new List<(string, float[])>
        {
            ("weak", new[] { 0.24f, 0.22f }),     // margin 0.02: high raw score, but no closer than any photo
            ("best", new[] { 0.28f, 0.20f }),     // margin 0.08
            ("good", new[] { 0.25f, 0.21f }),     // margin 0.04
            ("unrelated", new[] { 0.15f, 0.22f }),
        };

        var matches = SmartScoring.Search(library, Query, Neutral);

        Assert.Equal(new[] { "best", "good" }, matches.Select(m => m.Path));
        Assert.Equal(0.08f, matches[0].Score, 5);
    }

    [Fact]
    public void Search_returns_nothing_when_no_photo_matches()
    {
        // The old rule kept everything near the best score, so a query with no match showed most of the library.
        var library = Enumerable.Range(0, 100).Select(i => ($"photo{i}", new[] { 0.20f + i * 0.0001f, 0.21f })).ToList();
        Assert.Empty(SmartScoring.Search(library, Query, Neutral));
    }

    [Fact]
    public void Search_respects_the_limit()
    {
        var library = Enumerable.Range(0, 50).Select(i => ($"photo{i}", new[] { 0.30f + i * 0.001f, 0.20f })).ToList();
        var matches = SmartScoring.Search(library, Query, Neutral, limit: 10);
        Assert.Equal(10, matches.Count);
        Assert.Equal("photo49", matches[0].Path);
    }

    // Tag vectors: one axis per label, so a photo's similarity to label i is image[i].

    private static (string[] Labels, float[][] Vectors) Axes(params string[] labels) =>
        (labels, labels.Select((_, i) => labels.Select((_, j) => i == j ? 1f : 0f).ToArray()).ToArray());

    /// <summary>A photo whose similarities to the named labels are given; every other label gets the base similarity.</summary>
    private static float[] Photo(string[] labels, float baseSimilarity, params (string Label, float Similarity)[] scores)
    {
        var given = scores.ToDictionary(s => s.Label, s => s.Similarity);
        return labels.Select(l => given.GetValueOrDefault(l, baseSimilarity)).ToArray();
    }

    /// <summary>40 filler labels plus the named ones, so the softmax has competition like the real list.</summary>
    private static (string[] Labels, float[][] Vectors) LabelSet(params string[] named) =>
        Axes(named.Concat(Enumerable.Range(0, 40).Select(i => $"filler{i}")).ToArray());

    [Fact]
    public void Grouped_labels_pool_their_confidence_under_the_best_one()
    {
        var (labels, vectors) = LabelSet("portrait", "posed portrait", "dog");
        // On its own each portrait label is under the bar; together they're a clear portrait.
        var image = Photo(labels, 0.20f, ("portrait", 0.215f), ("posed portrait", 0.214f));

        var alone = SmartScoring.SuggestTags(image, labels, vectors, Array.Empty<string[]>());
        var grouped = SmartScoring.SuggestTags(image, labels, vectors, new[] { new[] { "portrait", "posed portrait" } });

        Assert.All(alone, s => Assert.True(s.Confidence < SmartScoring.OtherLabelConfidence));
        Assert.DoesNotContain(alone, s => s.Label == "posed portrait");
        Assert.Equal("portrait", grouped[0].Label);
        Assert.DoesNotContain(grouped, s => s.Label == "posed portrait");
        Assert.Equal(Softmax(image)[0] + Softmax(image)[1], grouped[0].Confidence, 4);
    }

    private static float[] Softmax(float[] similarities)
    {
        var exp = similarities.Select(s => MathF.Exp(SmartScoring.LogitScale * (s - similarities.Max()))).ToArray();
        return exp.Select(e => e / exp.Sum()).ToArray();
    }

    [Fact]
    public void The_best_label_is_suggested_below_the_bar_for_others()
    {
        var (labels, vectors) = LabelSet("dog", "grass");
        // Confidence for "dog" about 7%: enough for the best label, not for a second one.
        var image = Photo(labels, 0.20f, ("dog", 0.2125f), ("grass", 0.2120f));

        var tags = SmartScoring.SuggestTags(image, labels, vectors, Array.Empty<string[]>());

        var dog = Assert.Single(tags);
        Assert.Equal("dog", dog.Label);
        Assert.InRange(dog.Confidence, SmartScoring.BestLabelConfidence, SmartScoring.OtherLabelConfidence);
    }

    [Fact]
    public void Nothing_is_suggested_when_no_label_stands_out()
    {
        var (labels, vectors) = LabelSet("dog", "cat");
        var image = Photo(labels, 0.20f); // every label equally likely: 1 in 42
        Assert.Empty(SmartScoring.SuggestTags(image, labels, vectors, Array.Empty<string[]>()));
    }

    [Fact]
    public void Confident_labels_are_listed_most_confident_first_up_to_the_maximum()
    {
        var (labels, vectors) = LabelSet("beach", "ocean", "sunset", "surfing");
        var image = Photo(labels, 0.15f, ("beach", 0.27f), ("ocean", 0.265f), ("sunset", 0.26f), ("surfing", 0.255f));

        var tags = SmartScoring.SuggestTags(image, labels, vectors, Array.Empty<string[]>(), max: 3);

        Assert.Equal(new[] { "beach", "ocean", "sunset" }, tags.Select(t => t.Label));
    }

    [Fact]
    public void Label_groups_only_use_known_labels_once_each()
    {
        var known = new HashSet<string>(SmartLabels.All);
        var grouped = SmartLabels.Groups.SelectMany(g => g).ToList();
        Assert.All(grouped, l => Assert.Contains(l, known));
        Assert.Equal(grouped.Count, grouped.Distinct().Count());
        Assert.All(SmartLabels.Groups, g => Assert.True(g.Length >= 2));
    }

    [Fact]
    public void Labels_are_unique()
    {
        var duplicates = SmartLabels.All.GroupBy(l => l).Where(g => g.Count() > 1).Select(g => g.Key);
        Assert.Empty(duplicates);
    }
}
