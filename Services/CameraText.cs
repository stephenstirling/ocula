using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ocula.Services;

/// <summary>
/// Text about the camera: the names to look a camera up by, whether a search result is the
/// camera, and shutter speeds as people write them. Unit-tested (tests/Ocula.Tests/CameraTextTests.cs).
/// </summary>
public static class CameraText
{
    private const int MaxSearchNames = 6;

    /// <summary>
    /// Names to search for, most exact first: "Google Pixel 10 Pro XL", "Pixel 10 Pro XL",
    /// "Google Pixel 10 Pro", "Pixel 10 Pro", "Google Pixel 10", "Pixel 10". New phones and
    /// cameras often only have an entry for the base model. Every name keeps the model number,
    /// so "Google Pixel 10" never becomes just "Google Pixel".
    /// </summary>
    public static IReadOnlyList<string> SearchNames(string camera)
    {
        var words = camera.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var names = new List<string>();
        for (var count = words.Length; count >= 1; count--)
        {
            var name = string.Join(' ', words.Take(count));
            if (!name.Any(char.IsDigit)) break;
            names.Add(name);
            var withoutBrand = string.Join(' ', words.Skip(1).Take(count - 1));
            if (withoutBrand.Any(char.IsDigit)) names.Add(withoutBrand);
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxSearchNames).ToList();
    }

    /// <summary>The first word with a digit in it ("10", "R5", "ILCE-7M4"): a result must mention it.</summary>
    public static string? ModelNumber(string camera) =>
        camera.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(w => w.Any(char.IsDigit));

    /// <summary>Whether a title names the model: "Pixel 10" does for "10"; "Pixel 9" and "Pixel 10a" don't.</summary>
    public static bool Mentions(string? title, string? modelNumber) =>
        modelNumber == null ||
        (title ?? string.Empty).Split(WordBreaks, StringSplitOptions.RemoveEmptyEntries)
                               .Contains(modelNumber, StringComparer.OrdinalIgnoreCase);

    private static readonly char[] WordBreaks = { ' ', ',', '(', ')', '/', ':', ';', '"', '\'' };

    private static readonly string[] DeviceWords = { "camera", "phone", "tablet", "camcorder", "webcam", "drone", "unmanned aerial" };

    /// <summary>
    /// Whether a description is of a device that takes photos ("mirrorless camera", "Android
    /// smartphone by Google"), not a person, place or film with the same name.
    /// </summary>
    public static bool IsDevice(string? description) =>
        description != null && DeviceWords.Any(w => description.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>A shutter speed as cameras show it: "1/105 sec", "1/3 sec", "0.4 sec", "30 sec".</summary>
    public static string? Shutter(double seconds)
    {
        if (double.IsNaN(seconds) || seconds <= 0) return null;
        if (seconds >= 1) return seconds.ToString("0.#", CultureInfo.InvariantCulture) + " sec";

        var denominator = 1 / seconds;
        var rounded = Math.Round(denominator);
        return denominator >= 4 || Math.Abs(denominator - rounded) < 0.05
            ? $"1/{rounded.ToString(CultureInfo.InvariantCulture)} sec"
            : seconds.ToString("0.##", CultureInfo.InvariantCulture) + " sec";
    }

    private static readonly Regex RawShutter = new(@"\b(\d+)/(\d+) sec\b", RegexOptions.Compiled);

    /// <summary>
    /// Rewrites shutter speeds stored as raw fractions ("9539/1000000 sec", as some phones write
    /// them) in exposure text saved before Ocula formatted them itself.
    /// </summary>
    public static string TidyExposure(string exposure) =>
        RawShutter.Replace(exposure, m =>
            double.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var top) &&
            double.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var bottom) &&
            bottom > 0 && Shutter(top / bottom) is { } shutter
                ? shutter
                : m.Value);
}
