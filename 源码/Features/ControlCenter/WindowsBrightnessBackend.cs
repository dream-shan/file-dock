using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace FileDockDemo.Features.ControlCenter;

internal sealed record BrightnessRestorePoint(string Id,uint Minimum,uint Maximum,uint Raw);

internal sealed class WindowsBrightnessBackend:IBrightnessBackend
{
    private sealed class Entry
    {
        internal required string Id,Name,Adapter;
        internal bool Primary;
        internal long Connection;
        internal string? Wmi,Error;
        internal IntPtr Handle;
    }
    private readonly Dictionary<string,Entry> _devices=new(StringComparer.OrdinalIgnoreCase);
    private Action? _changed;
    private ManagementEventWatcher? _watcher;
    private long _connection;
    private int _dirty=1;
    private bool _started;
    private readonly bool _enabled;
    internal WindowsBrightnessBackend(bool enabled=true)=>_enabled=enabled;
    public void Start(Action changed)
    {
        Stop();if(!_enabled)return;_changed=changed;_started=true;
        SystemEvents.DisplaySettingsChanged+=DisplayChanged;
        try
        {
            _watcher=new ManagementEventWatcher("root\\wmi","SELECT * FROM WmiMonitorBrightnessEvent");
            _watcher.EventArrived+=BrightnessChanged;_watcher.Start();
        }
        catch{_watcher?.Dispose();_watcher=null;}
    }
    private void DisplayChanged(object? sender,EventArgs args){Interlocked.Increment(ref _connection);Interlocked.Exchange(ref _dirty,1);_changed?.Invoke();}
    private void BrightnessChanged(object sender,EventArrivedEventArgs args)=>_changed?.Invoke();
    internal static string NormalizeId(string id)
    {
        string value=id.Replace('#','\\');if(value.StartsWith("\\\\?\\",StringComparison.Ordinal))value=value[4..];
        int suffix=value.IndexOf("\\{",StringComparison.Ordinal);if(suffix>=0)value=value[..suffix];
        int underscore=value.LastIndexOf('_');if(underscore>=0&&value[(underscore+1)..].All(char.IsDigit))value=value[..underscore];
        return value.ToUpperInvariant();
    }
    private static Dictionary<string,(string Instance,int Percent)> WmiValues()
    {
        var values=new List<(string Instance,int Percent)>();
        try
        {
            using var query=new ManagementObjectSearcher(new ManagementScope("root\\wmi"),new ObjectQuery("SELECT InstanceName,CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE"),new EnumerationOptions{Timeout=TimeSpan.FromSeconds(4)});
            using var results=query.Get();foreach(ManagementObject row in results)using(row)
            {
                string instance=(string)row["InstanceName"];values.Add((instance,Convert.ToInt32(row["CurrentBrightness"])));
            }
        }catch(ManagementException){}catch(COMException){}catch(UnauthorizedAccessException){}
        return UniqueWmiValues(values);
    }
    internal static Dictionary<string,(string Instance,int Percent)> UniqueWmiValues(IEnumerable<(string Instance,int Percent)> source)
    {
        return source.GroupBy(value=>NormalizeId(value.Instance),StringComparer.OrdinalIgnoreCase).Where(group=>group.Count()==1).ToDictionary(group=>group.Key,group=>group.Single(),StringComparer.OrdinalIgnoreCase);
    }
    private void Clear()
    {
        foreach(var entry in _devices.Values)if(entry.Handle!=IntPtr.Zero)Native.DestroyPhysicalMonitor(entry.Handle);
        _devices.Clear();
    }
    private static List<Native.DisplayDevice> MonitorDevices(string adapter)
    {
        var devices=new List<Native.DisplayDevice>();
        for(uint index=0;index<64;index++)
        {
            var device=new Native.DisplayDevice{Size=Marshal.SizeOf<Native.DisplayDevice>()};
            if(!Native.EnumDisplayDevices(adapter,index,ref device,1))break;
            if((device.Flags&1)!=0)devices.Add(device);
        }
        return devices;
    }
    private void Rebuild(Dictionary<string,(string Instance,int Percent)> wmi)
    {
        Clear();long connection=Interlocked.Read(ref _connection);var handles=new List<IntPtr>();
        Native.MonitorCallback callback=(IntPtr monitor,IntPtr dc,ref Native.Rect rect,IntPtr data)=>{handles.Add(monitor);return true;};
        if(!Native.EnumDisplayMonitors(IntPtr.Zero,IntPtr.Zero,callback,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());
        int ordinal=0;
        foreach(var monitor in handles)
        {
            var info=new Native.MonitorInfo{Size=Marshal.SizeOf<Native.MonitorInfo>()};if(!Native.GetMonitorInfo(monitor,ref info))continue;
            var logical=MonitorDevices(info.Device);if(logical.Count==0)logical.Add(new Native.DisplayDevice{Id="unmapped:"+info.Device,Description="未识别的显示器"});
            foreach(var device in logical)
            {
                string id=NormalizeId(device.Id);var entry=new Entry{Id=id,Name=$"屏幕 {++ordinal} · {device.Description}",Adapter=info.Device,Primary=(info.Flags&1)!=0,Connection=connection};
                if(_devices.ContainsKey(id)){_devices[id].Error="无法区分同一显示路径，暂不可调节。";continue;}
                _devices.Add(id,entry);
                if(logical.Count!=1||id.StartsWith("UNMAPPED:",StringComparison.Ordinal)){entry.Error="无法可靠识别屏幕连接，暂不可调节。";continue;}
                if(wmi.TryGetValue(id,out var builtIn)){entry.Wmi=builtIn.Instance;continue;}
                if(!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(monitor,out uint count)||count!=1){entry.Error="此连接未提供可单独控制的 DDC/CI 屏幕。";continue;}
                var physical=new Native.PhysicalMonitor[1];
                if(!Native.GetPhysicalMonitorsFromHMONITOR(monitor,1,physical)){entry.Error="显示器连接暂不可访问。";continue;}
                entry.Handle=physical[0].Handle;
                if(!string.IsNullOrWhiteSpace(physical[0].Description))entry.Name=$"屏幕 {ordinal} · {physical[0].Description}";
                if(!Native.GetMonitorCapabilities(entry.Handle,out uint caps,out _)||(caps&2)==0)entry.Error="未提供亮度控制；外接屏请检查 DDC/CI 设置。";
            }
        }
    }
    public BrightnessSnapshot Read()
    {
        if(!_enabled)return BrightnessSnapshot.Empty();
        var wmi=WmiValues();if(Interlocked.Exchange(ref _dirty,0)!=0)Rebuild(wmi);
        var values=new List<BrightnessDevice>();
        foreach(var entry in _devices.Values)
        {
            int? percent=null;string? error=entry.Error;
            if(error==null)
            {
                if(entry.Wmi!=null)
                {
                    if(wmi.TryGetValue(entry.Id,out var value))percent=Math.Clamp(value.Percent,0,100);else error="内置屏亮度暂不可读取，请刷新。";
                }
                else if(Native.GetMonitorBrightness(entry.Handle,out uint minimum,out uint current,out uint maximum)&&maximum>minimum&&current>=minimum&&current<=maximum)percent=BrightnessRange.ToPercent(minimum,current,maximum);
                else error="亮度暂不可读取；请检查屏幕连接与 DDC/CI 设置。";
            }
            values.Add(new(entry.Id,entry.Name,entry.Primary,percent,error,entry.Connection));
        }
        return new(values);
    }
    private Entry Validate(BrightnessTarget target)
    {
        if(!_started||target.Connection!=Interlocked.Read(ref _connection)||!_devices.TryGetValue(target.Id,out var entry)||entry.Connection!=target.Connection||entry.Error!=null)
            throw new InvalidOperationException("屏幕连接已变化，请刷新后重试。");
        var paths=MonitorDevices(entry.Adapter);
        if(paths.Count!=1||NormalizeId(paths[0].Id)!=entry.Id)throw new InvalidOperationException("原屏幕已断开，请重新选择。");
        return entry;
    }
    public void Write(BrightnessTarget target,int percent)
    {
        var entry=Validate(target);percent=Math.Clamp(percent,0,100);
        if(entry.Wmi!=null)
        {
            using var query=new ManagementObjectSearcher(new ManagementScope("root\\wmi"),new ObjectQuery("SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE"),new EnumerationOptions{Timeout=TimeSpan.FromSeconds(4)});
            using var results=query.Get();bool found=false;
            foreach(ManagementObject row in results)using(row)
            {
                if((string)row["InstanceName"]!=entry.Wmi)continue;
                Validate(target);object? result=row.InvokeMethod("WmiSetBrightness",new object[]{0u,(byte)percent});
                if(result!=null&&Convert.ToUInt32(result)!=0)throw new InvalidOperationException("Windows 拒绝了亮度设置。");found=true;break;
            }
            if(!found)throw new InvalidOperationException("原内置屏亮度接口已不可用。");
        }
        else
        {
            if(!Native.GetMonitorBrightness(entry.Handle,out uint minimum,out _,out uint maximum)||maximum<=minimum)throw new InvalidOperationException("无法读取原屏幕亮度范围。");
            Validate(target);if(!Native.SetMonitorBrightness(entry.Handle,BrightnessRange.ToRaw(minimum,maximum,percent)))throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
    internal BrightnessRestorePoint Capture(BrightnessTarget target)
    {
        var entry=Validate(target);
        if(entry.Wmi!=null)
        {
            if(!WmiValues().TryGetValue(entry.Id,out var value))throw new InvalidOperationException("原亮度不可读取，不能安全测试。");
            return new(entry.Id,0,100,(uint)value.Percent);
        }
        if(!Native.GetMonitorBrightness(entry.Handle,out uint minimum,out uint current,out uint maximum)||maximum<=minimum)throw new InvalidOperationException("原亮度范围不可读取。");
        return new(entry.Id,minimum,maximum,current);
    }
    internal void Restore(BrightnessRestorePoint point)
    {
        var device=Read().Devices.SingleOrDefault(d=>d.Id==point.Id)??throw new InvalidOperationException("原屏幕已断开，无法恢复其亮度。");
        var entry=Validate(device.Target);
        if(entry.Wmi!=null){Write(device.Target,(int)point.Raw);return;}
        var range=Capture(device.Target);if(range.Minimum!=point.Minimum||range.Maximum!=point.Maximum)throw new InvalidOperationException("原屏幕亮度范围已变化，未进行恢复写入。");
        if(!Native.SetMonitorBrightness(entry.Handle,point.Raw))throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Stop()
    {
        _changed=null;if(_started)SystemEvents.DisplaySettingsChanged-=DisplayChanged;_started=false;Interlocked.Increment(ref _connection);Interlocked.Exchange(ref _dirty,1);
        if(_watcher!=null){_watcher.EventArrived-=BrightnessChanged;try{_watcher.Stop();}catch{} _watcher.Dispose();_watcher=null;}Clear();
    }
    public void Dispose()=>Stop();

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]internal struct Rect{internal int Left,Top,Right,Bottom;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]internal struct MonitorInfo{internal int Size;internal Rect Monitor,Work;internal uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]internal string Device;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]internal struct DisplayDevice{internal int Size;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]internal string Name;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]internal string Description;internal uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]internal string Id;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]internal string Key;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]internal struct PhysicalMonitor{internal IntPtr Handle;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]internal string Description;}
        internal delegate bool MonitorCallback(IntPtr monitor,IntPtr dc,ref Rect rect,IntPtr data);
        [DllImport("user32.dll",SetLastError=true)]internal static extern bool EnumDisplayMonitors(IntPtr dc,IntPtr clip,MonitorCallback callback,IntPtr data);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="GetMonitorInfoW")]internal static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="EnumDisplayDevicesW")]internal static extern bool EnumDisplayDevices(string adapter,uint index,ref DisplayDevice device,uint flags);
        [DllImport("dxva2.dll",SetLastError=true)]internal static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor,out uint count);
        [DllImport("dxva2.dll",SetLastError=true)]internal static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor,uint count,[Out]PhysicalMonitor[] physical);
        [DllImport("dxva2.dll")]internal static extern bool DestroyPhysicalMonitor(IntPtr monitor);
        [DllImport("dxva2.dll")]internal static extern bool GetMonitorCapabilities(IntPtr monitor,out uint caps,out uint temperature);
        [DllImport("dxva2.dll")]internal static extern bool GetMonitorBrightness(IntPtr monitor,out uint minimum,out uint current,out uint maximum);
        [DllImport("dxva2.dll",SetLastError=true)]internal static extern bool SetMonitorBrightness(IntPtr monitor,uint brightness);
    }
}

internal static class BrightnessRange
{
    internal static int ToPercent(uint minimum,uint current,uint maximum)=>maximum>minimum?(int)Math.Round((current-(double)minimum)/(maximum-minimum)*100):throw new ArgumentException("无效亮度范围");
    internal static uint ToRaw(uint minimum,uint maximum,int percent)=>maximum>minimum?(uint)Math.Round(minimum+(maximum-(double)minimum)*Math.Clamp(percent,0,100)/100):throw new ArgumentException("无效亮度范围");
}
