using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;
using Ocula.Models;

namespace Ocula.Services;

public sealed record ContactSheetOptions(string Title, int Columns, bool ShowRatings, bool Letter);

/// <summary>
/// Builds a printable PDF of thumbnails with file names, a title and the copyright line,
/// using Magick.NET (no extra components). Pages are drawn at 150 dpi, which prints cleanly
/// at small sizes and keeps the file light enough to email.
/// </summary>
public static class ContactSheet
{
    public static async Task CreateAsync(IReadOnlyList<ImageItem> items, ContactSheetOptions o, string output,
                                         IndexService index, IProgress<int> progress, CancellationToken token)
    {
        // A4 or US Letter at 150 dpi.
        uint width = o.Letter ? 1275u : 1240u, height = o.Letter ? 1650u : 1754u;
        const int margin = 70, gap = 24, header = 110, footer = 60, caption = CaptionHeight;
        var cell = (int)((width - 2 * margin - (o.Columns - 1) * gap) / o.Columns);
        var thumbHeight = (int)(cell * 0.72);
        var rowHeight = thumbHeight + caption + gap;
        var rows = Math.Max(1, (int)((height - 2 * margin - header - footer) / rowHeight));
        var perPage = rows * o.Columns;
        var pages = (int)Math.Ceiling(items.Count / (double)perPage);

        var font = Font("segoeui.ttf");
        var bold = Font("segoeuib.ttf");
        var symbols = Font("seguisym.ttf");

        using var collection = new MagickImageCollection();
        for (var page = 0; page < pages; page++)
        {
            var sheet = new MagickImage(MagickColors.White, width, height) { Density = new Density(150) };
            new Drawables()
                .Font(bold).FontPointSize(20).FillColor(new MagickColor("#18181A")).Text(margin, margin + 30, o.Title)
                .Font(font).FontPointSize(10).FillColor(new MagickColor("#5C5C64"))
                .Text(margin, margin + 62, $"{items.Count} photos · {DateTime.Now:d MMMM yyyy}")
                .Text(margin, height - margin + 10, $"{AppInfo.Copyright}    ·    Page {page + 1} of {pages}")
                .Draw(sheet);

            var slice = items.Skip(page * perPage).Take(perPage).ToList();
            for (var i = 0; i < slice.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var item = slice[i];
                var x = margin + (i % o.Columns) * (cell + gap);
                var y = margin + header + (i / o.Columns) * rowHeight;

                var bytes = await index.GetThumbAsync(item.Path)
                            ?? await ThumbnailService.GetBytesAsync(item.Path, 512, allowDecode: !item.IsCloudOnly);
                if (bytes != null)
                {
                    using var thumb = new MagickImage(bytes);
                    thumb.Resize((uint)cell, (uint)thumbHeight); // fits inside, keeping the shape
                    var tx = x + (cell - (int)thumb.Width) / 2;
                    var ty = y + (thumbHeight - (int)thumb.Height) / 2;
                    sheet.Composite(thumb, tx, ty, CompositeOperator.Over);
                }
                else
                {
                    new Drawables().FillColor(new MagickColor("#EEEEEC")).Rectangle(x, y, x + cell, y + thumbHeight).Draw(sheet);
                }

                var name = item.Name.Length > 34 ? item.Name[..33] + "…" : item.Name;
                var text = new Drawables().Font(font).FontPointSize(8).FillColor(new MagickColor("#18181A"))
                                          .Text(x, y + thumbHeight + 22, name);
                if (o.ShowRatings && item.Rating > 0)
                    text.Font(symbols).FillColor(new MagickColor("#C98A1E")).Text(x, y + thumbHeight + 44, new string('★', item.Rating));
                text.Draw(sheet);
                progress.Report(page * perPage + i + 1);
            }
            collection.Add(sheet);
        }

        await Task.Run(() => collection.Write(output, MagickFormat.Pdf), token);
    }

    private const int CaptionHeight = 52;

    private static string Font(string file) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), file);
}
