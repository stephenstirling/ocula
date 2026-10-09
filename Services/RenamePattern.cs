using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ocula.Services;

public sealed record RenameContext(string OriginalName, DateTime? Taken, string? Camera, string? EventName, int Sequence);

/// <summary>
/// File and folder naming patterns, shared by import and batch rename. Tokens:
///   {name}  original name        {seq} 0001 (or {seq:3} for 001)
///   {date}  2026-09-26           {time} 174206
///   {year} {month} {day}         {camera} EOS-R5     {event} Smith-wedding
/// Anything else is kept as typed. Characters Windows doesn't allow in file names are removed.
/// </summary>
public static class RenamePattern
{
    public const string DefaultFileName = "{date}_{event}_{seq}";
    public const string DefaultFolder = @"{year}\{date} {event}";

    private static readonly Regex Token = new(@"\{(\w+)(?::(\d+))?\}");

    public static string FileName(string pattern, RenameContext c) => Clean(Expand(pattern, c), allowFolders: false, c);

    public static string Folder(string pattern, RenameContext c) => Clean(Expand(pattern, c), allowFolders: true, c);

    private static string Expand(string pattern, RenameContext c)
    {
        var taken = c.Taken ?? DateTime.Now;
        return Token.Replace(pattern, m => m.Groups[1].Value.ToLowerInvariant() switch
        {
            "name" => c.OriginalName,
            "date" => taken.ToString("yyyy-MM-dd"),
            "time" => taken.ToString("HHmmss"),
            "year" => taken.ToString("yyyy"),
            "month" => taken.ToString("MM"),
            "day" => taken.ToString("dd"),
            "camera" => Slug(c.Camera),
            "event" => Slug(c.EventName),
            "seq" => c.Sequence.ToString(new string('0', m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 4)),
            _ => m.Value
        });
    }

    private static string Slug(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : Regex.Replace(text.Trim(), @"\s+", "-");

    private static string Clean(string text, bool allowFolders, RenameContext c)
    {
        var invalid = Path.GetInvalidFileNameChars().Where(ch => !(allowFolders && ch == '\\')).ToHashSet();
        var cleaned = new string(text.Where(ch => !invalid.Contains(ch)).ToArray());

        // Tidy up separators left behind by empty tokens, e.g. "2026-09-26__0001" or "2026-09-26 ".
        cleaned = Regex.Replace(cleaned, @"([_\- ]){2,}", "$1");
        var parts = cleaned.Split('\\').Select(p => p.Trim(' ', '_', '-', '.')).Where(p => p.Length > 0);
        cleaned = string.Join('\\', parts);
        return cleaned.Length > 0 ? cleaned : c.OriginalName;
    }
}
