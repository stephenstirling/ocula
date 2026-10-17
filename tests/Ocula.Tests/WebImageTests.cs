using System.ComponentModel;
using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

/// <summary>The picture rows in the Web download window: which address they download and what they say about themselves.</summary>
public class WebImageTests
{
    [Fact]
    public void It_downloads_the_thumbnail_until_a_larger_version_is_found()
    {
        var image = new WebImage { ThumbUrl = "https://t.test/th/a.jpg", Width = 250, Height = 375 };

        Assert.Equal("https://t.test/th/a.jpg", image.FullUrl);
        Assert.False(image.HasLargerVersion);
        Assert.Equal(375, image.LongSide);
        Assert.Equal("250×375", image.DimensionText);

        image.FullUrl = "https://t.test/full/a.jpg";

        Assert.Equal("https://t.test/full/a.jpg", image.FullUrl);
        Assert.True(image.HasLargerVersion);
        Assert.Equal(int.MaxValue, image.LongSide); // a linked full-size picture is assumed to be large
        Assert.Equal("250×375 preview, larger found", image.DimensionText);
    }

    [Fact]
    public void A_full_address_equal_to_the_thumbnail_is_not_a_larger_version()
    {
        var image = new WebImage { ThumbUrl = "https://t.test/a.jpg" };
        image.FullUrl = "https://t.test/a.jpg";
        Assert.False(image.HasLargerVersion);
    }

    [Fact]
    public void A_picture_whose_size_is_unknown_says_so_only_once_a_larger_version_is_found()
    {
        var image = new WebImage { ThumbUrl = "https://t.test/a.jpg" };
        Assert.Equal(string.Empty, image.DimensionText);
        image.FullUrl = "https://t.test/big/a.jpg";
        Assert.Equal("larger version found", image.DimensionText);
    }

    [Fact]
    public void A_problem_blocks_the_download_and_tells_the_row()
    {
        var image = new WebImage { ThumbUrl = "https://t.test/a.jpg" };
        var raised = new List<string?>();
        ((INotifyPropertyChanged)image).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.True(image.CanDownload);
        image.Problem = "Couldn't find the full-size version.";

        Assert.False(image.CanDownload);
        Assert.Equal("Couldn't find the full-size version.", image.StatusText); // a problem outranks the status and the size
        Assert.Contains(nameof(WebImage.Problem), raised);
        Assert.Contains(nameof(WebImage.CanDownload), raised);
        Assert.Contains(nameof(WebImage.StatusText), raised);
    }

    [Fact]
    public void The_status_line_prefers_a_problem_then_a_status_then_the_size()
    {
        var image = new WebImage { ThumbUrl = "https://t.test/a.jpg", Width = 640, Height = 480 };
        Assert.Equal("640×480", image.StatusText);
        image.Status = "Saved";
        Assert.Equal("Saved", image.StatusText);
        image.Problem = "No";
        Assert.Equal("No", image.StatusText);
    }

    [Fact]
    public void InPost_is_false_unless_the_grabber_says_the_picture_was_in_a_post()
    {
        Assert.False(new WebImage { ThumbUrl = "https://t.test/a.jpg" }.InPost);
        Assert.True(new WebImage { ThumbUrl = "https://t.test/a.jpg", InPost = true }.InPost);
    }

    [Theory]
    [InlineData("https://t.test/full/Katy_Perry_at_the_awards_01.jpg?x=1", "Katy_Perry_at_the_awards_01")]
    [InlineData("not an address", "")]
    public void FileName_is_the_downloaded_pictures_name_without_its_extension(string url, string expected) =>
        Assert.Equal(expected, new WebImage { ThumbUrl = url }.FileName);

    [Fact]
    public void A_page_snapshot_starts_with_nothing_found()
    {
        var page = new PageSnapshot();
        Assert.Empty(page.Images);
        Assert.Empty(page.PageUrls);
        Assert.Empty(page.Headings);
    }
}
