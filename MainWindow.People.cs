using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ocula.Services;
using Ocula.Models;
using Microsoft.UI.Xaml;

namespace Ocula;

/// <summary>
/// People in the main window: the face scanner running in the background, the People window,
/// and the helpers it uses to tag a person's photos or show them in the grid.
/// </summary>
public sealed partial class MainWindow
{
    private readonly FaceService _faceService = new();
    private FaceIndexer? _faceIndexer;
    private PeopleWindow? _people;
    private string _faceNote = string.Empty;

    /// <summary>The library database, for the People window.</summary>
    public IndexService Library => _index;

    private void InitialisePeople()
    {
        _faceIndexer = new FaceIndexer(_index, _faceService);
        _faceIndexer.StatusChanged += status => DispatcherQueue.TryEnqueue(() => ShowFaceStatus(status));
        _faceIndexer.GroupsChanged += () => DispatcherQueue.TryEnqueue(() => _people?.RefreshSoon());

        // Start after the library crawler and Smart recognition have had a head start. The timer is
        // kept in a field: a timer held only by a local variable can be collected before it fires, and
        // then People never started after a restart.
        _faceStartTimer = DispatcherQueue.CreateTimer();
        _faceStartTimer.Interval = TimeSpan.FromSeconds(40);
        _faceStartTimer.IsRepeating = false;
        _faceStartTimer.Tick += (_, _) => _faceIndexer?.Start();
        _faceStartTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _faceStartTimer;

    private void ShowFaceStatus(FaceStatus status)
    {
        _faceNote = !FaceIndexer.Enabled ? string.Empty
                  : status.Running && status.Waiting ? $" · People waiting for you to pause ({status.Done:N0} of {status.Total:N0})"
                  : status.Running ? $" · Finding faces {status.Done:N0} of {status.Total:N0}"
                  : string.Empty;
        CrawlText.Text = _crawlBase + _smartNote + _faceNote;
    }

    /// <summary>Called by Settings after People is switched on or off, or the models change.</summary>
    public void PeopleSettingsChanged()
    {
        _faceIndexer?.Stop();
        if (!FaceIndexer.Enabled)
        {
            _faceNote = string.Empty;
            CrawlText.Text = _crawlBase + _smartNote;
            return;
        }
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(500);
            _faceIndexer?.Start();
        });
    }

    /// <summary>
    /// Deletes every stored face and name (Settings and the People window) and turns People off.
    /// Off matters: the People window's dialog promises Ocula "will stop finding faces until you turn
    /// People on again", and with People still on, the next launch scanned every photo again and
    /// rebuilt the groups.
    /// </summary>
    public async Task DeleteFaceDataAsync()
    {
        // Wait for the photo being scanned to finish saving first; otherwise its faces were written after
        // the delete and left a stray face and scan record behind.
        if (_faceIndexer != null) await _faceIndexer.StopAsync();
        await _index.DeleteFaceDataAsync();
        AppSettings.PeopleEnabled = false;
        PeopleSettingsChanged();
    }

    private void OpenPeople()
    {
        if (_people == null)
        {
            _people = new PeopleWindow(this);
            _people.Closed += (_, _) => _people = null;
        }
        _people.Activate();
    }

    /// <summary>Adds a tag to the given library photos and writes it into the files.</summary>
    public async Task TagPhotosAsync(List<IndexRow> rows, string tag)
    {
        var items = rows.Select(row =>
        {
            var item = new ImageItem(row.Path, row.RawPath) { Modified = row.Modified };
            item.ApplyIndex(row);
            return item;
        }).ToList();
        await ApplyToItemsAsync(items, i => { if (!i.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))) i.Tags.Add(tag); });
    }

    /// <summary>Shows a person's photos in the grid.</summary>
    public async Task ShowPhotosAsync(string name, List<IndexRow> rows)
    {
        await OpenRowsAsync(rows, ViewKind.Search, 0, name, "photos of this person (as grouped by you)");
        Activate();
    }
}
