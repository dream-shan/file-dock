using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FileDockDemo.Features.WindowPinning;

namespace FileDockDemo;

internal sealed class ShortcutSettingsView
{
    private readonly List<(TextBox Box,Action<ShortcutBinding> Capture)> _editors=[];
    public FrameworkElement View {get;}
    internal bool Busy {get;private set;}
    internal ShortcutSettingsView(Func<ShortcutCommand,ShortcutBinding> current,
        Func<ShortcutCommand,string> status,Func<ShortcutCommand,ShortcutBinding,Task<ShortcutResult>> save)
    {
        var root=new StackPanel{Margin=new Thickness(20)};
        root.Children.Add(new TextBlock{Text="快捷键",FontSize=20,FontWeight=FontWeights.SemiBold});
        var hint=new TextBlock{Text="点击按键框后按组合键，保存后生效。至少包含 Ctrl 或 Alt。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,18)};
        hint.SetResourceReference(TextBlock.ForegroundProperty,"Muted");root.Children.Add(hint);
        foreach(var (command,title,description,defaultKey) in new[]{
            (ShortcutCommand.ToggleDock,"展开 / 收起文件坞","在任意应用中唤出文件坞",0x20),
            (ShortcutCommand.ToggleWindowPin,"窗口置顶 / 取消置顶","对当前应用窗口生效",0x54)})
        {
            var binding=current(command);
            var panel=new StackPanel{Margin=new Thickness(14)};
            var enabled=new CheckBox{Content=title,IsChecked=binding.Enabled,FontWeight=FontWeights.SemiBold};
            panel.Children.Add(enabled);
            var detail=new TextBlock{Text=description,Margin=new Thickness(0,6,0,10)};detail.SetResourceReference(TextBlock.ForegroundProperty,"Muted");panel.Children.Add(detail);
            var box=new TextBox{IsReadOnly=true,Text=Format(binding),MinHeight=36,Padding=new Thickness(10,6,10,6),IsEnabled=binding.Enabled};
            System.Windows.Automation.AutomationProperties.SetName(box,title+"组合键");
            void Capture(ShortcutBinding value){binding=new(enabled.IsChecked==true,value.Modifiers,value.Key);box.Text=Format(binding);}
            _editors.Add((box,Capture));
            box.PreviewKeyDown+=(_,e)=>{
                var key=e.Key==Key.System?e.SystemKey:e.Key;
                if(key is Key.Tab or Key.Escape)return;
                e.Handled=true;
                if(key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)return;
                Capture(new(true,(uint)Keyboard.Modifiers,KeyInterop.VirtualKeyFromKey(key)));
            };
            enabled.Checked+=(_,_)=>box.IsEnabled=true;enabled.Unchecked+=(_,_)=>box.IsEnabled=false;
            panel.Children.Add(box);
            var message=new TextBlock{Text=status(command),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,8),FontSize=12};message.SetResourceReference(TextBlock.ForegroundProperty,"Muted");panel.Children.Add(message);
            var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
            var reset=new Button{Content="默认组合键",Padding=new Thickness(10,5,10,5)};
            reset.SetResourceReference(FrameworkElement.StyleProperty,"QuietButton");reset.Click+=(_,_)=>Capture(new(true,3,defaultKey));actions.Children.Add(reset);
            var apply=new Button{Content="保存",Padding=new Thickness(16,5,16,5),Margin=new Thickness(8,0,0,0)};
            apply.SetResourceReference(FrameworkElement.StyleProperty,"QuietButton");apply.SetResourceReference(Control.ForegroundProperty,"Accent");actions.Children.Add(apply);
            apply.Click+=async(_,_)=>{
                if(Busy)return;
                Busy=true;root.IsEnabled=false;
                try{var result=await save(command,new(enabled.IsChecked==true,binding.Modifiers,binding.Key));message.Text=result.Success?status(command):result.Error;}
                catch(Exception ex){message.Text="保存失败："+ex.Message;}
                finally{root.IsEnabled=true;Busy=false;}
            };
            panel.Children.Add(actions);
            var border=new Border{Child=panel,CornerRadius=new CornerRadius(10),Margin=new Thickness(0,0,0,12),BorderThickness=new Thickness(1)};
            border.SetResourceReference(Border.BackgroundProperty,"PaneCard");border.SetResourceReference(Border.BorderBrushProperty,"PaneOutline");root.Children.Add(border);
        }
        View=new ScrollViewer{Content=root,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    }
    internal void CaptureRegistered(ShortcutBinding binding)
    {foreach(var editor in _editors)if(editor.Box.IsKeyboardFocusWithin){editor.Capture(binding);break;}}
    internal static string Format(ShortcutBinding binding)=>WindowPinningView.Format(binding.Modifiers,binding.Key);
}
