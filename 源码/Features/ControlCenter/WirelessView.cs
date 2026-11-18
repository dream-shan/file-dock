using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace FileDockDemo.Features.ControlCenter;

internal sealed class WirelessView:IDisposable
{
    private readonly WifiControlPane _wifi;
    private readonly BluetoothControlPane _bluetooth;
    internal FrameworkElement Home{get;}
    internal FrameworkElement WifiDetails=>_wifi.Details;
    internal FrameworkElement BluetoothDetails=>_bluetooth.Details;

    internal WirelessView(Action showWifi,Action showBluetooth,Action showAudio,bool native)
    {
        _wifi=new WifiControlPane(showWifi,native);
        _bluetooth=new BluetoothControlPane(showBluetooth,showAudio,native);
        var row=new UniformGrid{Columns=2,Margin=new Thickness(0,0,0,8)};
        row.Children.Add(_wifi.Tile);row.Children.Add(_bluetooth.Tile);Home=row;
    }

    internal void SetActive(bool active){_wifi.SetActive(active);_bluetooth.SetActive(active);}
    internal void ShowDetails(FrameworkElement? panel){_wifi.SetDetails(panel==WifiDetails);_bluetooth.SetDetails(panel==BluetoothDetails);}
    internal void Refresh(){_wifi.RequestRefresh();_bluetooth.RequestRefresh();}
    public void Dispose(){_wifi.Dispose();_bluetooth.Dispose();}

    internal static void OpenSettings(string uri,WirelessOperationScope scope)
    {
        try{using var process=Process.Start(new ProcessStartInfo(uri){UseShellExecute=true});}
        catch(Exception){scope.ShowError("无法打开 Windows 设置，请从开始菜单打开设置。");}
    }

    internal static TextBlock Caption(string text)
    {
        var caption=Ui.Text(text,11);caption.Margin=new Thickness(0,2,0,4);
        caption.SetResourceReference(TextBlock.ForegroundProperty,"Muted");return caption;
    }
}

internal sealed class WirelessTile:Border
{
    private readonly string _title;
    private readonly Button _toggle;
    private readonly TextBlock _summary;

    internal WirelessTile(string title,string icon,Action toggle,Action details)
    {
        _title=title;Margin=new Thickness(2,0,2,0);CornerRadius=new CornerRadius(7);BorderThickness=new Thickness(1);
        SetResourceReference(BackgroundProperty,"PaneCard");SetResourceReference(BorderBrushProperty,"PaneOutline");
        var panel=new DockPanel();Child=panel;
        _toggle=Ui.Button(icon,toggle);_toggle.FontFamily=new System.Windows.Media.FontFamily("Segoe MDL2 Assets");
        _toggle.SetResourceReference(FrameworkElement.StyleProperty,"QuietButton");_toggle.FontSize=16;_toggle.MinWidth=34;
        _toggle.Padding=new Thickness(5);_toggle.Margin=new Thickness(0);DockPanel.SetDock(_toggle,Dock.Left);panel.Children.Add(_toggle);
        var label=new StackPanel();label.Children.Add(new TextBlock{Text=title+"  ›"});
        _summary=new TextBlock{FontSize=11,TextTrimming=TextTrimming.CharacterEllipsis};_summary.SetResourceReference(TextBlock.ForegroundProperty,"Muted");label.Children.Add(_summary);
        var open=Ui.Button("",details);open.Content=label;open.Padding=new Thickness(4,2,5,3);open.Margin=new Thickness(0);open.HorizontalContentAlignment=HorizontalAlignment.Stretch;
        open.SetResourceReference(FrameworkElement.StyleProperty,"QuietButton");open.SetResourceReference(Control.ForegroundProperty,"Ink");
        AutomationProperties.SetName(open,title+" 详情");panel.Children.Add(open);
        Render("未读取",false,null,null);
    }

    internal void Render(string status,bool enabled,bool? on,string? message)
    {
        _summary.Text=status;_summary.ToolTip=status;_toggle.IsEnabled=enabled;
        _toggle.SetResourceReference(Control.ForegroundProperty,on==true?"Accent":"Muted");
        string title=(on==true?"关闭 ":"开启 ")+_title;
        _toggle.ToolTip=message??title;
        AutomationProperties.SetName(_toggle,title);
        AutomationProperties.SetHelpText(_toggle,message??status);
    }
}
