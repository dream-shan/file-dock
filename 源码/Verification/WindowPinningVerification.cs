using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileDockDemo.Features.WindowPinning;

namespace FileDockDemo;

internal static class WindowPinningVerification
{
    private static IEnumerable<T> FindControls<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T found)yield return found;
        foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach(var nested in FindControls<T>(child))yield return nested;
    }
    private sealed class Desktop : IPinDesktop
    {
        internal readonly Dictionary<IntPtr,PinTarget> Targets=new();
        internal readonly HashSet<IntPtr> Top=new();
        internal bool Fail;
        internal int Writes;
        public PinTarget? Inspect(IntPtr h)=>Targets.GetValueOrDefault(h);
        public bool IsTopmost(IntPtr h)=>Top.Contains(h);
        public bool SetTopmost(IntPtr h,bool value){Writes++;if(Fail)return false;if(value)Top.Add(h);else Top.Remove(h);return true;}
    }
    internal static void Run(MainWindow host,string directory,Action<bool,string> check)
    {
        var desktop=new Desktop();var a=new IntPtr(100);var b=new IntPtr(200);
        desktop.Targets[a]=new(a,1,2,"test","教程窗口");desktop.Targets[b]=new(b,2,3,"test","文字窗口");
        using var module=new WindowPinningModule(desktop,false);
        check(module.Toggle(IntPtr.Zero).Contains("不可")&&desktop.Writes==0,"Pinning rejects invalid targets without changing desktop");
        module.Toggle(a);module.Toggle(b);
        check(module.Items.Count==2&&desktop.Top.Count==2,"Pinning manages multiple independent windows");
        desktop.Fail=true;module.Remove(a);
        check(module.Items.Count==2&&module.RestoreAll()==2,"Failed unpin preserves ownership for retry and exit reporting");
        desktop.Fail=false;module.Toggle(a);
        check(module.Items.Count==1&&!desktop.Top.Contains(a),"Pin hotkey toggles managed window off");
        module.RestoreAll();desktop.Top.Add(a);int writes=desktop.Writes;module.Toggle(a);
        check(module.Items.Count==0&&desktop.Writes==writes,"Originally topmost window is not adopted or changed");
        desktop.Top.Remove(a);module.Toggle(a);desktop.Targets[a]=new(a,99,99,"other","replacement");writes=desktop.Writes;module.RestoreAll();
        check(module.Items.Count==0&&desktop.Writes==writes,"Reused HWND with different identity is never modified during cleanup");
        desktop.Top.Remove(a);module.Toggle(a);desktop.Top.Remove(a);module.Refresh();
        check(module.Items.Count==0,"Externally revoked topmost is reconciled without fighting application");
        module.Toggle(b);desktop.Targets.Remove(b);module.Refresh();
        check(module.Items.Count==0,"Closed windows are removed from session records");
        desktop.Targets[b]=new(b,2,3,"test","文字窗口");desktop.Top.Remove(b);module.Toggle(b);
        int picks=0;
        var view=new WindowPinningView(host,module,()=>picks++,true,3,0x54,(_,_,_)=>"saved","快捷键已启用：Ctrl + Alt + T");
        var surface=view.View;surface.Width=300;
        var frame=new System.Windows.Controls.Border{Child=surface,Padding=new Thickness(16),Width=332};frame.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty,"Surface");
        frame.Measure(new Size(332,700));frame.Arrange(new Rect(0,0,332,frame.DesiredSize.Height));frame.UpdateLayout();
        var pick=FindControls<System.Windows.Controls.Button>(surface).SingleOrDefault(b=>Equals(b.Content,"窗口置顶"));
        check(pick!=null,"Compact tool row provides a direct window pin button");
        pick!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        check(picks==1,"Tools card starts window selection directly");
        var details=FindControls<System.Windows.Controls.Button>(surface).Single(b=>Equals(b.Content,"›"));
        details.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        check(picks==1 && FindControls<System.Windows.Controls.Expander>(surface).Any(e=>e.IsExpanded && e.Header?.ToString()?.StartsWith("已置顶")==true),"Details control opens the pinned list without selecting a window");
        var pins=FindControls<System.Windows.Controls.Expander>(surface).First();view.OnShown();
        module.Remove(b);check(Equals(pins.Header,"已置顶 0 个窗口"),"Visible tools card refreshes pinned window count");
        view.OnHidden();module.Toggle(b);check(Equals(pins.Header,"已置顶 0 个窗口"),"Hidden tools card pauses window-list updates");
        view.OnShown();check(Equals(pins.Header,"已置顶 1 个窗口"),"Returning to tools refreshes the pinned window count");
        var back=FindControls<System.Windows.Controls.Button>(surface).Single(b=>Equals(b.Content,"← 工具"));
        back.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        check(pick.IsVisible || pick.Visibility==Visibility.Visible,"Back returns to compact tool row");
        var bitmap=new RenderTargetBitmap(332,(int)Math.Ceiling(frame.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(frame);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(Path.Combine(directory,"window-pinning.png")))png.Save(stream);
        view.OnClosed();
        check(surface.ActualWidth==300,"Pin manager renders at compact panel width with scrollable content");
        using(var target=new System.Windows.Forms.Form{Text="FileDock pin verification",ShowInTaskbar=false,StartPosition=System.Windows.Forms.FormStartPosition.Manual,Left=-15000,Top=-15000})
        {
            target.Show();var native=new PinNative(true);var hwnd=target.Handle;var foreground=NativeMethods.GetForegroundWindow();
            using var real=new WindowPinningModule(native,false);
            real.Toggle(hwnd);
            check(real.Items.Count==1&&native.IsTopmost(hwnd),"Native pin operation sets WS_EX_TOPMOST on actual HWND");
            check(NativeMethods.GetForegroundWindow()==foreground,"Pin operation preserves foreground focus");
            real.RestoreAll();check(!native.IsTopmost(hwnd),"Native cleanup restores ordinary window z-order");
            check(new PinNative().Inspect(hwnd)==null,"Production provider excludes FileDock owned windows");
            real.Toggle(hwnd);target.Hide();real.Refresh();
            check(real.Items.Count==1,"Hidden pinned window remains owned for later restoration");
            real.RestoreAll();check(!native.IsTopmost(hwnd),"Exit restoration also clears topmost from hidden windows");
            target.Show();
            real.Toggle(hwnd);target.Close();real.Refresh();check(real.Items.Count==0,"Native closed target is safely forgotten");
        }
        var store=new StorageService(Path.Combine(directory,"pin-config"));var state=new AppState{WindowPinHotkeyEnabled=false,WindowPinModifiers=6,WindowPinKey=0x50};store.Save(state);var loaded=store.Load();
        check(!loaded.WindowPinHotkeyEnabled&&loaded.WindowPinModifiers==6&&loaded.WindowPinKey==0x50,"Custom pin shortcut persists through existing storage");
        state.WindowPinKey=-1;store.Save(state);check(store.Load().WindowPinKey==0x54,"Invalid persisted pin shortcut normalizes safely");
        module.Dispose();check(!desktop.Top.Contains(b),"Module disposal restores only owned windows");
    }
}
