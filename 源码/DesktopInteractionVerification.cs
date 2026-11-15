using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace FileDockDemo;

// Opt-in interactive tests. A separate fixture process exercises production
// WinEvent/mouse hooks; no direct calls to reconciliation or click handlers.
internal static class DesktopInteractionVerification
{
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int L,T,R,B; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h,out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h,int command);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h,uint message,IntPtr w,IntPtr l);
    private delegate bool WindowCallback(IntPtr h,IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback,IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr h,int command);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr h,StringBuilder name,int length);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h,out uint process);
    [StructLayout(LayoutKind.Sequential)] private struct Placement { public int Length,Flags,Show; public Point Min,Max; public Rect Normal; }
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr h,ref Placement placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr h,ref Placement placement);
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X,Y; public uint Data,Flags,Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public MouseInput Mouse; }
    [DllImport("user32.dll",SetLastError=true)] private static extern uint SendInput(uint count,Input[] inputs,int size);

    internal static int RunFixture()
    {
        var app=new Application();
        return app.Run(new Window { Title="FileDock external desktop test fixture", Width=180,Height=140,
            WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,
            WindowStartupLocation=WindowStartupLocation.Manual,Left=20,Top=20 });
    }
    private static void Click(IntPtr target)
    {
        SetForegroundWindow(target);
        GetWindowRect(target,out var r);
        if(!SetCursorPos(r.L+30,r.T+60)) throw new InvalidOperationException("No interactive mouse desktop");
        var inputs=new[]{new Input { Mouse=new MouseInput {Flags=2} },new Input {Mouse=new MouseInput {Flags=4}}};
        if(SendInput(2,inputs,Marshal.SizeOf<Input>())!=2) throw new InvalidOperationException("Desktop mouse input was blocked");
    }
    internal static async Task Run(MainWindow window,string directory)
    {
        using var cancellation=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\FileDockDemo.DesktopTest.Cancel");
        GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(),out uint foregroundPid);
        if(foregroundPid!=0 && Process.GetProcessById((int)foregroundPid).ProcessName.Equals("LockApp",StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unlock the Windows desktop before running interactive tests.");
        var report=new List<string>();
        GetCursorPos(out var cursor);
        IntPtr previous=NativeMethods.GetForegroundWindow();
        var minimized=new List<(IntPtr Handle,int Restore)>();
        using var fixture=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"--desktop-fixture") {UseShellExecute=false});
        if(fixture==null) throw new InvalidOperationException("Fixture did not start");
        async Task Wait(Func<bool> condition,string label)
        {
            var until=Stopwatch.StartNew();
            while(!condition() && until.ElapsedMilliseconds<4000)
            {
                if(cancellation.WaitOne(0))throw new OperationCanceledException("Desktop test cancelled; restoring windows");
                await Task.Delay(50);
            }
            if(cancellation.WaitOne(0))throw new OperationCanceledException("Desktop test cancelled; restoring windows");
            if(!condition())
            {
                var native=new List<string>{"foreground="+NativeMethods.GetForegroundWindow()};
                EnumWindows((h,_)=>{if(NativeMethods.IsDesktopBlocker(h) && GetWindowRect(h,out var r)){
                    var name=new StringBuilder(128);GetClassName(h,name,128);
                    native.Add($"handle={h}; class={name}; bounds={r.L},{r.T},{r.R},{r.B}");}return true;},IntPtr.Zero);
                File.WriteAllLines(Path.Combine(directory,"desktop-state.txt"),native);
                throw new InvalidOperationException(label+"; "+window.DiagnosticSnapshot());
            }
            report.Add("PASS: "+label);
        }
        try
        {
            await Wait(()=>{fixture.Refresh();return fixture.MainWindowHandle!=IntPtr.Zero;},"External fixture has a native window");
            IntPtr other=fixture.MainWindowHandle;
            window.Left=500;window.Top=50;window.Width=380;window.Height=500;
            window.State.PanelDocked=false;window.State.PanelLeft=500;window.State.PanelTop=50;
            window.SetDisplayMode(PanelDisplayMode.Automatic);
            window.ShowPanel(false,false);
            IntPtr panel=new WindowInteropHelper(window).Handle;
            GetWindowRect(panel,out var bounds);
            // Establish a genuinely free desktop region; restore only windows
            // minimized by this test, preserving their normal/maximized state.
            EnumWindows((h,_)=>{
                if(h!=other && NativeMethods.IsDesktopBlocker(h) && GetWindowRect(h,out var r) &&
                    r.R>bounds.L-32 && r.L<bounds.R && r.B>bounds.T && r.T<bounds.B)
                { minimized.Add((h,IsZoomed(h)?3:9));ShowWindowAsync(h,6); }
                return true;
            },IntPtr.Zero);
            await Task.Delay(500);
            void Move(bool overlap) => SetWindowPos(other,IntPtr.Zero,overlap?bounds.L+20:Math.Max(0,bounds.L-320),bounds.T+20,180,140,0x0040);
            for(int cycle=0;cycle<20;cycle++)
            {
                Move(false);Click(other);
                await Wait(()=>window.IsVisible && window.PageTabsWindow!.IsVisible && (NativeMethods.GetExtendedWindowStyle(panel)&8)==0 && NativeMethods.GetForegroundWindow()==other,$"Round {cycle+1}: free area remains visible without focus or topmost");
                Move(true);
                await Wait(()=>!window.IsVisible && !window.PageTabsWindow!.IsVisible,$"Round {cycle+1}: overlap hides panel and rail");
                await Task.Delay(350);
                if(window.IsVisible) throw new InvalidOperationException("Hidden panel reopened while overlap persisted");
                Move(false);
                await Wait(()=>window.IsVisible && NativeMethods.GetForegroundWindow()==other,$"Round {cycle+1}: movement restores without stealing focus");
            }
            Move(true);
            await Wait(()=>!window.IsVisible,"Overlap before minimization hides panel");
            ShowWindow(other,6);
            await Wait(()=>window.IsVisible,"Minimizing the blocker restores automatic panel");
            ShowWindow(other,9);Move(false);Click(other);
            SetWindowPos(other,IntPtr.Zero,bounds.L-28,bounds.T+110,20,100,0x0040);
            await Wait(()=>!window.IsVisible && !window.PageTabsWindow!.IsVisible,"Rail-only overlap hides panel and rail");
            Move(false);
            await Wait(()=>window.IsVisible,"Moving away from the rail restores panel");
            ShowWindow(other,3);
            await Wait(()=>!window.IsVisible,"Maximized blocker hides automatic panel");
            ShowWindow(other,9);Move(false);
            await Wait(()=>window.IsVisible,"Restoring maximized blocker to free area restores panel");
            window.CollapsePanel();Move(true);Move(false);
            await Task.Delay(700);
            await Wait(()=>!window.IsVisible,"Explicit collapse survives external window events");
            window.SetDisplayMode(PanelDisplayMode.Manual);window.ShowPanel(false,false);Click(other);
            await Wait(()=>!window.IsVisible,"Real external click closes manual mode after passive reveal");
            window.SetDisplayMode(PanelDisplayMode.Pinned);window.ShowPanel(false,false);Move(false);Click(other);
            await Wait(()=>window.IsVisible && NativeMethods.GetForegroundWindow()==other && (NativeMethods.GetExtendedWindowStyle(panel)&8)!=0,"Pinned panel remains after a click reaching the external window");
            Move(true);
            await Task.Delay(700);
            await Wait(()=>window.IsVisible && window.PageTabsWindow!.IsVisible && (NativeMethods.GetExtendedWindowStyle(panel)&8)!=0,"Pinned panel and rail survive actual external clicks");
            ShowWindow(other,6);
            window.Left=SystemParameters.WorkArea.Right-window.ActualWidth-20;
            window.Top=50;window.UpdateLayout();
            GetWindowRect(panel,out bounds);
            foreach(string client in new[]{"ChatGPT","Weixin"})
            {
                IntPtr target=IntPtr.Zero;
                EnumWindows((h,_)=>{
                    GetWindowThreadProcessId(h,out uint pid);
                    try {
                        var placement=new Placement{Length=Marshal.SizeOf<Placement>()};
                        if(pid!=0 && IsWindowVisible(h) && Process.GetProcessById((int)pid).ProcessName.Equals(client,StringComparison.OrdinalIgnoreCase) &&
                            GetWindowPlacement(h,ref placement) && placement.Normal.R-placement.Normal.L>300 && placement.Normal.B-placement.Normal.T>200) {target=h;return false;}
                    } catch(ArgumentException) { }
                    return true;
                },IntPtr.Zero);
                if(target==IntPtr.Zero){report.Add("SKIP: "+client+" has no available client window");continue;}
                var saved=new Placement{Length=Marshal.SizeOf<Placement>()};
                if(!GetWindowPlacement(target,ref saved)) throw new InvalidOperationException("Cannot save client window placement");
                try
                {
                    window.SetDisplayMode(PanelDisplayMode.Automatic);window.ShowPanel(false,false);
                    ShowWindow(target,9);
                    await Task.Delay(600);
                    for(int cycle=0;cycle<3;cycle++)
                    {
                        SetWindowPos(target,IntPtr.Zero,20,bounds.T,Math.Min(900,bounds.L-100),600,0x0040);
                        GetWindowRect(target,out var clientRect);
                        if(clientRect.R>=bounds.L-50)throw new InvalidOperationException(client+" minimum size leaves no free test area");
                        SetForegroundWindow(target);SetCursorPos((clientRect.L+clientRect.R)/2,clientRect.T+8);
                        var click=new[]{new Input{Mouse=new MouseInput{Flags=2}},new Input{Mouse=new MouseInput{Flags=4}}};
                        if(SendInput(2,click,Marshal.SizeOf<Input>())!=2)throw new InvalidOperationException("Client click blocked");
                        await Wait(()=>window.IsVisible && NativeMethods.GetForegroundWindow()==target && (NativeMethods.GetExtendedWindowStyle(panel)&8)==0,client+$" round {cycle+1}: free area remains visible without stealing focus");
                        // Reveal passively while the same client keeps focus, then
                        // click it again: this path has no foreground-change event.
                        window.ShowPanel(false,false);
                        await Task.Delay(600); // Avoid a title-bar double-click.
                        SendInput(2,click,Marshal.SizeOf<Input>());
                        await Wait(()=>window.IsVisible && (NativeMethods.GetExtendedWindowStyle(panel)&8)==0,client+$" round {cycle+1}: click ends passive topmost reveal");
                        SetWindowPos(target,IntPtr.Zero,bounds.L+20,bounds.T+20,700,600,0x0040);
                        await Wait(()=>!window.IsVisible && !window.PageTabsWindow!.IsVisible,client+$" round {cycle+1}: overlap hides panel and rail");
                        await Task.Delay(1000);
                        if(window.IsVisible)throw new InvalidOperationException(client+" caused a hidden panel to reopen");
                        SetWindowPos(target,IntPtr.Zero,20,bounds.T,900,600,0x0040);
                        await Wait(()=>window.IsVisible && NativeMethods.GetForegroundWindow()==target,client+$" round {cycle+1}: moving away restores without focus change");
                    }
                }
                finally { SetWindowPlacement(target,ref saved); }
            }
        }
        finally
        {
            fixture.Refresh();
            if(!fixture.HasExited){PostMessage(fixture.MainWindowHandle,0x10,IntPtr.Zero,IntPtr.Zero);await Task.Delay(200);if(!fixture.HasExited)fixture.Kill();}
            foreach(var item in minimized)ShowWindowAsync(item.Handle,item.Restore);
            SetCursorPos(cursor.X,cursor.Y);if(previous!=IntPtr.Zero)SetForegroundWindow(previous);
            File.WriteAllLines(Path.Combine(directory,"desktop-verification.txt"),report);
        }
    }
}
