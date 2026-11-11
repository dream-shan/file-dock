using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace FileDockDemo;

internal static class DesktopPresenceVerification
{
    internal static async Task Run(MainWindow window, string directory, Action<bool,string> check)
    {
        var policy = new DesktopPresence();
        PanelDisplayDecision Decide(PanelDisplayMode mode, bool visible=true, bool blocked=false, bool interacting=false, bool fullscreen=false) =>
            policy.Decide(mode, visible, blocked, interacting, fullscreen);
        check(Decide(PanelDisplayMode.Automatic,visible:false).Visible, "Uncovered desktop automatically reveals the panel");
        check(!Decide(PanelDisplayMode.Automatic,blocked:true).Visible, "An overlapping window automatically collapses the panel");
        check(Decide(PanelDisplayMode.Automatic).Visible, "Non-overlapping applications leave the panel visible");
        policy.Reveal();
        check(Decide(PanelDisplayMode.Automatic,blocked:true,interacting:true)==new PanelDisplayDecision(true,true), "Explicit reveal temporarily floats above an application");
        policy.Observe(false);
        check(!Decide(PanelDisplayMode.Automatic,blocked:true).Visible, "Returning to another application ends the temporary reveal");
        check(Decide(PanelDisplayMode.Pinned,blocked:true)==new PanelDisplayDecision(true,true), "Pin stays expanded and topmost across application switches");
        policy.Dismiss();
        for(int i=0;i<6;i++)policy.Observe(false);
        check(!Decide(PanelDisplayMode.Automatic,visible:false).Visible, "Application and desktop switches never undo manual collapse");
        policy.Observe(true);
        check(policy.Dismissed,"Owned windows do not clear manual dismissal");
        policy.Reveal();
        check(!policy.Dismissed && policy.ManuallyRevealed,"An explicit reveal clears manual dismissal");
        check(Decide(PanelDisplayMode.Manual,blocked:true,interacting:true).Visible,"Manual mode stays open during panel interaction");
        policy.Observe(false);
        check(!Decide(PanelDisplayMode.Manual).Visible,"An external click closes manual mode even without a focus change");
        check(!Decide(PanelDisplayMode.Manual,visible:false).Visible,"An uncovered desktop never automatically opens manual mode");
        check(!Decide(PanelDisplayMode.Automatic,fullscreen:true).Visible,"Automatic mode yields to fullscreen applications");
        check(Decide(PanelDisplayMode.Pinned,blocked:true,fullscreen:true).Topmost,"Fullscreen does not silently cancel pinned mode");
        foreach(var mode in Enum.GetValues<PanelDisplayMode>())
        {
            policy.Dismiss();
            check(!Decide(mode,visible:false).Visible,$"Manual collapse overrides {mode}");
        }

        string data=Path.Combine(directory,"desktop-mode-config");Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data,"state.json"),"{\"Version\":1,\"State\":{\"KeepOpen\":true}}");
        var storage=new StorageService(data);var state=storage.Load();
        check(state.DisplayMode==PanelDisplayMode.Pinned && state.KeepOpen && state.AlwaysOnTop,"Legacy keep-open migrates to fixed topmost mode");
        state.DisplayMode=PanelDisplayMode.Manual;storage.Save(state);
        check(storage.Load().DisplayMode==PanelDisplayMode.Manual && !storage.Load().AutoDesktop,"Manual display mode persists");
        foreach(var old in new[]{new AppState{AlwaysOnTop=true,AutoDesktop=false},new AppState{AutoDesktop=false},new AppState()})
        {
            bool pinned=old.AlwaysOnTop,automatic=old.AutoDesktop;
            DesktopPresence.NormalizeMode(old);
            check(old.DisplayMode==(pinned?PanelDisplayMode.Pinned:automatic?PanelDisplayMode.Automatic:PanelDisplayMode.Manual),"Legacy display settings preserve user intent");
            check(old.UnpinnedMode==(automatic?PanelDisplayMode.Automatic:PanelDisplayMode.Manual),"Legacy unpinned preference survives migration");
        }

        var oldMode=window.State.DisplayMode!.Value;
        window.SetDisplayMode(PanelDisplayMode.Manual);
        window.ExpandPanel();
        IntPtr panel=new WindowInteropHelper(window).Handle;
        var fixture=new Window{Width=160,Height=120,Left=window.Left+20,Top=window.Top+20,
            ShowActivated=false,ShowInTaskbar=false,WindowStyle=WindowStyle.None,Topmost=true};
        try
        {
            var foreground=NativeMethods.GetForegroundWindow();
            window.ShowPanel(activate:false);
            check(NativeMethods.GetForegroundWindow()==foreground && (NativeMethods.GetExtendedWindowStyle(panel)&8)!=0,
                "Passive reveal temporarily covers applications without taking focus");
            window.KeepOpenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            IntPtr rail=new WindowInteropHelper(window.PageTabsWindow!).Handle;
            check(window.IsPinned && (NativeMethods.GetExtendedWindowStyle(panel)&8)!=0 && (NativeMethods.GetExtendedWindowStyle(rail)&8)!=0,
                "Pin immediately sets both panel and page rail topmost");
            fixture.Show();fixture.Activate();await Task.Delay(40);
            foreground=NativeMethods.GetForegroundWindow();window.ReconcileDesktopPresence();
            check(window.IsVisible && window.PageTabsWindow!.IsVisible && (NativeMethods.GetExtendedWindowStyle(panel)&8)!=0 && NativeMethods.GetForegroundWindow()==foreground,
                "Activating another window leaves the pinned panel visible without stealing focus");
            window.CollapsePanel();window.ReconcileDesktopPresence();
            check(!window.IsVisible && !window.PageTabsWindow!.IsVisible && window.IsPinned,"Explicit collapse hides the entire pinned panel and preserves its mode");
            window.ShowPanel(activate:false);
            check(window.IsVisible && (NativeMethods.GetExtendedWindowStyle(panel)&8)!=0,"Explicit reveal restores the pinned panel");
            window.KeepOpenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.WaitForSavesAsync();
            check(window.State.DisplayMode==PanelDisplayMode.Manual && (NativeMethods.GetExtendedWindowStyle(panel)&8)==0 && window.Storage.Load().DisplayMode==PanelDisplayMode.Manual,
                "Unpin restores and persists the previous manual mode");
            NativeMethods.SetCaptureTopmost(panel,false);await Task.Delay(80);
            IntPtr other=new WindowInteropHelper(fixture).Handle;
            check(NativeMethods.IsPanelOccluded(panel,true)==true,"Native occlusion detects a visible overlapping window above the panel");
            check(NativeMethods.IsPanelOccluded(panel)==false,"Own process popups are excluded from desktop occlusion");
            fixture.Left=window.Left+window.ActualWidth+80;await Task.Delay(40);
            check(NativeMethods.IsPanelOccluded(panel,true)==false,"Native occlusion ignores non-overlapping windows");
            fixture.Left=window.Left+20;fixture.Hide();
            check(!NativeMethods.IsDesktopBlocker(other,true),"Hidden windows do not block desktop presence");
            fixture.Show();fixture.WindowState=WindowState.Minimized;await Task.Delay(40);
            check(!NativeMethods.IsDesktopBlocker(other,true),"Minimized windows do not block desktop presence");
            fixture.WindowState=WindowState.Normal;NativeMethods.SetCaptureTopmost(other,false);NativeMethods.RaiseWithoutActivation(panel);
            check(NativeMethods.IsPanelOccluded(panel,true)==false,"Windows beneath the panel do not count as occlusion");
        }
        finally{fixture.Close();window.SetDisplayMode(oldMode);}
        check(!window.DesktopPresencePending,"Self-tests install no idle desktop polling loop");
    }
}
