using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace FileDockDemo.Features.ControlCenter;

internal sealed record BluetoothRadio(string Id,Radio Controller)
{
    internal bool On=>Controller.State==RadioState.On;
    internal bool Switchable=>Controller.State is RadioState.On or RadioState.Off;
}

internal sealed record PairedBluetoothDevice(string Id,string Name,string Type,bool? Connected,bool Input)
{
    internal string Status=>Connected switch{true=>"已连接",false=>"未连接",null=>"连接状态未知"};
}

internal static class WindowsBluetooth
{
    private const string Connected="System.Devices.Aep.IsConnected",Container="System.Devices.Aep.ContainerId",
        Major="System.Devices.Aep.Bluetooth.Cod.Major",Minor="System.Devices.Aep.Bluetooth.Cod.Minor",Appearance="System.Devices.Aep.Bluetooth.Le.Appearance";
    private static readonly string[] Properties=[Connected,Container,Major,Minor,Appearance];

    internal static async Task<IReadOnlyList<BluetoothRadio>> RadiosAsync(CancellationToken token)
    {
        var devices=await DeviceInformation.FindAllAsync(Radio.GetDeviceSelector()).AsTask(token);
        var radios=new List<BluetoothRadio>();
        foreach(var device in devices)
        {
            token.ThrowIfCancellationRequested();
            var radio=await Radio.FromIdAsync(device.Id).AsTask(token);
            if(radio?.Kind==RadioKind.Bluetooth)radios.Add(new(device.Id,radio));
        }
        return radios;
    }

    internal static async Task SetStateAsync(BluetoothRadio target,bool on,CancellationToken token)
    {
        var access=await Radio.RequestAccessAsync().AsTask(token);
        if(access!=RadioAccessStatus.Allowed)throw new WirelessException("Windows 拒绝改变蓝牙开关，请检查系统设置或组织策略。");
        token.ThrowIfCancellationRequested();
        var current=await Radio.FromIdAsync(target.Id).AsTask(token);
        if(current==null||current.Kind!=RadioKind.Bluetooth||current.State is not (RadioState.On or RadioState.Off))
            throw new WirelessException("蓝牙适配器已变化或被禁用，请刷新。");
        var state=on?RadioState.On:RadioState.Off;
        if(current.State==state)return;
        token.ThrowIfCancellationRequested();
        access=await current.SetStateAsync(state).AsTask(token);
        if(access!=RadioAccessStatus.Allowed)throw new WirelessException("Windows 无法切换此蓝牙适配器，请使用系统设置。");
        for(int attempt=0;attempt<12;attempt++)
        {
            token.ThrowIfCancellationRequested();
            if(current.State==state)return;
            await Task.Delay(125,token);
        }
        throw new WirelessException("Windows 尚未确认蓝牙状态，请刷新。");
    }

    internal static async Task<IReadOnlyList<PairedBluetoothDevice>> PairedAsync(CancellationToken token)
    {
        var classic=DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true),Properties,DeviceInformationKind.AssociationEndpoint).AsTask(token);
        var lowEnergy=DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),Properties,DeviceInformationKind.AssociationEndpoint).AsTask(token);
        var results=await Task.WhenAll(classic,lowEnergy);
        return results.SelectMany(r=>r).Where(d=>d.Pairing.IsPaired).Select(Describe)
            .GroupBy(d=>d.Id,StringComparer.OrdinalIgnoreCase).Select(Merge)
            .OrderByDescending(d=>d.Connected==true).ThenBy(d=>d.Name,StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static PairedBluetoothDevice Describe(DeviceInformation device)
    {
        var p=device.Properties;
        bool? connected=p.TryGetValue(Connected,out var status)&&status is bool value?value:null;
        string id=p.TryGetValue(Container,out var container)&&container is Guid guid&&guid!=Guid.Empty?guid.ToString():device.Id;
        string type=DeviceType(ReadNumber(p,Major),ReadNumber(p,Minor),ReadNumber(p,Appearance));
        string name=string.IsNullOrWhiteSpace(device.Name)?"蓝牙设备":device.Name;
        return new(id,name,type,connected,type is "键盘" or "鼠标" or "键盘／鼠标" or "输入设备");
    }

    private static int ReadNumber(IReadOnlyDictionary<string,object> properties,string key)=>properties.TryGetValue(key,out var value)?value switch
    {
        byte b=>b, ushort s=>s, uint u when u<=int.MaxValue=>(int)u, int i=>i,_=>-1
    }:-1;

    internal static string DeviceType(int major,int minor,int appearance)
    {
        if(major==4)return "音频设备";
        if(major==5)return (minor&0x30) switch{0x10=>"键盘",0x20=>"鼠标",0x30=>"键盘／鼠标",_=>"输入设备"};
        return appearance switch{961=>"键盘",962=>"鼠标",960=>"输入设备",_=>"蓝牙设备"};
    }

    private static PairedBluetoothDevice Merge(IGrouping<string,PairedBluetoothDevice> group)
    {
        var device=group.OrderBy(d=>d.Type=="蓝牙设备").ThenByDescending(d=>d.Connected==true).First();
        bool? connected=group.Any(d=>d.Connected==true)?true:group.All(d=>d.Connected==false)?false:null;
        return device with{Connected=connected,Input=group.Any(d=>d.Input)};
    }
}
