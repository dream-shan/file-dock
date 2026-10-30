using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace FileDockDemo;

// State survives Hide/Show; only explicit intent or a return to the desktop
// clears a manual dismissal. Native events never activate the panel.
internal sealed class DesktopPresence
{
    internal bool Dismissed { get; private set; }
    internal bool ManuallyRevealed { get; private set; }
    private bool _awayAfterDismiss;
    internal void Reveal() { Dismissed = false; ManuallyRevealed = true; _awayAfterDismiss = false; }
    internal void Dismiss(bool externalApp)
    { Dismissed = true; ManuallyRevealed = false; _awayAfterDismiss = externalApp; }
    internal void Observe(bool desktop, bool ownWindow)
    {
        if (ownWindow) return;
        ManuallyRevealed = false;
        if (!desktop) _awayAfterDismiss = true;
        else if (_awayAfterDismiss) { Dismissed = false; _awayAfterDismiss = false; }
    }
    internal bool WantVisible(bool visible, bool pinned, bool blocked, bool ownWindow)
    {
        if (Dismissed) return false;
        if (pinned || (ManuallyRevealed && ownWindow)) return visible;
        return !blocked;
    }
}

internal static partial class NativeMethods
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int length);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out WindowRect value, int size);
    internal static bool IsTopLevelDesktopEvent(IntPtr window) => window != IntPtr.Zero && GetAncestor(window, 2) == window && !IsOwnDesktopWindow(window);
    internal static bool IsOwnDesktopWindow(IntPtr window)
    { GetWindowThreadProcessId(window, out uint process); return process == (uint)Environment.ProcessId; }
    private static string WindowClass(IntPtr window)
    { var name = new StringBuilder(128); GetClassName(window, name, name.Capacity); return name.ToString(); }
    internal static bool IsDesktopSurface(IntPtr window) => WindowClass(window) is "Progman" or "WorkerW";
    private static bool IgnoredDesktopClass(string name) => name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "tooltips_class32" or "SysShadow";
    internal static bool IsDesktopBlocker(IntPtr window, bool includeOwnProcess = false)
    {
        if (window == IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window) ||
            (!includeOwnProcess && IsOwnDesktopWindow(window))) return false;
        if (IgnoredDesktopClass(WindowClass(window))) return false;
        if (DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        return true;
    }
    private static bool FrameBounds(IntPtr window, out WindowRect rect)
    {
        if (DwmGetWindowAttribute(window, 9, out rect, Marshal.SizeOf<WindowRect>()) != 0 || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            return GetWindowRect(window, out rect);
        return true;
    }
    internal static bool? IsPanelOccluded(IntPtr panel, bool includeOwnProcess = false)
    {
        if (!FrameBounds(panel, out var bounds)) return null;
        var seen = new HashSet<IntPtr>();
        // Walk only windows ABOVE the panel, including when its HWND is hidden.
        // Bound traversal and detect repeats because other processes can reorder
        // or destroy windows during a query.
        IntPtr current = GetWindow(panel, 3);
        for (int i = 0; current != IntPtr.Zero && i < 1024; i++)
        {
            if (!seen.Add(current)) return null;
            if (IsDesktopBlocker(current, includeOwnProcess) && FrameBounds(current, out var r) &&
                Math.Min(bounds.Right, r.Right) > Math.Max(bounds.Left, r.Left) &&
                Math.Min(bounds.Bottom, r.Bottom) > Math.Max(bounds.Top, r.Top)) return true;
            current = GetWindow(current, 3);
        }
        return current == IntPtr.Zero ? false : null;
    }
    internal static void SetDesktopZOrder(IntPtr panel, IntPtr foreground)
    {
        if ((GetExtendedWindowStyle(panel) & 8) != 0) SetCaptureTopmost(panel, false);
        // A temporary reveal was topmost. Put it behind the active application
        // before measuring actual overlap, but never behind the desktop itself.
        if (IsDesktopBlocker(foreground) && (GetExtendedWindowStyle(foreground) & 8) == 0 && IsAboveForeground(panel))
            SetWindowPos(panel, foreground, 0, 0, 0, 0, 0x0013);
        else if (IsDesktopSurface(foreground) && !IsAboveForeground(panel))
            SetWindowPos(panel, IntPtr.Zero, 0, 0, 0, 0, 0x0013);
    }
}
