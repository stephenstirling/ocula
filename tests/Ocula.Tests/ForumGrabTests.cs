using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

/// <summary>
/// The web grabber's forum logic: which pictures on a thread page are the posts' pictures (not avatars, signature banners,
/// logos or smilies), and which other pages of the thread it can see. Each test runs the real grab script in headless Edge on
/// a saved page in TestData/forums.
///
/// Those pages are hand-written to mimic XenForo, phpBB and Discourse markup. None was saved from a real forum, so they show
/// the script does what it says on that markup, not that it works on any real site.
/// </summary>
public class ForumGrabTests
{
    private static async Task<BrowserGrab.Result> GrabAsync(string fixture, string path, bool picturesFail = false)
    {
        Assert.SkipUnless(BrowserGrab.Edge != null, "Microsoft Edge isn't installed here, so the grab script can't be run.");
        return await BrowserGrab.RunAsync(fixture, path, picturesFail);
    }

    private static List<string> Thumbs(PageSnapshot page) => page.Images.Select(i => i.ThumbUrl).ToList();

    // ---------- XenForo ----------

    [Fact]
    public async Task XenForo_keeps_the_post_pictures_and_drops_avatars_signatures_logos_and_smilies()
    {
        var r = await GrabAsync("xenforo-thread.html", "/threads/katy-perry.5938936/");

        Assert.Equal(
            new[]
            {
                "https://t1.pixhost.to/thumbs/1234/aaa_1.jpg",
                "https://t1.pixhost.to/thumbs/1234/aaa_2.jpg",
                r.Origin + "/data/attachments/0/9-abc.jpg",
            },
            Thumbs(r.Page));
        Assert.All(r.Page.Images, image => Assert.True(image.InPost));

        // None of the page's furniture got through.
        Assert.DoesNotContain(Thumbs(r.Page), t => t.Contains("/data/avatars/"));              // poster avatars
        Assert.DoesNotContain(Thumbs(r.Page), t => t.Contains("sig.example.test"));            // signature banner
        Assert.DoesNotContain(Thumbs(r.Page), t => t.Contains("logo") || t.Contains("nav-home") || t.Contains("footer-badge"));
        Assert.DoesNotContain(Thumbs(r.Page), t => t.Contains("smilies"));                     // smilies, even inside a post
    }

    [Fact]
    public async Task XenForo_thumbnails_keep_the_address_of_the_page_they_link_to()
    {
        var r = await GrabAsync("xenforo-thread.html", "/threads/katy-perry.5938936/");

        Assert.Equal("https://pixhost.to/show/1234/aaa_1.jpg", r.Page.Images[0].LinkUrl);
        Assert.Equal("https://pixhost.to/show/1234/aaa_2.jpg", r.Page.Images[1].LinkUrl);
        Assert.Equal(r.Origin + "/attachments/photo-3-jpg.9/", r.Page.Images[2].LinkUrl);
        Assert.Equal(250, r.Page.Images[0].Width);
        Assert.Equal(375, r.Page.Images[0].Height);
        Assert.Equal("Katy Perry attends the 4th Annual Rare Impact Fund Benefit", r.Page.Title);
    }

    [Fact]
    public async Task XenForo_finds_the_other_pages_of_the_thread_once_each_and_not_the_current_one()
    {
        var r = await GrabAsync("xenforo-thread.html", "/threads/katy-perry.5938936/");

        Assert.Equal(
            new[]
            {
                r.Origin + "/threads/katy-perry.5938936/page-2",
                r.Origin + "/threads/katy-perry.5938936/page-3",
                r.Origin + "/threads/katy-perry.5938936/page-9",
            },
            r.Page.PageUrls); // "Next" points at page 2 again; it is listed once, and page 1 (this page) isn't listed
    }

    // ---------- phpBB ----------

    [Fact]
    public async Task PhpBB_keeps_the_post_pictures_and_drops_avatars_signatures_ranks_logos_and_smilies()
    {
        var r = await GrabAsync("phpbb-thread.html", "/viewtopic.php?t=7");

        Assert.Equal(
            new[] { "https://img1.imagetwist.com/th/001/aaa111.jpg", "https://img1.imagetwist.com/th/001/bbb222.jpg" },
            Thumbs(r.Page));
        Assert.All(r.Page.Images, image => Assert.True(image.InPost));
        Assert.Equal("https://imagetwist.com/aaa111/pc_1.jpg.html", r.Page.Images[0].LinkUrl);
        Assert.DoesNotContain(Thumbs(r.Page), t => t.Contains("avatar") || t.Contains("sig.example") || t.Contains("rank_") ||
                                                  t.Contains("site_logo") || t.Contains("smilies") || t.Contains("powered_by"));
    }

    [Fact]
    public async Task PhpBB_finds_the_other_pages_from_the_pagination_links()
    {
        var r = await GrabAsync("phpbb-thread.html", "/viewtopic.php?t=7");

        // The pages are ?t=7&start=25 and ?t=7&start=50; the Next button repeats the first of them, and page 1 (this page) has no link.
        Assert.Equal(
            new[] { r.Origin + "/viewtopic.php?t=7&start=25", r.Origin + "/viewtopic.php?t=7&start=50" },
            r.Page.PageUrls);
    }

    // ---------- Discourse ----------

