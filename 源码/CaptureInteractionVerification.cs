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
        check(window.IsVisible && window.CaptureInteractionHeld && !window.IsHitTestVisible && !NativeMethods.IsWindowEnabled(handle),
            "Expanded capture preserves panel pixels while disabling native and WPF input");
        check((NativeMethods.GetExtendedWindowStyle(handle) & 8) == 0,
            "Expanded capture relinquishes native topmost status to the screenshot overlay");
        check(!ToolTipService.GetIsEnabled(window.MemoryCard) && !window.MemoryCard.ActionAvailable,
            "Screenshot mode suppresses tooltips and the memory action");
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
        NativeMethods.SetCaptureTopmost(handle,false);
        window.BeginCaptureShortcut();window.CancelCaptureShortcutPending();
        check((NativeMethods.GetExtendedWindowStyle(handle)&8)==0,"Recovery preserves a panel that was originally non-topmost");
        NativeMethods.SetCaptureTopmost(handle,oldTopmost);
        window.BeginCaptureShortcut();await Task.Delay(1650);window.UpdateCaptureVisibility();
        check(!window.CaptureInteractionHeld && NativeMethods.IsWindowEnabled(handle),"Unused expanded screenshot shortcut expires and restores input");
    }
}
