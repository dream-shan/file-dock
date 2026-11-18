using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Windows.Networking.Connectivity;

namespace FileDockDemo.Features.ControlCenter;

internal sealed class WifiControlPane:IDisposable
{
    private readonly bool _native;
    private readonly Dispatcher _dispatcher=Dispatcher.CurrentDispatcher;
    private readonly WirelessOperationScope _scope=new();
    private readonly ComboBox _adapters=new(){DisplayMemberPath="Name",MinHeight=32};
    private readonly TextBlock _status=WirelessView.Caption(""),_operation=WirelessView.Caption(""),_networkPrompt=WirelessView.Caption(""),_editorTitle=Ui.Text("");
    private readonly StackPanel _list=new(),_editor=new();
    private readonly PasswordBox _password=new(){MinHeight=32,MaxLength=64};
    private readonly CheckBox _remember=new(){Content="记住此网络"},_automatic=new(){Content="自动连接"};
    private readonly Button _scan,_disconnect,_submit;
    private IReadOnlyList<WifiAdapter> _inventory=[];
    private IReadOnlyList<WifiNetwork> _networks=[];
    private NativeWifi? _watch;
    private Guid? _selected,_listedFor;
    private WifiNetwork? _entry;
    private CancellationTokenSource? _detailsLifetime;
    private bool _binding,_refreshPending,_networkEvent,_disposed;
    private int _visibleGeneration;
    private int _eventQueued;
    private DateTime _lastScan=DateTime.MinValue;

    internal WifiControlPane(Action showDetails,bool native)
    {
        _native=native;
        Tile=new WirelessTile("Wi-Fi","\uE701",()=>_ = ToggleAsync(),showDetails);
        Details=new StackPanel();
        Details.Children.Add(_status);
        AutomationProperties.SetName(_adapters,"Wi-Fi 网卡");Details.Children.Add(_adapters);
        var actions=new UniformGrid{Columns=2};
        _scan=Ui.Button("查看附近网络",()=>_ = ScanAsync());_scan.Margin=new Thickness(0,4,3,0);actions.Children.Add(_scan);
        _disconnect=Ui.Button("断开此网卡连接",()=>_ = DisconnectAsync());_disconnect.Margin=new Thickness(3,4,0,0);actions.Children.Add(_disconnect);Details.Children.Add(actions);
        Details.Children.Add(_operation);Details.Children.Add(_networkPrompt);Details.Children.Add(_list);
        _editor.Children.Add(_editorTitle);AutomationProperties.SetName(_password,"Wi-Fi 密码");_editor.Children.Add(_password);
        foreach(var option in new[]{_remember,_automatic})
        {
            option.Margin=new Thickness(0,6,0,0);option.SetResourceReference(Control.ForegroundProperty,"Ink");_editor.Children.Add(option);
        }
        _remember.Checked+=(_,_)=>_automatic.IsEnabled=true;
        _remember.Unchecked+=(_,_)=>{_automatic.IsChecked=false;_automatic.IsEnabled=false;};
        _submit=Ui.Button("连接所选网络",()=>_ = SubmitAsync());_editor.Children.Add(_submit);
        _editor.Children.Add(Ui.Button("取消输入",ClearEditor));Details.Children.Add(_editor);
        Details.Children.Add(Ui.Button("打开 Windows Wi-Fi 设置",()=>WirelessView.OpenSettings("ms-settings:network-wifi",_scope)));
        Details.Children.Add(Ui.Button("检查定位权限",()=>WirelessView.OpenSettings("ms-settings:privacy-location",_scope)));
        _adapters.SelectionChanged+=(_,_)=>{
            if(_binding||_adapters.SelectedItem is not WifiAdapter adapter)return;
            if(_selected==adapter.Id)return;
            _selected=adapter.Id;ClearNetworks();Render();
        };
        _scope.Changed+=OnScopeChanged;
        ClearEditor();Render();
    }

    internal WirelessTile Tile{get;}
    internal StackPanel Details{get;}
    private WifiAdapter? Selected=>_inventory.FirstOrDefault(a=>a.Id==_selected);

    internal void SetActive(bool active)
    {
        if(_disposed||active==_scope.Active)return;
        Interlocked.Increment(ref _visibleGeneration);
        if(!active)
        {
            if(_networkEvent){NetworkInformation.NetworkStatusChanged-=NetworkChanged;_networkEvent=false;}
            DropWatch();_inventory=[];ClearNetworks();_refreshPending=false;_scope.SetActive(false);return;
        }
        _scope.SetActive(true);
        if(_native)
        {
            try{NetworkInformation.NetworkStatusChanged+=NetworkChanged;_networkEvent=true;}
            catch(Exception){/* WLAN notifications and manual refresh remain available. */}
            RequestRefresh();
        }
    }

