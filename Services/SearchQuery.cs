using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Ocula.Models;
using Microsoft.Data.Sqlite;

namespace Ocula.Services;

/// <summary>
/// Turns what's typed in the search box into a library query. Plain words match file names,
/// tags, camera models and, for Capta captures, the source app, window title and the text in
/// the image (full-text search: whole words or the start of one); filters narrow it down and can be combined freely:
///
///   camera:R5          tag:wedding         tag:"smith wedding"   label:red
///   rating>=4          rating:5            stars:3               (also >, &lt;, &lt;=)
///   year:2025          date:2026-09        date:2026-09-26
///   hdr                raw                 gps                   folder:Lisbon
///   capture            hastext             app:Terminal          (Capta captures)
///   text:invoice       text:"order confirmation"                  (only the text found in a screenshot)
///
/// Every condition must match (AND). Values are passed as SQL parameters, never pasted in.
/// </summary>
public sealed class SearchQuery
{
    private static readonly Regex Token = new(@"(?<key>[a-z]+)?(?<op>:|>=|<=|>|<)?(?:""(?<q>[^""]*)""|(?<v>\S+))",
                                              RegexOptions.IgnoreCase);

    private readonly List<(string Key, string Op, string Value)> _filters = new();
    private readonly List<string> _words = new();

    public string Text { get; }

    public bool IsEmpty => _filters.Count == 0 && _words.Count == 0;

    public SearchQuery(string text)
    {
        Text = text.Trim();
        foreach (Match m in Token.Matches(Text))
        {
            var key = m.Groups["key"].Value.ToLowerInvariant();
            var op = m.Groups["op"].Value;
            var value = m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["v"].Value;

            if (op.Length > 0 && IsKnownKey(key)) _filters.Add((key, op, value));
            else if (op.Length == 0 && key.Length > 0 && value.Length == 0 && IsFlag(key)) _filters.Add((key, "", ""));
            else
            {
                // Not a filter: treat the whole thing as a word to look for.
                var word = (key + op + value).Trim();
                if (IsFlag(word.ToLowerInvariant())) _filters.Add((word.ToLowerInvariant(), "", ""));
                else if (word.Length > 0) _words.Add(word);
            }
        }
    }

    private static bool IsKnownKey(string key) =>
        key is "camera" or "tag" or "label" or "rating" or "stars" or "year" or "date" or "folder" or "app" or "text";

    private static bool IsFlag(string key) =>
        key is "hdr" or "raw" or "gps" or "soft" or "sharp" or "picked" or "rejected" or "video" or "capture" or "hastext";

    /// <summary>Builds the WHERE clause and adds its parameters to the command.</summary>
    public string ToSql(SqliteCommand cmd)
    {
        var parts = new List<string>();
        var n = 0;
        string Param(object value)
        {
            var name = "$p" + n++;
            cmd.Parameters.AddWithValue(name, value);
            return name;
        }

        // Whether the text Capta found in screenshots is searched (Settings > Privacy); the app and window title always are.
        var includeText = AppSettings.CaptureTextSearch;

        // LIKE with the typed text's own % and _ escaped, so they match themselves.
        string Like(string column, string value) => $"{column} LIKE {Param($"%{CaptureTextIndex.EscapeLike(value)}%")} ESCAPE '\\'";

        foreach (var word in _words)
        {
            // File name, tags and camera by substring; a Capta screenshot's app, window title and text by whole
            // word or start of a word (FTS5). The word is quoted whole, so nothing typed acts as an FTS5 operator.
            var clause = $"{Like("path", word)} OR {Like("tags", word)} OR {Like("camera", word)}";
            if (CaptureTextIndex.MatchExpression(word, includeText) is { } match)
                clause += $" OR path IN (SELECT path FROM capture_fts WHERE capture_fts MATCH {Param(match)})";
            parts.Add($"({clause})");
        }

        foreach (var (key, op, value) in _filters)
        {
            switch (key)
            {
                case "camera": parts.Add(Like("camera", value)); break;
                case "tag": parts.Add(Like("tags", value)); break;
                case "folder": parts.Add(Like("folder", value)); break;
                case "app": parts.Add(Like("capture", value)); break;
                case "capture": parts.Add("capture IS NOT NULL"); break;
                case "hastext": parts.Add(includeText ? "ocr IS NOT NULL AND ocr <> ''" : "1 = 0"); break;
                case "text":
                    // Only the text found in the image, not the app or window title.
                    if (includeText && CaptureTextIndex.TextOnlyExpression(value) is { } textMatch)
                        parts.Add($"path IN (SELECT path FROM capture_fts WHERE capture_fts MATCH {Param(textMatch)})");
                    else
                        parts.Add("1 = 0");
                    break;
                case "label":
                    parts.Add($"label = {Param(Capitalise(value))}");
                    break;
                case "rating":
                case "stars":
                    if (int.TryParse(value, out var stars))
                    {
                        var sqlOp = op switch { ">=" => ">=", "<=" => "<=", ">" => ">", "<" => "<", _ => "=" };
                        parts.Add($"rating {sqlOp} {Param(stars)}");
                    }
                    break;
                case "year":
                case "date":
                    // Dates are stored as ISO 8601 text, so a prefix match covers year, month or day.
                    parts.Add($"taken LIKE {Param(value + "%")}");
                    break;
                case "hdr": parts.Add("hdr IS NOT NULL"); break;
                case "soft": parts.Add($"sharp < {SharpnessService.SoftThreshold}"); break;
                case "sharp": parts.Add($"sharp >= {SharpnessService.SoftThreshold}"); break;
                case "picked": parts.Add("flag = 1"); break;
                case "video":
                    parts.Add("(" + string.Join(" OR ", SupportedFormats.Video.Select(ext => $"path LIKE '%{ext}'")) + ")");
                    break;
                case "rejected": parts.Add("flag = -1"); break;
                case "gps": parts.Add("lat IS NOT NULL"); break;
                case "raw":
                    var rawExtensions = SupportedFormats.Raw.Select(ext => $"path LIKE '%{ext}'");
                    parts.Add($"(raw IS NOT NULL OR {string.Join(" OR ", rawExtensions)})");
                    break;
            }
        }

        return parts.Count == 0 ? "1 = 1" : string.Join(" AND ", parts);
    }

    /// <summary>A short description of the search, e.g. "4★ and up · camera R5 · “wedding”".</summary>
    public string Describe()
    {
        var bits = new List<string>();
        foreach (var (key, op, value) in _filters)
        {
            bits.Add(key switch
            {
                "rating" or "stars" => op is ">=" or ">" ? $"{value}★ and up" : $"{value}★",
                "hdr" => "HDR",
                "raw" => "RAW",
                "gps" => "with location",
                "soft" => "soft focus",
                "sharp" => "in focus",
                "picked" => "picked",
                "video" => "videos",
                "rejected" => "rejected",
                "capture" => "Capta captures",
                "hastext" => "with text",
                _ => $"{key} {value}"
            });
        }
        bits.AddRange(_words.Select(w => $"“{w}”"));
        return string.Join(" · ", bits);
    }

    private static string Capitalise(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
}
