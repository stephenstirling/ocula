using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Ocula.Models;

namespace Ocula.Services;

/// <summary>An external editor. RAW-capable editors get the RAW file of a RAW+JPEG pair.</summary>
public sealed record Editor(string Name, string ExePath, bool PrefersRaw);

/// <summary>Finds installed photo editors and opens photos in them.</summary>
public static class EditorLauncher
{
    // Common install locations. Wildcards cover version numbers in folder and file names.
    private static readonly (string Name, string Pattern, bool Raw)[] Known =
    {
        ("Lightroom Classic", @"%ProgramFiles%\Adobe\Adobe Lightroom Classic\Lightroom.exe", true),
        ("Photoshop", @"%ProgramFiles%\Adobe\Adobe Photoshop*\Photoshop.exe", true),
        ("Capture One", @"%ProgramFiles%\Capture One\Capture One*\CaptureOne.exe", true),
        ("Affinity Photo", @"%ProgramFiles%\Affinity\Photo*\Photo.exe", true),
        ("darktable", @"%ProgramFiles%\darktable\bin\darktable.exe", true),
        ("RawTherapee", @"%ProgramFiles%\RawTherapee\*\rawtherapee.exe", true),
        ("GIMP", @"%ProgramFiles%\GIMP*\bin\gimp-*.exe", false),
    };

    public static List<Editor> Available()
    {
        var editors = new List<Editor>();
        foreach (var (name, pattern, raw) in Known)
        {
            if (Find(Environment.ExpandEnvironmentVariables(pattern)) is { } exe) editors.Add(new Editor(name, exe, raw));
        }
        foreach (var custom in AppSettings.CustomEditors)
        {
            if (File.Exists(custom.ExePath) && editors.All(e => !string.Equals(e.ExePath, custom.ExePath, StringComparison.OrdinalIgnoreCase)))
                editors.Add(custom);
        }
        return editors;
    }

    public static void Open(Editor editor, IEnumerable<ImageItem> items)
    {
        var start = new ProcessStartInfo(editor.ExePath) { UseShellExecute = false };
        foreach (var item in items)
            start.ArgumentList.Add(editor.PrefersRaw && item.RawPath != null ? item.RawPath : item.Path);
        Process.Start(start);
    }

    /// <summary>Resolves a path with * in folder or file names to the newest match.</summary>
    private static string? Find(string pattern)
    {
        var parts = pattern.Split('\\');
        IEnumerable<string> current = new[] { parts[0] + "\\" };
        foreach (var part in parts.Skip(1))
        {
            current = current.SelectMany(dir =>
            {
                try
                {
                    if (!part.Contains('*')) return new[] { Path.Combine(dir, part) }.Where(p => Directory.Exists(p) || File.Exists(p));
                    return Directory.EnumerateFileSystemEntries(dir, part);
                }
                catch
                {
                    return Enumerable.Empty<string>();
                }
            }).ToList();
        }
        return current.Where(File.Exists).OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }
}
