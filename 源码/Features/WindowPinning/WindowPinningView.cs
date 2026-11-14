using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FileDockDemo.Contracts;

namespace FileDockDemo.Features.WindowPinning;

internal sealed class WindowPinningView : IModuleDialog
{
    private readonly IModuleHost _host;
    private readonly WindowPinningModule _module;
    private readonly StackPanel _list = new();
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,8) };
    private readonly TextBox _shortcut = new() { IsReadOnly = true, MinHeight = 30 };
    private readonly CheckBox _enabled = new() { Content = "启用置顶快捷键", Margin = new Thickness(0,8,0,8) };
    private readonly Action _pick;
    private readonly Func<bool,uint,int,string> _save;
    private uint _mods; private int _key;
    private bool _rendering;
    public FrameworkElement View { get; }
    public bool HasChanges => false;
    internal WindowPinningView(IModuleHost host, WindowPinningModule module, Action pick,
        bool enabled, uint mods, int key, Func<bool,uint,int,string> save, string registrationStatus)
    {
        _host=host;_module=module;_pick=pick;_save=save;_mods=mods;_key=key;
        var root=new StackPanel(); root.SetResourceReference(Control.ForegroundProperty,"Ink");
        root.Children.Add(new TextBlock { Text="窗口置顶", FontSize=20, FontWeight=FontWeights.SemiBold });
        root.Children.Add(new TextBlock { Text="选择普通应用窗口；Esc 取消选择。", TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,8,0,8) });
        root.Children.Add(Button("选择窗口置顶 / 取消",()=>{_host.CloseDialog(this);_pick();}));
        root.Children.Add(Button("取消全部置顶",()=>{int failures=_module.RestoreAll();_message.Text=failures==0?"已取消全部受管理的置顶。":$"仍有 {failures} 个窗口取消失败，请重试。";Render();}));
        root.Children.Add(new ScrollViewer { Content=_list, MaxHeight=130, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, Margin=new Thickness(0,8,0,8) });
        _enabled.SetResourceReference(Control.ForegroundProperty,"Ink");
        _enabled.IsChecked=enabled;root.Children.Add(_enabled);
        root.Children.Add(new TextBlock { Text="点击下框后按组合键（至少包含 Ctrl 或 Alt）", TextWrapping=TextWrapping.Wrap });
        _shortcut.Text=Format(mods,key);_shortcut.PreviewKeyDown+=Record;root.Children.Add(_shortcut);
        root.Children.Add(Button("应用快捷键设置",()=>_message.Text=_save(_enabled.IsChecked==true,_mods,_key)));
        _message.Text=registrationStatus;root.Children.Add(_message);
        root.Children.Add(Button("关闭",RequestClose));
        View=new ScrollViewer{Content=root,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        _module.Changed+=Render;Render();
    }
    internal static string Format(uint mods,int key) => ((mods&2)!=0?"Ctrl + ":"")+((mods&1)!=0?"Alt + ":"")+((mods&4)!=0?"Shift + ":"")+((mods&8)!=0?"Win + ":"")+KeyInterop.KeyFromVirtualKey(key);
    private static Button Button(string text,Action action)
    {
        var b=new Button{Content=text,MinHeight=32,Margin=new Thickness(0,4,0,0),Padding=new Thickness(8,4,8,4)};
        b.SetResourceReference(Control.BackgroundProperty,"Surface");b.SetResourceReference(Control.ForegroundProperty,"Ink");b.SetResourceReference(Control.BorderBrushProperty,"Border");
        b.Click+=(_,_)=>action();return b;
    }
    private void Record(object sender,KeyEventArgs e)
    {
        var key=e.Key==Key.System?e.SystemKey:e.Key;
        if(key is Key.Tab or Key.Escape)return;
        e.Handled=true;var mods=Keyboard.Modifiers;
        if((mods&(ModifierKeys.Control|ModifierKeys.Alt))==0 || key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)return;
        _mods=(uint)mods;_key=KeyInterop.VirtualKeyFromKey(key);_shortcut.Text=Format(_mods,_key);
    }
    private void Render()
    {
        if(_rendering)return;_rendering=true;
        try {
        var items=_module.Items;_list.Children.Clear();
        if(items.Count==0)_list.Children.Add(new TextBlock{Text="暂无由文件坞置顶的窗口。",TextWrapping=TextWrapping.Wrap});
        foreach(var pin in items)
        {
            var row=new StackPanel{Margin=new Thickness(0,0,0,8)};
            row.Children.Add(new TextBlock{Text=pin.Title,TextTrimming=TextTrimming.CharacterEllipsis,ToolTip=pin.Title});
            row.Children.Add(new TextBlock{Text=PinNative.ProcessName(pin.Process),FontSize=12});
            row.Children.Add(Button("取消置顶",()=>{_message.Text=_module.Remove(pin.Handle);Render();}));_list.Children.Add(row);
        }
        } finally {_rendering=false;}
    }
    public void OnClosed()=>_module.Changed-=Render;
    public void FocusInitial()=>_shortcut.Focus();
    public void RestoreFocus() { }
    public void RequestClose()=>_host.CloseDialog(this);
    public void HandleWindowKey(KeyEventArgs e){if(e.Key==Key.Escape){RequestClose();e.Handled=true;}}
    public void RefreshTheme() { }
}
