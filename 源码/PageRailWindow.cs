using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
namespace FileDockDemo;

// A separate HWND gives capture tools a body-only main window rectangle.
internal sealed class PageRailWindow : Window
{
    private readonly MainWindow _owner;
    private readonly Grid _surface=new();
    private readonly FrameworkElement _tabs,_join;
    private bool _closed;
    internal PageRailWindow(MainWindow owner,FrameworkElement tabs,FrameworkElement join)
    {
        _owner=owner;_tabs=tabs;_join=join;
        Title="文件坞标签";Width=34;Height=96;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;
        AllowsTransparency=true;Background=Brushes.Transparent;ShowInTaskbar=false;ShowActivated=false;Focusable=false;
        Resources=owner.Resources;Content=_surface;
        tabs.Margin=new Thickness(0);join.Margin=new Thickness(0);_surface.Children.Add(tabs);_surface.Children.Add(join);
        SourceInitialized+=(_,_)=>{var h=new WindowInteropHelper(this).Handle;NativeMethods.HideFromTaskSwitcher(h);HwndSource.FromHwnd(h)?.AddHook(Hook);};
        PreviewKeyDown+=(_,e)=>_owner.HandleRailKey(e);
        Closed+=(_,_)=>_closed=true;
    }
    internal static void MigratePosition(AppState state)
    {
        if(state.PanelGeometryVersion>=1)return;
        if(state.DockSide=="Right" && state.PanelLeft.HasValue)state.PanelLeft+=32;
        state.PanelGeometryVersion=1;
    }
    private IntPtr Hook(IntPtr hwnd,int msg,IntPtr w,IntPtr l,ref bool handled)
    {
        if(_owner.TrySuppressCaptureInput(msg,w,out var result)){handled=true;return result;}
        if(msg==0x21){handled=true;return new IntPtr(3);} // MA_NOACTIVATE, still deliver the click.
        return IntPtr.Zero;
    }
    internal void Sync(bool right,int selectedIndex,int count)
    {
        if(_closed)return;
        _tabs.HorizontalAlignment=HorizontalAlignment.Stretch;
        _join.HorizontalAlignment=right?HorizontalAlignment.Left:HorizontalAlignment.Right;
        count=Math.Max(1,count);double height=count*52-8;Height=height;_join.Margin=new Thickness(0,selectedIndex*52,0,0);
        _tabs.Margin=right?new Thickness(2,0,0,0):new Thickness(0,0,2,0);
        if(!_owner.IsVisible){if(IsVisible)Hide();return;}
        if(Owner==null)Owner=_owner;
        if(!IsVisible){Left=_owner.Left+(right?_owner.ActualWidth-2:-32);Top=_owner.Top+90;Show();}
        var h=new WindowInteropHelper(this).Handle;var parent=new WindowInteropHelper(_owner).Handle;
        var point=_owner.PointToScreen(new Point(right?_owner.ActualWidth-2:-32,90));double scale=NativeMethods.WindowDpi(parent)/96d;
        NativeMethods.PlaceRail(h,(int)Math.Round(point.X),(int)Math.Round(point.Y),(int)Math.Round(34*scale),(int)Math.Round(height*scale));
        UpdateLayout();NativeMethods.ShapeRailWindow(h,scale,right,selectedIndex,count);
        bool topmost=(NativeMethods.GetExtendedWindowStyle(parent)&8)!=0;
        if(((NativeMethods.GetExtendedWindowStyle(h)&8)!=0)!=topmost)NativeMethods.SetCaptureTopmost(h,topmost);
        IsHitTestVisible=!_owner.CaptureInteractionHeld;
    }
    internal void DisposeRail(){if(!_closed)Close();}
}
