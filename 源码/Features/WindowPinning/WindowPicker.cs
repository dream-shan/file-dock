using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace FileDockDemo.Features.WindowPinning;

internal sealed class WindowPicker : Window
{
    private bool _selected;
    internal WindowPicker(Action<IntPtr> complete)
    {
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;
        AllowsTransparency=true;Background=new SolidColorBrush(Color.FromArgb(35,0,0,0));
        Topmost=true;Cursor=Cursors.Cross;
        Content=new TextBlock{Text="点击要置顶或取消置顶的窗口 · Esc 取消",Foreground=Brushes.White,
            Background=Brushes.Black,Padding=new Thickness(16),FontSize=18,
            HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,IsHitTestVisible=false};
        Loaded+=(_,_)=>{
            var bounds=System.Windows.Forms.SystemInformation.VirtualScreen;
            PinNative.SetWindowPos(new WindowInteropHelper(this).Handle,new IntPtr(-1),bounds.X,bounds.Y,bounds.Width,bounds.Height,0);
            Activate();Focus();
        };
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){e.Handled=true;Close();}};
        MouseLeftButtonUp+=(_,e)=>{
            if(_selected)return;_selected=true;e.Handled=true;
            PinNative.GetCursorPos(out var point);Hide();
            Dispatcher.BeginInvoke(()=>{Close();complete(PinNative.GetAncestor(PinNative.WindowFromPoint(point),2));});
        };
        MouseRightButtonUp+=(_,_)=>Close();
        Deactivated+=(_,_)=>{if(!_selected)Close();};
    }
}
