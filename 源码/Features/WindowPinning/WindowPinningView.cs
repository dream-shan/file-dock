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
    private readonly Action _pick;
    private bool _rendering, _subscribed;
    private readonly Expander _pins = new() { Margin = new Thickness(0,12,0,0) };
    private readonly Button _removeAll;
    private readonly StackPanel _toolList = new();
    private readonly StackPanel _details = new() { Visibility=Visibility.Collapsed };
    private readonly Button _run;
    public FrameworkElement View { get; }
    public bool HasChanges => false;
    internal WindowPinningView(IModuleHost host, WindowPinningModule module, Action pick,
        Action openShortcuts)
    {
        _host=host;_module=module;_pick=pick;
        var root=new StackPanel(); root.SetResourceReference(Control.ForegroundProperty,"Ink");
        _toolList.Children.Add(new TextBlock { Text="工具", FontSize=18, FontWeight=FontWeights.SemiBold, Margin=new Thickness(0,0,0,12) });
        var row=new Grid();row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(44)});
        _run=Button("窗口置顶",()=>_pick());_run.MinHeight=44;_run.Margin=new Thickness(0);_run.HorizontalContentAlignment=HorizontalAlignment.Left;_run.Padding=new Thickness(12,0,8,0);
        _run.SetResourceReference(FrameworkElement.StyleProperty,"QuietButton");_run.SetResourceReference(Control.ForegroundProperty,"Ink");
        System.Windows.Automation.AutomationProperties.SetName(_run,"窗口置顶：选择窗口");row.Children.Add(_run);
        var detailsButton=Button("›",ShowDetails);detailsButton.MinHeight=44;detailsButton.Margin=new Thickness(0);detailsButton.FontSize=20;detailsButton.ToolTip="已置顶窗口与快捷键设置";
        detailsButton.SetResourceReference(FrameworkElement.StyleProperty,"QuietButton");System.Windows.Automation.AutomationProperties.SetName(detailsButton,"窗口置顶详情");
        var divider=new Border{BorderThickness=new Thickness(1,0,0,0),Child=detailsButton};divider.SetResourceReference(Border.BorderBrushProperty,"PaneOutline");Grid.SetColumn(divider,1);row.Children.Add(divider);
        var rowBorder=new Border{Child=row,CornerRadius=new CornerRadius(8),BorderThickness=new Thickness(1)};rowBorder.SetResourceReference(Border.BackgroundProperty,"PaneCard");rowBorder.SetResourceReference(Border.BorderBrushProperty,"PaneOutline");_toolList.Children.Add(rowBorder);root.Children.Add(_toolList);
        var back=Button("← 工具",()=>{_details.Visibility=Visibility.Collapsed;_toolList.Visibility=Visibility.Visible;_run.Focus();});back.SetResourceReference(FrameworkElement.StyleProperty,"QuietButton");back.HorizontalAlignment=HorizontalAlignment.Left;back.Margin=new Thickness(0,0,0,8);_details.Children.Add(back);
        _details.Children.Add(new TextBlock{Text="窗口置顶",FontSize=18,FontWeight=FontWeights.SemiBold});
        var pinContent=new StackPanel();
        _removeAll=Button("取消全部",()=>{int failures=_module.RestoreAll();_message.Text=failures==0?"已取消全部受管理的置顶。":$"仍有 {failures} 个窗口取消失败，请重试。";Render();});
        pinContent.Children.Add(_removeAll);pinContent.Children.Add(_list);_pins.Content=pinContent;_pins.IsExpanded=true;_pins.SetResourceReference(Control.ForegroundProperty,"Ink");_details.Children.Add(_pins);
        _details.Children.Add(Button("快捷键设置…",openShortcuts));
        _details.Children.Add(_message);root.Children.Add(_details);
        _run.ToolTip="选择窗口置顶或取消；快捷键可在设置中统一管理";
        View=new ScrollViewer{Content=root,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        Render();
    }
    internal void ShowDetails(){_toolList.Visibility=Visibility.Collapsed;_details.Visibility=Visibility.Visible;Render();}
    internal static string Format(uint mods,int key) => ((mods&2)!=0?"Ctrl + ":"")+((mods&1)!=0?"Alt + ":"")+((mods&4)!=0?"Shift + ":"")+((mods&8)!=0?"Win + ":"")+KeyInterop.KeyFromVirtualKey(key);
    private static Button Button(string text,Action action)
    {
        var b=new Button{Content=text,MinHeight=32,Margin=new Thickness(0,4,0,0),Padding=new Thickness(8,4,8,4)};
        b.SetResourceReference(Control.BackgroundProperty,"Surface");b.SetResourceReference(Control.ForegroundProperty,"Ink");b.SetResourceReference(Control.BorderBrushProperty,"Border");
        b.Click+=(_,_)=>action();return b;
    }
    private void Render()
    {
        if(_rendering)return;_rendering=true;
        try {
        var items=_module.Items;_list.Children.Clear();_pins.Header=$"已置顶 {items.Count} 个窗口";
        _removeAll.Visibility=items.Count==0?Visibility.Collapsed:Visibility.Visible;
        if(items.Count==0)_list.Children.Add(new TextBlock{Text="暂无置顶窗口",Margin=new Thickness(0,8,0,0),TextWrapping=TextWrapping.Wrap});
        foreach(var pin in items)
        {
            var row=new StackPanel{Margin=new Thickness(0,0,0,8)};
            row.Children.Add(new TextBlock{Text=pin.Title,TextTrimming=TextTrimming.CharacterEllipsis,ToolTip=pin.Title});
            row.Children.Add(new TextBlock{Text=PinNative.ProcessName(pin.Process),FontSize=12});
            row.Children.Add(Button("取消置顶",()=>{_message.Text=_module.Remove(pin.Handle);Render();}));_list.Children.Add(row);
        }
        } finally {_rendering=false;}
    }
    internal void OnShown(){if(!_subscribed){_module.Changed+=Render;_subscribed=true;}Render();}
    internal void OnHidden(){if(_subscribed){_module.Changed-=Render;_subscribed=false;}}
    public void OnClosed()=>OnHidden();
    public void FocusInitial()=>_run.Focus();
    public void RestoreFocus() { }
    public void RequestClose()=>_host.CloseDialog(this);
    public void HandleWindowKey(KeyEventArgs e){if(e.Key==Key.Escape){RequestClose();e.Handled=true;}}
    public void RefreshTheme() { }
}
