using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace FileDockDemo;

public partial class MainWindow
{
    private bool _captureInteractionHeld, _captureWasTopmost, _captureWasHitTestVisible;
    private FrameworkElement[] _captureToolTips = Array.Empty<FrameworkElement>();
    internal bool CaptureInteractionHeld => _captureInteractionHeld;

    private void HoldCaptureInteraction()
    {
        if (_captureInteractionHeld || _quitting) return;
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        _captureWasTopmost = (NativeMethods.GetExtendedWindowStyle(handle) & 8) != 0;

        _captureWasHitTestVisible = IsHitTestVisible;
        _captureInteractionHeld = true;
        MemoryCard.CancelPendingAction();
        Mouse.Capture(null);
        IsHitTestVisible = false;
        _captureToolTips = VisualChildren(this).OfType<FrameworkElement>()
            .Where(element => ToolTipService.GetIsEnabled(element)).ToArray();
        foreach (var element in _captureToolTips)
        {
            ToolTipService.SetIsEnabled(element, false);
            if (element.ToolTip is ToolTip tip) tip.IsOpen = false;
            if (element.ContextMenu is ContextMenu menu) menu.IsOpen = false;
        }
        // Keep the HWND enabled: window detectors skip disabled windows.
        // WPF hit testing and the native input guard suspend actions instead.
        if (!NativeMethods.SetCaptureTopmost(handle, false)) DiagnosticLog.Write("capture-zorder-failed");
        UpdateMemoryActionHint();
        RecordDiagnosticState("capture-input-suspended");
    }

    private void RestoreCaptureInteraction()
    {
        if (!_captureInteractionHeld) return;
        _captureInteractionHeld = false;
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            if (!NativeMethods.SetCaptureTopmost(handle, _captureWasTopmost)) DiagnosticLog.Write("capture-zorder-restore-failed");
        }
        IsHitTestVisible = _captureWasHitTestVisible;
        foreach (var element in _captureToolTips) ToolTipService.SetIsEnabled(element, true);
        _captureToolTips = Array.Empty<FrameworkElement>();
        UpdateMemoryActionHint();
        RecordDiagnosticState("capture-input-restored");
    }

    internal bool TrySuppressCaptureInput(int message, IntPtr wParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (!_captureInteractionHeld) return false;
        // Leave hit testing, accessibility and geometry queries intact so screenshot
        // tools can discover the same HWND and its bounds. Do not use HTTRANSPARENT.
        if (message == 0x0021) { result = new IntPtr(4); return true; } // MA_NOACTIVATEANDEAT
        if (message is >= 0x0100 and <= 0x0109 or >= 0x0200 and <= 0x020E or >= 0x00A0 and <= 0x00AD)
            return true; // keyboard, client and non-client mouse input
        if (message == 0x0112)
        {
            long command = wParam.ToInt64() & 0xFFF0;
            return command is 0xF000 or 0xF010; // SC_SIZE / SC_MOVE
        }
        return false;
    }

    internal void CancelCaptureShortcutPending()
    {
        _capturePendingUntil = 0;
        UpdateCaptureVisibility();
    }
}
