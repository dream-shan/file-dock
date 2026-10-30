using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FileDockDemo;

public partial class MainWindow
{
    private readonly DesktopPresence _desktopPresence = new();
    private DispatcherTimer? _desktopPresenceTimer;
    private IntPtr _desktopVisibilityHook, _desktopMinimizeHook;
    internal long DesktopPresenceChecks { get; private set; }
    internal bool DesktopPresencePending => _desktopPresenceTimer?.IsEnabled == true;

    private void InitializeDesktopPresence()
    {
        ShowActivated = false;
        if (!State.AutoDesktop) return;
        // The explicit ExpandPanel path promotes/activates on demand.
        if (!_keepOpen) Topmost = false;
    }

    private void WatchDesktopPresence()
    {
        if (App.IsSelfTest) return;
        _desktopPresenceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _desktopPresenceTimer.Tick += (_, _) => { _desktopPresenceTimer.Stop(); ReconcileDesktopPresence(); };
        _desktopVisibilityHook = NativeMethods.SetWinEventHook(0x8000, 0x8003, IntPtr.Zero, _foregroundCallback!, 0, 0, 2);
        _desktopMinimizeHook = NativeMethods.SetWinEventHook(0x0016, 0x0017, IntPtr.Zero, _foregroundCallback!, 0, 0, 2);
        if (_desktopVisibilityHook == IntPtr.Zero || _desktopMinimizeHook == IntPtr.Zero)
            SetStatus("部分窗口通知不可用，仍可通过侧边入口或快捷键唤出。", true);
    }

    private void QueueDesktopPresence()
    {
        if (_quitting || !_loaded || !State.AutoDesktop || App.IsSelfTest || _desktopPresenceTimer == null) return;
        // One coalesced check per burst; the timer stops when the desktop is idle.
        if (!_desktopPresenceTimer.IsEnabled) _desktopPresenceTimer.Start();
    }

    private void ReconcileDesktopPresence()
    {
        DesktopPresenceChecks++;
        if (_quitting || !State.AutoDesktop || MemoryPaused || UpdateCaptureVisibility()) return;
        if (_inDialog || _dragging || _movingPanel || AnyMenuOpen() || _memoryAction.Busy) return;
        if (Mouse.LeftButton == MouseButtonState.Pressed || Mouse.Captured != null)
        { QueueDesktopPresence(); return; }
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return;
        bool own = NativeMethods.IsOwnDesktopWindow(foreground);
        _desktopPresence.Observe(NativeMethods.IsDesktopSurface(foreground), own);
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (_keepOpen || (_desktopPresence.ManuallyRevealed && own))
        {
            if (IsVisible && !_desktopPresence.Dismissed && (NativeMethods.GetExtendedWindowStyle(handle) & 8) == 0)
                NativeMethods.RaiseWithoutActivation(handle);
            return;
        }
        if (_desktopPresence.Dismissed) return;
        NativeMethods.SetDesktopZOrder(handle, foreground);
        bool? blocked = NativeMethods.IsPanelOccluded(handle);
        if (!blocked.HasValue) return;
        bool visible = _desktopPresence.WantVisible(IsVisible, _keepOpen, blocked.Value, own);
        if (!visible && IsVisible) CollapsePanelCore(manual: false);
        else if (visible && !IsVisible)
        {
            _isCollapsed = false; _dock?.Hide(); PositionPanel();
            Show(); // ShowActivated=false: do not focus, reset selection or move the cursor.
            NativeMethods.SetDesktopZOrder(handle, foreground);
            UpdatePerformanceActivity(); RecordDiagnosticState("desktop-auto-expanded");
        }
    }

    private void PanelActivatedForDesktop(object? sender, EventArgs e)
    {
        if (App.IsSelfTest || !State.AutoDesktop || _captureActive) return;
        _desktopPresence.Reveal();
        NativeMethods.RaiseWithoutActivation(new WindowInteropHelper(this).Handle);
    }

    private void QueueAfterPanelInteraction()
    {
        if (State.AutoDesktop) QueueDesktopPresence();
        else if (!_keepOpen && !_inDialog && !_dragging && !IsActive && !AnyMenuOpen() && !App.IsSelfTest)
            CollapsePanel();
    }

    private void SetAutoDesktop(bool enabled)
    {
        State.AutoDesktop = enabled; Persist();
        if (enabled) QueueDesktopPresence();
        else
        {
            _desktopPresenceTimer?.Stop();
            if (IsVisible && !_captureActive) NativeMethods.RaiseWithoutActivation(new WindowInteropHelper(this).Handle);
        }
    }

    private void DisposeDesktopPresence()
    {
        _desktopPresenceTimer?.Stop();
        if (_desktopVisibilityHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_desktopVisibilityHook);
        if (_desktopMinimizeHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_desktopMinimizeHook);
        _desktopVisibilityHook = _desktopMinimizeHook = IntPtr.Zero;
    }
}
