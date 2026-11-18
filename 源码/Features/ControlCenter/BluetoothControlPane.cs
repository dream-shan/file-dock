using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Windows.Devices.Radios;

namespace FileDockDemo.Features.ControlCenter;

internal sealed class BluetoothControlPane:IDisposable
{
    private readonly bool _native;
    private readonly Dispatcher _dispatcher=Dispatcher.CurrentDispatcher;
    private readonly WirelessOperationScope _scope=new();
    private readonly TextBlock _status=WirelessView.Caption(""),_message=WirelessView.Caption("");
    private readonly StackPanel _list=new();
    private readonly Button _sound;
    private IReadOnlyList<BluetoothRadio> _radios=[];
    private IReadOnlyList<PairedBluetoothDevice> _paired=[];
    private CancellationTokenSource? _detailsLifetime;
    private bool _refreshPending,_disposed;
    private int _generation,_eventQueued;
    private BluetoothRadio? Radio=>_radios.Count==1?_radios[0]:null;

    internal BluetoothControlPane(Action showDetails,Action showAudio,bool native)
    {
        _native=native;
        Tile=new WirelessTile("蓝牙","\uE702",()=>_ = ToggleAsync(),showDetails);
        Details=new StackPanel();Details.Children.Add(_status);Details.Children.Add(_message);
        Details.Children.Add(Ui.Button("添加设备／管理蓝牙",()=>WirelessView.OpenSettings("ms-settings:bluetooth",_scope)));
        _sound=Ui.Button("调节已连接音频设备的音量",showAudio);Details.Children.Add(_sound);
        Details.Children.Add(Ui.Text("已配对设备"));Details.Children.Add(_list);
        Details.Children.Add(Ui.Button("刷新蓝牙状态",RequestRefresh));
        _scope.Changed+=ScopeChanged;Render();
    }

    internal WirelessTile Tile{get;}
    internal StackPanel Details{get;}

    internal void SetActive(bool active)
    {
        if(_disposed||active==_scope.Active)return;
        Interlocked.Increment(ref _generation);
        if(!active){ReplaceRadios([]);_paired=[];_refreshPending=false;}
        _scope.SetActive(active);
        if(active)RequestRefresh();
    }

    internal void SetDetails(bool visible)
    {
        if(visible==(_detailsLifetime!=null))return;
        if(visible){_detailsLifetime=new CancellationTokenSource();RequestRefresh();}
        else{var old=_detailsLifetime;_detailsLifetime=null;old?.Cancel();old?.Dispose();_paired=[];}
        Render();
    }

    private void ReplaceRadios(IReadOnlyList<BluetoothRadio> radios)
    {
        foreach(var adapter in _radios)adapter.Controller.StateChanged-=StateChanged;
        _radios=radios;
        foreach(var adapter in radios)adapter.Controller.StateChanged+=StateChanged;
    }

    private void StateChanged(Radio radio,object args)
    {
        int generation=Volatile.Read(ref _generation);
        if(Interlocked.Exchange(ref _eventQueued,1)!=0)return;
        _= _dispatcher.BeginInvoke(DispatcherPriority.Background,()=>{
            Interlocked.Exchange(ref _eventQueued,0);
            if(generation!=_generation||!_scope.Active)return;
            Render();RequestRefresh();
        });
    }

    internal void RequestRefresh()
    {
        if(!_scope.Active||!_native)return;
        if(_scope.Busy){_refreshPending=true;return;}
        _refreshPending=false;
        _= _scope.RunAsync(async token=>{await Read(token);return null;},preserveMessage:true);
    }

    private async Task Read(CancellationToken token)
    {
        var radios=await WindowsBluetooth.RadiosAsync(token);
        token.ThrowIfCancellationRequested();ReplaceRadios(radios);
        if(_detailsLifetime is not { } details)return;
        using var listRequest=CancellationTokenSource.CreateLinkedTokenSource(token,details.Token);
        try
        {
            var paired=await WindowsBluetooth.PairedAsync(listRequest.Token);
            if(!listRequest.IsCancellationRequested)_paired=paired;
        }
        catch(OperationCanceledException) when(listRequest.IsCancellationRequested){}
    }

    private void ScopeChanged()
    {
        Render();
        if(_refreshPending&&_scope.Active&&!_scope.Busy)
            _= _dispatcher.BeginInvoke(DispatcherPriority.Background,()=>{if(_refreshPending)RequestRefresh();});
    }

    private Task ToggleAsync()
    {
        if(Radio is not {Switchable:true} target||_scope.Busy)return Task.CompletedTask;
        bool on=!target.On;
        return _scope.RunAsync(async token=>{
            if(!on)
            {
                bool mayInterrupt;
                try
                {
                    var paired=await WindowsBluetooth.PairedAsync(token);
                    mayInterrupt=paired.Any(d=>d.Connected!=false&&(d.Input||d.Type=="蓝牙设备"));
                }
                catch(Exception) when(!token.IsCancellationRequested){mayInterrupt=true;}
                token.ThrowIfCancellationRequested();
                if(mayInterrupt&&!ConfirmOff())return null;
            }
            token.ThrowIfCancellationRequested();
            try{await WindowsBluetooth.SetStateAsync(target,on,token);}
            finally{if(!token.IsCancellationRequested)await Read(token);}
            return null;
        });
    }

    private bool ConfirmOff()
    {
        const string body="关闭蓝牙可能断开鼠标或键盘。请确认有其它输入方式可以重新打开蓝牙。";
        const string title="关闭蓝牙";
        return (Window.GetWindow(Tile) is { } owner
            ?MessageBox.Show(owner,body,title,MessageBoxButton.OKCancel,MessageBoxImage.Warning,MessageBoxResult.Cancel)
            :MessageBox.Show(body,title,MessageBoxButton.OKCancel,MessageBoxImage.Warning,MessageBoxResult.Cancel))==MessageBoxResult.OK;
    }

    private void Render()
    {
        if(_disposed)return;
        var radio=Radio;
        string state=radio==null?_radios.Count>1?"多个适配器，请使用系统设置":_scope.Busy?"正在读取…":_scope.Message!=null?"暂不可用":"未检测到适配器"
            :!radio.Switchable?"硬件或系统已禁用":radio.On?"已开启":"已关闭";
        int connected=_paired.Count(d=>d.Connected==true);
        string summary=radio?.On==true&&connected>0?$"已连接 {connected} 台":state;
        Tile.Render(summary,_scope.Active&&!_scope.Busy&&radio?.Switchable==true,radio?.On,_scope.Message);
        if(_scope.Message!=null&&_detailsLifetime==null)Tile.ToolTip=_scope.Message;else Tile.ClearValue(FrameworkElement.ToolTipProperty);
        _status.Text=state;
        _message.Text=_scope.Busy?"正在处理…":_scope.Message??"";
        _message.Visibility=string.IsNullOrEmpty(_message.Text)?Visibility.Collapsed:Visibility.Visible;
        _sound.Visibility=_paired.Any(d=>d.Connected==true&&d.Type=="音频设备")?Visibility.Visible:Visibility.Collapsed;
        _list.Children.Clear();
        foreach(var device in _paired.Take(80))
            _list.Children.Add(Ui.Text($"{device.Name} · {device.Type} · {device.Status}"));
        if(_paired.Count==0)_list.Children.Add(WirelessView.Caption(_scope.Busy?"正在读取配对记录…":"暂无可显示的配对设备。"));
    }

    public void Dispose(){if(_disposed)return;SetDetails(false);SetActive(false);_disposed=true;_scope.Dispose();}
}
