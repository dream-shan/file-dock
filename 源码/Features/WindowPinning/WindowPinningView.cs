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
    private bool _rendering, _subscribed;
    private readonly Expander _pins = new() { Margin = new Thickness(0,12,0,0) };
    private readonly Button _removeAll;
    private readonly StackPanel _toolList = new();
    private readonly StackPanel _details = new() { Visibility=Visibility.Collapsed };
    private readonly Button _run;
    private readonly StackPanel? _screenshotOptions;
    public FrameworkElement View { get; }
    public bool HasChanges => false;
    internal WindowPinningView(IModuleHost host, WindowPinningModule module, Action pick,
        bool enabled, uint mods, int key, Func<bool,uint,int,string> save, string registrationStatus,
        Action? manageShortcut=null,Action? manageAll=null, Action? screenshot=null, Action<Panel>? screenshotOptions=null)
    {
        _host=host;_module=module;_pick=pick;_save=save;_mods=mods;_key=key;
        var root=new StackPanel(); root.SetResourceReference(Control.ForegroundProperty,"Ink");
        _toolList.Children.Add(new TextBlock { Text="工具", FontSize=18, FontWeight=FontWeights.SemiBold, Margin=new Thickness(0,0,0,12) });
        _run=Button("窗口置顶",_pick);
        _toolList.Children.Add(ToolRow(_run,"\uE718","ToolPinAccent","窗口置顶：选择窗口","窗口置顶详情","已置顶窗口与快捷键设置",ShowDetails));
        root.Children.Add(_toolList);
        if (screenshot != null && screenshotOptions != null)
        {
            var options = new StackPanel { Visibility = Visibility.Collapsed };
            var runScreenshot = Button("截图", screenshot);
            var backToTools = Button("← 工具", () => {
                options.Visibility = Visibility.Collapsed;
                _toolList.Visibility = Visibility.Visible;
                runScreenshot.Focus();
            });
            backToTools.SetResourceReference(FrameworkElement.StyleProperty,"QuietButton");
            backToTools.SetResourceReference(Control.ForegroundProperty,"Accent");
            backToTools.HorizontalAlignment=HorizontalAlignment.Left;
            backToTools.Margin=new Thickness(0,0,0,8);
            options.Children.Add(backToTools);
            screenshotOptions(options);
            root.Children.Add(options);
            _toolList.Children.Add(ToolRow(runScreenshot,"\uE722","ToolShotAccent","截图：选择区域","截图选项","包含文件坞与快捷键设置",() => {
                _details.Visibility = _toolList.Visibility = Visibility.Collapsed;
                options.Visibility = Visibility.Visible;
                backToTools.Focus();
            }));
            _screenshotOptions = options;
        }
        if(manageAll!=null)
        {
            var entry=Button("快捷键管理",manageAll);
            entry.MinHeight=48;entry.Margin=new Thickness(0);entry.Padding=new Thickness(8,0,12,0);
            entry.FontSize=13.5;entry.FontWeight=FontWeights.Medium;
            entry.HorizontalContentAlignment=HorizontalAlignment.Left;
            entry.SetResourceReference(FrameworkElement.StyleProperty,"QuietButton");
            entry.SetResourceReference(Control.ForegroundProperty,"Ink");
            var row=new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
            var badge=IconBadge("\uE765","ToolKeyAccent");badge.Margin=new Thickness(10,0,2,0);
            row.Children.Add(badge);
            Grid.SetColumn(entry,1);row.Children.Add(entry);
            var chevron=new TextBlock{Text="›",FontSize=18,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,14,1),IsHitTestVisible=false};
            chevron.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
            Grid.SetColumn(chevron,2);row.Children.Add(chevron);
            var card=new Border{Child=row,CornerRadius=new CornerRadius(14),BorderThickness=new Thickness(1),Margin=new Thickness(0,0,0,8)};
            card.SetResourceReference(Border.BackgroundProperty,"PaneCard");
            card.SetResourceReference(Border.BorderBrushProperty,"PaneOutline");
            _toolList.Children.Add(card);
        }
        var back=Button("← 工具",()=>{_details.Visibility=Visibility.Collapsed;_toolList.Visibility=Visibility.Visible;_run.Focus();});back.SetResourceReference(FrameworkElement.StyleProperty,"QuietButton");back.SetResourceReference(Control.ForegroundProperty,"Accent");back.HorizontalAlignment=HorizontalAlignment.Left;back.Margin=new Thickness(0,0,0,8);_details.Children.Add(back);
        _details.Children.Add(new TextBlock{Text="窗口置顶",FontSize=18,FontWeight=FontWeights.SemiBold});
        var pinContent=new StackPanel();
        _removeAll=Button("取消全部",()=>{int failures=_module.RestoreAll();_message.Text=failures==0?"已取消全部受管理的置顶。":$"仍有 {failures} 个窗口取消失败，请重试。";Render();});
        pinContent.Children.Add(_removeAll);pinContent.Children.Add(_list);_pins.Content=pinContent;_pins.IsExpanded=true;_pins.SetResourceReference(Control.ForegroundProperty,"Ink");_details.Children.Add(_pins);
        _enabled.IsChecked=enabled;
        if(manageShortcut==null){
        var shortcutSettings=new Expander { Header="快捷键设置", Margin=new Thickness(0,10,0,0) };shortcutSettings.SetResourceReference(Control.ForegroundProperty,"Ink");
        var settings=new StackPanel();
        _enabled.SetResourceReference(Control.ForegroundProperty,"Ink");
        _enabled.IsChecked=enabled;settings.Children.Add(_enabled);
        settings.Children.Add(new TextBlock { Text="点击下框后按组合键（至少包含 Ctrl 或 Alt）", TextWrapping=TextWrapping.Wrap });
        _shortcut.Text=Format(mods,key);_shortcut.PreviewKeyDown+=Record;settings.Children.Add(_shortcut);
        settings.Children.Add(Button("应用快捷键设置",()=>{_message.Text=_save(_enabled.IsChecked==true,_mods,_key);UpdateTooltip();}));
        shortcutSettings.Content=settings;shortcutSettings.IsExpanded=true;_details.Children.Add(shortcutSettings);
        }else _details.Children.Add(Button("快捷键设置…",manageShortcut));
        _message.Text=registrationStatus;_details.Children.Add(_message);UpdateTooltip();root.Children.Add(_details);
        View=new ScrollViewer{Content=root,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        Render();
    }
    private void UpdateTooltip()=>_run.ToolTip="选择窗口置顶或取消 · "+(_enabled.IsChecked==true?Format(_mods,_key):"快捷键已禁用");
    internal void UpdateShortcut(ShortcutBinding binding,string status){_mods=binding.Modifiers;_key=binding.Key;_enabled.IsChecked=binding.Enabled;_message.Text=status;UpdateTooltip();}
    private static Border IconBadge(string glyph, string accentResource)
    {
        var icon = new TextBlock { Text = glyph, FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "OnAccent");
        var badge = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Child = icon, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        badge.SetResourceReference(Border.BackgroundProperty, accentResource);
        return badge;
    }
    private static Border ToolRow(Button run, string glyph, string accentResource, string actionName, string detailsName, string hint, Action showDetails)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        var badge = IconBadge(glyph, accentResource);
        badge.Margin = new Thickness(10, 0, 2, 0);
        row.Children.Add(badge);

        run.MinHeight = 48;
        run.Margin = new Thickness(0);
        run.Padding = new Thickness(8,0,8,0);
        run.FontSize = 13.5;
        run.FontWeight = FontWeights.Medium;
        run.HorizontalContentAlignment = HorizontalAlignment.Left;
        run.SetResourceReference(FrameworkElement.StyleProperty, "QuietButton");
        run.SetResourceReference(Control.ForegroundProperty, "Ink");
        System.Windows.Automation.AutomationProperties.SetName(run, actionName);
        Grid.SetColumn(run, 1);
        row.Children.Add(run);

        var details = Button("›", showDetails);
        details.MinHeight = 48;
        details.Margin = new Thickness(0);
        details.FontSize = 19;
        details.ToolTip = hint;
        details.SetResourceReference(FrameworkElement.StyleProperty, "QuietButton");
        System.Windows.Automation.AutomationProperties.SetName(details, detailsName);
        var divider = new Border { BorderThickness = new Thickness(1,0,0,0), Margin = new Thickness(0,10,0,10), Child = details };
        divider.SetResourceReference(Border.BorderBrushProperty,"PaneOutline");
        Grid.SetColumn(divider,2);
        row.Children.Add(divider);

        var border = new Border { Child = row, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Margin = new Thickness(0,0,0,8) };
        border.SetResourceReference(Border.BackgroundProperty,"PaneCard");
        border.SetResourceReference(Border.BorderBrushProperty,"PaneOutline");
        return border;
    }
    internal void ShowDetails(){if(_screenshotOptions!=null)_screenshotOptions.Visibility=Visibility.Collapsed;_toolList.Visibility=Visibility.Collapsed;_details.Visibility=Visibility.Visible;Render();}
    internal static string Format(uint mods,int key) => ((mods&2)!=0?"Ctrl + ":"")+((mods&1)!=0?"Alt + ":"")+((mods&4)!=0?"Shift + ":"")+((mods&8)!=0?"Win + ":"")+KeyInterop.KeyFromVirtualKey(key);
    private static Button Button(string text,Action action)
    {
        var b=new Button{Content=text,MinHeight=32,Margin=new Thickness(0,4,0,0),Padding=new Thickness(10,4,10,4)};
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
    public void FocusInitial()=>_shortcut.Focus();
    public void RestoreFocus() { }
    public void RequestClose()=>_host.CloseDialog(this);
    public void HandleWindowKey(KeyEventArgs e){if(e.Key==Key.Escape){RequestClose();e.Handled=true;}}
    public void RefreshTheme() { }
}
