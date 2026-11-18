using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Windows.Networking.Connectivity;

namespace FileDockDemo.Features.ControlCenter;

internal sealed record WifiAdapter(Guid Id,string Name,bool? Enabled,bool Blocked,uint Link,string? NetworkName,string? Connectivity)
{
    internal bool Connected=>Link==1;
    internal string Status=>Enabled switch
    {
        false=>Blocked?"硬件已禁用":"已关闭", null=>"开关状态未知",
        _=>Link switch{1=>NetworkName??"已连接", 3=>"正在断开", 5 or 6 or 7=>"正在连接", _=>"未连接"}
    };
}

internal sealed class NativeWifi:IDisposable
{
    private readonly Native.ClientHandle _handle;
    private readonly Native.Callback _callback;
    private const uint RadioOpcode=4,CurrentConnectionOpcode=7,AccessDenied=5,ServiceStopped=1062;
    private const uint ScanComplete=7,ScanFail=8,ConnectionComplete=10;
    private sealed record Notice(Guid Interface,uint Code,uint Mode=0,string? Profile=null,string? Ssid=null,uint Reason=0);
    private event Action<Notice>? Notification;
    internal event Action? Changed;

    internal NativeWifi(bool notifications=true)
    {
        Check(Native.WlanOpenHandle(2,IntPtr.Zero,out _,out _handle));
        _callback=OnNotification;
        try{if(notifications)Check(Native.WlanRegisterNotification(_handle,8,false,_callback,IntPtr.Zero,IntPtr.Zero,out _));}
        catch{_handle.Dispose();throw;}
    }

    private void OnNotification(ref Native.Notification data,IntPtr context)
    {
        if(data.Source!=8)return;
        try
        {
            if(data.Code is ScanComplete or ScanFail or ConnectionComplete)
            {
                if(data.Code==ConnectionComplete&&data.Data!=IntPtr.Zero&&data.Size>=Marshal.SizeOf<Native.ConnectionNotice>())
                {
                    var value=Marshal.PtrToStructure<Native.ConnectionNotice>(data.Data);
                    Notification?.Invoke(new(data.Interface,data.Code,value.Mode,value.Profile,SsidHex(value.Ssid),value.Reason));
                }
                else Notification?.Invoke(new(data.Interface,data.Code));
            }
            if(data.Code is 6 or 9 or 10 or 11 or 13 or 14 or 15 or 16 or 20 or 21 or 27)Changed?.Invoke();
        }
        catch(Exception){/* Do not allow managed exceptions to cross the native callback. */}
    }

    internal static IReadOnlyList<WifiAdapter> ReadAdapters()
    {
        using var wifi=new NativeWifi(false);return wifi.Adapters();
    }

    internal IReadOnlyList<WifiAdapter> Adapters()
    {
        IntPtr list=IntPtr.Zero;
        try
        {
            Check(Native.WlanEnumInterfaces(_handle,IntPtr.Zero,out list));
            int count=Count(list,256);var adapters=new List<WifiAdapter>(count);
            int size=Marshal.SizeOf<Native.InterfaceInfo>();
            for(int i=0;i<count;i++)
            {
                var item=Marshal.PtrToStructure<Native.InterfaceInfo>(IntPtr.Add(list,8+i*size));
                var radios=Radios(item.Id);
                bool blocked=radios.Length>0&&radios.All(r=>r.Hardware==2);
                bool? enabled=blocked?false:radios.Any(r=>r.Hardware==1&&r.Software==1)?true:
                    radios.Length>0&&radios.All(r=>r.Hardware==2||r.Hardware==1&&r.Software==2)?false:null;
                (string? name,string? connectivity)=item.State==1?ReadConnectivity(item.Id):(null,null);
                adapters.Add(new(item.Id,item.Name,enabled,blocked,item.State,name,connectivity));
            }
            return adapters;
        }
        finally{if(list!=IntPtr.Zero)Native.WlanFreeMemory(list);}
    }

