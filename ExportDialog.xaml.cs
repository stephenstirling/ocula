using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ocula.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Ocula;

public sealed partial class ExportDialog : ContentDialog
{
    private readonly IntPtr _windowHandle;
    private List<ExportPreset> _presets;

    /// <summary>The settings to export with, set when the user presses Export.</summary>
    public ExportPreset? Result { get; private set; }

    public ExportDialog(int imageCount, IntPtr windowHandle)
    {
        InitializeComponent();
        _windowHandle = windowHandle;
        CountText.Text = imageCount == 1 ? "Exporting 1 image." : $"Exporting {imageCount} images.";

        _presets = ExportPresetStore.Load();
        PresetBox.ItemsSource = _presets;
        var last = _presets.FindIndex(p => p.Name == AppSettings.LastExportPreset);
        PresetBox.SelectedIndex = Math.Max(0, last);
    }

    private void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetBox.SelectedItem is ExportPreset preset) Show(preset);
    }

    private void Show(ExportPreset p)
    {
        FormatBox.SelectedItem = p.Format;
        LongEdgeBox.Value = p.LongEdge;
        QualityBox.Value = p.Quality;
        RemoveLocationBox.IsOn = p.RemoveLocation;
        RemoveAllBox.IsOn = p.RemoveAllMetadata;
        UseRawBox.IsOn = p.UseRaw;
        WatermarkBox.Text = p.Watermark;
        SuffixBox.Text = p.Suffix;
        DestinationBox.Text = p.Destination;
    }

    private ExportPreset Read(string name) => new()
    {
        Name = name,
        Format = FormatBox.SelectedItem as string ?? "JPEG",
        LongEdge = double.IsNaN(LongEdgeBox.Value) ? 0u : (uint)Math.Max(0, LongEdgeBox.Value),
        Quality = (uint)QualityBox.Value,
        RemoveLocation = RemoveLocationBox.IsOn,
        RemoveAllMetadata = RemoveAllBox.IsOn,
        UseRaw = UseRawBox.IsOn,
        Watermark = WatermarkBox.Text,
        Suffix = SuffixBox.Text.Trim(),
        Destination = DestinationBox.Text.Trim()
    };

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null) DestinationBox.Text = folder.Path;
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        var name = PresetNameBox.Text.Trim();
        if (name.Length == 0) return;

        var preset = Read(name);
        var existing = _presets.FindIndex(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (existing >= 0) _presets[existing] = preset; else _presets.Add(preset);
        ExportPresetStore.Save(_presets);

        _presets = ExportPresetStore.Load();
        PresetBox.ItemsSource = _presets;
        PresetBox.SelectedIndex = _presets.FindIndex(p => p.Name == name);
        SavePresetFlyout.Hide();
    }

    private void OnExportClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var name = (PresetBox.SelectedItem as ExportPreset)?.Name ?? "Custom";
        var settings = Read(name);

        if (settings.Destination.Length == 0 || !Path.IsPathFullyQualified(settings.Destination))
        {
            ErrorText.Text = "Choose a folder to save the exported images to.";
            ErrorText.Visibility = Visibility.Visible;
            args.Cancel = true;
            return;
        }

        // Remember the destination with the preset, so "Web" keeps pointing at the same folder
        // (for example the website folder on the NAS).
        var index = _presets.FindIndex(p => p.Name == name);
        if (index >= 0)
        {
            _presets[index].Destination = settings.Destination;
            ExportPresetStore.Save(_presets);
        }
        AppSettings.LastExportPreset = name;
        Result = settings;
    }
}
