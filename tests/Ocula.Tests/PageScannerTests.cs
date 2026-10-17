using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

/// <summary>
/// The C# half of the web grabber: turning the script's answer into a page snapshot (Parse), and finding the full-size
/// picture behind each thumbnail (ResolveAsync). The pages the resolver is given are small synthetic landing pages written for
/// these tests to look like each image host's (not saved from the real hosts), and no real network is used.
/// </summary>
public class PageScannerTests
{
    // ---------- Parse ----------

    [Fact]
    public void Parse_reads_pictures_post_flags_and_page_links()
    {
        var page = PageScanner.Parse("""
            {"url":"https://forum.test/threads/x.1/","title":"A thread","description":"d",
             "headings":["H"],"crumbs":["Home"],"keywords":["k"],
             "images":[
               {"src":"https://t.test/a.jpg","w":250,"h":375,"alt":"a","caption":"c","link":"https://host.test/show/a","inPost":true},
               {"src":"https://t.test/b.jpg","w":0,"h":0,"alt":"","caption":"","link":null,"inPost":false}],
             "pageUrls":["https://forum.test/threads/x.1/page-2","https://forum.test/threads/x.1/page-3"]}
            """);

        Assert.Equal("A thread", page.Title);
        Assert.Equal(2, page.Images.Count);
        Assert.True(page.Images[0].InPost);
        Assert.Equal("https://host.test/show/a", page.Images[0].LinkUrl);
        Assert.Equal(250, page.Images[0].Width);
        Assert.False(page.Images[1].InPost);
        Assert.Null(page.Images[1].LinkUrl);
        Assert.Equal(new[] { "https://forum.test/threads/x.1/page-2", "https://forum.test/threads/x.1/page-3" }, page.PageUrls);
    }

    [Fact]
    public void Parse_tolerates_missing_and_wrongly_typed_fields()
    {
        var page = PageScanner.Parse("""{"title":5,"images":[{"src":"https://t.test/a.jpg","w":"big","inPost":"yes"}],"pageUrls":[1,null,"","https://forum.test/p2"]}""");

        Assert.Equal(string.Empty, page.Title);
        Assert.Single(page.Images);
        Assert.Equal(0, page.Images[0].Width);
        Assert.False(page.Images[0].InPost);                       // only a real JSON true counts
        Assert.Equal(new[] { "https://forum.test/p2" }, page.PageUrls); // non-strings and empty strings are skipped
        Assert.Empty(PageScanner.Parse("{}").PageUrls);
    }

    [Theory]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/secret.jpg")]
    [InlineData("/relative/a.jpg")]
    [InlineData("")]
    public void Parse_drops_pictures_whose_address_isnt_a_web_address(string src)
    {
        var json = JsonSerializer.Serialize(new { images = new[] { new { src } } });
        Assert.Empty(PageScanner.Parse(json).Images);
    }

    [Fact]
    public void Parse_ignores_a_link_that_is_not_a_web_address_or_is_the_picture_itself()
    {
        var page = PageScanner.Parse("""
            {"images":[{"src":"https://t.test/a.jpg","link":"javascript:void(0)"},
                       {"src":"https://t.test/b.jpg","link":"https://t.test/b.jpg"}]}
            """);

        Assert.Null(page.Images[0].LinkUrl);
        Assert.Null(page.Images[1].LinkUrl);
    }

    [Fact]
    public void Parse_does_not_judge_page_links_so_a_link_to_another_site_comes_through_as_it_is()
    {
        // Parse only reads. Whether a page link may be followed (same site only) is for the code that would follow it.
        var page = PageScanner.Parse("""{"pageUrls":["https://elsewhere.test/page-2"]}""");
        Assert.Equal(new[] { "https://elsewhere.test/page-2" }, page.PageUrls);
    }

    [Fact]
    public void Parse_throws_on_text_that_isnt_json() =>
        Assert.ThrowsAny<JsonException>(() => PageScanner.Parse("not json"));

    [Theory]
    [InlineData("https://x.test/a.jpg", true)]
    [InlineData("https://x.test/a.JPEG?size=large", true)]
    [InlineData("https://x.test/a.webp#frag", true)]
    [InlineData("https://x.test/a.jpg.html", false)]
    [InlineData("https://x.test/show/123", false)]
    public void IsImageUrl_goes_by_the_file_extension(string url, bool expected) =>
        Assert.Equal(expected, PageScanner.IsImageUrl(url));

    // ---------- FindCandidates ----------

