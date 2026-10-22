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
    private PeopleSharing? _peopleSharing;
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

        // "Share people with Porta": keeps people-index.json current while the switch is on, and removes a leftover file while it is off.
        _peopleSharing = new PeopleSharing(_index);
        if (AppSettings.SharePeopleWithPorta) _peopleSharing.RequestRefresh();
        else _ = _peopleSharing.RemoveAllAsync();
    }

    /// <summary>Called by Settings when "Share people with Porta" is switched on or off.</summary>
    public void PeopleSharingChanged()
    {
        if (_peopleSharing == null) return;
        if (AppSettings.SharePeopleWithPorta) _ = _peopleSharing.RefreshAsync();
        else _ = _peopleSharing.RemoveAllAsync();
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
        if (_peopleSharing != null) await _peopleSharing.RemoveAllAsync();   // the shared file and pictures go with the face data
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

    /// <summary>
    /// ocula:person?id=...: shows the photos of the person with that id (from people-index.json) in the grid, as "Show these photos" in
    /// People does. Says so if the person is gone: renamed to nothing, hidden, merged into another group, or face data deleted.
    /// </summary>
    public async Task OpenPersonLinkAsync(string? uid)
    {
        if (!FaceIndexer.Enabled)
        {
            ShowError("People is off", "Turn on People in Settings to open someone's photos.");
            return;
        }
        if (uid == null || await _index.FindPersonByUidAsync(uid) is not { } person)
        {
            ShowError("That person isn't in Ocula any more",
                      "They may have been renamed to nothing, hidden, merged into another group, or their face data deleted.");
            return;
        }
        var paths = await _index.GetPersonPhotoPathsAsync(person.Id);
        await ShowPhotosAsync(person.Name, await _index.GetRowsAsync(paths));
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
