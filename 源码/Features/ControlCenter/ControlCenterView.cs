using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Threading;

namespace FileDockDemo.Features.ControlCenter;

internal sealed class ControlCenterView : UserControl,IDisposable
{
    private readonly AudioController _audio;
    private readonly StackPanel _home=new(),_details=new(),_sessionPanel=new();
    private readonly TextBlock _status=Ui.Text(""),_detailTitle=Ui.Text("",16);
    private readonly AudioLevelRow _output,_microphone;
    private readonly ComboBox _outputs=new(),_microphones=new();
    private readonly Dictionary<AudioTarget,int> _pending=new();
    private readonly Dictionary<string,AudioLevelRow> _sessions=new();
    private readonly DispatcherTimer _writeTimer;
    private readonly Button _sessionsButton;
    private readonly StackPanel _devicePanel=new();
    private readonly BrightnessView? _brightness;
    private readonly PowerView? _power;
    private readonly HardwareView? _hardware;
    private readonly BrightnessController? _brightnessController;
    private readonly PowerController? _powerController;
    private readonly HardwareController? _hardwareController;
    private readonly WirelessView? _wireless;
    private string? _chosenOutput,_chosenMic;
    private string _deviceSignature="";
    private bool _updating,_disposed,_paused;
    internal ControlCenterView(AudioController audio,BrightnessController? brightness=null,PowerController? power=null,string? preferredScreen=null,Func<string?,Task<bool>>? saveScreen=null,Action? openPowerSettings=null,bool wireless=false,bool nativeWireless=true,HardwareController? hardware=null)
    {
        _audio=audio;_writeTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(80)};_writeTimer.Tick+=(_,_)=>Flush();
        var root=new StackPanel();var header=new DockPanel{Margin=new Thickness(0,0,0,8)};
        var refresh=Ui.Button("刷新",()=>{CancelPending();_audio.Refresh();_brightnessController?.Refresh();_powerController?.Refresh();_hardwareController?.Refresh();_wireless?.Refresh();});refresh.SetResourceReference(StyleProperty,"QuietButton");refresh.SetResourceReference(ForegroundProperty,"Accent");refresh.Padding=new Thickness(8,4,8,4);DockPanel.SetDock(refresh,Dock.Right);header.Children.Add(refresh);var headingTitle=Ui.Text("控制",18);headingTitle.FontWeight=FontWeights.SemiBold;headingTitle.Margin=new Thickness(0);header.Children.Add(headingTitle);root.Children.Add(header);
        if(wireless)
        {
            _wireless=new WirelessView(()=>ShowExtra(_wireless!.WifiDetails,"Wi-Fi"),()=>ShowExtra(_wireless!.BluetoothDetails,"蓝牙"),()=>ShowDetails(true),nativeWireless);
            _home.Children.Add(_wireless.Home);
        }
        _output=new AudioLevelRow("音量",QueueVolume,SetMute,Flush,HasPending,false);_microphone=new AudioLevelRow("麦克风",QueueVolume,SetMute,Flush,HasPending,true);
        _home.Children.Add(_output);_home.Children.Add(_microphone);
        _brightnessController=brightness;_powerController=power;_hardwareController=hardware;
        if(brightness!=null){_brightness=new(brightness,preferredScreen,saveScreen??(_=>Task.FromResult(false)));_home.Children.Add(_brightness.Home);_brightness.Details.Visibility=Visibility.Collapsed;_brightness.DetailsRequested+=()=>ShowExtra(_brightness.Details,"显示器");}
        if(power!=null){_power=new(power,openPowerSettings??(()=>{}));_home.Children.Add(_power.Home);_power.Details.Visibility=Visibility.Collapsed;_power.DetailsRequested+=()=>ShowExtra(_power.Details,"电源");}
        if(hardware!=null){_hardware=new(hardware);_home.Children.Add(_hardware.Home);_hardware.Details.Visibility=Visibility.Collapsed;_hardware.DetailsRequested+=()=>ShowExtra(_hardware.Details,"硬件与性能");}
        var audioLinks=new StackPanel();
        _sessionsButton=Ui.Button("应用音量  ›",()=>ShowDetails(false));
        _sessionsButton.SetResourceReference(StyleProperty,"QuietButton");_sessionsButton.SetResourceReference(ForegroundProperty,"Ink");_sessionsButton.HorizontalContentAlignment=HorizontalAlignment.Left;_sessionsButton.Margin=new Thickness(0);_sessionsButton.Padding=new Thickness(12,9,12,9);_sessionsButton.MinHeight=38;
        audioLinks.Children.Add(_sessionsButton);
        var linkDivider=new Border{Height=1,Margin=new Thickness(12,0,12,0)};linkDivider.SetResourceReference(Border.BackgroundProperty,"PaneOutline");audioLinks.Children.Add(linkDivider);
        var devicesButton=Ui.Button("音频设备  ›",()=>ShowDetails(true));
        devicesButton.SetResourceReference(StyleProperty,"QuietButton");devicesButton.SetResourceReference(ForegroundProperty,"Ink");devicesButton.HorizontalContentAlignment=HorizontalAlignment.Left;devicesButton.Margin=new Thickness(0);devicesButton.Padding=new Thickness(12,9,12,9);devicesButton.MinHeight=38;
        audioLinks.Children.Add(devicesButton);
        var linkCard=new Border{Child=audioLinks,CornerRadius=new CornerRadius(14),BorderThickness=new Thickness(1),Margin=new Thickness(0,0,0,8)};
        linkCard.SetResourceReference(Border.BackgroundProperty,"PaneCard");linkCard.SetResourceReference(Border.BorderBrushProperty,"PaneOutline");
        _home.Children.Add(linkCard);root.Children.Add(_home);
        var backButton=Ui.Button("← 控制",()=>{Flush();_wireless?.ShowDetails(null);_details.Visibility=Visibility.Collapsed;_home.Visibility=Visibility.Visible;ScrollTop();});
        backButton.SetResourceReference(StyleProperty,"QuietButton");backButton.SetResourceReference(ForegroundProperty,"Accent");backButton.HorizontalAlignment=HorizontalAlignment.Left;backButton.Padding=new Thickness(6,4,8,4);backButton.Margin=new Thickness(0,0,0,4);
        _details.Children.Add(backButton);_detailTitle.FontWeight=FontWeights.SemiBold;_details.Children.Add(_detailTitle);
        _devicePanel.Children.Add(Ui.Text("选择要调节的设备，不改变 Windows 默认设备。"));
        DeviceSelector(_devicePanel,"输出设备",_outputs,false);DeviceSelector(_devicePanel,"输入设备",_microphones,true);
        _details.Children.Add(_devicePanel);_details.Children.Add(_sessionPanel);_details.Visibility=Visibility.Collapsed;root.Children.Add(_details);
        if(_brightness!=null)_details.Children.Add(_brightness.Details);if(_power!=null)_details.Children.Add(_power.Details);if(_hardware!=null)_details.Children.Add(_hardware.Details);
        if(_wireless!=null)
        {
            _wireless.WifiDetails.Visibility=Visibility.Collapsed;_details.Children.Add(_wireless.WifiDetails);
            _wireless.BluetoothDetails.Visibility=Visibility.Collapsed;_details.Children.Add(_wireless.BluetoothDetails);
        }
        _status.FontSize=11;_status.SetResourceReference(TextBlock.ForegroundProperty,"Muted");root.Children.Add(_status);Content=Ui.Scroll(root);
        _audio.Changed+=Render;IsVisibleChanged+=(_,_)=>{
            if(!_disposed)SyncActivity();
        };
        Unloaded+=(_,_)=>{if(!_disposed){CancelPending();SetActive(false);}};
        Render(AudioSnapshot.Empty());
    }
    private void DeviceSelector(Panel panel,string label,ComboBox combo,bool input)
    {
        panel.Children.Add(Ui.Text(label));combo.MinHeight=32;combo.Margin=new Thickness(0,0,0,8);combo.DisplayMemberPath="Label";System.Windows.Automation.AutomationProperties.SetName(combo,label);
        combo.SelectionChanged+=(_,_)=>{
            if(_updating||combo.SelectedItem is not AudioDevice device)return;CancelPending();
            string? id=device.Id.Length==0?null:device.Id;if(input)_chosenMic=id;else _chosenOutput=id;_audio.SelectDevice(input,id);
        };panel.Children.Add(combo);
    }
    private bool HasPending(AudioTarget target)=>_pending.ContainsKey(target);
    private void QueueVolume(AudioTarget target,int value){_pending[target]=value;_writeTimer.Stop();_writeTimer.Start();}
    private void SetMute(AudioTarget target,bool mute){_audio.SetLevel(target,mute:mute);}
    private void Flush(){_writeTimer.Stop();foreach(var pair in _pending)_audio.SetLevel(pair.Key,volume:pair.Value);_pending.Clear();_brightness?.Flush();}
    private void CancelPending(){_writeTimer.Stop();_pending.Clear();_brightness?.Cancel();}
    internal void SetPaused(bool paused){if(_paused==paused||_disposed)return;_paused=paused;SyncActivity();}
    private void SyncActivity(){bool active=IsVisible&&!_paused;if(!active)CancelPending();SetActive(active);}
    private void SetActive(bool active){_audio.SetActive(active);_brightnessController?.SetActive(active);_powerController?.SetActive(active);_hardwareController?.SetActive(active);_wireless?.SetActive(active);}
    private void ScrollTop(){if(Content is ScrollViewer scroll)scroll.ScrollToTop();}
    internal void RefreshWireless()=>_wireless?.Refresh();
    private void ShowWirelessPanel(FrameworkElement? panel)
    {
        if(_wireless==null)return;
        _wireless.WifiDetails.Visibility=panel==_wireless.WifiDetails?Visibility.Visible:Visibility.Collapsed;
        _wireless.BluetoothDetails.Visibility=panel==_wireless.BluetoothDetails?Visibility.Visible:Visibility.Collapsed;
        _wireless.ShowDetails(panel);
    }
    private void ShowExtra(FrameworkElement detail,string title)
    {
        Flush();_home.Visibility=Visibility.Collapsed;_details.Visibility=Visibility.Visible;_devicePanel.Visibility=_sessionPanel.Visibility=Visibility.Collapsed;
        if(_brightness!=null)_brightness.Details.Visibility=Visibility.Collapsed;if(_power!=null)_power.Details.Visibility=Visibility.Collapsed;if(_hardware!=null)_hardware.Details.Visibility=Visibility.Collapsed;detail.Visibility=Visibility.Visible;_detailTitle.Text=title;
        ShowWirelessPanel(detail);ScrollTop();
    }
    private void ShowDetails(bool devices)
    {
        Flush();_home.Visibility=Visibility.Collapsed;_details.Visibility=Visibility.Visible;
        if(_brightness!=null)_brightness.Details.Visibility=Visibility.Collapsed;if(_power!=null)_power.Details.Visibility=Visibility.Collapsed;if(_hardware!=null)_hardware.Details.Visibility=Visibility.Collapsed;
        _devicePanel.Visibility=devices?Visibility.Visible:Visibility.Collapsed;_sessionPanel.Visibility=devices?Visibility.Collapsed:Visibility.Visible;
        _detailTitle.Text=devices?"音频设备":"应用音量";
        ShowWirelessPanel(null);ScrollTop();
    }
    private void Render(AudioSnapshot snapshot)
    {
        if(_disposed)return;
        _output.Bind(snapshot.Output);_microphone.Bind(snapshot.Microphone);
        _status.Text=snapshot.Error??snapshot.Output?.Error??snapshot.Microphone?.Error??"";
        _sessionsButton.Content=$"应用音量（{snapshot.Sessions.Count}）  ›";
        string signature=string.Join("|",snapshot.Devices.Select(d=>d.Id+d.Name+d.Default));
        if(signature!=_deviceSignature||_outputs.ItemsSource==null)
        {
            _deviceSignature=signature;_updating=true;
            try
            {
                Fill(_outputs,snapshot.Devices.Where(d=>!d.Input).ToArray(),_chosenOutput,false);
                Fill(_microphones,snapshot.Devices.Where(d=>d.Input).ToArray(),_chosenMic,true);
            }finally{_updating=false;}
        }
        var ids=snapshot.Sessions.Select(s=>s.Target.SessionId!).ToHashSet();
        foreach(var id in _sessions.Keys.Where(id=>!ids.Contains(id)).ToArray()){_sessionPanel.Children.Remove(_sessions[id]);_sessions.Remove(id);}
        foreach(var level in snapshot.Sessions)
        {
            string id=level.Target.SessionId!;
            if(!_sessions.TryGetValue(id,out var row)){row=new AudioLevelRow(level.Name,QueueVolume,SetMute,Flush,HasPending);_sessions.Add(id,row);_sessionPanel.Children.Add(row);}
            row.Bind(level);
        }
        if(_sessions.Count==0)
        {
            if(!_sessionPanel.Children.OfType<TextBlock>().Any())_sessionPanel.Children.Add(Ui.Text("当前输出设备暂无音频会话。播放音频后会自动更新，也可点击刷新。"));
        }
        else foreach(var text in _sessionPanel.Children.OfType<TextBlock>().ToArray())_sessionPanel.Children.Remove(text);
    }
    private static void Fill(ComboBox combo,AudioDevice[] devices,string? selected,bool input)
    {
        var entries=new[]{new AudioDevice("",devices.Length==0?"无可用设备":"跟随系统默认",input,false)}.Concat(devices).ToArray();
        combo.ItemsSource=entries;combo.SelectedItem=entries.FirstOrDefault(d=>d.Id==selected)??entries[0];combo.IsEnabled=devices.Length>0;
    }
    public void Dispose(){if(_disposed)return;_disposed=true;CancelPending();_audio.Changed-=Render;_audio.Dispose();_brightness?.Dispose();_power?.Dispose();_hardware?.Dispose();_wireless?.Dispose();}
}

