using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace FileDockDemo.Features.ControlCenter;

internal sealed class BrightnessView:IDisposable
{
    private readonly BrightnessController _controller;
    private readonly Func<string?,Task<bool>> _save;
    private readonly StackPanel _home=new(),_details=new(),_rows=new();
    private readonly ComboBox _selector=new(){MinHeight=30,DisplayMemberPath="Label"};
    private readonly TextBlock _message=Ui.Text("",11);
    private readonly Dictionary<string,BrightnessRow> _screens=new();
    private readonly BrightnessRow _main;
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromMilliseconds(120)};
    private readonly Dictionary<BrightnessTarget,int> _pending=new();
    private string? _preferred,_saveError;
    private bool _updating,_saving,_disposed;
    internal FrameworkElement Home=>_home;
    internal FrameworkElement Details=>_details;
    internal event Action? DetailsRequested;
    internal BrightnessView(BrightnessController controller,string? preferred,Func<string?,Task<bool>> save)
    {
        _controller=controller;_preferred=preferred;_save=save;
        _main=new("亮度",Queue,Flush,target=>_pending.ContainsKey(target));
        var header=new DockPanel();var details=Ui.Button("显示器 ›",()=>DetailsRequested?.Invoke());details.SetResourceReference(FrameworkElement.StyleProperty,"QuietButton");DockPanel.SetDock(details,Dock.Right);header.Children.Add(details);header.Children.Add(Ui.Text("亮度",14));
        _home.Children.Add(header);_home.Children.Add(_main);
        _details.Children.Add(Ui.Text("主页面调节的屏幕"));AutomationProperties.SetName(_selector,"亮度屏幕选择");_details.Children.Add(_selector);_details.Children.Add(_rows);
        _details.Children.Add(_message);_message.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
        _selector.SelectionChanged+=async(_,_)=>{
            if(_updating||_saving||_selector.SelectedItem is not BrightnessDevice item||item.Id==_preferred)return;
            Cancel();_saving=true;_selector.IsEnabled=false;
            try{if(await _save(item.Id)){_preferred=item.Id;_saveError=null;}else _saveError="屏幕偏好保存失败，保留原选择。";}
            catch(Exception error){_saveError="屏幕偏好保存失败："+error.Message;}
            finally{_saving=false;if(!_disposed)Render(_controller.Current);}
        };
        _timer.Tick+=(_,_)=>Flush();_controller.Changed+=Render;Render(_controller.Current);
    }
    private void Queue(BrightnessTarget target,int percent){_pending[target]=percent;_timer.Stop();_timer.Start();}
    internal void Flush(){_timer.Stop();foreach(var pair in _pending)_controller.SetBrightness(pair.Key,pair.Value);_pending.Clear();}
    internal void Cancel(){_timer.Stop();_pending.Clear();}
    private void Render(BrightnessSnapshot snapshot)
    {
        if(_disposed)return;
        foreach(var target in _pending.Keys.Where(t=>!snapshot.Devices.Any(d=>d.Target==t&&d.Percent.HasValue)).ToArray())_pending.Remove(target);
        var selected=snapshot.Devices.FirstOrDefault(d=>d.Id==_preferred)??snapshot.Devices.FirstOrDefault(d=>d.Primary)??snapshot.Devices.FirstOrDefault();
        _main.Bind(selected);_message.Text=_saveError??snapshot.Error??"外接屏需要支持并开启 DDC/CI；切回页面或点击刷新可重新读取。";
        _updating=true;
        try{_selector.ItemsSource=snapshot.Devices;_selector.SelectedItem=selected;_selector.IsEnabled=!_saving&&snapshot.Devices.Count>0;}finally{_updating=false;}
        var ids=snapshot.Devices.Select(d=>d.Id).ToHashSet();
        foreach(var id in _screens.Keys.Where(id=>!ids.Contains(id)).ToArray()){_rows.Children.Remove(_screens[id]);_screens.Remove(id);}
        foreach(var device in snapshot.Devices)
        {
            if(!_screens.TryGetValue(device.Id,out var row)){row=new("屏幕亮度",Queue,Flush,t=>_pending.ContainsKey(t),true);_screens.Add(device.Id,row);_rows.Children.Add(row);}
            row.Bind(device);
        }
    }
    public void Dispose(){if(_disposed)return;_disposed=true;Cancel();_controller.Changed-=Render;_controller.Dispose();}
}

