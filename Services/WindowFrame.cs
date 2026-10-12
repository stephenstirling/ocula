using System;
using System.Runtime.InteropServices;

namespace Ocula.Services;

/// <summary>
/// Windows 10 and 11 draw an invisible resize border (about 7 px at 100%) around each window, so
/// a window moved to exactly a spot's rectangle shows a gap beside its neighbours. These are the
/// border's sizes, from the window's full rectangle and the frame DWM actually draws.
/// </summary>
public static class WindowFrame
{
    public readonly record struct Insets(int Left, int Top, int Right, int Bottom);

    /// <summary>How far the window's rectangle extends past its visible frame on each side.</summary>
    public static Insets InvisibleBorders(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var window) ||
            DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<RECT>()) != 0)
            return default;
        return new Insets(
            Math.Max(0, frame.Left - window.Left),
            Math.Max(0, frame.Top - window.Top),
            Math.Max(0, window.Right - frame.Right),
            Math.Max(0, window.Bottom - frame.Bottom));
    }

    /// <summary>The window rectangle whose visible frame fills <paramref name="spot"/> exactly.</summary>
    public static SpotRect Expand(SpotRect spot, Insets borders) => new(
        spot.X - borders.Left,
        spot.Y - borders.Top,
        spot.Width + borders.Left + borders.Right,
        spot.Height + borders.Top + borders.Bottom);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
}