    [Fact]
    public void FindCandidates_puts_the_declared_main_image_first_then_named_then_bigger_pictures()
    {
        var html = """
            <html><head><meta property="og:image" content="/og.jpg"><link rel="image_src" href="https://x.test/linked.jpg"></head>
            <body><img src="/small.jpg" width="40" height="40"><img id="photo" src="/named.jpg" width="100" height="100">
            <img src="/big.jpg" width="800" height="600"><img src="data:image/gif;base64,AAAA"></body></html>
            """;

        var found = PageScanner.FindCandidates(html, new Uri("https://x.test/page/1")).ToList();

        Assert.Equal(
            new[] { "https://x.test/og.jpg", "https://x.test/linked.jpg", "https://x.test/named.jpg", "https://x.test/big.jpg", "https://x.test/small.jpg" },
            found);
    }

    // ---------- ResolveAsync ----------

    private static readonly Uri ForumPage = new("https://forum.test/threads/x.1/");

    private static async Task<(WebImage Image, List<Seen> Requests)> ResolveAsync(
        string linkUrl, Func<HttpRequestMessage, HttpResponseMessage> respond, string? cookies = null, string thumb = "https://t.test/thumb.jpg")
    {
        var handler = new StubHandler(respond);
        using var http = new HttpClient(handler);
        var image = new WebImage { ThumbUrl = thumb, LinkUrl = linkUrl };
        await PageScanner.ResolveAsync(http, new[] { image }, ForumPage, cookies, null, CancellationToken.None);
        return (image, handler.Requests);
    }

