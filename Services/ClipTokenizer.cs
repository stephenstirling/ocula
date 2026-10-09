using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ocula.Services;

/// <summary>
/// The text tokenizer CLIP expects (byte-level BPE, the same one OpenAI's CLIP uses), so a
/// search such as "sunset over water" becomes the 77 numbers the text model reads.
/// Reads vocab.json and merges.txt from the downloaded model files.
/// </summary>
public sealed class ClipTokenizer
{
    private const int StartToken = 49406;
    private const int EndToken = 49407;
    public const int Length = 77;

    private static readonly Regex Pattern = new(
        @"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|\p{L}+|\p{N}|[^\s\p{L}\p{N}]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<byte, char> ByteToChar = BuildByteMap();

    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<(string, string), int> _ranks = new();
    private readonly Dictionary<string, string[]> _cache = new();

    public ClipTokenizer(string vocabPath, string mergesPath)
    {
        _vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(vocabPath))
                 ?? throw new InvalidDataException("vocab.json is empty.");

        var rank = 0;
        foreach (var line in File.ReadLines(mergesPath).Skip(1)) // first line is a version header
        {
            var parts = line.Split(' ');
            if (parts.Length == 2) _ranks[(parts[0], parts[1])] = rank++;
        }
    }

    /// <summary>Returns exactly 77 token ids: start, the words, end, then zero padding.</summary>
    public long[] Encode(string text)
    {
        text = Regex.Replace(text, @"\s+", " ").Trim().ToLowerInvariant();

        var ids = new List<long> { StartToken };
        foreach (Match match in Pattern.Matches(text))
        {
            var mapped = new StringBuilder();
            foreach (var b in Encoding.UTF8.GetBytes(match.Value)) mapped.Append(ByteToChar[b]);
            foreach (var piece in Bpe(mapped.ToString()))
                if (_vocab.TryGetValue(piece, out var id)) ids.Add(id);
        }

        if (ids.Count > Length - 1) ids = ids.Take(Length - 1).ToList();
        ids.Add(EndToken);

        var result = new long[Length];
        for (var i = 0; i < ids.Count; i++) result[i] = ids[i];
        return result;
    }

    /// <summary>How many of the 77 slots hold real tokens (start + words + end).</summary>
    public static int UsedLength(long[] ids)
    {
        var n = Array.IndexOf(ids, (long)EndToken);
        return n < 0 ? ids.Length : n + 1;
    }

    private string[] Bpe(string token)
    {
        if (_cache.TryGetValue(token, out var cached)) return cached;

        var word = token.Select(c => c.ToString()).ToList();
        word[^1] += "</w>";

        while (word.Count > 1)
        {
            var bestRank = int.MaxValue;
            var bestIndex = -1;
            for (var i = 0; i < word.Count - 1; i++)
            {
                if (_ranks.TryGetValue((word[i], word[i + 1]), out var r) && r < bestRank)
                {
                    bestRank = r;
                    bestIndex = i;
                }
            }
            if (bestIndex < 0) break;

            var first = word[bestIndex];
            var second = word[bestIndex + 1];
            var merged = new List<string>();
            for (var i = 0; i < word.Count;)
            {
                if (i < word.Count - 1 && word[i] == first && word[i + 1] == second)
                {
                    merged.Add(first + second);
                    i += 2;
                }
                else
                {
                    merged.Add(word[i]);
                    i++;
                }
            }
            word = merged;
        }

        var result = word.ToArray();
        _cache[token] = result;
        return result;
    }

    // GPT-2 / CLIP map every byte to a printable character so the vocabulary has no raw whitespace.
    private static Dictionary<byte, char> BuildByteMap()
    {
        var printable = Enumerable.Range('!', '~' - '!' + 1)
            .Concat(Enumerable.Range(0xA1, 0xAC - 0xA1 + 1))
            .Concat(Enumerable.Range(0xAE, 0xFF - 0xAE + 1))
            .ToList();

        var map = new Dictionary<byte, char>();
        foreach (var b in printable) map[(byte)b] = (char)b;

        var extra = 0;
        for (var b = 0; b < 256; b++)
            if (!map.ContainsKey((byte)b)) map[(byte)b] = (char)(256 + extra++);
        return map;
    }
}
