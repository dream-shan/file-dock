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
    private readonly BrightnessController? _brightnessController;
    private readonly PowerController? _powerController;
    private string? _chosenOutput,_chosenMic;
    private string _deviceSignature="";
    private bool _updating,_disposed,_paused;
    internal ControlCenterView(AudioController audio,BrightnessController? brightness=null,PowerController? power=null,string? preferredScreen=null,Func<string?,Task<bool>>? saveScreen=null,Action? openPowerSettings=null)
    {
        _audio=audio;_writeTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(80)};_writeTimer.Tick+=(_,_)=>Flush();
        var root=new StackPanel();var header=new DockPanel();
        var refresh=Ui.Button("刷新",()=>{CancelPending();_audio.Refresh();_brightnessController?.Refresh();_powerController?.Refresh();});refresh.SetResourceReference(StyleProperty,"QuietButton");DockPanel.SetDock(refresh,Dock.Right);header.Children.Add(refresh);header.Children.Add(Ui.Text("控制",18));root.Children.Add(header);
        _output=new AudioLevelRow("音量",QueueVolume,SetMute,Flush,HasPending);_microphone=new AudioLevelRow("麦克风",QueueVolume,SetMute,Flush,HasPending);
        _home.Children.Add(_output);_home.Children.Add(_microphone);
        _brightnessController=brightness;_powerController=power;
        if(brightness!=null){_brightness=new(brightness,preferredScreen,saveScreen??(_=>Task.FromResult(false)));_home.Children.Add(_brightness.Home);_brightness.Details.Visibility=Visibility.Collapsed;_brightness.DetailsRequested+=()=>ShowExtra(_brightness.Details,"显示器");}
        if(power!=null){_power=new(power,openPowerSettings??(()=>{}));_home.Children.Add(_power.Home);_power.Details.Visibility=Visibility.Collapsed;_power.DetailsRequested+=()=>ShowExtra(_power.Details,"电源");}
        _sessionsButton=Ui.Button("应用音量  ›",()=>ShowDetails(false));_home.Children.Add(_sessionsButton);
        _home.Children.Add(Ui.Button("音频设备  ›",()=>ShowDetails(true)));root.Children.Add(_home);
        _details.Children.Add(Ui.Button("← 控制",()=>{Flush();_details.Visibility=Visibility.Collapsed;_home.Visibility=Visibility.Visible;}));_details.Children.Add(_detailTitle);
        _devicePanel.Children.Add(Ui.Text("选择要调节的设备，不改变 Windows 默认设备。"));
        DeviceSelector(_devicePanel,"输出设备",_outputs,false);DeviceSelector(_devicePanel,"输入设备",_microphones,true);
        _details.Children.Add(_devicePanel);_details.Children.Add(_sessionPanel);_details.Visibility=Visibility.Collapsed;root.Children.Add(_details);
        if(_brightness!=null)_details.Children.Add(_brightness.Details);if(_power!=null)_details.Children.Add(_power.Details);
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
    private void SetActive(bool active){_audio.SetActive(active);_brightnessController?.SetActive(active);_powerController?.SetActive(active);}
    private void ShowExtra(FrameworkElement detail,string title)
    {
        Flush();_home.Visibility=Visibility.Collapsed;_details.Visibility=Visibility.Visible;_devicePanel.Visibility=_sessionPanel.Visibility=Visibility.Collapsed;
        if(_brightness!=null)_brightness.Details.Visibility=Visibility.Collapsed;if(_power!=null)_power.Details.Visibility=Visibility.Collapsed;detail.Visibility=Visibility.Visible;_detailTitle.Text=title;
    }
    private void ShowDetails(bool devices)
    {
        Flush();_home.Visibility=Visibility.Collapsed;_details.Visibility=Visibility.Visible;
        if(_brightness!=null)_brightness.Details.Visibility=Visibility.Collapsed;if(_power!=null)_power.Details.Visibility=Visibility.Collapsed;
        _devicePanel.Visibility=devices?Visibility.Visible:Visibility.Collapsed;_sessionPanel.Visibility=devices?Visibility.Collapsed:Visibility.Visible;
        _detailTitle.Text=devices?"音频设备":"应用音量";
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
    public void Dispose(){if(_disposed)return;_disposed=true;CancelPending();_audio.Changed-=Render;_audio.Dispose();_brightness?.Dispose();_power?.Dispose();}
}

internal sealed class AudioLevelRow : StackPanel
{
    private readonly TextBlock _name=Ui.Text("",11),_percent=Ui.Text("—");
    private readonly Slider _slider=new(){Minimum=0,Maximum=100,TickFrequency=1,IsSnapToTickEnabled=true,VerticalAlignment=VerticalAlignment.Center};
    private readonly ToggleButton _mute=new(){MinWidth=56,Height=30,Margin=new Thickness(6,0,0,0)};
    private readonly Func<AudioTarget,bool> _pending;
    private AudioLevel? _level;
    private bool _updating;
    internal AudioLevelRow(string title,Action<AudioTarget,int> volume,Action<AudioTarget,bool> mute,Action flush,Func<AudioTarget,bool> pending)
    {
        _pending=pending;Margin=new Thickness(0,0,0,12);
        var heading=new Grid();heading.ColumnDefinitions.Add(new(){Width=GridLength.Auto});heading.ColumnDefinitions.Add(new());heading.Children.Add(Ui.Text(title,14));_name.Margin=new Thickness(8,0,0,0);_name.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(_name,1);heading.Children.Add(_name);Children.Add(heading);_name.TextTrimming=TextTrimming.CharacterEllipsis;_name.TextWrapping=TextWrapping.NoWrap;_name.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
        var row=new Grid();row.ColumnDefinitions.Add(new());row.ColumnDefinitions.Add(new(){Width=new GridLength(42)});row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        row.Children.Add(_slider);Grid.SetColumn(_percent,1);_percent.TextAlignment=TextAlignment.Right;row.Children.Add(_percent);Grid.SetColumn(_mute,2);_mute.SetResourceReference(Control.ForegroundProperty,"Ink");row.Children.Add(_mute);Children.Add(row);
        _mute.Template=(ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ToggleButton">
              <Border x:Name="Frame" Background="{DynamicResource PaneCard}" BorderBrush="{DynamicResource PaneOutline}" BorderThickness="1" CornerRadius="6" Padding="7,3">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource Hover}"/></Trigger>
                <Trigger Property="IsChecked" Value="True"><Setter Property="Foreground" Value="{DynamicResource Accent}"/><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
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
            _mute.IsChecked=enabled&&level!.Muted;_mute.Content=enabled&&level!.Muted?"已静音":"静音";
        }finally{_updating=false;}
    }
}
