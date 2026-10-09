using System;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;

namespace Ocula.Services;

/// <summary>Opens an image in the Windows Photos app, or lets the user pick an app.</summary>
public static class PhotosLauncher
{
    // The Store identity of the built-in Microsoft Photos app.
    private const string PhotosPackageFamilyName = "Microsoft.Windows.Photos_8wekyb3d8bbwe";

    public static async Task<bool> OpenInPhotosAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);

        var options = new LauncherOptions { TargetApplicationPackageFamilyName = PhotosPackageFamilyName };
        if (await Launcher.LaunchFileAsync(file, options)) return true;

        // Photos isn't installed, or doesn't accept this file type (e.g. a RAW format without
        // the Raw Image Extension): fall back to whatever app Windows uses for this file.
        return await Launcher.LaunchFileAsync(file);
    }

    public static async Task<bool> OpenWithAsync(string path, IntPtr windowHandle)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var options = new LauncherOptions { DisplayApplicationPicker = true };
        // Desktop apps must tell WinRT dialogs which window owns them.
        WinRT.Interop.InitializeWithWindow.Initialize(options, windowHandle);
        return await Launcher.LaunchFileAsync(file, options);
    }
}
