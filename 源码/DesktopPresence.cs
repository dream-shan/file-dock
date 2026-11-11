using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace FileDockDemo;

public enum PanelDisplayMode { Automatic, Manual, Pinned }

internal readonly record struct PanelDisplayDecision(bool Visible, bool Topmost);

// One policy for panel visibility and stacking. A manual dismissal is sticky
// until an explicit reveal; application and desktop events never clear it.
internal sealed class DesktopPresence
{
    internal bool Dismissed { get; private set; }
    internal bool ManuallyRevealed { get; private set; }
    internal void Reveal() { Dismissed = false; ManuallyRevealed = true; }
    internal void Dismiss() { Dismissed = true; ManuallyRevealed = false; }
    internal void Observe(bool ownWindow)
    { if (!ownWindow) ManuallyRevealed = false; }
    internal PanelDisplayDecision Decide(PanelDisplayMode mode, bool visible, bool blocked, bool interacting, bool fullscreen)
    {
        if (Dismissed) return new(false, false);
        if (mode == PanelDisplayMode.Pinned) return new(true, true);
        bool revealed = ManuallyRevealed && interacting;
        if (fullscreen && !revealed) return new(false, false);
        return new(mode == PanelDisplayMode.Manual ? visible && revealed : revealed || !blocked, revealed);
    }

    internal static void NormalizeMode(AppState state)
    {
        if (state.DisplayMode == null)
            state.UnpinnedMode = state.AutoDesktop ? PanelDisplayMode.Automatic : PanelDisplayMode.Manual;
        state.DisplayMode ??= state.KeepOpen || state.AlwaysOnTop ? PanelDisplayMode.Pinned :
            state.AutoDesktop ? PanelDisplayMode.Automatic : PanelDisplayMode.Manual;
        if (!Enum.IsDefined(state.DisplayMode.Value)) state.DisplayMode = PanelDisplayMode.Automatic;
        if (state.UnpinnedMode is not (PanelDisplayMode.Automatic or PanelDisplayMode.Manual))
            state.UnpinnedMode = PanelDisplayMode.Automatic;
        // Keep legacy fields coherent for older installations and rollback.
        state.KeepOpen = state.AlwaysOnTop = state.DisplayMode == PanelDisplayMode.Pinned;
        state.AutoDesktop = (state.DisplayMode == PanelDisplayMode.Pinned ? state.UnpinnedMode : state.DisplayMode) == PanelDisplayMode.Automatic;
    }
}

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)] internal struct PointerPoint { public int X, Y; }
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(PointerPoint point);
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
            if (GetWindow(current,4)!=panel && IsDesktopBlocker(current, includeOwnProcess) && FrameBounds(current, out var r) &&
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

    internal static bool IsFullscreenOnPanelScreen(IntPtr foreground, IntPtr panel)
    {
        if (!IsDesktopBlocker(foreground) || !FrameBounds(foreground, out var rect)) return false;
        var screen = System.Windows.Forms.Screen.FromHandle(panel).Bounds;
        return rect.Left <= screen.Left && rect.Top <= screen.Top && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;
    }
}
