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
        _title=title;Margin=new Thickness(3,0,3,0);Padding=new Thickness(8,8,6,8);CornerRadius=new CornerRadius(14);BorderThickness=new Thickness(1);
        SetResourceReference(BackgroundProperty,"PaneCard");SetResourceReference(BorderBrushProperty,"PaneOutline");
        var panel=new DockPanel();Child=panel;
        _toggle=Ui.Button(icon,toggle);_toggle.FontFamily=new System.Windows.Media.FontFamily("Segoe MDL2 Assets");
        _toggle.FontSize=15;_toggle.Width=34;_toggle.Height=34;_toggle.MinHeight=34;
        _toggle.Padding=new Thickness(0);_toggle.Margin=new Thickness(0,0,8,0);_toggle.VerticalAlignment=VerticalAlignment.Center;
        _toggle.Template=(ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
              <Border x:Name="Circle" Width="34" Height="34" CornerRadius="17" Background="{TemplateBinding Background}">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Circle" Property="Opacity" Value="0.88"/></Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="Circle" Property="Opacity" Value="0.75"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        DockPanel.SetDock(_toggle,Dock.Left);panel.Children.Add(_toggle);
        var label=new StackPanel{VerticalAlignment=VerticalAlignment.Center};
        var titleBlock=new TextBlock{Text=title+"  ›",FontSize=13,FontWeight=FontWeights.SemiBold};
        titleBlock.SetResourceReference(TextBlock.ForegroundProperty,"Ink");
        label.Children.Add(titleBlock);
        _summary=new TextBlock{FontSize=11,Margin=new Thickness(0,1,0,0),TextTrimming=TextTrimming.CharacterEllipsis};_summary.SetResourceReference(TextBlock.ForegroundProperty,"Muted");label.Children.Add(_summary);
        var open=Ui.Button("",details);open.Content=label;open.Padding=new Thickness(2,2,4,2);open.Margin=new Thickness(0);open.MinHeight=34;open.HorizontalContentAlignment=HorizontalAlignment.Stretch;
        open.SetResourceReference(FrameworkElement.StyleProperty,"QuietButton");open.SetResourceReference(Control.ForegroundProperty,"Ink");
        AutomationProperties.SetName(open,title+" 详情");panel.Children.Add(open);
        Render("未读取",false,null,null);
    }

    internal void Render(string status,bool enabled,bool? on,string? message)
    {
        _summary.Text=status;_summary.ToolTip=status;_toggle.IsEnabled=enabled;
        _toggle.SetResourceReference(Control.BackgroundProperty,on==true?"Accent":"Hover");
        _toggle.SetResourceReference(Control.ForegroundProperty,on==true?"OnAccent":"Muted");
        string title=(on==true?"关闭 ":"开启 ")+_title;
        _toggle.ToolTip=message??title;
        AutomationProperties.SetName(_toggle,title);
        AutomationProperties.SetHelpText(_toggle,message??status);
    }
}