    private static (string?,string?) ReadConnectivity(Guid id)
    {
        try
        {
            var profile=NetworkInformation.GetConnectionProfiles().FirstOrDefault(p=>p.IsWlanConnectionProfile&&p.NetworkAdapter?.NetworkAdapterId==id);
            if(profile==null)return(null,null);
            string connectivity=profile.GetNetworkConnectivityLevel() switch
            {
                NetworkConnectivityLevel.InternetAccess=>"可访问互联网",NetworkConnectivityLevel.ConstrainedInternetAccess=>"互联网访问受限",
                NetworkConnectivityLevel.LocalAccess=>"仅本地连接",_=>"未检测到互联网"
            };
            return(profile.WlanConnectionProfileDetails?.GetConnectedSsid(),connectivity);
        }
        catch(Exception){return(null,null);}
    }

    private Native.RadioState[] Radios(Guid id)
    {
        IntPtr data=IntPtr.Zero;
        try
        {
            if(Native.WlanQueryInterface(_handle,ref id,RadioOpcode,IntPtr.Zero,out uint size,out data,IntPtr.Zero)!=0||data==IntPtr.Zero||size<4)return [];
            int count=Marshal.ReadInt32(data);int length=Marshal.SizeOf<Native.RadioState>();
            if(count<0||count>64||size<4+count*length)return [];
            return Enumerable.Range(0,count).Select(i=>Marshal.PtrToStructure<Native.RadioState>(IntPtr.Add(data,4+i*length))).ToArray();
        }
        finally{if(data!=IntPtr.Zero)Native.WlanFreeMemory(data);}
    }

    internal async Task SetRadioAsync(Guid id,bool on,CancellationToken token)
    {
        var states=Radios(id);
        if(states.Length==0||states.All(r=>r.Hardware!=1))throw new WirelessException("无线网卡不可由软件切换，请检查硬件开关或系统设置。");
        foreach(var state in states)
        {
            token.ThrowIfCancellationRequested();
            if(state.Hardware!=1||state.Software==(on?1u:2u))continue;
            var target=new Native.RadioState{Index=state.Index,Software=on?1u:2u};
            Check(Native.WlanSetInterface(_handle,ref id,RadioOpcode,(uint)Marshal.SizeOf<Native.RadioState>(),ref target,IntPtr.Zero));
        }
        for(int attempt=0;attempt<12;attempt++)
        {
            token.ThrowIfCancellationRequested();
            if(Adapters().FirstOrDefault(a=>a.Id==id)?.Enabled==on)return;
            await Task.Delay(125,token).ConfigureAwait(false);
        }
        throw new WirelessException("Windows 尚未确认 Wi-Fi 开关状态，请刷新。");
    }