    [Fact]
    public async Task Discourse_keeps_the_post_pictures_and_drops_avatars_emoji_logos_and_quoted_posts()
    {
        var r = await GrabAsync("discourse-topic.html", "/t/tyla-performs/55");

        Assert.Equal(
            new[]
            {
                "https://fan.test/uploads/default/optimized/2X/a/aaa_2_690x460.jpeg",
                "https://fan.test/uploads/default/optimized/2X/b/bbb_2_690x460.jpeg",
            },
            Thumbs(r.Page));
        Assert.Equal("https://fan.test/uploads/default/original/2X/a/aaa.jpeg", r.Page.Images[0].LinkUrl); // lightbox: the original upload
        Assert.DoesNotContain(Thumbs(r.Page), t => t.Contains("user_avatar") || t.Contains("emoji") || t.Contains("logo") || t.Contains("ccc_2_300x200"));
        Assert.Equal("Tyla performs during the A Pop World Tour", r.Page.Title);
    }

    [Fact]
    public async Task Discourse_has_no_page_links_to_find_because_it_loads_more_posts_by_scrolling()
    {
        var r = await GrabAsync("discourse-topic.html", "/t/tyla-performs/55");
        Assert.Empty(r.Page.PageUrls);
    }

    // ---------- How the choice is made ----------

    [Fact]
    public async Task With_only_one_post_picture_the_other_pictures_on_the_page_are_kept_too()
    {
        // Post pictures are preferred only when there are at least two. With one, an unrelated picture elsewhere on the page stays.
        var r = await GrabAsync("single-post-image.html", "/threads/one.1/");

        Assert.Equal(2, r.Page.Images.Count);
        Assert.True(r.Page.Images[0].InPost);
        Assert.False(r.Page.Images[1].InPost);
        Assert.Contains("related-thread.jpg", r.Page.Images[1].ThumbUrl);
    }

    [Fact]
    public async Task A_page_with_no_pagination_has_no_page_links()
    {
        var r = await GrabAsync("single-post-image.html", "/threads/one.1/");
        Assert.Empty(r.Page.PageUrls);
    }

    /// <summary>
    /// CURRENT BEHAVIOUR, FOR REVIEW (not new in the Forum Scraper commit, but it matters for forums). The script reads a picture's
    /// size from img.width when it has no natural size. In Chrome a picture that FAILED to load and has alt text reports the size of
    /// its alt text (about 56 x 18), not its width and height attributes, so it is dropped as an icon. phpBB's BBCode pictures have
    /// alt="Image", so a thread whose thumbnails fail to load (an image host that blocks hotlinking, or being offline) yields no
    /// pictures, although the links around them lead to the full-size pictures. A failed picture with EMPTY alt text keeps its
    /// attribute size, so XenForo's (alt="") survive.
    /// </summary>
    [Fact]
    public async Task Thumbnails_that_failed_to_load_and_have_alt_text_are_dropped_as_if_they_were_icons()
    {
        var phpbb = await GrabAsync("phpbb-thread.html", "/viewtopic.php?t=7", picturesFail: true);
        Assert.Empty(phpbb.Page.Images);                                                    // alt="Image"

        var xenforo = await GrabAsync("xenforo-thread.html", "/threads/katy-perry.5938936/", picturesFail: true);
        Assert.Equal(2, xenforo.Page.Images.Count(i => i.ThumbUrl.Contains("pixhost.to"))); // alt=""
    }

    /// <summary>
    /// Fixed after the Forum Scraper commit (f311a33), which dropped anything inside header, nav, aside or footer on EVERY page, so the
    /// lead picture of an article whose theme puts it in the article's header was no longer offered. The forum rules (header, nav,
    /// aside and footer pictures dropped, larger minimum size, name-based avatar/logo filter) now apply only on a page that has a
    /// forum message container, so an ordinary article behaves as it did before the Forum Scraper.
    /// </summary>
    [Fact]
    public async Task On_an_ordinary_article_a_picture_inside_a_header_or_aside_is_still_offered()
    {
        var r = await GrabAsync("article-with-header-image.html", "/2026/lake");

        Assert.Equal(
            new[]
            {
                "https://blog.test/img/lake-hero.jpg",     // inside <header>
                "https://blog.test/img/lake-boat.jpg",
                "https://blog.test/img/lake-dock.jpg",
                "https://blog.test/img/related-1.jpg",     // inside <aside>
            },
            Thumbs(r.Page));
    }

    /// <summary>
    /// A blog whose entries carry a "post" class is not a forum: only the forum message containers (.message-body, .bbWrapper,
    /// .postbody, .cooked and so on) switch the forum rules on, so 100 px thumbnails, a header picture and a picture whose address
    /// contains "icon" are all still offered.
    /// </summary>
    [Fact]
    public async Task A_blog_that_uses_a_post_class_is_not_treated_as_a_forum()
    {
        var r = await GrabAsync("blog-with-post-class.html", "/trip/day-one");

        var thumbs = Thumbs(r.Page);
        Assert.Contains("https://blog.test/img/day1-hero.jpg", thumbs);      // inside <header>
        Assert.Contains("https://blog.test/img/thumb/a.jpg", thumbs);        // 100 px, above the 80 px minimum
        Assert.Contains("https://blog.test/img/thumb/b.jpg", thumbs);
        Assert.Contains("https://blog.test/img/day1-extra.jpg", thumbs);
        Assert.Contains("https://blog.test/img/site-icon-badge.png", thumbs); // "icon" in the name only matters on forums
        Assert.Contains("https://blog.test/img/nav-picture.jpg", thumbs);    // inside <nav>
        Assert.All(r.Page.Images, image => Assert.False(image.InPost));
    }
}
