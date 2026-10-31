using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace FileDockDemo;

internal static partial class NativeMethods
{
    internal const int HotkeyId = 0xD0C;
    private const int ExtendedStyleIndex = -20;
    private const long ToolWindowStyle = 0x00000080, AppWindowStyle = 0x00040000;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] private static extern int GetWindowLong32(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong32(IntPtr window, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    internal static long GetExtendedWindowStyle(IntPtr window)
    {
        Marshal.SetLastPInvokeError(0);
        long style = IntPtr.Size == 8 ? GetWindowLongPtr64(window, ExtendedStyleIndex).ToInt64() : GetWindowLong32(window, ExtendedStyleIndex);
        int error = Marshal.GetLastPInvokeError();
        if (style == 0 && error != 0) throw new Win32Exception(error);
        return style;
    }

    internal static void HideFromTaskSwitcher(IntPtr window)
    {
        // ShowInTaskbar alone does not exclude WPF windows from Alt+Tab.
        // Keep activation enabled so the panel still accepts clicks and typing.
        long style = (GetExtendedWindowStyle(window) | ToolWindowStyle) & ~AppWindowStyle;
        Marshal.SetLastPInvokeError(0);
        long previous = IntPtr.Size == 8
            ? SetWindowLongPtr64(window, ExtendedStyleIndex, new IntPtr(style)).ToInt64()
            : SetWindowLong32(window, ExtendedStyleIndex, unchecked((int)style));
        int error = Marshal.GetLastPInvokeError();
        if (previous == 0 && error != 0) throw new Win32Exception(error);
        // Refresh cached styles without moving, resizing, activating or reordering.
        if (!SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, 0x0037))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    internal static bool IsAboveForeground(IntPtr window)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == window) return true;
        // Bound traversal because another process can destroy/reorder windows.
        IntPtr current = foreground;
        for (int i = 0; i < 512 && current != IntPtr.Zero; i++)
        {
            current = GetWindow(current, 3); // GW_HWNDPREV: next window above.
            if (current == window) return true;
        }
        return false;
    }
    internal delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint threadId, uint time);
    [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
    // HWND_TOPMOST, NOSIZE | NOMOVE | NOACTIVATE. Restore z-order without
    // stealing focus from the foreground application or polling every second.
    [DllImport("user32.dll")] internal static extern bool EnableWindow(IntPtr window, bool enabled);
    [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(IntPtr window);
    internal static bool SetCaptureTopmost(IntPtr window,bool topmost) => SetWindowPos(window,new IntPtr(topmost ? -1 : -2),0,0,0,0,0x0013);
    internal static bool RaiseWithoutActivation(IntPtr window) =>
        SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr region);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")] internal static extern uint WindowDpi(IntPtr window);
    internal static void RoundEdgeWindow(IntPtr window, double width, double height)
    {
        double scale = WindowDpi(window) / 96d;
        var region = CreateRoundRectRgn(0, 0, (int)Math.Ceiling(width * scale) + 1, (int)Math.Ceiling(height * scale) + 1, (int)(16 * scale), (int)(16 * scale));
        if (region != IntPtr.Zero && SetWindowRgn(window, region, true) == 0) DeleteObject(region);
    }
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr target,IntPtr first,IntPtr second,int mode);
    internal static void ShapePagePanel(IntPtr window,double width,double height,bool tabsRight,bool filesSelected)
    {
        double scale=WindowDpi(window)/96d;
        int P(double v)=>(int)Math.Round(v*scale);
        var body=CreateRoundRectRgn(P(tabsRight ? 0 : 32),0,P(tabsRight ? width-32 : width)+1,P(height)+1,P(40),P(40));
        if(body==IntPtr.Zero)return;
        try
        {
            foreach(double top in new[]{90d,142d})
            {
                bool selected=top==90 ? filesSelected : !filesSelected;
                var tab=CreateRoundRectRgn(P(tabsRight ? width-34 : selected ? 0 : 4),P(top),P(tabsRight ? width-(selected ? 0 : 4) : 34)+1,P(top+44)+1,P(16),P(16));
                if(tab==IntPtr.Zero)return;
                try{if(CombineRgn(body,body,tab,2)==0)return;}finally{DeleteObject(tab);}
            }
            if(SetWindowRgn(window,body,true)!=0)body=IntPtr.Zero;
        }
        finally{if(body!=IntPtr.Zero)DeleteObject(body);}
    }}
