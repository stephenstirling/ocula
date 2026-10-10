using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

public class ClipTokenizerTests
{
    private static readonly string Data = Path.Combine(AppContext.BaseDirectory, "TestData", "clip");
    private static readonly ClipTokenizer Tokenizer = new(Path.Combine(Data, "vocab.json"), Path.Combine(Data, "merges.txt"));

    private const long Start = 49406;
    private const long End = 49407;

    /// <summary>
    /// Reference ids from the model's own tokenizer files (Xenova/clip-vit-base-patch32, revision
    /// d15189d7028b43f1d3e65039190477f6af591c2a):
    ///   - 49406 &lt;|startoftext|&gt; and 49407 &lt;|endoftext|&gt;: tokenizer.json, "post_processor"
    ///     (RobertaProcessing cls/sep) and "added_tokens".
    ///   - each word is a whole-word token in vocab.json (and tokenizer.json "model.vocab", which is
    ///     identical): "a&lt;/w&gt;" 320, "photo&lt;/w&gt;" 1125, "of&lt;/w&gt;" 539, "cat&lt;/w&gt;" 2368.
    /// </summary>
    private static readonly long[] PhotoOfACat = { Start, 320, 1125, 539, 320, 2368, End };

    [Fact]
    public void A_photo_of_a_cat_matches_the_reference_ids()
    {
        var ids = Tokenizer.Encode("a photo of a cat");

        Assert.Equal(ClipTokenizer.Length, ids.Length);
        Assert.Equal(PhotoOfACat, ids.Take(PhotoOfACat.Length));
    }

    [Fact]
    public void Pads_with_end_of_text_like_the_reference_tokenizer()
    {
        // tokenizer_config.json and special_tokens_map.json: pad_token is <|endoftext|>.
        var ids = Tokenizer.Encode("a photo of a cat");

        Assert.All(ids.Skip(PhotoOfACat.Length), id => Assert.Equal(End, id));
        Assert.Equal(PhotoOfACat.Length, ClipTokenizer.UsedLength(ids));
    }

    [Fact]
    public void Lowercases_and_collapses_whitespace_like_the_reference_normalizer()
    {
        // tokenizer.json "normalizer": NFC, then \s+ -> " ", then Lowercase.
        Assert.Equal(Tokenizer.Encode("a photo of a cat"), Tokenizer.Encode("  A \t Photo\nof  A CAT "));
    }

    [Fact]
    public void Applies_NFC_so_decomposed_accents_match_precomposed_ones()
    {
        var precomposed = "café";   // é as one code point
        var decomposed = "café";   // e + combining acute accent
        Assert.Equal(Tokenizer.Encode(precomposed), Tokenizer.Encode(decomposed));
    }

    [Fact]
    public void Long_text_is_cut_to_77_tokens_ending_with_end_of_text()
    {
        var ids = Tokenizer.Encode(string.Join(' ', Enumerable.Repeat("cat", 200)));

        Assert.Equal(77, ids.Length);
        Assert.Equal(Start, ids[0]);
        Assert.Equal(End, ids[76]);
        Assert.All(ids.Skip(1).Take(75), id => Assert.Equal(2368, id));
    }
}
