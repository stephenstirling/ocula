using System;
using System.Collections.Generic;
using System.Linq;

namespace Ocula.Services;

public sealed record SmartSuggestion(string Label, float Confidence);

/// <summary>A search hit. Score is how much closer the photo is to the query than to a neutral "a photo".</summary>
public sealed record SmartMatch(string Path, float Score);

/// <summary>
/// The decisions made on top of CLIP's similarities: which photos a search shows, and which labels
/// are suggested as tags. Knows nothing about settings, models or the library, so the rules can be
/// unit-tested and measured on labelled photos (tools/SmartEval) exactly as the app runs them.
/// </summary>
public static class SmartScoring
{
    // ================= Search =================

    /// <summary>
    /// Prompts for "any photo at all". Some photos are close to every sentence (CLIP finds them
    /// generally photo-like), so a raw similarity says little; how much closer a photo is to the
    /// query than to these prompts says whether the query is really in it.
    /// </summary>
    public static readonly IReadOnlyList<string> NeutralPrompts = new[] { "a photo", "a photo of something", "a picture", "an image" };

    /// <summary>
    /// How much closer than neutral a photo must be to count as a match. Measured on 178 labelled
    /// photos with both model tiers (docs/SMART-EVALUATION.md): nine in ten right photos scored
    /// 0.028 or more (median 0.044), and 99% of wrong ones less than 0.016.
    /// </summary>
    public const float SearchMargin = 0.025f;

    private static readonly string[] DescribedAlready =
        { "a photo", "photo of", "photos of", "a picture", "picture of", "pictures of", "an image", "image of", "images of" };

    /// <summary>The sentence sent to CLIP for a search: "dog" becomes "a photo of dog", which CLIP matches far more reliably.</summary>
    public static string SearchPrompt(string text)
    {
        var trimmed = text.Trim();
        return DescribedAlready.Any(p => trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ? trimmed : $"a photo of {trimmed}";
    }

    /// <summary>The element-wise mean. A photo's similarity to the mean is the mean of its similarities.</summary>
    public static float[] Average(IReadOnlyList<float[]> vectors)
    {
        var mean = new float[vectors[0].Length];
        foreach (var v in vectors)
            for (var i = 0; i < mean.Length; i++) mean[i] += v[i] / vectors.Count;
        return mean;
    }

    /// <summary>
    /// The photos that match a query, best first: those at least <see cref="SearchMargin"/> closer to
    /// the query than to <paramref name="neutral"/> (the average of the neutral prompts' embeddings).
    /// A query that matches nothing returns nothing, however large the library.
    /// </summary>
    public static List<SmartMatch> Search(IEnumerable<(string Path, float[] Vector)> library, float[] query, float[] neutral,
                                          int limit = 3000, float margin = SearchMargin)
    {
        // query . v - neutral . v  ==  (query - neutral) . v, so one dot product per photo.
        var direction = new float[query.Length];
        for (var i = 0; i < direction.Length; i++) direction[i] = query[i] - neutral[i];

        return library.Select(item => new SmartMatch(item.Path, Dot(direction, item.Vector)))
                      .Where(m => m.Score >= margin)
                      .OrderByDescending(m => m.Score)
                      .Take(limit)
                      .ToList();
    }

    // ================= Tag suggestions =================

    /// <summary>CLIP's learned temperature: turns cosine similarity into a sharper probability.</summary>
    public const float LogitScale = 100f;

    /// <summary>The best label is suggested from this confidence, so most photos get at least one tag.</summary>
    public const float BestLabelConfidence = 0.05f;

    /// <summary>Further labels need this much: below it they're too often wrong.</summary>
    public const float OtherLabelConfidence = 0.10f;

    /// <summary>
    /// Best-matching labels for a photo, most confident first. Confidence is a softmax over all
    /// labels (how much better a label fits than the rest). Labels in the same group share their
    /// confidence and appear once, under the group's best label, so "portrait" and "posed
    /// portrait" don't split a clear portrait into two weak guesses.
    /// </summary>
    public static IReadOnlyList<SmartSuggestion> SuggestTags(float[] image, IReadOnlyList<string> labels, IReadOnlyList<float[]> labelVectors,
                                                             IReadOnlyList<string[]> groups, int max = 6)
    {
        if (labels.Count == 0 || labelVectors.Count != labels.Count) return Array.Empty<SmartSuggestion>();

        var logits = labelVectors.Select(v => LogitScale * Dot(image, v)).ToArray();
        var top = logits.Max();
        var exp = logits.Select(s => MathF.Exp(s - top)).ToArray();
        var sum = exp.Sum();

        var confidence = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < labels.Count; i++) confidence[labels[i]] = exp[i] / sum;

        var candidates = new List<SmartSuggestion>();
        var grouped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var members = group.Where(confidence.ContainsKey).ToList();
            if (members.Count == 0) continue;
            grouped.UnionWith(members);
            candidates.Add(new SmartSuggestion(members.MaxBy(m => confidence[m])!, members.Sum(m => confidence[m])));
        }
        candidates.AddRange(confidence.Where(kv => !grouped.Contains(kv.Key)).Select(kv => new SmartSuggestion(kv.Key, kv.Value)));

        var ranked = candidates.OrderByDescending(s => s.Confidence).ToList();
        if (ranked[0].Confidence < BestLabelConfidence) return Array.Empty<SmartSuggestion>();
        return ranked.Take(1)
                     .Concat(ranked.Skip(1).TakeWhile(s => s.Confidence >= OtherLabelConfidence))
                     .Take(max)
                     .ToList();
    }

    public static float Dot(float[] a, float[] b)
    {
        var sum = 0f;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++) sum += a[i] * b[i];
        return sum;
    }
}
