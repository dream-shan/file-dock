using System;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Interop;

namespace FileDockDemo;

internal static class CaptureInteractionVerification
{
    internal static async Task Run(MainWindow window,Action<bool,string> check)
    {
        window.ExpandPanel();
        IntPtr handle = new WindowInteropHelper(window).Handle;
        bool oldHitTest = window.IsHitTestVisible;
        bool oldTopmost = (NativeMethods.GetExtendedWindowStyle(handle) & 8) != 0;
        window.BeginCaptureShortcut();
        check(window.IsVisible && window.CaptureInteractionHeld && !window.IsHitTestVisible && NativeMethods.IsWindowEnabled(handle),
            "Expanded capture keeps the native window detectable while suspending WPF mouse input");
        check((NativeMethods.GetExtendedWindowStyle(handle) & 8) == 0,
            "Expanded capture relinquishes native topmost status to the screenshot overlay");
        check(!ToolTipService.GetIsEnabled(window.MemoryCard) && !window.MemoryCard.ActionAvailable,
            "Screenshot mode suppresses tooltips and the memory action");
        long captureStyle = NativeMethods.GetExtendedWindowStyle(handle);
        check((captureStyle & 0x80) != 0 && (captureStyle & 0x40000) == 0,
            "Capture preserves tool-window and taskbar exclusion styles");
        check(!window.TrySuppressCaptureInput(0x0084, IntPtr.Zero, out _) &&
              !window.TrySuppressCaptureInput(0x003D, IntPtr.Zero, out _) &&
              new WindowInteropHelper(window).Handle == handle,
            "Capture leaves native hit-test and accessibility queries available on the same HWND");
        check(window.TrySuppressCaptureInput(0x0201, IntPtr.Zero, out _) &&
              window.TrySuppressCaptureInput(0x0100, IntPtr.Zero, out _) &&
              window.TrySuppressCaptureInput(0x00A1, IntPtr.Zero, out _) &&
              window.TrySuppressCaptureInput(0x0112, new IntPtr(0xF010), out _) &&
              window.TrySuppressCaptureInput(0x0112, new IntPtr(0xF000), out _),
            "Capture blocks mouse, keyboard and native move/resize actions");
        check(window.TrySuppressCaptureInput(0x0021, IntPtr.Zero, out IntPtr activation) && activation.ToInt32()==4,
            "Capture rejects mouse activation without disabling the window");
        int attempts=0;
        window.MemoryCleanupForVerification=()=>{attempts++;return Task.FromResult(default(MemoryCleanupResult));};
        window.MemoryCard.RequestAction();await window.OrganizeMemoryAsync();
        check(attempts==0,"Screenshot mode prevents accidental memory cleanup even through an action request");
        window.MemoryCleanupForVerification=null;
        window.CollapsePanel();window.ExpandPanel();
        check(window.IsVisible && window.CaptureInteractionHeld,"Panel commands cannot close or reactivate the panel during screenshot selection");
        window.BeginCaptureShortcut();window.CancelCaptureShortcutPending();
        check(!window.CaptureInteractionHeld && window.IsHitTestVisible==oldHitTest && NativeMethods.IsWindowEnabled(handle),
            "Canceling screenshot mode restores input without a stuck disabled window");
        check(((NativeMethods.GetExtendedWindowStyle(handle)&8)!=0)==oldTopmost && ToolTipService.GetIsEnabled(window.MemoryCard),
            "Screenshot recovery restores the prior topmost and tooltip settings");
        check(!window.TrySuppressCaptureInput(0x0201, IntPtr.Zero, out _) &&
              !window.TrySuppressCaptureInput(0x0100, IntPtr.Zero, out _),
            "Screenshot recovery releases the native input guard");
        NativeMethods.SetCaptureTopmost(handle,false);
        window.BeginCaptureShortcut();window.CancelCaptureShortcutPending();
        check((NativeMethods.GetExtendedWindowStyle(handle)&8)==0,"Recovery preserves a panel that was originally non-topmost");
        NativeMethods.SetCaptureTopmost(handle,oldTopmost);
        window.BeginCaptureShortcut();await Task.Delay(1650);window.UpdateCaptureVisibility();
        check(!window.CaptureInteractionHeld && NativeMethods.IsWindowEnabled(handle),"Unused expanded screenshot shortcut expires and restores input");
    }
}
