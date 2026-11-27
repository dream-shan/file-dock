using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileDockDemo;

internal sealed class ApplicationsView : UserControl,IDisposable
{
    private const string DragFormat="FileDock.ApplicationEntry";
    private readonly WrapPanel _items=new();
    private readonly ScrollViewer _scroll;
    private readonly Func<IReadOnlyList<ApplicationEntry>> _entries;
    private readonly Action<ApplicationEntry> _launch,_edit,_shortcut;
    private readonly Func<string[],Task> _add;
    private readonly Func<string,int,Task> _move;
    private readonly Func<string,Task> _remove;
    private readonly Action<bool> _menu;
    private readonly Dictionary<string,ImageSource> _icons=new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _iconWorkers=new(2);
    private string? _pressed;
    private Point _start;
    private bool _suppressClick,_disposed;
    internal ApplicationsView(Func<IReadOnlyList<ApplicationEntry>> entries,Action addDialog,Func<string[],Task> add,
        Action<ApplicationEntry> launch,Action<ApplicationEntry> edit,Action<ApplicationEntry> shortcut,
        Func<string,int,Task> move,Func<string,Task> remove,Action<bool> menu)
    {
        _entries=entries;_launch=launch;_edit=edit;_shortcut=shortcut;_move=move;_remove=remove;_add=add;_menu=menu;
        var root=new Grid();root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new());
        var header=new DockPanel{Margin=new Thickness(0,0,0,10)};var addButton=Ui.Button("＋ 添加",addDialog);addButton.SetResourceReference(StyleProperty,"QuietButton");addButton.SetResourceReference(ForegroundProperty,"Accent");addButton.Margin=new Thickness(0);DockPanel.SetDock(addButton,Dock.Right);header.Children.Add(addButton);var title=Ui.Text("应用",18);title.FontWeight=FontWeights.SemiBold;title.Margin=new Thickness(0);header.Children.Add(title);root.Children.Add(header);
        _scroll=Ui.Scroll(_items);Grid.SetRow(_scroll,1);root.Children.Add(_scroll);Content=root;AllowDrop=true;
        SizeChanged+=(_,_)=>SizeTiles();IsVisibleChanged+=(_,_)=>{if(IsVisible)Render();};
        PreviewDragOver+=(_,e)=>{e.Effects=e.Data.GetDataPresent(DragFormat)?DragDropEffects.Move:e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;};
        Drop+=async(_,e)=>{e.Handled=true;if(e.Data.GetData(DataFormats.FileDrop) is string[] paths)await _add(paths);};
    }
    internal void Render()
    {
        if(_disposed)return;double offset=_scroll.VerticalOffset;_items.Children.Clear();
        var entries=_entries();
        if(entries.Count==0){_items.Children.Add(Ui.Text("拖入程序或快捷方式，或点击“＋ 添加”。"));return;}
        foreach(var entry in entries)
        {
            var image=new Image{Width=34,Height=34};RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
            var fallback=new TextBlock{Text="\uE7C3",FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=24,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};fallback.SetResourceReference(TextBlock.ForegroundProperty,"Accent");
            var iconPlate=new Border{Width=46,Height=46,CornerRadius=new CornerRadius(12),Margin=new Thickness(0,0,0,6),BorderThickness=new Thickness(1)};
            iconPlate.SetResourceReference(Border.BackgroundProperty,"PaneCard");iconPlate.SetResourceReference(Border.BorderBrushProperty,"PaneOutline");
            var icon=new Grid();icon.Children.Add(fallback);icon.Children.Add(image);iconPlate.Child=icon;
            var content=new StackPanel{HorizontalAlignment=HorizontalAlignment.Center};content.Children.Add(iconPlate);var label=new TextBlock{Text=entry.DisplayName,FontSize=11.5,TextTrimming=TextTrimming.CharacterEllipsis,TextAlignment=TextAlignment.Center};content.Children.Add(label);
            var button=new Button{Content=content,Height=84,Margin=new Thickness(2),Padding=new Thickness(5,6,5,5),ToolTip=entry.DisplayName+"\n"+entry.Path,Tag=entry.Id};button.SetResourceReference(StyleProperty,"QuietButton");button.SetResourceReference(ForegroundProperty,"Ink");System.Windows.Automation.AutomationProperties.SetName(button,entry.DisplayName);
            button.Click+=(_,_)=>{if(!_suppressClick)_launch(entry);};
            button.PreviewMouseLeftButtonDown+=(_,e)=>{_pressed=entry.Id;_start=e.GetPosition(this);_suppressClick=false;};
            button.PreviewMouseMove+=(_,e)=>{
                var point=e.GetPosition(this);if(e.LeftButton!=MouseButtonState.Pressed||_pressed!=entry.Id||Math.Abs(point.X-_start.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(point.Y-_start.Y)<SystemParameters.MinimumVerticalDragDistance)return;
                _pressed=null;_suppressClick=true;DragDrop.DoDragDrop(button,new DataObject(DragFormat,entry.Id),DragDropEffects.Move);
            };
            button.Drop+=async(_,e)=>{if(e.Data.GetData(DragFormat) is not string id)return;e.Handled=true;int index=_entries().ToList().FindIndex(a=>a.Id==entry.Id);await _move(id,index);};
            var menu=new ContextMenu();void Item(string text,Action action){var item=new MenuItem{Header=text};item.Click+=(_,_)=>action();menu.Items.Add(item);}
            Item("编辑入口…",()=>_edit(entry));Item("设置快捷键…",()=>_shortcut(entry));
            Item("向前移动",async()=>await _move(entry.Id,_entries().ToList().FindIndex(a=>a.Id==entry.Id)-1));
            Item("向后移动",async()=>await _move(entry.Id,_entries().ToList().FindIndex(a=>a.Id==entry.Id)+1));
            Item("移除入口",async()=>await _remove(entry.Id));menu.Opened+=(_,_)=>_menu(true);menu.Closed+=(_,_)=>_menu(false);button.ContextMenu=menu;
            button.Loaded+=async(_,_)=>{if(IsVisible)await LoadIcon(entry.Path,image,fallback);};_items.Children.Add(button);
        }
        SizeTiles();_scroll.ScrollToVerticalOffset(offset);
    }
    private void SizeTiles()
    {
        double width=Math.Max(1,_scroll.ViewportWidth>0?_scroll.ViewportWidth:ActualWidth);int columns=Math.Max(1,(int)(width/78));double cell=Math.Floor(width/columns)-4;
        foreach(var button in _items.Children.OfType<Button>())button.Width=Math.Max(50,cell);
    }
    private async Task LoadIcon(string path,Image target,TextBlock fallback)
    {
        if(_disposed)return;
        if(_icons.TryGetValue(path,out var cached)){target.Source=cached;fallback.Visibility=Visibility.Collapsed;return;}
        await _iconWorkers.WaitAsync();
        try
        {
            if(_disposed||!IsVisible)return;
            var source=await Task.Run(()=>ReadIcon(path));if(source==null||_disposed)return;
            if(_icons.Count>=64)_icons.Remove(_icons.Keys.First());_icons[path]=source;target.Source=source;fallback.Visibility=Visibility.Collapsed;
        }
        catch(Exception){/* An unavailable shell icon keeps the fallback; launch still reports target failures. */}
        finally{_iconWorkers.Release();}
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct ShellFileInfo{internal IntPtr Icon;internal int Index;internal uint Attributes;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]internal string Display;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=80)]internal string Type;}
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr SHGetFileInfo(string path,uint attributes,out ShellFileInfo info,uint size,uint flags);
    [DllImport("user32.dll")]private static extern bool DestroyIcon(IntPtr icon);
    private static ImageSource? ReadIcon(string path)
    {
        if(!File.Exists(path))return null;
        SHGetFileInfo(path,0,out var info,(uint)Marshal.SizeOf<ShellFileInfo>(),0x100);
        if(info.Icon==IntPtr.Zero)return null;
        try{var image=Imaging.CreateBitmapSourceFromHIcon(info.Icon,Int32Rect.Empty,BitmapSizeOptions.FromWidthAndHeight(36,36));image.Freeze();return image;}finally{DestroyIcon(info.Icon);}
    }
    public void Dispose(){_disposed=true;_icons.Clear();}
}