internal sealed class AudioLevelRow : Border
{
    private readonly TextBlock _name=Ui.Text("",11),_percent=Ui.Text("—",11.5);
    private readonly Slider _slider=new(){Minimum=0,Maximum=100,TickFrequency=1,IsSnapToTickEnabled=true,VerticalAlignment=VerticalAlignment.Center};
    private readonly ToggleButton _mute=new(){Width=26,Height=26,Margin=new Thickness(0,0,8,0),FontFamily=new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),FontSize=13};
    private readonly Func<AudioTarget,bool> _pending;
    private readonly bool _isMic;
    private AudioLevel? _level;
    private bool _updating;
    internal AudioLevelRow(string title,Action<AudioTarget,int> volume,Action<AudioTarget,bool> mute,Action flush,Func<AudioTarget,bool> pending,bool isMic=false)
    {
        _pending=pending;_isMic=isMic;
        CornerRadius=new CornerRadius(14);BorderThickness=new Thickness(1);Padding=new Thickness(11,8,11,9);Margin=new Thickness(0,0,0,8);
        SetResourceReference(BackgroundProperty,"PaneCard");SetResourceReference(BorderBrushProperty,"PaneOutline");
        var stack=new StackPanel();Child=stack;
        var heading=new Grid{Margin=new Thickness(0,0,0,7)};
        heading.ColumnDefinitions.Add(new(){Width=GridLength.Auto});heading.ColumnDefinitions.Add(new());heading.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var titleText=Ui.Text(title,12.5);titleText.FontWeight=FontWeights.SemiBold;titleText.Margin=new Thickness(0);heading.Children.Add(titleText);
        _name.Margin=new Thickness(7,0,6,0);_name.VerticalAlignment=VerticalAlignment.Center;_name.TextTrimming=TextTrimming.CharacterEllipsis;_name.TextWrapping=TextWrapping.NoWrap;_name.SetResourceReference(TextBlock.ForegroundProperty,"Muted");Grid.SetColumn(_name,1);heading.Children.Add(_name);
        _percent.FontWeight=FontWeights.SemiBold;_percent.Margin=new Thickness(0);_percent.VerticalAlignment=VerticalAlignment.Center;_percent.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
        System.Windows.Documents.Typography.SetNumeralAlignment(_percent,FontNumeralAlignment.Tabular);Grid.SetColumn(_percent,2);heading.Children.Add(_percent);
        stack.Children.Add(heading);
        var row=new Grid();row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});row.ColumnDefinitions.Add(new());
        _mute.SetResourceReference(Control.ForegroundProperty,"Ink");row.Children.Add(_mute);
        _slider.SetResourceReference(FrameworkElement.StyleProperty,"AppleCapsuleSlider");Grid.SetColumn(_slider,1);row.Children.Add(_slider);
        stack.Children.Add(row);
        _mute.Template=(ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ToggleButton">
              <Border x:Name="Frame" Width="26" Height="26" CornerRadius="13" Background="{DynamicResource Hover}" BorderBrush="{DynamicResource PaneOutline}" BorderThickness="1">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource Pressed}"/></Trigger>
                <Trigger Property="IsChecked" Value="True"><Setter Property="Foreground" Value="{DynamicResource OnAccent}"/><Setter TargetName="Frame" Property="Background" Value="{DynamicResource Error}"/><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource Error}"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        System.Windows.Automation.AutomationProperties.SetName(_slider,title+"百分比");System.Windows.Automation.AutomationProperties.SetName(_mute,title+"静音");
        _slider.ValueChanged+=(_,_)=>{if(_updating||_level==null)return;_percent.Text=((int)_slider.Value)+"%";volume(_level.Target,(int)_slider.Value);};
        _slider.AddHandler(Thumb.DragCompletedEvent,new DragCompletedEventHandler((_,_)=>flush()));_slider.PreviewMouseLeftButtonUp+=(_,_)=>flush();_slider.PreviewKeyUp+=(_,_)=>flush();
        _mute.Click+=(_,_)=>{if(_level!=null)mute(_level.Target,_mute.IsChecked==true);};
    }
    internal void Bind(AudioLevel? level)
    {
        bool changed=_level?.Target!=level?.Target;_level=level;
        _name.Text=level?.Name??"无可用设备";_name.ToolTip=level?.Name;bool enabled=level!=null&&level.Error==null;_slider.IsEnabled=_mute.IsEnabled=enabled;
        _updating=true;
        try
        {
            if(changed||!_slider.IsMouseCaptureWithin&&(level==null||!_pending(level.Target))){_slider.Value=level?.Volume??0;_percent.Text=enabled?level!.Volume+"%":"—";}
            bool muted=enabled&&level!.Muted;
            _mute.IsChecked=muted;
            _mute.Content=_isMic?(muted?"\uEC54":"\uE720"):(muted?"\uE74F":"\uE767");
            _mute.ToolTip=muted?"已静音 · 点击取消静音":"静音";
        }finally{_updating=false;}
    }
}
