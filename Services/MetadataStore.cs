using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Ocula.Models;
using Windows.Storage;

namespace Ocula.Services;

public sealed record UserData(List<string> Tags, int Rating, string? Label);

/// <summary>
/// Reads and writes tags, star ratings and colour labels in the files themselves, so
/// Lightroom, Bridge, darktable, File Explorer and Photos all see the same values.
///
/// - JPEG, TIFF, HEIC, JPEG XR: tags and rating go inside the file via the Windows property
///   system (System.Keywords, System.Rating). Windows has no property for colour labels,
///   so for these files labels live in Ocula's library only.
/// - RAW and other formats: everything goes in an XMP sidecar next to the image
///   (IMG_0001.CR2 -> IMG_0001.xmp), the Lightroom convention. The RAW file is never modified.
/// - RAW+JPEG pairs: both of the above, so the values travel with either file.
/// </summary>
public static class MetadataStore
{
    private const string KeywordsProperty = "System.Keywords";
    private const string RatingProperty = "System.Rating";

    private static readonly HashSet<string> Embeddable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".tif", ".tiff", ".heic", ".heif", ".jxr", ".wdp"
    };

    public static bool Embeds(string path) => Embeddable.Contains(Path.GetExtension(path));

    public static string Describe(ImageItem item)
    {
        if (item.RawPath != null)
            return $"Saved inside {Path.GetFileName(item.Path)} and to {Path.GetFileName(XmpSidecar.SidecarPath(item.RawPath))} for the RAW file.";
        if (Embeds(item.Path))
            return "Tags and rating are saved inside the file. Colour labels are kept in Ocula's library.";
        return $"Saved to {Path.GetFileName(XmpSidecar.SidecarPath(item.Path))} next to the file, so the original is never changed.";
    }

    public static async Task<UserData> ReadAsync(ImageItem item)
    {
        var tags = new List<string>();
        var rating = 0;
        string? label = null;

        foreach (var path in item.AllPaths)
        {
            if (Embeds(path))
            {
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(path);
                    var props = await file.Properties.RetrievePropertiesAsync(new[] { KeywordsProperty, RatingProperty });
                    if (props.TryGetValue(KeywordsProperty, out var k) && k is string[] keywords) tags.AddRange(keywords);
                    if (props.TryGetValue(RatingProperty, out var r) && r is uint stars) rating = Math.Max(rating, FromWindowsRating(stars));
                }
                catch
                {
                    // Unreadable metadata shouldn't stop the image from showing.
                }
            }
            else
            {
                var xmp = await Task.Run(() => XmpSidecar.Read(path));
                tags.AddRange(xmp.Tags);
                rating = Math.Max(rating, xmp.Rating);
                label ??= xmp.Label;
            }
        }

        var clean = tags.Where(t => !string.IsNullOrWhiteSpace(t))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
        return new UserData(clean, rating, label);
    }

    public static async Task WriteAsync(ImageItem item)
    {
        var tags = item.Tags.Select(t => t.Trim()).Where(t => t.Length > 0)
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        foreach (var path in item.AllPaths)
        {
            if (Embeds(path))
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                await file.Properties.SavePropertiesAsync(new Dictionary<string, object>
                {
                    [KeywordsProperty] = tags,
                    [RatingProperty] = ToWindowsRating(item.Rating)
                });
            }
            else
            {
                await Task.Run(() => XmpSidecar.Write(path, tags, item.Rating, item.Label));
            }
        }
    }

    // Windows stores ratings as 0–99; File Explorer shows 1, 25, 50, 75, 99 as 1–5 stars.
    private static int FromWindowsRating(uint value) =>
        value == 0 ? 0 : value < 13 ? 1 : value < 38 ? 2 : value < 63 ? 3 : value < 88 ? 4 : 5;

    private static uint ToWindowsRating(int stars) => stars switch
    {
        1 => 1u, 2 => 25u, 3 => 50u, 4 => 75u, 5 => 99u, _ => 0u
    };
}

