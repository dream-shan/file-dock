using System;
using System.Runtime.InteropServices;
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
    private IntPtr _passiveRevealForeground;
    private IntPtr _outsideClickHook;
    private NativeMethods.CaptureKeyCallback? _outsideClickCallback;
    internal long DesktopPresenceChecks { get; private set; }
    internal bool DesktopPresencePending => _desktopPresenceTimer?.IsEnabled == true;
    internal bool IsPinned => State.DisplayMode == PanelDisplayMode.Pinned;

    private void InitializeDesktopPresence()
    {
        ShowActivated = false;
        SourceInitialized += (_, _) =>
        {
            if (IsPinned)
                NativeMethods.SetCaptureTopmost(new WindowInteropHelper(this).Handle, true);
        };
    }

    private void WatchDesktopPresence()
    {
        if (App.IsSelfTest) return;
        _desktopPresenceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _desktopPresenceTimer.Tick += (_, _) => { _desktopPresenceTimer.Stop(); ReconcileDesktopPresence(); };
        _desktopVisibilityHook = NativeMethods.SetWinEventHook(0x8000, 0x8003, IntPtr.Zero, _foregroundCallback!, 0, 0, 2);
        _desktopMinimizeHook = NativeMethods.SetWinEventHook(0x0016, 0x0017, IntPtr.Zero, _foregroundCallback!, 0, 0, 2);
        // A tray reveal leaves focus in the application. Its next click may not
        // produce a foreground change, so observe button-down only (no input log).
        _outsideClickCallback = (code, message, data) =>
        {
            if (IsVisible && !IsPinned && !_captureActive && code >= 0 && message.ToInt32() is 0x0201 or 0x0204 or 0x0207 or 0x020B)
            {
                var point = Marshal.PtrToStructure<NativeMethods.PointerPoint>(data);
                if (!NativeMethods.IsOwnDesktopWindow(NativeMethods.WindowFromPoint(point)))
                    Dispatcher.BeginInvoke(ObserveOutsideClick, DispatcherPriority.Background);
            }
            return NativeMethods.CallNextHookEx(_outsideClickHook, code, message, data);
        };
        _outsideClickHook = NativeMethods.SetWindowsHookEx(14, _outsideClickCallback, NativeMethods.GetModuleHandle(null), 0);
        if (_desktopVisibilityHook == IntPtr.Zero || _desktopMinimizeHook == IntPtr.Zero)
            SetStatus("部分窗口通知不可用，仍可通过侧边入口或快捷键唤出。", true);
        if (_outsideClickHook == IntPtr.Zero)
            SetStatus("外部点击通知不可用，仍可通过切换窗口、Esc 或快捷键收起。", true);
    }

    private void ObserveOutsideClick()
    {
        if (!IsVisible || IsPinned || _captureActive || _inDialog || _dragging || _movingPanel || AnyMenuOpen()) return;
        _passiveRevealForeground = IntPtr.Zero;
        _desktopPresence.Observe(false);
        QueueDesktopPresence();
    }

    private void QueueDesktopPresence()
    {
        if (_quitting || !_loaded || App.IsSelfTest || _desktopPresenceTimer == null) return;
        // One coalesced check per burst; the timer stops when the desktop is idle.
        if (!_desktopPresenceTimer.IsEnabled) _desktopPresenceTimer.Start();
    }

    internal void ReconcileDesktopPresence()
    {
        DesktopPresenceChecks++;
        if (_quitting || MemoryPaused || UpdateCaptureVisibility()) return;
        if (_inDialog || _dragging || _movingPanel || AnyMenuOpen() || _memoryAction.Busy) return;
        if (Mouse.LeftButton == MouseButtonState.Pressed || Mouse.Captured != null)
        { QueueDesktopPresence(); return; }
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return;
        bool passiveReveal = IsVisible && _passiveRevealForeground == foreground;
        if (!passiveReveal) _passiveRevealForeground = IntPtr.Zero;
        bool own = NativeMethods.IsOwnDesktopWindow(foreground);
        _desktopPresence.Observe(own || passiveReveal);
        IntPtr handle = new WindowInteropHelper(this).Handle;
        bool blocked = false;
        if (State.DisplayMode == PanelDisplayMode.Automatic && !_desktopPresence.Dismissed &&
            !(_desktopPresence.ManuallyRevealed && (own || passiveReveal)))
        {
            NativeMethods.SetDesktopZOrder(handle, foreground);
            bool? occluded = NativeMethods.IsPanelOccluded(handle);
            if (!occluded.HasValue) return;
            blocked = occluded.Value;
        }
        var decision = _desktopPresence.Decide(State.DisplayMode!.Value, IsVisible, blocked, own || passiveReveal,
            NativeMethods.IsFullscreenOnPanelScreen(foreground, handle));
        ApplyPanelDisplay(decision);
    }

    private void ApplyPanelDisplay(PanelDisplayDecision decision, bool reposition = true)
    {
        if (!decision.Visible)
        {
            if (IsVisible) CollapsePanelCore(manual: false);
            else QueueDockRaise();
            return;
        }
        if (!IsVisible)
        {
            _isCollapsed = false; _dock?.Hide();
            if (reposition) PositionPanel();
            Show(); // ShowActivated=false: preserve the active application's focus.
            UpdatePerformanceActivity(); RecordDiagnosticState("display-mode-expanded");
        }
        IntPtr handle = new WindowInteropHelper(this).Handle;
        bool topmost = (NativeMethods.GetExtendedWindowStyle(handle) & 8) != 0;
        if (topmost != decision.Topmost)
            NativeMethods.SetCaptureTopmost(handle, decision.Topmost);
        UpdatePageRail();
    }

    private void PanelActivatedForDesktop(object? sender, EventArgs e)
    {
        if (App.IsSelfTest || _captureActive) return;
        _desktopPresence.Reveal();
        ApplyPanelDisplay(new PanelDisplayDecision(true, true), reposition: false);
    }

    private void QueueAfterPanelInteraction()
    {
        QueueDesktopPresence();
    }

    internal void SetDisplayMode(PanelDisplayMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == PanelDisplayMode.Pinned && !IsPinned)
            State.UnpinnedMode = State.DisplayMode!.Value;
        State.DisplayMode = mode;
        DesktopPresence.NormalizeMode(State);
        UpdateKeepOpen();
        if (!_captureInteractionHeld)
            ApplyPanelDisplay(new PanelDisplayDecision(IsVisible, IsPinned), reposition: false);
        Persist();
        QueueDesktopPresence();
    }

    private void DisposeDesktopPresence()
    {
        _desktopPresenceTimer?.Stop();
        if (_desktopVisibilityHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_desktopVisibilityHook);
        if (_desktopMinimizeHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_desktopMinimizeHook);
        if (_outsideClickHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_outsideClickHook);
        _outsideClickHook = IntPtr.Zero;
        _desktopVisibilityHook = _desktopMinimizeHook = IntPtr.Zero;
    }
}
