using System;
using Microsoft.Win32;

namespace Ocula.Services;

/// <summary>
/// Registers the ocula: URL protocol for a build that is not installed as a package, under HKCU\Software\Classes\ocula (no administrator
/// rights needed). A packaged Ocula is registered by Windows from its manifest, under that same key, so this is skipped for it.
/// </summary>
public static class ProtocolRegistration
{
    public const string Scheme = "ocula";

    /// <summary>
    /// Makes <c>ocula:...</c> start <paramref name="exePath"/> with the link as its only argument. <paramref name="classes"/> is
    /// HKCU\Software\Classes (a different key can be passed for testing). Does nothing if it is already registered to that exe.
    /// </summary>
    public static void Register(RegistryKey classes, string exePath)
    {
        var command = $"\"{exePath}\" \"%1\"";
        if (IsRegistered(classes, command)) return;

        using var key = classes.CreateSubKey(Scheme);
        key.SetValue(string.Empty, "URL:Ocula");
        key.SetValue("URL Protocol", string.Empty);
        using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue(string.Empty, $"\"{exePath}\",0");
        using (var open = key.CreateSubKey(@"shell\open\command")) open.SetValue(string.Empty, command);
    }

    private static bool IsRegistered(RegistryKey classes, string command)
    {
        using var open = classes.OpenSubKey(Scheme + @"\shell\open\command");
        return open?.GetValue(string.Empty) as string == command;
    }
}
