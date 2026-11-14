using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace FileDockDemo.Features.WindowPinning;

internal sealed record PinTarget(IntPtr Handle, uint Process, uint Thread, string ClassName, string Title);

internal interface IPinDesktop
{
    PinTarget? Inspect(IntPtr window);
    bool IsTopmost(IntPtr window);
    bool SetTopmost(IntPtr window, bool value);
}

// Session records only: never persist HWNDs or window titles.
internal sealed class WindowPinningModule : IDisposable
{
    private readonly IPinDesktop _desktop;
    private readonly Dictionary<IntPtr, PinTarget> _pins = new();
    private readonly PinNative.EventCallback _callback;
    private IntPtr _hook;
    private readonly bool _watch;
    internal event Action? Changed;
    internal WindowPinningModule(IPinDesktop desktop, bool watch = true)
    {
        _desktop = desktop; _watch = watch;
        _callback = (_, evt, hwnd, obj, child, _, _) => {
            if(obj!=0||child!=0)return;
            if(evt==0x8001 && _pins.Remove(hwnd)) { UpdateHook(); Changed?.Invoke(); }
            else if(evt is 0x8004 or 0x800C && _pins.ContainsKey(hwnd)) Refresh();
        };
    }
    private void UpdateHook()
    {
        if (_watch && _pins.Count>0 && _hook==IntPtr.Zero)
            _hook=PinNative.SetWinEventHook(0x8001,0x800C,IntPtr.Zero,_callback,0,0,0);
        if (_pins.Count==0 && _hook!=IntPtr.Zero) { PinNative.UnhookWinEvent(_hook);_hook=IntPtr.Zero; }
    }
    internal IReadOnlyList<PinTarget> Items { get { Refresh(); return _pins.Values.ToArray(); } }
    private bool Same(PinTarget saved, PinTarget? current) => current != null &&
        saved.Process == current.Process && saved.Thread == current.Thread && saved.ClassName == current.ClassName;
    internal void Refresh()
    {
        bool changed = false;
        foreach (var pin in _pins.Values.ToArray()) {
            var current=_desktop.Inspect(pin.Handle);
            if (!Same(pin, current) || !_desktop.IsTopmost(pin.Handle))
                changed |= _pins.Remove(pin.Handle);
            else if(current!=null && current.Title!=pin.Title) {_pins[pin.Handle]=current;changed=true;}
        }
        if (changed) { UpdateHook(); Changed?.Invoke(); }
    }
    internal string Toggle(IntPtr hwnd)
    {
        Refresh();
        if (_pins.ContainsKey(hwnd)) return Remove(hwnd);
        var target = _desktop.Inspect(hwnd);
        if (target == null) return "此窗口不可置顶，请选择普通应用窗口。";
        if (_desktop.IsTopmost(hwnd)) return "此窗口原本已置顶，不由文件坞接管。";
        if (!_desktop.SetTopmost(hwnd, true) || !_desktop.IsTopmost(hwnd))
            return "置顶失败：目标可能受权限限制或已关闭。";
        // Recheck identity before owning a window which may have disappeared during the call.
        if (!Same(target, _desktop.Inspect(hwnd))) return "目标窗口已变化，请重新选择。";
        _pins[hwnd] = target; UpdateHook(); Changed?.Invoke(); return "已置顶：" + target.Title;
    }
    internal string Remove(IntPtr hwnd)
    {
        if (!_pins.TryGetValue(hwnd, out var target)) return "此窗口不在管理列表中。";
        if (!Same(target, _desktop.Inspect(hwnd))) { _pins.Remove(hwnd); UpdateHook(); Changed?.Invoke(); return "窗口已关闭或变化。"; }
        if (!_desktop.SetTopmost(hwnd, false) || _desktop.IsTopmost(hwnd)) return "取消失败：目标可能受权限限制，请重试。";
        _pins.Remove(hwnd); UpdateHook(); Changed?.Invoke(); return "已取消置顶：" + target.Title;
    }
    internal int RestoreAll()
    {
        Refresh(); foreach (var hwnd in _pins.Keys.ToArray()) Remove(hwnd); return _pins.Count;
    }
    public void Dispose()
    {
        RestoreAll();
        if (_hook != IntPtr.Zero) { PinNative.UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        Changed = null;
    }
}

internal sealed class PinNative : IPinDesktop
{
    private readonly bool _allowOwnProcess;
    internal PinNative(bool allowOwnProcess = false) => _allowOwnProcess = allowOwnProcess;
    public PinTarget? Inspect(IntPtr hwnd)
    {
        // Hidden windows can return later: retain identity so exit cleanup still restores them.
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || GetAncestor(hwnd, 2) != hwnd) return null;
        uint thread = GetWindowThreadProcessId(hwnd, out uint pid);
        if (thread == 0 || (!_allowOwnProcess && pid == Environment.ProcessId)) return null;
        var cls = new StringBuilder(256); GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "#32768") return null;
        var title = new StringBuilder(512); GetWindowText(hwnd, title, title.Capacity);
        return new PinTarget(hwnd, pid, thread, cls.ToString(), title.Length==0?cls.ToString():title.ToString());
    }
    public bool IsTopmost(IntPtr hwnd) => (GetWindowLongPtr(hwnd, -20).ToInt64() & 8) != 0;
    public bool SetTopmost(IntPtr hwnd, bool value) => SetWindowPos(hwnd, value ? new IntPtr(-1) : new IntPtr(-2), 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002 | 0x0200);
    internal static string ProcessName(uint pid) { try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; } catch { return "应用已退出"; } }
    internal delegate void EventCallback(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, EventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int length);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [StructLayout(LayoutKind.Sequential)] internal struct ScreenPoint { internal int X, Y; }
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out ScreenPoint point);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(ScreenPoint point);
}
