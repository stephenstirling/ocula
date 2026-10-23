using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Ocula.Models;

namespace Ocula.Services;

public sealed record PersonFolderTagPreview(int Scanned, int Changes, int Ambiguous, int Unmatched, IReadOnlyList<string> Examples);

/// <summary>
/// Plans and applies local person-card keyword changes. Preview never writes metadata. The most
/// recent apply operation is journaled so it can be undone from the settings window.
/// </summary>
public sealed class PersonFolderTaggingService
{
    private sealed record Rule(string Folder, string CardName);
    private sealed record UndoEntry(string Path, string? RawPath, List<string> Tags, int Rating, string? Label);
    private sealed record Journal(List<UndoEntry> Entries);
    private sealed record Planned(ImageItem Item, UserData Before, List<string> AfterTags, string CardName);

    private readonly IndexService _index;
    private readonly string _journalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ocula", "person-folder-tagging-undo.json");
    private List<Planned> _pending = new();

    public PersonFolderTaggingService(IndexService index) => _index = index;

    public bool CanUndo => File.Exists(_journalPath);

    public async Task<PersonFolderTagPreview> PreviewAsync(
        IEnumerable<string> roots, IReadOnlyList<ResolvedPersonCard> cards, string rulesText, bool includeParents)
    {
        _pending.Clear();
        var rules = ParseRules(rulesText);
        var scanned = 0;
        var ambiguous = 0;
        var unmatched = 0;

        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var folder in EnumerateFolders(root))
            {
                List<ImageItem> items;
                try { items = FolderScanner.Scan(folder); }
                catch { continue; }
                Dictionary<string, IndexRow> indexedRows;
                try { indexedRows = await _index.GetFolderAsync(folder); }
                catch { indexedRows = new Dictionary<string, IndexRow>(StringComparer.OrdinalIgnoreCase); }

                foreach (var item in items)
                {
                    scanned++;
                    if (item.IsCloudOnly || item.IsVideo || SupportedFormats.IsPhotoshop(item.Path)) continue;

                    var selection = ResolveCard(item, cards, rules, includeParents);
                    if (selection.Ambiguous) { ambiguous++; continue; }
                    if (selection.Card == null) { unmatched++; continue; }

                    UserData before;
                    try { before = await MetadataStore.ReadAsync(item); }
                    catch { continue; }
                    if (indexedRows.TryGetValue(item.Path, out var indexed))
                        before = new UserData(before.Tags, before.Rating > 0 ? before.Rating : indexed.Rating, before.Label ?? indexed.Label);

                    var after = before.Tags.ToList();
                    var additions = PersonCardTagBuilder.FromFacts(selection.Card.Facts, selection.Card.GroupName)
                        .Where(tag => !tag.StartsWith("person:", StringComparison.OrdinalIgnoreCase));
                    PersonCardTagBuilder.MergeInto(after, new[] { selection.Card.GroupName }.Concat(additions));
                    if (after.SequenceEqual(before.Tags, StringComparer.OrdinalIgnoreCase)) continue;

                    item.Tags = after;
                    item.Rating = before.Rating;
                    item.Label = before.Label;
                    _pending.Add(new Planned(item, before, after, selection.Card.GroupName));
                }
            }
        }

        var examples = _pending.Take(12)
            .Select(p => $"{p.Item.Path}  →  {p.CardName} (+{p.AfterTags.Count - p.Before.Tags.Count} keywords)")
            .ToList();
        return new PersonFolderTagPreview(scanned, _pending.Count, ambiguous, unmatched, examples);
    }

    public async Task<int> ApplyPreviewAsync()
    {
        if (_pending.Count == 0) return 0;
        Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
        var journal = new Journal(new List<UndoEntry>());
        await SaveJournalAsync(journal);

        var applied = 0;
        foreach (var plan in _pending)
        {
            // Persist the previous state before touching the file. If a write fails or the app
            // exits unexpectedly, undo can safely restore this snapshot.
            journal.Entries.Add(new UndoEntry(plan.Item.Path, plan.Item.RawPath,
                plan.Before.Tags.ToList(), plan.Before.Rating, plan.Before.Label));
            await SaveJournalAsync(journal);

            plan.Item.Tags = plan.AfterTags.ToList();
            plan.Item.Rating = plan.Before.Rating;
            plan.Item.Label = plan.Before.Label;
            await MetadataStore.WriteAsync(plan.Item);
            plan.Item.Modified = FolderScanner.ModifiedOf(plan.Item);
            plan.Item.Indexed = false;
            await Indexer.IndexAsync(plan.Item, _index, withThumbnail: false);
            plan.Item.Label = plan.Before.Label; // embedded formats keep labels in Ocula's index
            await _index.SaveAsync(plan.Item.ToRow());
            applied++;
        }

        _pending.Clear();
        return applied;
    }

    public async Task<int> UndoLastAsync()
    {
        if (!File.Exists(_journalPath)) return 0;
        Journal? journal;
        try { journal = JsonSerializer.Deserialize<Journal>(await File.ReadAllTextAsync(_journalPath)); }
        catch { return 0; }
        if (journal == null || journal.Entries.Count == 0) return 0;

        var restored = 0;
        foreach (var entry in journal.Entries.AsEnumerable().Reverse())
        {
            try
            {
                if (!File.Exists(entry.Path)) continue;
                var item = new ImageItem(entry.Path, entry.RawPath)
                {
                    Tags = entry.Tags.ToList(),
                    Rating = entry.Rating,
                    Label = entry.Label
                };
                await MetadataStore.WriteAsync(item);
                item.Modified = FolderScanner.ModifiedOf(item);
                await Indexer.IndexAsync(item, _index, withThumbnail: false);
                item.Label = entry.Label;
                await _index.SaveAsync(item.ToRow());
                restored++;
            }
            catch
            {
                // Keep the journal intact so the user can retry a partial undo.
            }
        }

        if (restored == journal.Entries.Count) File.Delete(_journalPath);
        return restored;
    }

    public static (ResolvedPersonCard? Card, bool Ambiguous) ResolveCardForItem(ImageItem item, IReadOnlyList<ResolvedPersonCard> cards) =>
        ResolveCard(item, cards, ParseRules(AppSettings.PersonFolderTagRules), AppSettings.PersonFolderTagIncludeSubfolders);

    private static (ResolvedPersonCard? Card, bool Ambiguous) ResolveCard(
        ImageItem item, IReadOnlyList<ResolvedPersonCard> cards, IReadOnlyList<Rule> rules, bool includeParents)
    {
        var directory = new DirectoryInfo(item.Folder);
        var paths = new List<string> { Path.GetFullPath(directory.FullName).TrimEnd(Path.DirectorySeparatorChar) };
        if (includeParents)
            for (var parent = directory.Parent; parent != null; parent = parent.Parent)
                paths.Add(Path.GetFullPath(parent.FullName).TrimEnd(Path.DirectorySeparatorChar));

        // An explicit rule takes precedence. The closest matching folder wins; conflicting rules
        // at that same depth are treated as ambiguous rather than guessed.
        var matchingRules = rules.SelectMany(rule =>
        {
            var depth = paths.FindIndex(p => string.Equals(p, rule.Folder, StringComparison.OrdinalIgnoreCase));
            return depth < 0 ? Array.Empty<(Rule Rule, int Depth)>() : new[] { (rule, depth) };
        }).ToList();

        if (matchingRules.Count > 0)
        {
            var closestDepth = matchingRules.Min(r => r.Depth);
            var names = matchingRules.Where(r => r.Depth == closestDepth)
                .Select(r => r.Rule.CardName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count != 1) return (null, true);
            var wanted = names[0];
            var resolved = cards.Where(c =>
                NameEquals(c.GroupName, wanted) || NameEquals(c.Facts.Name, wanted) ||
                NameEquals(c.Facts.FullName, wanted)).DistinctBy(c => c.PersonId).ToList();
            return resolved.Count switch
            {
                1 => (resolved[0], false),
                > 1 => (null, true),
                _ => (null, true) // a bad explicit rule must not silently fall back to a different person
            };
        }

        var folderNames = new HashSet<string>(paths.Select(p => Path.GetFileName(p) ?? string.Empty).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);
        var matches = cards.Where(card =>
            folderNames.Contains(card.GroupName) ||
            folderNames.Contains(card.Facts.Name ?? string.Empty) ||
            folderNames.Contains(card.Facts.FullName ?? string.Empty))
            .DistinctBy(card => card.PersonId).ToList();
        return matches.Count switch
        {
            1 => (matches[0], false),
            > 1 => (null, true),
            _ => (null, false)
        };
    }

    private static bool NameEquals(string? a, string? b) =>
        string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static List<Rule> ParseRules(string text)
    {
        var rules = new List<Rule>();
        foreach (var line in (text ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
            var separator = trimmed.IndexOf("=>", StringComparison.Ordinal);
            if (separator <= 0 || separator + 2 >= trimmed.Length) continue;
            var folder = trimmed[..separator].Trim().Trim('"');
            var card = trimmed[(separator + 2)..].Trim().Trim('"');
            if (folder.Length == 0 || card.Length == 0) continue;
            try { folder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar); }
            catch { continue; }
            rules.Add(new Rule(folder, card));
        }
        return rules;
    }

    private static IEnumerable<string> EnumerateFolders(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var folder = stack.Pop();
            yield return folder;
            DirectoryInfo[] children;
            try { children = new DirectoryInfo(folder).GetDirectories(); }
            catch { continue; }
            foreach (var child in children.OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if ((child.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0)
                        stack.Push(child.FullName);
                }
                catch { }
            }
        }
    }

    private async Task SaveJournalAsync(Journal journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
        var temporary = _journalPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(journal));
        File.Move(temporary, _journalPath, overwrite: true);
    }
}
