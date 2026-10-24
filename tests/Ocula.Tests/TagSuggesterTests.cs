using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

/// <summary>Tag suggestions worked out from a thread's title and its pictures' names (the forum-aware changes in the Forum Scraper commit).</summary>
public class TagSuggesterTests
{
    private static PageSnapshot Thread(string title, params string[] fileNames) => new()
    {
        Url = "https://forum.example.com/threads/x.1/",
        Title = title,
        Images = fileNames.Select(n => new WebImage { ThumbUrl = "https://img.example.net/" + n }).ToList(),
    };

    private static List<string> Labels(PageSnapshot page) => TagSuggester.Suggest(page).Select(t => t.Label).ToList();

    [Fact]
    public void A_celebrity_thread_title_gives_the_name_as_a_ticked_tag_without_the_event_words()
    {
        var page = Thread(
            "APPEARANCE - Katy Perry - Attends 4th Annual Rare Impact Fund Benefit in Los Angeles, California, Oct 14, 2026",
            "Katy_Perry_at_the_4th_annual_Rare_Impact_Fund_Benefit_01.jpg", "Katy_Perry_at_the_4th_annual_Rare_Impact_Fund_Benefit_02.jpg",
            "Katy_Perry_at_the_4th_annual_Rare_Impact_Fund_Benefit_03.jpg");

        var tags = TagSuggester.Suggest(page);

        var katy = Assert.Single(tags, t => t.Label == "Katy Perry");
        Assert.True(katy.On);
        Assert.DoesNotContain(tags, t => t.Label.Contains("Appearance") || t.Label.Contains("Attends") || t.Label.Contains("Los Angeles") || t.Label.Contains("California"));
    }

    [Fact]
    public void A_thread_prefix_followed_by_a_name_is_stripped()
    {
        Assert.Contains("Priyanka Chopra", Labels(Thread("Photos: Priyanka Chopra at the 3rd Annual InStyle Imagemaker Awards",
            "Priyanka_Chopra_01.jpg", "Priyanka_Chopra_02.jpg")));
        Assert.Contains("Tyla", Labels(Thread("DISCUSSION | Tyla performs during the A Pop World Tour at Brixton Academy", "Tyla_01.jpg", "Tyla_02.jpg")));
    }

    [Fact]
    public void A_hyphenated_name_stays_whole()
    {
        Assert.Contains("Mary-Kate Olsen", Labels(Thread("Mary-Kate Olsen at a gala", "Mary-Kate_Olsen_1.jpg", "Mary-Kate_Olsen_2.jpg")));
    }

    [Fact]
    public void A_spaced_dash_separates_two_names()
    {
        var labels = Labels(Thread("Anna Berg - Carl Dahl at the premiere", "a.jpg", "b.jpg"));
        Assert.Contains("Anna Berg", labels);
        Assert.Contains("Carl Dahl", labels);
        Assert.DoesNotContain("Anna Berg Carl Dahl", labels);
    }

    [Fact]
    public void A_body_focused_word_in_the_title_is_never_suggested_but_the_name_next_to_it_is()
    {
        var labels = Labels(Thread("Pregnant Busy Philipps Shares Baby's Sonogram Picture!", "busy-philipps-ultrasound-pic-01.jpg", "busy-philipps-ultrasound-pic-02.jpg"));

        Assert.DoesNotContain(labels, l => l.Contains("Pregnan", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(labels, l => l.Contains("Busy Philipps"));
    }

    [Fact]
    public void A_body_focused_topic_word_is_not_suggested_but_an_ordinary_one_is()
    {
        var labels = Labels(Thread("Day out", "beach_bikini_01.jpg", "beach_bikini_02.jpg", "beach_bikini_03.jpg", "beach_bikini_04.jpg"));

        Assert.Contains("beach", labels);
        Assert.DoesNotContain(labels, l => l.Contains("bikini", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_body_focused_word_joined_by_a_hyphen_is_not_suggested()
    {
        var labels = Labels(Thread("Day out", "beach_bikini-clad_01.jpg", "beach_bikini-clad_02.jpg", "beach_bikini-clad_03.jpg"));

        Assert.DoesNotContain(labels, l => l.Contains("bikini", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Pregnant", true)]
    [InlineData("PREGNANT", true)]
    [InlineData("Pregnant Celebrities", true)]
    [InlineData("baby bump", true)]
    [InlineData("baby_bump_01.jpg", true)]
    [InlineData("Bikini-clad", true)]
    [InlineData("Lingerie", true)]
    [InlineData("Busy Philipps", false)]
    [InlineData("Brazil", false)]       // starts with "bra" but isn't the word
    [InlineData("Bumpkin", false)]
    [InlineData("Sunset", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Body_focused_words_are_matched_as_whole_words(string? text, bool expected)
    {
        Assert.Equal(expected, BodyFocusedWords.IsBodyFocused(text));
    }

    [Fact]
    public void Nothing_is_suggested_from_a_page_with_nothing_to_go_on()
    {
        Assert.Empty(TagSuggester.Suggest(new PageSnapshot { Url = "https://x.test/", Title = "page home" }));
    }
}