    internal void SetDetails(bool visible)
    {
        if(visible==(_detailsLifetime!=null))return;
        if(visible)_detailsLifetime=new CancellationTokenSource();
        else{var old=_detailsLifetime;_detailsLifetime=null;old?.Cancel();old?.Dispose();ClearNetworks();}
        Render();
    }

    private void DropWatch()
    {
        var old=_watch;_watch=null;if(old==null)return;
        old.Changed-=QueueRefresh;
        _=Task.Run(old.Dispose);
    }

    private void NetworkChanged(object sender)=>QueueRefresh();
    private void QueueRefresh()
    {
        int generation=Volatile.Read(ref _visibleGeneration);
        if(Interlocked.Exchange(ref _eventQueued,1)!=0)return;
        _= _dispatcher.BeginInvoke(DispatcherPriority.Background,()=>{
            Interlocked.Exchange(ref _eventQueued,0);
            if(generation==_visibleGeneration)RequestRefresh();
        });
    }
    internal void RequestRefresh()
    {
        if(!_scope.Active||!_native)return;
        if(_scope.Busy){_refreshPending=true;return;}
        _refreshPending=false;
        _= _scope.RunAsync(async token=>{
            if(_watch==null)
            {
                NativeWifi? watch=null;
                try{watch=await Task.Run(()=>new NativeWifi(),token);}
                catch(WirelessException){/* If WLAN events are restricted, inventory and Windows network notifications still work. */}
                if(watch!=null)
                {
                    if(token.IsCancellationRequested){_=Task.Run(watch.Dispose);token.ThrowIfCancellationRequested();}
                    _watch=watch;watch.Changed+=QueueRefresh;
                }
            }
            await UpdateInventory(token);
            if(_detailsLifetime is { } details&&_listedFor is Guid id&&_entry==null&&Selected is{Enabled:true})
            {
                using var listRequest=CancellationTokenSource.CreateLinkedTokenSource(token,details.Token);
                try{await UpdateListedNetworks(id,listRequest.Token);}
                catch(OperationCanceledException) when(listRequest.IsCancellationRequested){}
            }
            return null;
        },preserveMessage:true);
    }

    private void OnScopeChanged()
    {
        Render();
        if(_refreshPending&&_scope.Active&&!_scope.Busy)
            _= _dispatcher.BeginInvoke(DispatcherPriority.Background,()=>{if(_refreshPending)RequestRefresh();});
    }

    private async Task UpdateInventory(CancellationToken token)
    {
        var inventory=await Task.Run(NativeWifi.ReadAdapters,token);
        token.ThrowIfCancellationRequested();_inventory=inventory;
        var chosen=Selected??inventory.FirstOrDefault(a=>a.Connected)??inventory.FirstOrDefault();
        if(_selected!=chosen?.Id){_selected=chosen?.Id;ClearNetworks();}
        if(chosen?.Enabled!=true&&_listedFor!=null)ClearNetworks();
    }

    private async Task UpdateListedNetworks(Guid id,CancellationToken token)
    {
        try
        {
            var networks=await Task.Run(()=>{using var wifi=new NativeWifi(false);token.ThrowIfCancellationRequested();return wifi.Networks(id);},token);
            token.ThrowIfCancellationRequested();
            if(_detailsLifetime!=null&&_listedFor==id&&_selected==id)_networks=networks;
        }
        catch(WirelessException){if(!token.IsCancellationRequested&&_listedFor==id)ClearNetworks();throw;}
    }

    private Task ScanAsync()
    {
        if(Selected is not { Enabled:true } adapter||_detailsLifetime==null||_scope.Busy)return Task.CompletedTask;
        if(DateTime.UtcNow-_lastScan<TimeSpan.FromSeconds(3)){_scope.ShowError("刚刷新过，请稍候再扫描。");return Task.CompletedTask;}
        _lastScan=DateTime.UtcNow;ClearEditor();
        return _scope.RunAsync(async token=>{
            var networks=await Task.Run(async()=>{using var wifi=new NativeWifi();return await wifi.ScanAsync(adapter.Id,token);},token);
            token.ThrowIfCancellationRequested();_listedFor=adapter.Id;_networks=networks;return null;
        },_detailsLifetime.Token);
    }

    private Task ToggleAsync()
    {
        if(Selected is not { Enabled:bool on,Blocked:false } adapter||_scope.Busy)return Task.CompletedTask;
        return _scope.RunAsync(async token=>{
            try{await Task.Run(async()=>{using var wifi=new NativeWifi(false);await wifi.SetRadioAsync(adapter.Id,!on,token);},token);}
            finally{if(!token.IsCancellationRequested)await UpdateInventory(token);}
            return null;
        });
    }