    internal IReadOnlyList<WifiNetwork> Networks(Guid id)
    {
        IntPtr list=IntPtr.Zero;
        try
        {
            Check(Native.WlanGetAvailableNetworkList(_handle,ref id,0,IntPtr.Zero,out list),true);
            int size=Marshal.SizeOf<Native.AvailableNetwork>();int count=Count(list,4096);
            var result=new List<WifiNetwork>(count);
            for(int i=0;i<count;i++)
            {
                var item=Marshal.PtrToStructure<Native.AvailableNetwork>(IntPtr.Add(list,8+i*size));
                string hex=SsidHex(item.Ssid);
                if(hex.Length==0||item.Bss!=1)continue;
                WifiSecurity security=(item.Secured,item.Authentication,item.Cipher) switch
                {
                    (0,1,0)=>WifiSecurity.Open, (1,7,4)=>WifiSecurity.Wpa2Personal, (1,9,4)=>WifiSecurity.Wpa3Personal, _=>WifiSecurity.Other
                };
                string? profile=(item.Flags&2)!=0&&!string.IsNullOrEmpty(item.Profile)?item.Profile:null;
                result.Add(new(hex,WifiProfile.SsidName(Convert.FromHexString(hex)),profile,security,Math.Min(100,item.Signal),(item.Flags&1)!=0,item.Connectable!=0,item.Authentication,item.Cipher));
            }
            return result.OrderByDescending(n=>n.Connected).ThenByDescending(n=>n.Signal).ThenBy(n=>n.Name,StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        finally{if(list!=IntPtr.Zero)Native.WlanFreeMemory(list);}
    }

    internal async Task<IReadOnlyList<WifiNetwork>> ScanAsync(Guid id,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var signal=new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(Notice n){if(n.Interface==id&&n.Code is ScanComplete or ScanFail)signal.TrySetResult(n.Code);}
        Notification+=Handler;
        try
        {
            Check(Native.WlanScan(_handle,ref id,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero),true);
            try{if(await signal.Task.WaitAsync(TimeSpan.FromSeconds(5),token).ConfigureAwait(false)==ScanFail)throw new WirelessException("Wi-Fi 扫描失败，请重试。");}
            catch(TimeoutException){/* Windows documents a scan timeout before reading the available list. */}
            token.ThrowIfCancellationRequested();return Networks(id);
        }
        finally{Notification-=Handler;}
    }

    internal async Task<string> ConnectAsync(Guid id,WifiNetwork target,string password,bool remember,bool automatic,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var actual=Networks(id).FirstOrDefault(target.SameTarget);
        if(actual==null||!actual.CanJoin)throw new WirelessException("网络已变化或无法连接，请刷新后重新选择。");
        if(actual.Connected)return "已连接";
        bool isNew=actual.Profile==null;
        string name=actual.Profile??$"{actual.Name} ({Guid.NewGuid():N})";
        string? xml=isNew?WifiProfile.Create(actual,name,password,false):null;
        uint mode=isNew?1u:0u;
        var signal=new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(Notice n)
        {
            if(n.Interface==id&&n.Code==ConnectionComplete&&n.Mode==mode&&n.Ssid==actual.HexSsid&&n.Profile==name)signal.TrySetResult(n.Reason);
        }
        Notification+=Handler;
        try
        {
            token.ThrowIfCancellationRequested();
            var bytes=Convert.FromHexString(actual.HexSsid);
            var buffer=new byte[32];bytes.CopyTo(buffer,0);
            var targetSsid=new Native.Ssid{Length=(uint)bytes.Length,Bytes=buffer};
            IntPtr pinnedSsid=Marshal.AllocHGlobal(Marshal.SizeOf<Native.Ssid>());
            try
            {
                Marshal.StructureToPtr(targetSsid,pinnedSsid,false);
                var request=new Native.ConnectParameters{Mode=mode,Profile=xml??name,Ssid=pinnedSsid,Bss=1};
                token.ThrowIfCancellationRequested();Check(Native.WlanConnect(_handle,ref id,ref request,IntPtr.Zero));
            }
            finally{Marshal.FreeHGlobal(pinnedSsid);}
            uint? reason;
            try{reason=await signal.Task.WaitAsync(TimeSpan.FromSeconds(22),token).ConfigureAwait(false);}
            catch(TimeoutException){reason=null;}
            if(reason is >0)throw new WirelessException($"Windows 未能连接该网络（原因码 {reason}）。请检查密码或信号。");
            bool verified=false;
            for(int attempt=0;attempt<5;attempt++)
            {
                token.ThrowIfCancellationRequested();
                if(MatchesCurrent(id,actual,name,mode)){verified=true;break;}
                await Task.Delay(125,token).ConfigureAwait(false);
            }
            if(!verified)throw new WirelessException("Windows 尚未确认已连接到所选网络，请刷新查看实际状态。");
            if(!isNew||!remember)return "已连接";
            string savedXml=WifiProfile.Create(actual,name,password,automatic);
            token.ThrowIfCancellationRequested();
            if(!MatchesCurrent(id,actual,name,mode))return "连接状态已变化，Windows 未保存该网络。";
            token.ThrowIfCancellationRequested();
            // Save the specific profile the user selected. Saving "whichever temporary
            // profile is current" could copy another app's profile after a concurrent switch.
            uint result=Native.WlanSetProfile(_handle,ref id,2,savedXml,null,false,IntPtr.Zero,out uint saveReason);
            return result==0?"已连接并由 Windows 保存":$"已连接，但 Windows 未保存该网络（{result}/{saveReason}）。";
        }
        finally{Notification-=Handler;}
    }

    private bool MatchesCurrent(Guid id,WifiNetwork network,string name,uint mode)
    {
        IntPtr data=IntPtr.Zero;
        try
        {
            uint result=Native.WlanQueryInterface(_handle,ref id,CurrentConnectionOpcode,IntPtr.Zero,out uint size,out data,IntPtr.Zero);
            if(result is 5023 or 1168)return false; // Disconnected or adapter removed.
            Check(result,true);
            if(data==IntPtr.Zero||size<Marshal.SizeOf<Native.CurrentConnection>())return false;
            var current=Marshal.PtrToStructure<Native.CurrentConnection>(data);
            bool correctLink=current.State==1&&current.Profile==name&&SsidHex(current.Association.Ssid)==network.HexSsid;
            if(!correctLink)return false;
            // Saved profiles may negotiate a different valid suite in transition mode.
            // A newly generated profile, by contrast, specifies exactly one suite.
            return mode==0 || (current.Mode==mode && current.Security.Authentication==network.Authentication && current.Security.Cipher==network.Cipher);
        }
        finally{if(data!=IntPtr.Zero)Native.WlanFreeMemory(data);}
    }

    internal async Task DisconnectAsync(WifiAdapter target,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Guid id=target.Id;
        var current=Adapters().FirstOrDefault(a=>a.Id==id);
        if(current?.Connected!=true||(target.NetworkName!=null&&target.NetworkName!=current.NetworkName))
            throw new WirelessException("当前网卡的连接已变化，请刷新。");
        token.ThrowIfCancellationRequested();Check(Native.WlanDisconnect(_handle,ref id,IntPtr.Zero));
        for(int attempt=0;attempt<20;attempt++)
        {
            token.ThrowIfCancellationRequested();
            if(Adapters().FirstOrDefault(a=>a.Id==id) is { Link: 4 })return;
            await Task.Delay(150,token).ConfigureAwait(false);
        }
        throw new WirelessException("Windows 尚未确认断开，请刷新查看连接。");
    }

    private static int Count(IntPtr list,int limit)
    {
        if(list==IntPtr.Zero)throw new WirelessException("Windows 返回空的 Wi-Fi 列表。");
        int count=Marshal.ReadInt32(list);
        return count>=0&&count<=limit?count:throw new WirelessException("Windows 返回无效的 Wi-Fi 列表。");
    }
    private static string SsidHex(Native.Ssid ssid)=>ssid.Length is >0 and <=32?Convert.ToHexString(ssid.Bytes.AsSpan(0,(int)ssid.Length)):"";
    private static void Check(uint result,bool privateDetail=false)
    {
        if(result==0)return;
        throw new WirelessException(result switch
        {
            AccessDenied when privateDetail=>"Windows 拒绝读取 Wi-Fi 详情。请检查定位访问或在系统设置中管理 Wi-Fi。",
            AccessDenied=>"Windows 拒绝这项 Wi-Fi 操作，请使用系统设置。",
            ServiceStopped=>"Windows 无线服务未运行，请检查系统网络设置。",
            _=>$"Windows Wi-Fi 操作失败（错误 {result}）。"
        });
    }

    public void Dispose(){_handle.Dispose();GC.KeepAlive(_callback);}

    private static class Native
    {
        internal sealed class ClientHandle:SafeHandleZeroOrMinusOneIsInvalid
        {
            public ClientHandle():base(true){}
            protected override bool ReleaseHandle()=>WlanCloseHandle(handle,IntPtr.Zero)==0;
        }
        [StructLayout(LayoutKind.Sequential)]internal struct Ssid{internal uint Length;[MarshalAs(UnmanagedType.ByValArray,SizeConst=32)]internal byte[] Bytes;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]internal struct InterfaceInfo{internal Guid Id;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]internal string Name;internal uint State;}
        [StructLayout(LayoutKind.Sequential)]internal struct RadioState{internal uint Index,Software,Hardware;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]internal struct AvailableNetwork
        {
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]internal string Profile;
            internal Ssid Ssid;internal uint Bss,Count,Connectable,Reason,PhyCount;
            [MarshalAs(UnmanagedType.ByValArray,SizeConst=8)]internal uint[] Phys;
            internal uint MorePhys,Signal,Secured,Authentication,Cipher,Flags,Reserved;
        }
        [StructLayout(LayoutKind.Sequential)]internal struct Association
        {
            internal Ssid Ssid;internal uint Bss;
            [MarshalAs(UnmanagedType.ByValArray,SizeConst=6)]internal byte[] Bssid;
            internal uint PhyType,PhyIndex,Signal,Receive,Transmit;
        }
        [StructLayout(LayoutKind.Sequential)]internal struct Security{internal uint Secured,OneX,Authentication,Cipher;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]internal struct CurrentConnection
        {
            internal uint State,Mode;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]internal string Profile;
            internal Association Association;internal Security Security;
        }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]internal struct ConnectionNotice{internal uint Mode;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]internal string Profile;internal Ssid Ssid;internal uint Bss,Secured,Reason,Flags;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]internal struct ConnectParameters{internal uint Mode;[MarshalAs(UnmanagedType.LPWStr)]internal string Profile;internal IntPtr Ssid,Bssid;internal uint Bss,Flags;}
        [StructLayout(LayoutKind.Sequential)]internal struct Notification{internal uint Source,Code;internal Guid Interface;internal uint Size;internal IntPtr Data;}
        internal delegate void Callback(ref Notification data,IntPtr context);
        [DllImport("wlanapi.dll")]internal static extern uint WlanOpenHandle(uint version,IntPtr reserved,out uint negotiated,out ClientHandle handle);
        [DllImport("wlanapi.dll")]internal static extern uint WlanCloseHandle(IntPtr handle,IntPtr reserved);
        [DllImport("wlanapi.dll")]internal static extern void WlanFreeMemory(IntPtr memory);
        [DllImport("wlanapi.dll")]internal static extern uint WlanRegisterNotification(ClientHandle handle,uint source,[MarshalAs(UnmanagedType.Bool)]bool ignoreDuplicates,Callback callback,IntPtr context,IntPtr reserved,out uint previous);
        [DllImport("wlanapi.dll")]internal static extern uint WlanEnumInterfaces(ClientHandle handle,IntPtr reserved,out IntPtr list);
        [DllImport("wlanapi.dll")]internal static extern uint WlanQueryInterface(ClientHandle handle,ref Guid id,uint opcode,IntPtr reserved,out uint size,out IntPtr data,IntPtr valueType);
        [DllImport("wlanapi.dll")]internal static extern uint WlanSetInterface(ClientHandle handle,ref Guid id,uint opcode,uint size,ref RadioState data,IntPtr reserved);
        [DllImport("wlanapi.dll")]internal static extern uint WlanGetAvailableNetworkList(ClientHandle handle,ref Guid id,uint flags,IntPtr reserved,out IntPtr list);
        [DllImport("wlanapi.dll")]internal static extern uint WlanScan(ClientHandle handle,ref Guid id,IntPtr ssid,IntPtr data,IntPtr reserved);
        [DllImport("wlanapi.dll")]internal static extern uint WlanConnect(ClientHandle handle,ref Guid id,ref ConnectParameters parameters,IntPtr reserved);
        [DllImport("wlanapi.dll")]internal static extern uint WlanDisconnect(ClientHandle handle,ref Guid id,IntPtr reserved);
        [DllImport("wlanapi.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]internal static extern uint WlanSetProfile(ClientHandle handle,ref Guid id,uint flags,string xml,string? access,[MarshalAs(UnmanagedType.Bool)]bool overwrite,IntPtr reserved,out uint reason);
    }
}
