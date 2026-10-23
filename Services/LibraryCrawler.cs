using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ocula.Models;

namespace Ocula.Services;

public sealed record CrawlStatus(bool Running, bool Paused, int Checked, int Updated, string? Folder);

/// <summary>
/// Catalogues every photo under the folders in the tree, in the background, so that library
/// search finds photos in folders you haven't opened yet and folders open instantly, which
/// matters most on a NAS, where reading files over the network is slow.
///
/// It's deliberately gentle: a few photos at a time on local drives, one at a time with
/// short pauses on network drives, and it can be paused. Unchanged photos are skipped
/// after a quick check, so later runs finish quickly.
/// </summary>
public sealed class LibraryCrawler
{
    private readonly IndexService _index;
    private CancellationTokenSource? _cts;
    private volatile bool _paused;
    private int _checked;
    private int _updated;

    public LibraryCrawler(IndexService index) => _index = index;

    /// <summary>Raised on a background thread; marshal to the UI before touching controls.</summary>
    public event Action<CrawlStatus>? StatusChanged;

    public bool IsRunning => _cts != null;
    public bool IsPaused => _paused;

    public void Start(IEnumerable<string> roots)
    {
        Stop();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _paused = false;
        var list = roots.ToList();
        _ = Task.Run(() => RunAsync(list, cts));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    public void Pause() { _paused = true; Report(null); }
    public void Resume() { _paused = false; Report(null); }

    public static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try
        {
            var root = Path.GetPathRoot(path);
            return root != null && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch
        {
            return false;
        }
    }

    private async Task RunAsync(List<string> roots, CancellationTokenSource cts)
    {
        var token = cts.Token;
        _checked = 0;
        _updated = 0;

        // Use only saved local cards. The crawler must never trigger online person lookups.
        List<ResolvedPersonCard> personCards;
        try
        {
            personCards = AppSettings.WritePersonCardTagsToFiles
                ? await _index.GetAllPersonCardsAsync()
                : new List<ResolvedPersonCard>();
        }
        catch
        {
            personCards = new List<ResolvedPersonCard>();
        }

        try
        {
            foreach (var root in roots)
            {
                foreach (var folder in Folders(root))
                {
                    token.ThrowIfCancellationRequested();
                    while (_paused) await Task.Delay(500, token);

                    List<ImageItem> items;
                    try { items = FolderScanner.Scan(folder); }
                    catch { continue; }
                    if (items.Count == 0) continue;

                    var state = await _index.GetFolderStateAsync(folder);
                    var stale = new List<ImageItem>();
                    foreach (var item in items)
                    {
                        _checked++;
                        var fresh = state.TryGetValue(item.Path, out var s) && s.Modified == item.Modified;
                        if (!fresh || (!s.Complete && !item.IsCloudOnly)) stale.Add(item);
                    }
                    Report(folder);

                    var network = IsNetworkPath(folder);
                    var batchSize = network ? 1 : 3;
                    for (var i = 0; i < stale.Count; i += batchSize)
                    {
                        token.ThrowIfCancellationRequested();
                        while (_paused) await Task.Delay(500, token);

                        await Task.WhenAll(stale.Skip(i).Take(batchSize)
                            .Select(async item =>
                            {
                                if (!item.IsCloudOnly && !item.IsVideo && personCards.Count > 0)
                                    await ApplyFolderPersonCardTagsAsync(item, personCards);
                                await Indexer.IndexAsync(item, _index, withThumbnail: true);
                            }));
                        _updated += Math.Min(batchSize, stale.Count - i);
                        if (i % 12 == 0) Report(folder);
                        if (network) await Task.Delay(25, token); // leave bandwidth for whatever you're doing
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return; // stopped or restarted; the new run reports its own status
        }
        finally
        {
            if (_cts == cts) _cts = null;
        }
        Report(null);
    }

    private void Report(string? folder) =>
        StatusChanged?.Invoke(new CrawlStatus(_cts != null, _paused, _checked, _updated, folder));

    /// <summary>
    /// Apply saved person-card keywords when a directory segment exactly matches a card name.
    /// This runs only for new/changed files found by the crawler and merges rather than replaces
    /// metadata. Ambiguous folders are deliberately left alone.
    /// </summary>
    private static async Task ApplyFolderPersonCardTagsAsync(ImageItem item, IReadOnlyList<ResolvedPersonCard> cards)
    {
        var folderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (DirectoryInfo? directory = new DirectoryInfo(item.Folder); directory != null; directory = directory.Parent)
            folderNames.Add(directory.Name);

        var matches = cards.Where(card =>
            folderNames.Contains(card.GroupName) ||
            folderNames.Contains(card.Facts.Name ?? string.Empty) ||
            folderNames.Contains(card.Facts.FullName ?? string.Empty))
            .ToList();

        if (matches.Count != 1) return;

        var match = matches[0];
        var existing = await MetadataStore.ReadAsync(item);
        var tags = existing.Tags.ToList();
        var additions = PersonCardTagBuilder.FromFacts(match.Facts, match.GroupName)
            .Where(tag => !tag.StartsWith("person:", StringComparison.OrdinalIgnoreCase));
        PersonCardTagBuilder.MergeInto(tags, new[] { match.GroupName }.Concat(additions));

        if (tags.Count == existing.Tags.Count) return;

        item.Tags = tags;
        item.Rating = existing.Rating;
        item.Label = existing.Label;
        await MetadataStore.WriteAsync(item);

        // Embedded metadata writes may update the file timestamp. Keep the index row aligned
        // with the file so the crawler doesn't immediately treat its own write as a new change.
        item.Modified = FolderScanner.ModifiedOf(item);
    }

    private static IEnumerable<string> Folders(string root)
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

            foreach (var child in children.OrderByDescending(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if ((child.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0)
                    stack.Push(child.FullName);
            }
        }
    }
}