    private static HttpResponseMessage Html(string html) =>
        new(HttpStatusCode.OK) { Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Image(int bytes)
    {
        var content = new ByteArrayContent(new byte[bytes]);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    [Fact]
    public async Task A_link_straight_to_an_image_file_is_used_with_no_request()
    {
        var (image, requests) = await ResolveAsync("https://host.test/full/a.jpg", _ => throw new InvalidOperationException("no request expected"));

        Assert.Equal("https://host.test/full/a.jpg", image.FullUrl);
        Assert.True(image.HasLargerVersion);
        Assert.Null(image.Problem);
        Assert.Empty(requests);
    }

    // The Pixhost and ImageTwist tests below use landing addresses WITHOUT a file extension, to exercise the code that reads the
    // landing page. Real ones usually end in .jpg ("pixhost.to/show/1234/ID_name.jpg"), and the resolver treats any address that
    // ends in an image extension as the picture itself and never opens it: see the skipped test further down.

    [Fact]
    public async Task Pixhost_page_reading_gives_the_full_image_and_never_the_thumbnail_from_og_image()
    {
        var (image, requests) = await ResolveAsync("https://pixhost.to/show/1234/aaa_1", _ => Html("""
            <html><head><meta property="og:image" content="https://t1.pixhost.to/thumbs/1234/aaa_1.jpg"></head>
            <body><img id="image" class="image-img" src="https://img1.pixhost.to/images/1234/aaa_1.jpg"></body></html>
            """));

        Assert.Equal("https://img1.pixhost.to/images/1234/aaa_1.jpg", image.FullUrl);
        Assert.Single(requests); // the landing page only; no size checks on guesses
    }

    [Fact]
    public async Task Pixhost_page_reading_without_a_full_image_on_the_page_is_a_problem_not_a_thumbnail()
    {
        var (image, _) = await ResolveAsync("https://pixhost.to/show/1234/aaa_1", request =>
            request.RequestUri!.Host == "pixhost.to"
                ? Html("""<html><head><meta property="og:image" content="https://t1.pixhost.to/thumbs/1234/aaa_1.jpg"></head><body>nothing</body></html>""")
                : Image(500));

        Assert.False(image.HasLargerVersion);
        Assert.Equal("Couldn't find the full-size version.", image.Problem);
    }

    /// <summary>
    /// KNOWN DEFECT in the Forum Scraper commit (f311a33), found while writing these tests. ResolveOneAsync's first rule is "a link
    /// that ends in an image extension is the picture", which is checked before any host rule. A Pixhost landing address is
    /// pixhost.to/show/NNNN/ID_name.jpg (the commit's own comment says so), and an ImageTwist one is imagetwist.com/CODE/name.jpg,
    /// so the host code that reads those landing pages is never reached: the "full-size" address is the web page itself, and a
    /// download would save HTML as a .jpg. Un-skip this when the order of the two rules is settled. (TryHostDirect, which is meant to
    /// handle these addresses, can't help: it only acts on addresses that end in an image extension, which the first rule already took.)
    /// </summary>
    [Fact(Skip = "Known defect in f311a33: addresses ending in .jpg are taken as the picture before the Pixhost/ImageTwist rules run. See the summary comment.")]
    public async Task Pixhost_and_ImageTwist_landing_addresses_that_end_in_jpg_are_opened_and_read()
    {
        var (pix, _) = await ResolveAsync("https://pixhost.to/show/1234/aaa_1.jpg", _ => Html("""
            <html><body><img id="image" class="image-img" src="https://img1.pixhost.to/images/1234/aaa_1.jpg"></body></html>
            """));
        Assert.Equal("https://img1.pixhost.to/images/1234/aaa_1.jpg", pix.FullUrl);

        var (twist, _) = await ResolveAsync("https://imagetwist.com/aaa111/pc_1.jpg", _ => Html("""
            <html><body><img src="https://img1.imagetwist.com/i/001/aaa111/pc_1.jpg" id="image" class="pic"></body></html>
            """));
        Assert.Equal("https://img1.imagetwist.com/i/001/aaa111/pc_1.jpg", twist.FullUrl);
    }

    [Fact]
    public async Task ImageTwist_gives_the_full_image_and_skips_th_thumbnail_paths()
    {
        var (image, _) = await ResolveAsync("https://imagetwist.com/aaa111/pc_1.jpg.html", _ => Html("""
            <html><head><meta property="og:image" content="https://img1.imagetwist.com/th/001/aaa111.jpg"></head>
            <body><img src="https://img1.imagetwist.com/i/001/aaa111/pc_1.jpg" id="image" class="pic img img-responsive"></body></html>
            """));

        Assert.Equal("https://img1.imagetwist.com/i/001/aaa111/pc_1.jpg", image.FullUrl);
    }

    [Fact]
    public async Task ImageTwist_still_finds_the_image_when_the_id_comes_before_the_src()
    {
        // The first pattern's id-before-src branch captures nothing, so this works through the later patterns.
        var (image, _) = await ResolveAsync("https://imagetwist.com/aaa111/pc_1.jpg.html", _ => Html("""
            <html><body><img id="image" alt="x" src="https://img1.imagetwist.com/i/001/aaa111/pc_1.jpg"></body></html>
            """));

        Assert.Equal("https://img1.imagetwist.com/i/001/aaa111/pc_1.jpg", image.FullUrl);
    }

    [Fact]
    public async Task ImageBam_gives_the_main_image()
    {
        var (image, _) = await ResolveAsync("https://www.imagebam.com/view/ME123", _ => Html("""
            <html><body><img class="main-image image-lg" src="https://images3.imagebam.com/aa/bb/cc/ME123.jpg"></body></html>
            """));

        Assert.Equal("https://images3.imagebam.com/aa/bb/cc/ME123.jpg", image.FullUrl);
    }

    [Fact]
    public async Task TurboImageHost_gives_the_main_image()
    {
        var (image, _) = await ResolveAsync("https://www.turboimagehost.com/p/12345/a.jpg.html", _ => Html("""
            <html><body><img id="imageid" src="https://s6d1.turboimg.net/t1/12345_a.jpg"></body></html>
            """));

        Assert.Equal("https://s6d1.turboimg.net/t1/12345_a.jpg", image.FullUrl);
    }

    [Fact]
    public async Task An_unknown_site_uses_its_og_image_when_it_is_a_real_picture_of_a_sensible_size()
    {
        var (image, _) = await ResolveAsync("https://other.test/gallery/7", request =>
            request.RequestUri!.AbsolutePath == "/gallery/7"
                ? Html("""<html><head><meta property="og:image" content="https://other.test/full/7.jpg"></head></html>""")
                : Image(80_000));

        Assert.Equal("https://other.test/full/7.jpg", image.FullUrl);
    }

    [Fact]
    public async Task An_unknown_sites_picture_found_among_the_pages_other_pictures_must_be_over_about_30_KB()
    {
        // No og:image here, so the page's own pictures are ranked and size-checked: under about 30 KB is an icon or placeholder.
        var landing = """<html><body><img src="/img/logo.png" width="100" height="100"></body></html>""";
        var (image, _) = await ResolveAsync("https://other.test/gallery/7", request =>
            request.RequestUri!.AbsolutePath == "/gallery/7" ? Html(landing) : Image(10_000));

        Assert.Equal("Couldn't find the full-size version.", image.Problem);
        Assert.False(image.CanDownload);
    }

    /// <summary>
    /// KNOWN DEFECT in the Forum Scraper commit (f311a33). A page's og:image that looks like an image file is now accepted straight away
    /// (the "Generic" block in TryHostFromHtml), before the size check that used to guard against thumbnails, logos and placeholders
    /// (about 30 KB). Many sites set og:image to their logo or a small preview, so on an unknown site the "full-size" picture can now be
    /// an icon. Un-skip when the generic rule is size-checked like every other candidate.
    /// </summary>
    [Fact(Skip = "Known defect in f311a33: an og:image that is an image file skips the 30 KB size check. See the summary comment.")]
    public async Task An_unknown_sites_og_image_is_size_checked_like_the_other_candidates()
    {
        var (image, _) = await ResolveAsync("https://other.test/gallery/7", request =>
            request.RequestUri!.AbsolutePath == "/gallery/7"
                ? Html("""<html><head><meta property="og:image" content="https://other.test/img/site-logo.png"></head></html>""")
                : Image(10_000));

        Assert.Equal("Couldn't find the full-size version.", image.Problem);
    }

    [Fact]
    public async Task A_linked_page_that_wont_open_marks_the_picture_with_a_problem()
    {
        var (image, _) = await ResolveAsync("https://pixhost.to/show/9/gone", _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Equal("Couldn't open the linked page.", image.Problem);
    }

    [Fact]
    public async Task Each_linked_page_is_opened_once_and_no_link_on_it_is_followed()
    {
        // "It never goes further than one link": the landing page offers a next page, another gallery and a link to a forum, and none is fetched.
        var landing = """
            <html><head><link rel="next" href="https://pixhost.to/show/9/next.html"><meta property="og:image" content="https://pixhost.to/images/9/full.jpg"></head>
            <body><a href="https://pixhost.to/show/9/next.html">Next</a><a href="https://forum.test/threads/y.2/">Back to the thread</a>
            <a href="https://pixhost.to/gallery/77">More from this gallery</a></body></html>
            """;
        var (image, requests) = await ResolveAsync("https://other.test/show/9/a.html", request =>
            request.RequestUri!.AbsolutePath == "/show/9/a.html" ? Html(landing) : Image(80_000));

        Assert.Equal("https://pixhost.to/images/9/full.jpg", image.FullUrl);
        Assert.Single(requests, r => r.Uri.AbsolutePath == "/show/9/a.html");                       // the landing page, once
        Assert.DoesNotContain(requests, r => r.Uri.AbsolutePath is "/show/9/next.html" or "/gallery/77" or "/threads/y.2/");
        Assert.True(requests.Count <= 1 + 8);                                                       // plus at most 8 size checks of pictures
    }

    [Fact]
    public async Task The_pages_cookies_go_only_to_the_pages_own_site()
    {
        var (_, requests) = await ResolveAsync("https://forum.test/attachments/photo.9/", request =>
            Html("""<html><head><meta property="og:image" content="https://forum.test/data/attachments/9.jpg"></head></html>"""),
            cookies: "xf_session=abc");
        Assert.All(requests, r => Assert.Equal("xf_session=abc", r.Cookie));                        // same site: sent

        var (_, other) = await ResolveAsync("https://pixhost.to/show/9/a.html", request =>
            Html("""<html><body><img id="image" src="https://img1.pixhost.to/images/9/a.jpg"></body></html>"""),
            cookies: "xf_session=abc");
        Assert.All(other, r => Assert.Null(r.Cookie));                                              // another site: never sent
    }

    [Fact]
    public async Task Pictures_that_dont_link_anywhere_are_left_alone_and_nothing_is_requested()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("no request expected"));
        using var http = new HttpClient(handler);
        var image = new WebImage { ThumbUrl = "https://t.test/a.jpg" };

        await PageScanner.ResolveAsync(http, new[] { image }, ForumPage, null, null, CancellationToken.None);

        Assert.Equal("https://t.test/a.jpg", image.FullUrl);
        Assert.False(image.HasLargerVersion);
        Assert.Empty(handler.Requests);
    }

    // ---------- A fake network ----------

    private sealed record Seen(Uri Uri, string? Cookie);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly object _gate = new();
        public List<Seen> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            lock (_gate)
                Requests.Add(new Seen(request.RequestUri!, request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : null));
            return Task.FromResult(respond(request));
        }
    }
}
