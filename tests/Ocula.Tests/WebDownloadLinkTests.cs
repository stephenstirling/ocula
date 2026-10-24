using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

/// <summary>
/// ocula:webdownload: the link the owner's Chrome extension sends (docs/CHROME-EXTENSION.md). Any web page can launch an
/// ocula: link, so the page address, tags, picture filter and save folder are all checked.
/// </summary>
public class WebDownloadLinkTests
{
    private const string Page = "https%3A%2F%2Fwww.justjared.com%2F2013%2F06%2F07%2Fx%2F";

    [Fact]
    public void A_web_download_link_carries_the_page_folder_tags_and_picture_filter()
    {
        var link = OculaLink.Parse(
            "ocula:webdownload?url=" + Page +
            "&folder=Z%3A%5CBabes%5CBusy%20Philipps&tags=Busy%20Philipps%3Bcandid&media=jj-justjared-media");

        Assert.NotNull(link);
        Assert.Equal("webdownload", link!.Verb);
        Assert.Equal("https://www.justjared.com/2013/06/07/x/", link.Url);
        Assert.Equal(@"Z:\Babes\Busy Philipps", link.Folder);        // %20 is a real space
        Assert.Equal(new[] { "Busy Philipps", "candid" }, link.Tags);
        Assert.Equal("jj-justjared-media", link.Media);
        Assert.False(link.InBackground);                              // it should come to the front
    }

    [Fact]
    public void Folder_tags_and_media_are_optional()
    {
        var link = OculaLink.Parse("ocula:webdownload?url=" + Page);

        Assert.NotNull(link);
        Assert.Null(link!.Folder);
        Assert.Empty(link.Tags!);
        Assert.Null(link.Media);
    }

    [Theory]
    [InlineData("ocula:webdownload")]
    [InlineData("ocula:webdownload?url=")]
    [InlineData("ocula:webdownload?url=notaurl")]
    [InlineData("ocula:webdownload?url=file%3A%2F%2F%2FC%3A%2FWindows%2Fwin.ini")]
    [InlineData("ocula:webdownload?url=javascript%3Aalert(1)")]
    [InlineData("ocula:webdownload?url=ocula%3Acaptures")]
    [InlineData("ocula:webdownload?url=ftp%3A%2F%2Fexample.com%2Fa.jpg")]
    [InlineData("ocula:webdownload?url=https%3A%2F%2Fuser%3Apassword%40example.com%2F")]   // no embedded sign-in
    public void A_missing_or_unsafe_page_address_drops_the_link(string text)
    {
        Assert.Null(OculaLink.Parse(text));
    }

    [Fact]
    public void Tags_are_tidied_deduplicated_and_limited()
    {
        var many = string.Join(";", Enumerable.Range(0, 40).Select(i => "tag" + i));
        var tags = OculaLink.ParseTags(";; a ;A;b\tc;" + new string('x', 61) + ";" + many);

        Assert.Equal(OculaLink.MaxTags, tags.Count);
        Assert.Equal("a", tags[0]);                                   // "A" is the same tag
        Assert.Equal("b c", tags[1]);                                 // a tab becomes a space
        Assert.DoesNotContain(tags, t => t.Length > OculaLink.MaxTagLength);   // the 61-character item is dropped
    }

    [Fact]
    public void A_tag_of_exactly_the_limit_is_kept()
    {
        Assert.Single(OculaLink.ParseTags(new string('x', OculaLink.MaxTagLength)));
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("a\tb")]
    public void A_picture_filter_with_white_space_is_dropped(string media)
    {
        var link = OculaLink.Parse("ocula:webdownload?url=" + Page + "&media=" + Uri.EscapeDataString(media));

        Assert.NotNull(link);
        Assert.Null(link!.Media);
    }

    [Fact]
    public void A_picture_filter_over_the_limit_is_dropped()
    {
        var link = OculaLink.Parse("ocula:webdownload?url=" + Page + "&media=" + new string('m', OculaLink.MaxMediaLength + 1));

        Assert.Null(link!.Media);
    }