    private Task DisconnectAsync()
    {
        if(Selected is not {Connected:true} adapter||_detailsLifetime==null||_scope.Busy)return Task.CompletedTask;
        return _scope.RunAsync(async token=>{
            try{await Task.Run(async()=>{using var wifi=new NativeWifi(false);await wifi.DisconnectAsync(adapter,token);},token);}
            finally{if(!token.IsCancellationRequested)await UpdateInventory(token);}
            if(_listedFor==adapter.Id)await UpdateListedNetworks(adapter.Id,token);
            return "已断开当前连接";
        },_detailsLifetime.Token);
    }

    private void Choose(WifiNetwork network)
    {
        if(_scope.Busy||_detailsLifetime==null||_selected!=_listedFor||!network.CanJoin||network.Connected)return;
        ClearEditor();
        if(network.Profile!=null){_ = ConnectAsync(network,"",false,false);return;}
        _entry=network;_editorTitle.Text="连接 "+network.Name;_editor.Visibility=Visibility.Visible;
        _password.Visibility=network.Security==WifiSecurity.Open?Visibility.Collapsed:Visibility.Visible;
        _automatic.IsEnabled=false;Render();if(_password.IsVisible)_password.Focus();else _submit.Focus();
    }

    private Task SubmitAsync()
    {
        if(_entry==null||_scope.Busy)return Task.CompletedTask;
        var target=_entry;string password=_password.Password;bool remember=_remember.IsChecked==true,automatic=_automatic.IsChecked==true;
        _password.Clear();return ConnectAsync(target,password,remember,automatic);
    }

    private Task ConnectAsync(WifiNetwork target,string password,bool remember,bool automatic)
    {
        if(Selected is not { Enabled:true } adapter||_detailsLifetime==null||_listedFor!=adapter.Id)return Task.CompletedTask;
        return _scope.RunAsync(async token=>{
            string result;
            try{result=await Task.Run(async()=>{using var wifi=new NativeWifi();return await wifi.ConnectAsync(adapter.Id,target,password,remember,automatic,token);},token);}
            finally{if(!token.IsCancellationRequested)await UpdateInventory(token);}
            token.ThrowIfCancellationRequested();ClearEditor();
            if(_listedFor==adapter.Id)
            {
                try{await UpdateListedNetworks(adapter.Id,token);}
                catch(WirelessException){return result+"。网络列表可手动刷新。";}
            }
            return result;
        },_detailsLifetime.Token);
    }

    private void ClearNetworks(){_listedFor=null;_networks=[];ClearEditor();}
    private void ClearEditor(){_entry=null;_password.Clear();_remember.IsChecked=false;_automatic.IsChecked=false;_editor.Visibility=Visibility.Collapsed;}

    private void Render()
    {
        if(_disposed)return;
        var adapter=Selected;bool ready=_scope.Active&&!_scope.Busy;
        string summary=adapter?.Status??(_scope.Busy?"正在读取…":_scope.Message!=null?"暂不可用":"未检测到网卡");
        Tile.Render(summary,ready&&adapter is{Enabled:not null,Blocked:false},adapter?.Enabled,_scope.Message);
        if(_scope.Message!=null&&_detailsLifetime==null)Tile.ToolTip=_scope.Message;else Tile.ClearValue(FrameworkElement.ToolTipProperty);
        _status.Text=adapter?.Connectivity is { } access?$"{summary} · {access}":summary;
        _operation.Text=_scope.Busy?"正在处理…":_scope.Message??"";
        _operation.Visibility=string.IsNullOrEmpty(_operation.Text)?Visibility.Collapsed:Visibility.Visible;
        _binding=true;
        try{if(_adapters.ItemsSource!=_inventory)_adapters.ItemsSource=_inventory;_adapters.SelectedItem=adapter;}
        finally{_binding=false;}
        _adapters.Visibility=_inventory.Count>1?Visibility.Visible:Visibility.Collapsed;_adapters.IsEnabled=ready;
        _scan.Content=_listedFor.HasValue?"刷新附近网络":"查看附近网络";
        _scan.IsEnabled=ready&&adapter?.Enabled==true;_disconnect.IsEnabled=ready&&adapter?.Connected==true;
        _editor.IsEnabled=ready;
        _networkPrompt.Text=_listedFor.HasValue?(_networks.Count==0?"附近没有可用网络。":""):(adapter?.Enabled==true?"查看附近网络时，Windows 可能要求定位权限。":"打开 Wi-Fi 后可查看附近网络。");
        _list.Children.Clear();
        foreach(var network in _networks.Take(80))
        {
            string label=network.Name+"\n"+network.Description;
            if(!network.CanJoin&&!network.Connected)label+=" · 请使用系统设置";
            var button=Ui.Button(label,()=>Choose(network));button.Content=new TextBlock{Text=label,TextWrapping=TextWrapping.Wrap};
            button.HorizontalContentAlignment=HorizontalAlignment.Left;button.IsEnabled=ready&&network.CanJoin&&!network.Connected;_list.Children.Add(button);
        }
    }

    public void Dispose(){if(_disposed)return;SetDetails(false);SetActive(false);_disposed=true;_scope.Dispose();}
}
