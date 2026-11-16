using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace FileDockDemo.Features.ControlCenter;

internal sealed record PowerRestorePoint(Guid Plan,Guid? AC,Guid? DC);

internal sealed class PowerModeAccess
{
    private readonly Dictionary<PowerSupply,string> _errors=new();
    internal void Report(PowerSupply supply,uint result)
    {
        if(result is 1 or 5 or 50 or 120 or 1260 or 1314)_errors[supply]="系统不允许此供电状态的模式切换："+new Win32Exception((int)result).Message+"；可刷新后重试。";
    }
    internal string? Error(PowerSupply supply)=>_errors.TryGetValue(supply,out var error)?error:null;
    internal void Reset()=>_errors.Clear();
}

internal sealed class WindowsPowerBackend:IPowerBackend
{
    private static readonly Guid Efficiency=Guid.Parse("961cc777-2547-4f9d-8174-7d86181b8a7a"),Performance=Guid.Parse("ded574b5-45a0-4f42-8737-46345c09c238");
    private Action? _changed;
    private Native.EffectiveCallback? _callback;
    private IntPtr _registration;
    private int _effective=-1,_generation;
    private bool _started;
    private readonly PowerModeAccess _modeAccess=new();
    private readonly bool _enabled;
    internal WindowsPowerBackend(bool enabled=true)=>_enabled=enabled;
    public void Start(Action changed)
    {
        Stop();_modeAccess.Reset();if(!_enabled)return;_changed=changed;_started=true;SystemEvents.PowerModeChanged+=PowerChanged;int generation=Volatile.Read(ref _generation);
        _callback=(mode,_)=>{if(generation!=Volatile.Read(ref _generation))return;Volatile.Write(ref _effective,mode);_changed?.Invoke();};
        try{if(Native.PowerRegisterForEffectivePowerModeNotifications(2,_callback,IntPtr.Zero,out _registration)!=0)_registration=IntPtr.Zero;}
        catch(EntryPointNotFoundException){_registration=IntPtr.Zero;}
    }
    private void PowerChanged(object sender,PowerModeChangedEventArgs args)=>_changed?.Invoke();
    private static Native.SystemPowerStatus Status()
    {
        if(!Native.GetSystemPowerStatus(out var status))throw new Win32Exception(Marshal.GetLastWin32Error());return status;
    }
    private static PowerSupply Supply(Native.SystemPowerStatus status)=>status.AC switch{0=>PowerSupply.Battery,1=>PowerSupply.AC,_=>PowerSupply.Unknown};
    internal static (bool? Present,int? Percent,bool Charging) Battery(byte flags,byte percent)
    {
        if(flags==255)return(null,null,false);if((flags&128)!=0)return(false,null,false);
        return(true,percent<=100?percent:null,(flags&8)!=0);
    }
    private static PowerModeChoice? UserMode(PowerSupply supply,out string? error)
    {
        error=null;if(supply==PowerSupply.Unknown){error="供电状态未知，暂不可切换模式。";return null;}
        try
        {
            Guid value;uint result=supply==PowerSupply.AC?Native.PowerGetUserConfiguredACPowerMode(out value):Native.PowerGetUserConfiguredDCPowerMode(out value);
            if(result!=0){error="系统暂未提供电源模式："+new Win32Exception((int)result).Message;return null;}
            if(value==Guid.Empty)return PowerModeChoice.Balanced;if(value==Efficiency)return PowerModeChoice.Efficiency;if(value==Performance)return PowerModeChoice.Performance;
            error="当前模式无法由通用接口识别，请使用系统设置。";return null;
        }
        catch(EntryPointNotFoundException){error="此 Windows 版本未提供公开的电源模式接口。";return null;}
    }
    private static List<PowerPlan> Plans()
    {
        var plans=new List<PowerPlan>();
        for(uint index=0;index<256;index++)
        {
            uint size=16;var buffer=new byte[16];uint result=Native.PowerEnumerate(IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,16,index,buffer,ref size);
            if(result==259)return plans;if(result!=0)throw new Win32Exception((int)result);if(size!=16)throw new InvalidOperationException("系统返回了无效电源计划。");
            var id=new Guid(buffer);uint length=0;result=Native.PowerReadFriendlyName(IntPtr.Zero,ref id,IntPtr.Zero,IntPtr.Zero,null,ref length);
            string name=id.ToString();
            if((result==0||result==234)&&length>0&&length<=65536)
            {
                var text=new byte[length];if(Native.PowerReadFriendlyName(IntPtr.Zero,ref id,IntPtr.Zero,IntPtr.Zero,text,ref length)==0)name=Encoding.Unicode.GetString(text).TrimEnd('\0');
            }
            plans.Add(new(id,name));
        }
        throw new InvalidOperationException("电源计划数量超出可安全读取的范围。");
    }
    private static Guid ActivePlan()
    {
        uint result=Native.PowerGetActiveScheme(IntPtr.Zero,out var pointer);
        try{if(result!=0)throw new Win32Exception((int)result);if(pointer==IntPtr.Zero)throw new InvalidOperationException("当前电源计划不可读取。");return Marshal.PtrToStructure<Guid>(pointer);}
        finally{if(pointer!=IntPtr.Zero)Native.LocalFree(pointer);}
    }
    public PowerSnapshot Read()
    {
        if(!_enabled)return PowerSnapshot.Empty();
        PowerSupply supply=PowerSupply.Unknown;bool? hasBattery=null;int? battery=null;bool charging=false;string? statusError=null;
        try{var status=Status();supply=Supply(status);(hasBattery,battery,charging)=Battery(status.Flags,status.Percent);}catch(Exception error){statusError="供电状态不可读："+error.Message;}
        var mode=UserMode(supply,out var modeError);modeError??=_modeAccess.Error(supply);var plans=new List<PowerPlan>();Guid? active=null;string? planError=null;
        try{plans=Plans();active=ActivePlan();}catch(Exception error){planError="电源计划不可用："+error.Message;}
        string? effective=Volatile.Read(ref _effective) switch{0=>"节能",1=>"偏重能效",2=>"平衡",3=>"高性能",4=>"最佳性能",5=>"游戏",6=>"混合现实",_=>null};
        return new(supply,hasBattery,battery,charging,mode,effective,plans,active,modeError,planError,statusError);
    }
    public void SetMode(PowerModeChoice mode,PowerSupply expectedSupply)
    {
        if(!_started||expectedSupply==PowerSupply.Unknown||Supply(Status())!=expectedSupply)throw new InvalidOperationException("供电状态已变化，请刷新后重试。");
        if(_modeAccess.Error(expectedSupply) is {} rejected)throw new InvalidOperationException(rejected);
        if(UserMode(expectedSupply,out var error)==null)throw new InvalidOperationException(error);
        Guid id=mode switch{PowerModeChoice.Efficiency=>Efficiency,PowerModeChoice.Balanced=>Guid.Empty,PowerModeChoice.Performance=>Performance,_=>throw new ArgumentOutOfRangeException(nameof(mode))};
        if(Supply(Status())!=expectedSupply)throw new InvalidOperationException("供电状态已变化，请重新选择。");
        uint result;
        try{result=expectedSupply==PowerSupply.AC?Native.PowerSetUserConfiguredACPowerMode(ref id):Native.PowerSetUserConfiguredDCPowerMode(ref id);}
        catch(EntryPointNotFoundException){_modeAccess.Report(expectedSupply,120);throw new InvalidOperationException(_modeAccess.Error(expectedSupply));}
        _modeAccess.Report(expectedSupply,result);
        if(result!=0)throw new Win32Exception((int)result);
    }
    public void SetPlan(Guid id)
    {
        if(!_started||!Plans().Any(p=>p.Id==id))throw new InvalidOperationException("原电源计划已不存在，请刷新。");
        uint result=Native.PowerSetActiveScheme(IntPtr.Zero,ref id);if(result!=0)throw new Win32Exception((int)result);
    }
    internal PowerRestorePoint Capture()
    {
        Guid? Mode(bool ac)
        {
            try{Guid value;uint result=ac?Native.PowerGetUserConfiguredACPowerMode(out value):Native.PowerGetUserConfiguredDCPowerMode(out value);return result==0&&(value==Guid.Empty||value==Efficiency||value==Performance)?value:null;}
            catch(EntryPointNotFoundException){return null;}
        }
        return new(ActivePlan(),Mode(true),Mode(false));
    }
    internal void Restore(PowerRestorePoint point)
    {
        var errors=new List<string>();
        try{SetPlan(point.Plan);}catch(Exception error){errors.Add("原计划："+error.Message);}
        foreach(bool ac in new[]{true,false})
        {
            var saved=ac?point.AC:point.DC;if(!saved.HasValue)continue;
            try{Guid id=saved.Value;uint result=ac?Native.PowerSetUserConfiguredACPowerMode(ref id):Native.PowerSetUserConfiguredDCPowerMode(ref id);if(result!=0)throw new Win32Exception((int)result);}
            catch(Exception error){errors.Add((ac?"AC":"DC")+" 模式："+error.Message);}
        }
        if(errors.Count>0)throw new InvalidOperationException("电源恢复未完成："+string.Join("；",errors));
    }
    public void Stop()
    {
        _changed=null;Interlocked.Increment(ref _generation);
        if(_started)SystemEvents.PowerModeChanged-=PowerChanged;_started=false;
        if(_registration!=IntPtr.Zero){Native.PowerUnregisterFromEffectivePowerModeNotifications(_registration);_registration=IntPtr.Zero;}
        _callback=null;Volatile.Write(ref _effective,-1);
    }
    public void Dispose()=>Stop();
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]internal struct SystemPowerStatus{internal byte AC,Flags,Percent,Saver;internal uint Life,FullLife;}
        internal delegate void EffectiveCallback(int mode,IntPtr context);
        [DllImport("kernel32.dll",SetLastError=true)]internal static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
        [DllImport("kernel32.dll")]internal static extern IntPtr LocalFree(IntPtr pointer);
        [DllImport("powrprof.dll")]internal static extern uint PowerGetUserConfiguredACPowerMode(out Guid mode);
        [DllImport("powrprof.dll")]internal static extern uint PowerGetUserConfiguredDCPowerMode(out Guid mode);
        [DllImport("powrprof.dll")]internal static extern uint PowerSetUserConfiguredACPowerMode(ref Guid mode);
        [DllImport("powrprof.dll")]internal static extern uint PowerSetUserConfiguredDCPowerMode(ref Guid mode);
        [DllImport("powrprof.dll")]internal static extern uint PowerEnumerate(IntPtr root,IntPtr scheme,IntPtr subgroup,uint access,uint index,[Out]byte[] buffer,ref uint size);
        [DllImport("powrprof.dll")]internal static extern uint PowerReadFriendlyName(IntPtr root,ref Guid scheme,IntPtr subgroup,IntPtr setting,[Out]byte[]? buffer,ref uint size);
        [DllImport("powrprof.dll")]internal static extern uint PowerGetActiveScheme(IntPtr root,out IntPtr scheme);
        [DllImport("powrprof.dll")]internal static extern uint PowerSetActiveScheme(IntPtr root,ref Guid scheme);
        [DllImport("powrprof.dll")]internal static extern int PowerRegisterForEffectivePowerModeNotifications(uint version,EffectiveCallback callback,IntPtr context,out IntPtr handle);
        [DllImport("powrprof.dll")]internal static extern int PowerUnregisterFromEffectivePowerModeNotifications(IntPtr handle);
    }
}
