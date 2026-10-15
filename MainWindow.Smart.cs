using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Ocula.Services;
using Ocula.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ocula;

/// <summary>
/// Smart recognition in the main window: describe-it search ("? sunset over water"), tag
/// suggestions for the selected photo, and the background analysis status. All of it is off
/// until the user turns it on in Settings and the model has been downloaded.
/// </summary>
public sealed partial class MainWindow
{
    private readonly SmartService _smart = new();
    private SmartIndexer? _smartIndexer;
    private readonly ObservableCollection<string> _suggested = new();
    private string _crawlBase = string.Empty;
    private string _smartNote = string.Empty;

    private void InitialiseSmart()
    {
        SuggestList.ItemsSource = _suggested;
        _smartIndexer = new SmartIndexer(_index, _smart);
        _smartIndexer.StatusChanged += status => DispatcherQueue.TryEnqueue(() => ShowSmartStatus(status));

        // Start after the library crawler has had a head start. Kept in a field so it can't be collected
        // before it fires (see InitialisePeople).
        _smartStartTimer = DispatcherQueue.CreateTimer();
        _smartStartTimer.Interval = TimeSpan.FromSeconds(20);
        _smartStartTimer.IsRepeating = false;
        _smartStartTimer.Tick += (_, _) => _smartIndexer?.Start();
        _smartStartTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _smartStartTimer;

    /// <summary>Called by Settings after Smart recognition is switched on or off, or the model changes.</summary>
    public void SmartSettingsChanged()
    {
        _smartIndexer?.Stop();
        _suggested.Clear();
        SuggestPanel.Visibility = Visibility.Collapsed;
        if (!SmartService.Enabled)
        {
            _smartNote = string.Empty;
            CrawlText.Text = _crawlBase;
            return;
        }
        // Give a cancelled run a moment to finish, then start again.
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(500);
            _smartIndexer?.Start();
            if (_current != null) _ = UpdateSmartSuggestionsAsync(_current, _selectionVersion);
        });
    }

    /// <summary>Deletes every stored embedding (used by Settings > Delete recognition data).</summary>
    public async Task DeleteSmartDataAsync()
    {
        _smartIndexer?.Stop();
        await _index.DeleteEmbeddingsAsync();
        _smart.NotifyChanged();
    }

    private void ShowSmartStatus(SmartStatus status)
    {
        _smartNote = !SmartService.Enabled ? string.Empty
                   : status.Running && status.Waiting ? $" · Smart recognition waiting for you to pause ({status.Done:N0} of {status.Total:N0})"
                   : status.Running ? $" · Analysing photos {status.Done:N0} of {status.Total:N0}"
                   : status.Total > 0 ? $" · Smart recognition ready ({status.Done:N0} photos)" : string.Empty;
        CrawlText.Text = _crawlBase + _smartNote + _faceNote;
    }

    // ================= Describe-it search =================

    /// <summary>Handles "? some words". Returns true if the text was a smart search.</summary>
    private async Task<bool> TrySmartSearchAsync(string text)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith('?')) return false;

        var words = trimmed[1..].Trim();
        if (words.Length == 0) return true;

        if (!SmartService.Enabled)
        {
            ShowError("Smart recognition is off", "Turn it on in Settings > Smart recognition. Ocula downloads a small model once and then works offline.");
            return true;
        }

        var matches = await _smart.SearchAsync(words, _index);
        var rows = await _index.GetRowsAsync(matches.Select(m => m.Path).ToList());
        var note = rows.Count == 0 ? "no close matches yet (photos are still being analysed)" : "best matches first";
        await OpenRowsAsync(rows, ViewKind.Search, 0, "Smart search", $"“{words}” · {note}");
        SaveSearchButton.Visibility = Visibility.Collapsed; // a "describe it" search can't be saved as a smart album
        return true;
    }

    // ================= Suggested tags =================

    private async Task UpdateSmartSuggestionsAsync(ImageItem item, int version)
    {
        _suggested.Clear();
        SuggestPanel.Visibility = Visibility.Collapsed;
        if (!SmartService.Enabled || item.IsVideo || item.IsCloudOnly) return;

        try
        {
            if (!await _smart.LoadAsync() || version != _selectionVersion) return;

            var vector = await _index.GetEmbeddingAsync(item.Path, _smart.ModelId);
            if (vector == null)
            {
                // Not analysed yet: do this one now so the suggestions don't wait for the background pass.
                var bytes = await ThumbnailService.GetBytesAsync(item.Path, 448, allowDecode: true, highPriority: true);
                if (bytes == null) return;
                vector = await Task.Run(() => _smart.EncodeImage(bytes));
                await _index.SaveEmbeddingAsync(item.Path, item.Modified, _smart.ModelId, vector);
            }
            if (version != _selectionVersion) return;

            var suggestions = await Task.Run(() => _smart.SuggestTags(vector));
            foreach (var s in suggestions)
                if (!item.Tags.Contains(s.Label, StringComparer.OrdinalIgnoreCase)) _suggested.Add(s.Label);
            SuggestPanel.Visibility = _suggested.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            // Suggestions are a nicety; never interrupt browsing for them.
        }
    }

    private async void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || _current == null) return;
        _suggested.Remove(tag);
        if (_suggested.Count == 0) SuggestPanel.Visibility = Visibility.Collapsed;

        await ApplyToSelectionAsync(i =>
        {
            if (!i.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) i.Tags.Add(tag);
        });
    }

    private async void AddAllSuggestions_Click(object sender, RoutedEventArgs e)
    {
        var tags = _suggested.ToList();
        _suggested.Clear();
        SuggestPanel.Visibility = Visibility.Collapsed;
        await ApplyToSelectionAsync(i =>
        {
            foreach (var tag in tags)
                if (!i.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) i.Tags.Add(tag);
        });
    }
}
