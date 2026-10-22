using System;
using System.Collections.Generic;
using System.IO;

namespace Ocula.Services;

/// <summary>
/// An ocula: link, as Capta (the sibling screenshot app) launches them (Capta's
/// docs/ocula-integration.md is the contract):
///   ocula:captures?folder=&lt;capture folder&gt;            show all captures
///   ocula:import?file=&lt;path&gt;&amp;folder=&lt;…&gt;          add a capture to the library, in the background
///   ocula:edit?file=&lt;path&gt;&amp;folder=&lt;…&gt;            open a capture in the editor
/// and, for Porta ("Share people with Porta", see PeopleExport):
///   ocula:person?id=&lt;id&gt;                              show the photos of a person named in People
///                                                      (the id is the "id" in people-index.json)
/// Values are percent-encoded (Uri.EscapeDataString). Capta checks that Ocula is installed by
/// asking Windows whether anything handles "ocula:", so the scheme must stay registered.
/// </summary>
public sealed record OculaLink(string Verb, string? File, string? Folder, string? Id = null)
{
    public const string Scheme = "ocula";

    /// <summary>Imports must not take the focus: many can arrive in a row while you work.</summary>
    public bool InBackground => Verb == "import";

    public static OculaLink? Parse(string? link)
    {
        if (link == null || !link.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = link[(Scheme.Length + 1)..];
        var question = rest.IndexOf('?');
        var verb = (question < 0 ? rest : rest[..question]).Trim('/').ToLowerInvariant();
        if (verb.Length == 0) return null;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (question >= 0)
        {
            foreach (var pair in rest[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = pair.IndexOf('=');
                var key = equals < 0 ? pair : pair[..equals];
                var value = equals < 0 ? string.Empty : pair[(equals + 1)..];
                try { values[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value); }
                catch (UriFormatException) { }
            }
        }

        string? Value(string key) => values.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        // A person's id is 8 to 32 lower-case hex characters. Anything else is dropped, so it can never reach a query as something else.
        var id = Value("id")?.ToLowerInvariant();
        return new OculaLink(verb, Value("file"), Value("folder"), id != null && System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9a-f]{8,32}$") ? id : null);
    }

    /// <summary>
    /// The first argument that is an ocula: link, as an unpackaged Ocula is launched by the HKCU protocol registration
    /// (<c>Ocula.exe "ocula:person?id=..."</c>). Quotes around it are ignored.
    /// </summary>
    public static OculaLink? FromArguments(IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
            if (Parse(argument.Trim().Trim('"')) is { } link) return link;
        return null;
    }

    /// <summary>Capta's capture folder as the link gives it, else Capta's default: Pictures\Captures.</summary>
    public static string CaptureFolderOrDefault(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && Path.IsPathFullyQualified(folder)
            ? Path.TrimEndingDirectorySeparator(folder)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Captures");

    /// <summary>
    /// Whether a file is in a temporary folder: the system temp folder, or a packaged app's
    /// TempState (where Capta saves a capture before handing it over). Those get copied into the
    /// capture folder so they last.
    /// </summary>
    public static bool IsTemporary(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) return true;
        var parts = full.Split(Path.DirectorySeparatorChar);
        var packages = Array.FindIndex(parts, p => p.Equals("Packages", StringComparison.OrdinalIgnoreCase));
        return packages >= 0 && packages + 2 < parts.Length &&
               (parts[packages + 2].Equals("TempState", StringComparison.OrdinalIgnoreCase) ||
                parts[packages + 2].Equals("AC", StringComparison.OrdinalIgnoreCase) && packages + 3 < parts.Length &&
                parts[packages + 3].Equals("Temp", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Where a handed-over capture lives from now on. A temporary file is copied into the capture
    /// folder (a free name is picked if one is taken); any other file stays where it is.
    /// </summary>
    public static string Adopt(string file, string captureFolder)
    {
        if (!IsTemporary(file)) return file;
        Directory.CreateDirectory(captureFolder);
        var target = FreePath(Path.Combine(captureFolder, Path.GetFileName(file)));
        System.IO.File.Copy(file, target);
        System.IO.File.SetLastWriteTimeUtc(target, System.IO.File.GetLastWriteTimeUtc(file));
        return target;
    }

    /// <summary>The path itself if nothing is there, otherwise "name (2).ext", "name (3).ext"…</summary>
    public static string FreePath(string path)
    {
        if (!System.IO.File.Exists(path)) return path;
        var folder = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{name} ({i}){ext}");
            if (!System.IO.File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>Whether a folder is one of the library folders or inside one.</summary>
    public static bool IsInside(string folder, IEnumerable<string> roots)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        foreach (var root in roots)
        {
            var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (full.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
