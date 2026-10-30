using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace FileDockDemo;

internal static class DesktopPresenceVerification
{
    internal static async Task Run(MainWindow window, string directory, Action<bool,string> check)
    {
        var policy = new DesktopPresence();
        check(policy.WantVisible(false, false, false, false), "Uncovered desktop automatically reveals the panel");
        check(!policy.WantVisible(true, false, true, false), "An overlapping window automatically collapses the panel");
        check(policy.WantVisible(true, false, false, false), "Unrelated non-overlapping windows leave the panel visible");
        policy.Reveal();
        check(policy.WantVisible(true, false, true, true), "Explicit reveal remains interactive above an overlapping application");
        policy.Observe(false, false);
        check(!policy.WantVisible(true, false, true, false), "Returning to the overlapping application ends a temporary reveal");
        check(policy.WantVisible(true, true, true, false), "Keep-open pin overrides automatic occlusion");
        policy.Dismiss(false); policy.Observe(true, false);
        check(!policy.WantVisible(false, false, false, false), "Manual collapse does not immediately bounce open on the desktop");
        policy.Observe(false, false); policy.Observe(true, false);
        check(policy.WantVisible(false, false, false, false), "Returning to the desktop after another application clears manual dismissal");
        policy.Dismiss(true); policy.Observe(false, true);
        check(policy.Dismissed, "Owned windows do not clear a manual dismissal");
        policy.Reveal();
        check(!policy.Dismissed && policy.ManuallyRevealed, "Explicit edge or hotkey reveal clears a manual dismissal");
        policy.Dismiss(false);
        check(!policy.WantVisible(false, true, false, false), "Manual collapse takes precedence over a persistent keep-open pin");

        string data = Path.Combine(directory, "desktop-mode-config"); Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data,"state.json"), "{\"Version\":1,\"State\":{\"KeepOpen\":true}}");
        var storage = new StorageService(data); var state = storage.Load();
        check(state.AutoDesktop && state.KeepOpen, "Old configuration enables desktop mode without discarding the existing pin");
        state.AutoDesktop = false; storage.Save(state);
        check(!storage.Load().AutoDesktop, "The desktop behavior setting persists when disabled");

        window.ExpandPanel();
        IntPtr panel = new WindowInteropHelper(window).Handle;
        bool wasTopmost = (NativeMethods.GetExtendedWindowStyle(panel) & 8) != 0;
        var fixture = new Window { Width = 160, Height = 120, Left = window.Left + 20, Top = window.Top + 20,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Topmost = true };
        try
        {
            NativeMethods.SetCaptureTopmost(panel, false); fixture.Show(); await Task.Delay(80);
            IntPtr other = new WindowInteropHelper(fixture).Handle;
            check(NativeMethods.IsPanelOccluded(panel, true) == true, "Native overlap detection sees a visible window above the panel");
            check(NativeMethods.IsPanelOccluded(panel) == false, "Own process popups are excluded from desktop occlusion");
            fixture.Left = window.Left + window.ActualWidth + 80; await Task.Delay(40);
            check(NativeMethods.IsPanelOccluded(panel, true) == false, "Native overlap detection ignores windows outside the panel bounds");
            fixture.Left = window.Left + 20; fixture.Hide();
            check(!NativeMethods.IsDesktopBlocker(other,true), "Hidden windows do not count as desktop blockers");
            fixture.Show(); fixture.WindowState = WindowState.Minimized; await Task.Delay(40);
            check(!NativeMethods.IsDesktopBlocker(other,true), "Minimized windows do not count as desktop blockers");
            fixture.WindowState = WindowState.Normal; NativeMethods.SetCaptureTopmost(other,false); NativeMethods.RaiseWithoutActivation(panel);
            check(NativeMethods.IsPanelOccluded(panel,true) == false, "Overlapping windows below the panel do not count as occlusion");
        }
        finally { fixture.Close(); NativeMethods.SetCaptureTopmost(panel,wasTopmost); }
        check(!window.DesktopPresencePending, "Self-tests do not install an idle desktop polling loop");
    }
}
