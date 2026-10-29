using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FileDockDemo;

internal static partial class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out WindowRect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    private struct WindowRect { public int Left, Top, Right, Bottom; }
    internal delegate IntPtr CaptureKeyCallback(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int id, CaptureKeyCallback callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string? name);
    [DllImport("dwmapi.dll")] internal static extern int DwmFlush();

    internal static bool IsCaptureShortcut(int key)
    {
        // Read only modifiers, whose earlier events have already updated the
        // asynchronous state. The current key comes from the hook payload.
        bool win = GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0;
        bool shift = GetAsyncKeyState(0x10) < 0;
        bool alt = GetAsyncKeyState(0x12) < 0;
        return key == 0x2C || (key == 0x53 && win && shift)
            || (key == 0x70 && !win && !alt && !shift); // Snipaste F1 / Ctrl+F1.
    }

    internal static bool IsCaptureSurface(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window) || GetAncestor(window, 2) != window) return false;
        if (!GetWindowRect(window, out var rect)) return false;
        var screen = System.Windows.Forms.Screen.FromHandle(window).Bounds;
        if (rect.Right - rect.Left < screen.Width * 0.9 || rect.Bottom - rect.Top < screen.Height * 0.9) return false;
        GetWindowThreadProcessId(window, out uint pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (process.ProcessName.Equals("Snipaste", StringComparison.OrdinalIgnoreCase))
            {
                var title = new StringBuilder(256); GetWindowText(window, title, title.Capacity);
                return title.ToString().Equals("Snipper - Snipaste", StringComparison.OrdinalIgnoreCase);
            }
            return process.ProcessName.Equals("ScreenClippingHost", StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.Equals("SnippingTool", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}

public partial class MainWindow
{
    private IntPtr _captureHook;
    private NativeMethods.WinEventCallback? _captureCallback;
    private readonly HashSet<IntPtr> _captureSurfaces = new();
    private bool _captureActive;
    private DispatcherTimer? _captureRecovery;
    private IntPtr _captureKeyHook;
    private NativeMethods.CaptureKeyCallback? _captureKeyCallback;
    private long _capturePendingUntil;

    private void WatchCaptureWindows()
    {
        _captureRecovery = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _captureRecovery.Tick += (_, _) => UpdateCaptureVisibility();
        _captureKeyCallback = (code, message, data) =>
        {
            if (code >= 0 && (message == (IntPtr)0x100 || message == (IntPtr)0x104) && !_quitting)
            {
                int key = Marshal.ReadInt32(data);
                if (key == 0x1B) _capturePendingUntil = 0;
                else if (NativeMethods.IsCaptureShortcut(key)) BeginCaptureShortcut();
            }
            // Never consume or simulate the user's keystroke.
            return NativeMethods.CallNextHookEx(_captureKeyHook, code, message, data);
        };
        _captureKeyHook = NativeMethods.SetWindowsHookEx(13, _captureKeyCallback, NativeMethods.GetModuleHandle(null), 0);
        if (_captureKeyHook == IntPtr.Zero) SetStatus("截图快捷键监听不可用，截图前请暂时收起或退出文件坞。", true);
        _captureCallback = (_, eventType, window, objectId, childId, _, _) =>
        {
            if (objectId != 0 || childId != 0 || window == IntPtr.Zero) return;
            if (eventType == 0x8002 && !NativeMethods.IsCaptureSurface(window)) return;
            if (eventType == 0x8003 && !_captureActive) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_quitting) return;
                if (eventType == 0x8002 && NativeMethods.IsCaptureSurface(window)) _captureSurfaces.Add(window);
                if (eventType == 0x8003) _captureSurfaces.Remove(window);
                UpdateCaptureVisibility();
            }, DispatcherPriority.Normal);
        };
        _captureHook = NativeMethods.SetWinEventHook(0x8002, 0x8003, IntPtr.Zero, _captureCallback, 0, 0, 2);
    }
    internal void BeginCaptureShortcut()
    {
        if (!_isCollapsed || _dock == null || _captureActive) return;
        _capturePendingUntil = Environment.TickCount64 + 1500;
        _captureActive = true;
        RecordDiagnosticState("capture-shortcut");
        _dock.Hide();
        _captureRecovery?.Start();
        // Finish hiding before passing the shortcut to the screenshot tool,
        // which may snapshot the desktop before showing its selection window.
        NativeMethods.DwmFlush();
    }

    internal bool UpdateCaptureVisibility()
    {
        _captureSurfaces.RemoveWhere(w => !NativeMethods.IsCaptureSurface(w));
        var foreground = NativeMethods.GetForegroundWindow();
        if (NativeMethods.IsCaptureSurface(foreground)) _captureSurfaces.Add(foreground);
        if (_captureSurfaces.Count > 0) _capturePendingUntil = 0;
        bool active = _captureSurfaces.Count > 0 || Environment.TickCount64 < _capturePendingUntil;
        bool wasActive = _captureActive;
        _captureActive = active;
        if (active != wasActive) RecordDiagnosticState("capture-state-changed");
        if (active) { _dock?.Hide(); _captureRecovery?.Start(); }
        else { _captureRecovery?.Stop(); if (wasActive && _isCollapsed) QueueDockRaise(); }
        return active;
    }
}