internal sealed class BrightnessRow:StackPanel
{
    private readonly TextBlock _name=Ui.Text("",11),_value=Ui.Text("—"),_error=Ui.Text("",11);
    private readonly Slider _slider=new(){Minimum=0,Maximum=100,IsSnapToTickEnabled=true,TickFrequency=1,VerticalAlignment=VerticalAlignment.Center};
    private readonly Func<BrightnessTarget,bool> _pending;
    private BrightnessDevice? _device;
    private BrightnessTarget? _gestureTarget;
    private bool _binding,_cancelledGesture;
    internal BrightnessRow(string name,Action<BrightnessTarget,int> change,Action flush,Func<BrightnessTarget,bool> pending,bool showError=false)
    {
        _pending=pending;Margin=new Thickness(0,0,0,8);_name.TextTrimming=TextTrimming.CharacterEllipsis;_name.TextWrapping=TextWrapping.NoWrap;_name.SetResourceReference(TextBlock.ForegroundProperty,"Muted");Children.Add(_name);
        var line=new Grid();line.ColumnDefinitions.Add(new());line.ColumnDefinitions.Add(new(){Width=new GridLength(46)});line.Children.Add(_slider);Grid.SetColumn(_value,1);_value.TextAlignment=TextAlignment.Right;line.Children.Add(_value);Children.Add(line);
        if(showError){_error.SetResourceReference(TextBlock.ForegroundProperty,"Muted");Children.Add(_error);}
        AutomationProperties.SetName(_slider,name+"百分比");
        void Begin(){_cancelledGesture=false;_gestureTarget=_device?.Target;}
        void Finish(){_gestureTarget=null;if(!_cancelledGesture)flush();}
        _slider.PreviewMouseLeftButtonDown+=(_,_)=>Begin();_slider.PreviewKeyDown+=(_,_)=>Begin();
        _slider.AddHandler(Thumb.DragStartedEvent,new DragStartedEventHandler((_,_)=>Begin()));
        _slider.ValueChanged+=(_,_)=>{
            if(_binding||_device==null||!_slider.IsEnabled)return;
            if(_cancelledGesture){_binding=true;try{_slider.Value=_device.Percent??0;}finally{_binding=false;}return;}
            _value.Text=((int)_slider.Value)+"%";change(_gestureTarget??_device.Target,(int)_slider.Value);
        };
        _slider.AddHandler(Thumb.DragCompletedEvent,new DragCompletedEventHandler((_,_)=>Finish()));_slider.PreviewMouseLeftButtonUp+=(_,_)=>Finish();_slider.PreviewKeyUp+=(_,_)=>Finish();
    }
    internal void Bind(BrightnessDevice? device)
    {
        bool changed=_device?.Target!=device?.Target;
        if(changed&&_gestureTarget!=null){_cancelledGesture=true;_gestureTarget=null;if(_slider.IsMouseCaptureWithin)Mouse.Capture(null);}
        _device=device;_binding=true;
        try
        {
            _name.Text=device?.Label??"无可用屏幕";_name.ToolTip=device?.Label;
            _slider.IsEnabled=device?.Percent!=null&&device.Error==null;_slider.ToolTip=device?.Error;
            if(changed||!_slider.IsMouseCaptureWithin&&(device==null||!_pending(device.Target))){_slider.Value=device?.Percent??0;_value.Text=_slider.IsEnabled?device!.Percent+"%":"—";}
            _error.Text=device?.Error??"";_error.Visibility=string.IsNullOrEmpty(_error.Text)?Visibility.Collapsed:Visibility.Visible;
        }finally{_binding=false;}
    }
}