    // ---- the save folder

    private static readonly string[] Library = { @"Z:\Babes", @"D:\Photos" };

    [Theory]
    [InlineData(@"Z:\Babes\Busy Philipps", @"Z:\Babes\Busy Philipps")]
    [InlineData(@"Z:\Babes\Busy Philipps\", @"Z:\Babes\Busy Philipps")]       // trailing slash
    [InlineData(@"z:\babes\Busy Philipps", @"z:\babes\Busy Philipps")]        // case doesn't matter
    [InlineData(@"Z:\Babes", @"Z:\Babes")]                                    // a library folder itself
    [InlineData(@"D:\Photos\New\Folder", @"D:\Photos\New\Folder")]            // not created yet
    public void A_folder_inside_the_library_is_kept(string folder, string expected)
    {
        Assert.Equal(expected, OculaLink.DownloadFolderOrNull(folder, Library));
    }

    [Theory]
    [InlineData(@"C:\Users\someone\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup")]
    [InlineData(@"Z:\Babes2\x")]                                              // looks like the library folder, isn't
    [InlineData(@"Z:\Babes\..\Windows")]                                      // climbs out
    [InlineData(@"Z:\Babes\..\..")]
    [InlineData(@"\\evil\share\x")]
    [InlineData(@"Babes\x")]                                                  // not a full path
    [InlineData(@"\Babes\x")]                                                 // no drive
    [InlineData("Z:Babes")]                                                   // drive-relative
    [InlineData("Z:\\Babes\\a|b")]                                            // not allowed in a name
    [InlineData("Z:\\Babes\\a*b")]
    [InlineData("Z:\\Babes\\a?b")]
    [InlineData("Z:\\Babes\\x:stream")]                                       // an NTFS stream, not a folder
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_folder_outside_the_library_or_not_a_full_path_is_ignored(string? folder)
    {
        Assert.Null(OculaLink.DownloadFolderOrNull(folder, Library));
    }

    [Fact]
    public void With_no_library_folders_no_suggested_folder_is_used()
    {
        Assert.Null(OculaLink.DownloadFolderOrNull(@"Z:\Babes\x", Array.Empty<string>()));
    }

    // ---- other links are unaffected

    [Fact]
    public void Capta_and_Porta_links_still_parse_as_before()
    {
        var import = OculaLink.Parse("ocula:import?file=C%3A%5Ca.png&folder=C%3A%5Cc");
        Assert.Equal("import", import!.Verb);
        Assert.True(import.InBackground);
        Assert.Null(import.Url);
        Assert.Null(import.Tags);

        var person = OculaLink.Parse("ocula:person?id=b65e86ce6eb973c3");
        Assert.Equal("b65e86ce6eb973c3", person!.Id);
        Assert.Null(person.Url);

        Assert.Equal("captures", OculaLink.Parse("ocula:captures?folder=C%3A%5Cc")!.Verb);
    }

    [Fact]
    public void A_web_download_link_given_as_a_command_line_argument_is_found()
    {
        var link = OculaLink.FromArguments(new[] { "\"ocula:webdownload?url=" + Page + "\"" });

        Assert.Equal("webdownload", link!.Verb);
    }

    [Fact]
    public void The_trim_option_is_read_only_when_it_says_justjared()
    {
        Assert.True(OculaLink.Parse("ocula:webdownload?url=https%3A%2F%2Fexample.com%2Fa&trim=justjared")!.TrimJustJared);
        Assert.True(OculaLink.Parse("ocula:webdownload?url=https%3A%2F%2Fexample.com%2Fa&trim=JustJared")!.TrimJustJared);
        Assert.False(OculaLink.Parse("ocula:webdownload?url=https%3A%2F%2Fexample.com%2Fa&trim=yes")!.TrimJustJared);
        Assert.False(OculaLink.Parse("ocula:webdownload?url=https%3A%2F%2Fexample.com%2Fa")!.TrimJustJared);
    }
}
