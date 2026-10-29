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
    private bool _captureInteractionHeld, _captureWasTopmost, _captureWasEnabled, _captureWasHitTestVisible;
    private FrameworkElement[] _captureToolTips = Array.Empty<FrameworkElement>();
    internal bool CaptureInteractionHeld => _captureInteractionHeld;

    private void HoldCaptureInteraction()
    {
        if (_captureInteractionHeld || _quitting) return;
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        _captureWasTopmost = (NativeMethods.GetExtendedWindowStyle(handle) & 8) != 0;
        _captureWasEnabled = NativeMethods.IsWindowEnabled(handle);
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
        NativeMethods.EnableWindow(handle, false);
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
            NativeMethods.EnableWindow(handle, _captureWasEnabled);
            if (!NativeMethods.SetCaptureTopmost(handle, _captureWasTopmost)) DiagnosticLog.Write("capture-zorder-restore-failed");
        }
        IsHitTestVisible = _captureWasHitTestVisible;
        foreach (var element in _captureToolTips) ToolTipService.SetIsEnabled(element, true);
        _captureToolTips = Array.Empty<FrameworkElement>();
        UpdateMemoryActionHint();
        RecordDiagnosticState("capture-input-restored");
    }

    internal void CancelCaptureShortcutPending()
    {
        _capturePendingUntil = 0;
        UpdateCaptureVisibility();
    }
}
