using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

public class WebDownloaderTests
{
    private sealed class Fake : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var bytes = Enumerable.Range(0, 1000).Select(i => (byte)(i % 251)).ToArray();
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(response);
        }
    }

    private static readonly Uri Page = new("https://example.com/page");
    private static List<WebImage> One() => new() { new WebImage { ThumbUrl = "https://example.com/a.jpg", FullUrl = "https://example.com/a.jpg" } };

    private static string NewFolder() => Path.Combine(Path.GetTempPath(), "ocula-dl-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_first_download_is_saved_and_not_taken_for_a_duplicate_of_its_own_temporary_file()
    {
        var folder = NewFolder();
        try
        {
            var result = await WebDownloader.RunAsync(new HttpClient(new Fake()), One(), folder, Page, null, null, CancellationToken.None);
            Assert.Single(result.Saved);
            Assert.Equal(0, result.Duplicates);
            Assert.Equal(1000, new FileInfo(result.Saved[0]).Length);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task A_prepare_step_changes_the_file_before_it_is_kept_so_only_one_copy_exists_and_a_repeat_is_a_duplicate()
    {
        var folder = NewFolder();
        try
        {
            Action<string> cut = f => File.WriteAllBytes(f, File.ReadAllBytes(f).Take(900).ToArray());

            var first = await WebDownloader.RunAsync(new HttpClient(new Fake()), One(), folder, Page, null, null, CancellationToken.None, cut);
            Assert.Single(first.Saved);
            var files = Directory.GetFiles(folder);
            Assert.Single(files);                                   // no untrimmed original, no .part left behind
            Assert.Equal(900, new FileInfo(files[0]).Length);

            var again = await WebDownloader.RunAsync(new HttpClient(new Fake()), One(), folder, Page, null, null, CancellationToken.None, cut);
            Assert.Empty(again.Saved);
            Assert.Equal(1, again.Duplicates);
            Assert.Single(Directory.GetFiles(folder));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
