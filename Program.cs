using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Ocula.Services;

namespace Ocula;

/// <summary>
/// Custom entry point that keeps Ocula to a single window. If Ocula is already running,
/// a new launch (Open with, the folder right-click menu, or "ocula D:\Photos") passes its
/// file or folder to the running window and exits.
///
/// Requires DISABLE_XAML_GENERATED_MAIN in the .csproj (see README), otherwise the
/// template's generated Main is used instead of this one.
/// </summary>
public static class Program
{
    private const string InstanceKey = "OculaMainWindow";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (RedirectToRunningInstance()) return 0;
        if (ImportInBackground()) return 0;

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    private static bool RedirectToRunningInstance()
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);

        if (main.IsCurrent)
        {
            // We're the first instance: listen for later launches.
            main.Activated += (_, e) => App.OnRedirectedActivation(e);
            return false;
        }

        // Another Ocula is running. Redirecting is asynchronous, but this STA thread must not
        // exit or pump messages normally until it's done, so wait with CoWaitForMultipleObjects
        // (the pattern Microsoft's Windows App SDK documentation uses).
        var done = CreateEvent(IntPtr.Zero, true, false, null);
        Task.Run(() =>
        {
            main.RedirectActivationToAsync(activation).AsTask().Wait();
            SetEvent(done);
        });
        _ = CoWaitForMultipleObjects(0, 0xFFFFFFFF, 1, new[] { done }, out _);

        // A capture sent from Capta mustn't pull Ocula in front of what you're doing.
        if (App.GetLink(activation) is { InBackground: true }) return true;

        // We were just launched by the user, so Windows lets us bring the other window forward.
        try
        {
            SetForegroundWindow(Process.GetProcessById((int)main.ProcessId).MainWindowHandle);
        }
        catch
        {
            // The other instance may have closed in the meantime.
        }
        return true;
    }

    /// <summary>
    /// A capture sent from Capta while Ocula isn't open: copy it into the capture folder and make
    /// sure that folder is in the library, without opening a window. Ocula indexes it the next
    /// time it runs.
    /// </summary>
    private static bool ImportInBackground()
    {
        if (App.GetLink(AppInstance.GetCurrent().GetActivatedEventArgs()) is not { InBackground: true, File: { } file } link)
            return false;
        try
        {
            var folder = OculaLink.CaptureFolderOrDefault(link.Folder ?? AppSettings.CapturesFolder);
            AppSettings.CapturesFolder = folder;
            var path = OculaLink.Adopt(file, folder);
            var roots = AppSettings.Folders;
            var parent = System.IO.Path.GetDirectoryName(path)!;
            if (!OculaLink.IsInside(parent, roots))
            {
                roots.Add(parent);
                AppSettings.Folders = roots;
            }
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            // Nothing to show it in; Capta still has the capture.
        }
        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint dwFlags, uint dwMilliseconds, uint nHandles, IntPtr[] pHandles, out uint dwIndex);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
