using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Ocula.Models;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Ocula.Services;

public sealed record CardInfo(string Root, string Label);

/// <summary>One photo (or RAW+JPEG pair, or video) on a memory card.</summary>
public sealed class ImportItem : INotifyPropertyChanged
{
    private BitmapImage? _thumbnail;

    public required List<string> Files { get; init; }
    public required string Stem { get; init; }
    public required DateTime Taken { get; init; }
    public required long Size { get; init; }
    public required string Key { get; init; }
    public string? Camera { get; init; }
    public bool AlreadyImported { get; set; }
    public bool ThumbnailRequested { get; set; }

    public string MainFile => Files.FirstOrDefault(f => !SupportedFormats.IsRaw(f) && !ImportService.IsVideo(f)) ?? Files[0];
    public string Caption => Stem + (Files.Count > 1 ? " · RAW+JPG" : ImportService.IsVideo(Files[0]) ? " · video" : string.Empty);

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record ImportOptions(
    string DestinationRoot, string FolderPattern, string EventName,
    bool Rename, string NamePattern, IReadOnlyList<string> Tags,
    string? BackupRoot, bool Eject);

public sealed record ImportResult(int Imported, IReadOnlyList<string> Folders, IReadOnlyList<string> Failed);

/// <summary>
/// Finds camera cards, lists what's on them, and copies photos into the library.
/// Copies are checked for the right size, keep the original file dates, and are recorded
/// so the same photo isn't imported twice, even after the card is reformatted and reused.
/// </summary>
public static class ImportService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".mts", ".m2ts", ".avi", ".crm"
    };

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    /// <summary>Removable drives with a DCIM folder (how cameras lay out their cards).</summary>
    public static List<CardInfo> FindCards()
    {
        var cards = new List<CardInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Removable || !drive.IsReady) continue;
                if (!Directory.Exists(Path.Combine(drive.RootDirectory.FullName, "DCIM"))) continue;
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name.TrimEnd('\\') : drive.VolumeLabel;
                cards.Add(new CardInfo(drive.RootDirectory.FullName, label));
            }
            catch
            {
                // Drive removed while checking.
            }
        }
        return cards;
    }

    /// <summary>Lists photos and videos on the card, pairing RAW+JPEG files by name.</summary>
    public static List<ImportItem> Scan(CardInfo card, CancellationToken token)
    {
        var dcim = Path.Combine(card.Root, "DCIM");
        var files = Directory.EnumerateFiles(dcim, "*", SearchOption.AllDirectories)
            .Where(f => SupportedFormats.IsSupported(f) || IsVideo(f))
            .Where(f => !Path.GetFileName(f).StartsWith('.'))
            .ToList();

        var items = new List<ImportItem>();
        foreach (var group in files.GroupBy(f => Path.Combine(Path.GetDirectoryName(f)!, Path.GetFileNameWithoutExtension(f)),
                                             StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var groupFiles = group.OrderBy(f => SupportedFormats.IsRaw(f) ? 1 : 0).ToList();
            var main = groupFiles[0];
            var info = IsVideo(main) ? null : ExifService.Read(main);
            var taken = info?.Taken ?? File.GetLastWriteTime(main);
            var size = groupFiles.Sum(f => new FileInfo(f).Length);

            items.Add(new ImportItem
            {
                Files = groupFiles,
                Stem = Path.GetFileNameWithoutExtension(main),
                Taken = taken,
                Size = size,
                Camera = info == null ? null : ExifService.DisplayName(info),
                // Name + size + capture time identifies a photo even after the card is reused.
                Key = $"{Path.GetFileName(main)}|{new FileInfo(main).Length}|{taken.Ticks}"
            });
        }
        return items.OrderBy(i => i.Taken).ToList();
    }

    public static async Task<ImportResult> ImportAsync(IReadOnlyList<ImportItem> items, ImportOptions o, IndexService index,
                                                       IProgress<(int Done, int Total)> progress, CancellationToken token)
    {
        var folders = new List<string>();
        var failed = new List<string>();
        var sequence = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var done = 0;

        foreach (var item in items.OrderBy(i => i.Taken))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var context = new RenameContext(item.Stem, item.Taken, item.Camera, o.EventName, 1);
                var relativeFolder = RenamePattern.Folder(o.FolderPattern, context);
                var folder = Path.Combine(o.DestinationRoot, relativeFolder);
                Directory.CreateDirectory(folder);
                if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) folders.Add(folder);

                // Pick a name that's free for every file in the item (so pairs stay matched).
                var stem = item.Stem;
                var seq = sequence.GetValueOrDefault(folder, 0);
                for (var attempt = 0; attempt < 10000; attempt++)
                {
                    seq++;
                    stem = o.Rename
                        ? RenamePattern.FileName(o.NamePattern, context with { Sequence = seq })
                        : attempt == 0 ? item.Stem : $"{item.Stem}_{attempt + 1}";
                    var candidate = stem;
                    if (item.Files.All(f => !File.Exists(Path.Combine(folder, candidate + Path.GetExtension(f))))) break;
                }
                sequence[folder] = seq;

                var copied = new List<string>();
                foreach (var source in item.Files)
                {
                    var target = Path.Combine(folder, stem + Path.GetExtension(source));
                    await CopyAsync(source, target, token);
                    copied.Add(target);

                    if (!string.IsNullOrWhiteSpace(o.BackupRoot))
                    {
                        var backupFolder = Path.Combine(o.BackupRoot, relativeFolder);
                        Directory.CreateDirectory(backupFolder);
                        await CopyAsync(source, Path.Combine(backupFolder, Path.GetFileName(target)), token);
                    }
                }

                if (o.Tags.Count > 0)
                {
                    var main = copied.FirstOrDefault(SupportedFormats.IsPairable) ?? copied[0];
                    var raw = copied.FirstOrDefault(SupportedFormats.IsRaw);
                    if (!IsVideo(main))
                    {
                        var photo = new ImageItem(main, raw != null && raw != main ? raw : null) { Tags = o.Tags.ToList() };
                        await MetadataStore.WriteAsync(photo);
                    }
                }

                await index.MarkImportedAsync(item.Key);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed.Add($"{item.Stem}: {ex.Message}");
            }
            progress.Report((++done, items.Count));
        }

        return new ImportResult(items.Count - failed.Count, folders, failed);
    }

    private static async Task CopyAsync(string source, string target, CancellationToken token)
    {
        const int buffer = 1 << 20;
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, buffer,
                                                FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer,
                                                 FileOptions.Asynchronous))
        {
            await input.CopyToAsync(output, buffer, token);
        }

        // Check the copy is complete before the card can be wiped, and keep the original dates.
        if (new FileInfo(target).Length != new FileInfo(source).Length)
            throw new IOException("The copy is incomplete.");
        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
        File.SetCreationTimeUtc(target, File.GetCreationTimeUtc(source));
    }

    /// <summary>Safely ejects a drive, like "Eject" in File Explorer. Call on the UI thread.</summary>
    public static bool Eject(string driveRoot)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return false;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic thisPc = shell.NameSpace(17); // "This PC"
            dynamic drive = thisPc.ParseName(driveRoot);
            drive.InvokeVerb("Eject");
            return true;
        }
        catch
        {
            return false;
        }
    }
}