public sealed record XmpData(List<string> Tags, int Rating, string? Label);

/// <summary>
/// XMP sidecar support: reads and writes keywords (dc:subject), xmp:Rating and xmp:Label
/// while keeping everything else another app may have written (develop settings etc.).
/// </summary>
public static class XmpSidecar
{
    private static readonly XNamespace X = "adobe:ns:meta/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";

    public static string SidecarPath(string imagePath) => Path.ChangeExtension(imagePath, ".xmp");

    public static XmpData Read(string imagePath)
    {
        var sidecar = SidecarPath(imagePath);
        if (!File.Exists(sidecar)) return new XmpData(new List<string>(), 0, null);
        try
        {
            var doc = XDocument.Load(sidecar);
            var tags = doc.Descendants(Dc + "subject").Descendants(Rdf + "li").Select(li => li.Value).ToList();

            // Apps write these either as attributes or as child elements.
            string? Value(XName name) =>
                doc.Descendants(Rdf + "Description").Select(d => (string?)d.Attribute(name) ?? (string?)d.Element(name))
                   .FirstOrDefault(v => v != null);

            var rating = int.TryParse(Value(Xmp + "Rating"), out var r) ? Math.Clamp(r, 0, 5) : 0;
            var label = Value(Xmp + "Label");
            return new XmpData(tags, rating, string.IsNullOrWhiteSpace(label) ? null : label);
        }
        catch
        {
            return new XmpData(new List<string>(), 0, null);
        }
    }

    public static void Write(string imagePath, IReadOnlyCollection<string> tags, int rating, string? label)
    {
        var sidecar = SidecarPath(imagePath);
        var doc = File.Exists(sidecar) ? XDocument.Load(sidecar) : NewDocument();

        var rdf = doc.Descendants(Rdf + "RDF").FirstOrDefault();
        if (rdf == null)
        {
            rdf = new XElement(Rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", Rdf));
            doc.Root!.Add(rdf);
        }

        var description = rdf.Elements(Rdf + "Description").FirstOrDefault();
        if (description == null)
        {
            description = new XElement(Rdf + "Description", new XAttribute(Rdf + "about", ""));
            rdf.Add(description);
        }

        if (description.GetPrefixOfNamespace(Dc) == null) description.Add(new XAttribute(XNamespace.Xmlns + "dc", Dc));
        if (description.GetPrefixOfNamespace(Xmp) == null) description.Add(new XAttribute(XNamespace.Xmlns + "xmp", Xmp));

        // Keywords
        description.Elements(Dc + "subject").Remove();
        if (tags.Count > 0)
        {
            description.Add(new XElement(Dc + "subject",
                new XElement(Rdf + "Bag", tags.Select(t => new XElement(Rdf + "li", t)))));
        }

        // Rating and label (SetAttributeValue with null removes the attribute)
        description.Elements(Xmp + "Rating").Remove();
        description.Elements(Xmp + "Label").Remove();
        description.SetAttributeValue(Xmp + "Rating", rating > 0 ? rating : null);
        description.SetAttributeValue(Xmp + "Label", label);

        // Write to a temp file first so a crash mid-save can't leave a half-written sidecar.
        var temp = sidecar + ".tmp";
        doc.Save(temp);
        File.Move(temp, sidecar, overwrite: true);
    }

    private static XDocument NewDocument() => new(
        new XElement(X + "xmpmeta",
            new XAttribute(XNamespace.Xmlns + "x", X),
            new XElement(Rdf + "RDF",
                new XAttribute(XNamespace.Xmlns + "rdf", Rdf),
                new XElement(Rdf + "Description",
                    new XAttribute(Rdf + "about", ""),
                    new XAttribute(XNamespace.Xmlns + "dc", Dc),
                    new XAttribute(XNamespace.Xmlns + "xmp", Xmp)))));
}
