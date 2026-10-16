using System.Buffers.Binary;
using System.Text;
using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

/// <summary>
/// The hand-off from Capta: its ocula: links (OculaLink) and the details it writes into each
/// PNG (CaptaCapture). The formats are Capta's (CaptureActions.cs and CaptureMetadata.cs there).
/// </summary>
public class CaptaIntegrationTests
{
    // ---------- ocula: links ----------

    [Fact]
    public void Import_link_gives_the_file_and_folder_and_runs_in_the_background()
    {
        var file = @"C:\Users\me\AppData\Local\Packages\StephenStirling.Capta_abc\TempState\Ocula\Capture 2026-10-10 093000.png";
        var folder = @"D:\Pictures\Screens & more";
        var link = OculaLink.Parse($"ocula:import?file={Uri.EscapeDataString(file)}&folder={Uri.EscapeDataString(folder)}&future=1");

        Assert.NotNull(link);
        Assert.Equal("import", link.Verb);
        Assert.Equal(file, link.File);
        Assert.Equal(folder, link.Folder);
        Assert.True(link.InBackground);
    }

    [Theory]
    [InlineData("ocula:captures", "captures")]
    [InlineData("OCULA:Captures", "captures")]
    [InlineData("ocula://edit/?file=x", "edit")]
    public void Verb_is_read_without_a_query(string text, string verb)
    {
        var link = OculaLink.Parse(text);
        Assert.NotNull(link);
        Assert.Equal(verb, link.Verb);
        Assert.False(link.InBackground);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ocula:")]
    [InlineData("https://example.com/import?file=x")]
    public void Other_text_is_not_a_link(string? text) => Assert.Null(OculaLink.Parse(text));

    [Fact]
    public void Missing_or_relative_folder_means_Captas_default()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Captures");
        Assert.Equal(expected, OculaLink.CaptureFolderOrDefault(null));
        Assert.Equal(expected, OculaLink.CaptureFolderOrDefault("Captures"));
        Assert.Equal(@"D:\Shots", OculaLink.CaptureFolderOrDefault(@"D:\Shots\"));
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Packages\StephenStirling.Capta_abc\TempState\Ocula\a.png", true)]
    [InlineData(@"C:\Users\me\AppData\Local\Packages\StephenStirling.Capta_abc\AC\Temp\a.png", true)]
    [InlineData(@"C:\Users\me\AppData\Local\Packages\StephenStirling.Capta_abc\LocalState\a.png", false)]
    [InlineData(@"C:\Users\me\Pictures\Captures\Recording 2026-10-10 093000.mp4", false)]
    public void Capta_temp_files_are_temporary(string path, bool temporary) =>
        Assert.Equal(temporary, OculaLink.IsTemporary(path));

    [Fact]
    public void Temporary_capture_is_copied_into_the_capture_folder_under_a_free_name()
    {
        var work = Directory.CreateTempSubdirectory("ocula-tests-");
        try
        {
            var source = Path.Combine(work.FullName, "Capture.png");
            File.WriteAllText(source, "png");
            var captures = Path.Combine(Path.GetTempPath(), "..", "ocula-tests-captures-" + Guid.NewGuid().ToString("N"));
            captures = Path.GetFullPath(captures);
            try
            {
                Assert.True(OculaLink.IsTemporary(source));
                var first = OculaLink.Adopt(source, captures);
                var second = OculaLink.Adopt(source, captures);

                Assert.Equal(Path.Combine(captures, "Capture.png"), first);
                Assert.Equal(Path.Combine(captures, "Capture (2).png"), second);
                Assert.Equal("png", File.ReadAllText(second));
            }
            finally
            {
                if (Directory.Exists(captures)) Directory.Delete(captures, recursive: true);
            }
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_file_outside_temp_stays_where_it_is() =>
        Assert.Equal(@"D:\Captures\Recording.mp4", OculaLink.Adopt(@"D:\Captures\Recording.mp4", @"D:\Elsewhere"));

    [Theory]
    [InlineData(@"C:\Pictures\Captures", true)]
    [InlineData(@"C:\Pictures", true)]
    [InlineData(@"c:\pictures\captures\2026", true)]
    [InlineData(@"C:\Pictures2", false)]
    [InlineData(@"D:\Captures", false)]
    public void Inside_means_a_library_folder_or_below_it(string folder, bool inside) =>
        Assert.Equal(inside, OculaLink.IsInside(folder, new[] { @"C:\Pictures\", @"E:\Photos" }));

    // ---------- Capta's PNG metadata ----------

    private const string CaptaJson =
        "{\"version\":1,\"software\":\"Capta\",\"mode\":\"Window\",\"capturedAt\":\"2026-10-10T14:32:00+01:00\"," +
        "\"sourceApp\":\"Settings\",\"windowTitle\":\"Network \\u2014 Settings\",\"width\":1280,\"height\":816," +
        "\"toneMappedFromHdr\":false,\"ocrText\":\"Network \\u00B7 Wi-Fi Connected\"}";

    [Fact]
    public void Reads_Captas_details_from_a_PNG()
    {
        using var png = Png(("tEXt", Text("Software", "Something")), ("tEXt", Text("Capta", CaptaJson)));
        var capture = CaptaCapture.Read(png);

        Assert.NotNull(capture);
        Assert.Equal("Window", capture.Mode);
        Assert.Equal("Settings", capture.SourceApp);
        Assert.Equal("Network — Settings", capture.WindowTitle);
        Assert.Equal("Network · Wi-Fi Connected", capture.Text);
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 14, 32, 0, TimeSpan.FromHours(1)), capture.CapturedAt);
        Assert.Equal("Window\nSettings\nNetwork — Settings", capture.Summary);
    }

    [Fact]
    public void Details_after_the_image_data_are_found_too()
    {
        using var png = Png(("IDAT", new byte[] { 1, 2, 3 }), ("tEXt", Text("Capta", CaptaJson)));
        Assert.Equal("Window", CaptaCapture.Read(png)?.Mode);
    }

    [Fact]
    public void A_PNG_without_Captas_chunk_is_not_a_capture()
    {
        using var png = Png(("tEXt", Text("Comment", "hello")), ("IDAT", new byte[] { 1 }));
        Assert.Null(CaptaCapture.Read(png));
    }

    [Fact]
    public void Not_a_PNG_is_not_a_capture() =>
        Assert.Null(CaptaCapture.Read(new MemoryStream(Encoding.ASCII.GetBytes("GIF89a not a png"))));

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"software\":\"Other\",\"mode\":\"Region\"}")]
    public void Unusable_metadata_is_ignored(string json) => Assert.Null(CaptaCapture.Parse(json));

    [Fact]
    public void Missing_fields_are_tolerated()
    {
        var capture = CaptaCapture.Parse("{\"mode\":\"Region\",\"capturedAt\":5}");
        Assert.NotNull(capture);
        Assert.Equal("Region", capture.Mode);
        Assert.Null(capture.SourceApp);
        Assert.Null(capture.CapturedAt);
        Assert.Null(capture.Text);
    }

    // ---------- Hostile files ----------

    [Fact]
    public void A_huge_tEXt_chunk_that_is_not_Captas_is_skipped_without_being_read()
    {
        using var inner = Png(("tEXt", Text("Comment", new string('x', 15 * 1024 * 1024))), ("tEXt", Text("Capta", CaptaJson)));
        var counting = new CountingStream(inner);

        Assert.Equal("Window", CaptaCapture.Read(counting)?.Mode); // Capta's chunk after it is still found
        Assert.True(counting.BytesRead < 4096, $"read {counting.BytesRead} bytes");
    }

    [Fact]
    public void A_Capta_chunk_over_the_size_limit_is_ignored()
    {
        var big = "{\"software\":\"Capta\",\"ocrText\":\"" + new string('a', CaptaCapture.MaxChunkBytes) + "\"}";
        using var png = Png(("tEXt", Text("Capta", big)));
        Assert.Null(CaptaCapture.Read(png));
    }

    [Fact]
    public void Odd_tEXt_chunks_do_not_throw()
    {
        using var empty = Png(("tEXt", Array.Empty<byte>()), ("tEXt", Text("Capta", CaptaJson)));
        Assert.Equal("Window", CaptaCapture.Read(empty)?.Mode);

        using var noKeyword = Png(("tEXt", Encoding.Latin1.GetBytes("no zero byte here at all")), ("tEXt", Text("Capta", CaptaJson)));
        Assert.Equal("Window", CaptaCapture.Read(noKeyword)?.Mode);

        using var emptyKeyword = Png(("tEXt", Text("", "value")), ("tEXt", Text("Capta", CaptaJson)));
        Assert.Equal("Window", CaptaCapture.Read(emptyKeyword)?.Mode);

        using var cut = Png(("tEXt", Text("Capta", CaptaJson)));
        cut.SetLength(cut.Length - 40); // cut off in the middle of the chunk
        Assert.Null(CaptaCapture.Read(cut));
    }

    [Fact]
    public void Text_fields_are_cleaned_and_capped_when_read()
    {
        var json = "{\"software\":\"Capta\",\"mode\":\"Reg\\u0000ion\"," +
                   "\"sourceApp\":\"" + new string('a', 5000) + "\"," +
                   "\"windowTitle\":\"Invoice \\u202Eexe.jpg\\r\\nFake: second line\"," +
                   "\"ocrText\":\"line one\\r\\nline two\\u0000 <b>bold</b> ..\\\\..\\\\x " + new string('z', 200_000) + "\"}";
        var capture = CaptaCapture.Parse(json)!;

        Assert.Equal("Reg ion", capture.Mode);
        Assert.Equal(CaptureTextIndex.MaxFieldChars, capture.SourceApp!.Length);
        Assert.Equal("Invoice exe.jpg Fake: second line", capture.WindowTitle); // one line, no override
        Assert.DoesNotContain('\n', capture.Summary.Split('\n', 3)[2]);
        Assert.Equal(3, capture.Summary.Split('\n').Length); // the title can't forge extra lines in the summary
        Assert.StartsWith("line one\nline two  <b>bold</b> ..\\..\\x ", capture.Text); // the NUL became a space, next to the typed one
        Assert.Equal(CaptureTextIndex.MaxTextChars, capture.Text!.Length);
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); BytesRead += n; return n; }
        public override int Read(Span<byte> buffer) { var n = inner.Read(buffer); BytesRead += n; return n; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] Text(string keyword, string text) =>
        Encoding.Latin1.GetBytes(keyword).Concat(new byte[] { 0 }).Concat(Encoding.Latin1.GetBytes(text)).ToArray();

    /// <summary>A PNG made of the signature, an IHDR, the given chunks and IEND. CRCs aren't checked, so they're zero.</summary>
    private static MemoryStream Png(params (string Type, byte[] Data)[] chunks)
    {
        var stream = new MemoryStream();
        stream.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string type, byte[] data)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            stream.Write(length);
            stream.Write(Encoding.ASCII.GetBytes(type));
            stream.Write(data);
            stream.Write(new byte[4]);
        }
        Chunk("IHDR", new byte[13]);
        foreach (var (type, data) in chunks) Chunk(type, data);
        Chunk("IEND", Array.Empty<byte>());
        stream.Position = 0;
        return stream;
    }
}
