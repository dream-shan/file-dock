using System;
using System.Threading.Tasks;
namespace FileDockDemo;
internal static class EdgeRecoveryVerification
{
    internal static async Task Run(MainWindow w,Action<bool,string> check)
    {
        var dock=w.DockEntry;
        try
        {
            w.CollapsePanel();dock.Show();var foreground=NativeMethods.GetForegroundWindow();
            check(dock.NativeTopmost,"Showing the entry establishes native topmost status");
            NativeMethods.SetCaptureTopmost(dock.Handle,false);
            check(!dock.NativeTopmost,"Fixture reproduces visible entry with lost native topmost flag");
            await Task.Delay(250);
            check(dock.IsVisible && dock.NativeTopmost,"Window-position event repairs entry demotion automatically");
            check(NativeMethods.GetForegroundWindow()==foreground,"Entry demotion recovery preserves foreground focus");
            NativeMethods.SetCaptureTopmost(dock.Handle,false);dock.Hide();dock.Show();
            check(dock.NativeTopmost,"Hide and show repair a cached WinForms topmost state");
            w.BeginCaptureShortcut();check(!dock.IsVisible,"Recovery does not reveal the entry during screenshot selection");
            w.CancelCaptureShortcutPending();await Task.Delay(250);
            check(dock.IsVisible && dock.NativeTopmost,"Screenshot cancellation restores a visible topmost entry");
        }
        finally{w.CancelCaptureShortcutPending();dock.Hide();w.ExpandPanel();}
    }
}
