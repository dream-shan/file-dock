using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace FileDockDemo.Features.ControlCenter;

// All public methods execute on the controller's MTA thread. Native callbacks
// only enqueue invalidation, never dispose/query COM while Windows holds a lock.
internal sealed class CoreAudioBackend : IAudioBackend,IAudioSessionEventsHandler
{
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _notifications;
    private readonly List<MMDevice> _devices=[];
    private readonly Dictionary<string,AudioSessionControl> _sessions=new();
    private MMDevice? _output,_microphone;
    private Action<AudioChange>? _changed;
    private string? _defaultOutput,_defaultInput;
    private string? _sessionError;
    private bool _disposed;
    public void Start(Action<AudioChange> changed)
    {
        if(_disposed)throw new ObjectDisposedException(nameof(CoreAudioBackend));
        Stop();_changed=changed;
        _enumerator=new MMDeviceEnumerator();
        _notifications=_enumerator.CreateNotificationClient(false);
        _notifications.DeviceAdded+=(_,_)=>Notify(AudioChange.Devices);
        _notifications.DeviceRemoved+=(_,_)=>Notify(AudioChange.Devices);
        _notifications.DeviceStateChanged+=(_,_)=>Notify(AudioChange.Devices);
        _notifications.DefaultDeviceChanged+=(_,_)=>Notify(AudioChange.Devices);
        // Volume writes can also raise property notifications: do not rebuild on them.
    }
    private void Notify(AudioChange flags)=>_changed?.Invoke(flags);
    private void VolumeChanged(AudioVolumeNotificationData data)=>Notify(AudioChange.Values);
    private void SessionCreated(object sender,AudioSessionControl session)=>Notify(AudioChange.Sessions);
    private string? DefaultId(DataFlow flow)
    {
        if(_enumerator!.TryGetDefaultAudioEndpoint(flow,Role.Multimedia,out var device))
        {using(device)return device.ID;}return null;
    }
    private void ClearSessions()
    {
        foreach(var session in _sessions.Values){try{session.UnRegisterEventClient(this);}catch{}try{session.Dispose();}catch{}}
        _sessions.Clear();_sessionError=null;
    }
    private void ClearDevices()
    {
        ClearSessions();
        if(_output!=null){try{_output.AudioEndpointVolume.OnVolumeNotification-=VolumeChanged;}catch{}try{_output.AudioSessionManager.OnSessionCreated-=SessionCreated;}catch{}}
        if(_microphone!=null){try{_microphone.AudioEndpointVolume.OnVolumeNotification-=VolumeChanged;}catch{}}
        _output=_microphone=null;
        foreach(var device in _devices){try{device.Dispose();}catch{}}_devices.Clear();
    }
    private void RebuildDevices(string? output,string? microphone)
    {
        ClearDevices();_defaultOutput=DefaultId(DataFlow.Render);_defaultInput=DefaultId(DataFlow.Capture);
        using var collection=_enumerator!.EnumerateAudioEndPoints(DataFlow.All,DeviceState.Active);
        foreach(var device in collection)_devices.Add(device);
        MMDevice? Select(DataFlow flow,string? desired,string? defaultId)=>_devices.FirstOrDefault(d=>d.DataFlow==flow&&d.ID==desired)??_devices.FirstOrDefault(d=>d.DataFlow==flow&&d.ID==defaultId)??_devices.FirstOrDefault(d=>d.DataFlow==flow);
        _output=Select(DataFlow.Render,output,_defaultOutput);_microphone=Select(DataFlow.Capture,microphone,_defaultInput);
        if(_output!=null)
        {
            try{_output.AudioEndpointVolume.OnVolumeNotification+=VolumeChanged;}catch{}
            try{_output.AudioSessionManager.OnSessionCreated+=SessionCreated;}catch(Exception error){_sessionError=error.Message;}
        }
        if(_microphone!=null){try{_microphone.AudioEndpointVolume.OnVolumeNotification+=VolumeChanged;}catch{}}
    }
    private void RebuildSessions()
    {
        ClearSessions();if(_output==null)return;
        try
        {
            var manager=_output.AudioSessionManager;manager.RefreshSessions();var sessions=manager.Sessions;
            for(int index=0;index<sessions.Count;index++)
            {
                var session=sessions[index];bool retained=false;
                try
                {
                    if((int)session.State==2)continue; // AudioSessionStateExpired
                    string id=session.GetSessionInstanceIdentifier;
                    if(_sessions.ContainsKey(id))continue;
                    session.RegisterEventClient(this);_sessions.Add(id,session);retained=true;
                }
                catch{ /* A session may disappear during enumeration. */ }
                finally{if(!retained)session.Dispose();}
            }
        }
        catch(Exception error){_sessionError="应用音量不可用："+error.Message;}
    }
    private static int Percent(float value)=>(int)Math.Round(Math.Clamp(value,0,1)*100);
    private static AudioLevel? Endpoint(MMDevice? device)
    {
        if(device==null)return null;
        var target=new AudioTarget(device.ID);string name=device.FriendlyName;
        try{var volume=device.AudioEndpointVolume;return new(target,name,Percent(volume.MasterVolumeLevelScalar),volume.Mute);}
        catch(Exception error){return new(target,name,0,false,"设备暂不可调节："+error.Message);}
    }
    private static string SessionName(AudioSessionControl session)
    {
        if(session.IsSystemSoundsSession)return "系统声音";
        string name=session.DisplayName;
        if(!string.IsNullOrWhiteSpace(name)&&!name.StartsWith("@",StringComparison.Ordinal))return name;
        try{using var process=Process.GetProcessById((int)session.GetProcessID);return process.ProcessName;}
        catch{return "音频应用";}
    }
    public AudioSnapshot Read(string? output,string? microphone,AudioChange change)
    {
        if(_disposed)throw new ObjectDisposedException(nameof(CoreAudioBackend));
        if(_enumerator==null){var changed=_changed??(_=>{});Start(changed);change=AudioChange.All;}
        if((change&AudioChange.Devices)!=0||_devices.Count==0)RebuildDevices(output,microphone);
        if((change&(AudioChange.Devices|AudioChange.Sessions))!=0)RebuildSessions();
        var devices=new List<AudioDevice>();
        foreach(var device in _devices){try{devices.Add(new(device.ID,device.FriendlyName,device.DataFlow==DataFlow.Capture,device.ID==(device.DataFlow==DataFlow.Capture?_defaultInput:_defaultOutput)));}catch{}}
        var values=new List<AudioLevel>();
        if(_output!=null)foreach(var pair in _sessions)
        {
            try{var volume=pair.Value.SimpleAudioVolume;values.Add(new(new(_output.ID,pair.Key),SessionName(pair.Value),Percent(volume.Volume),volume.Mute));}
            catch{ /* Closed audio sessions are reconciled by the next session event or refresh. */ }
        }
        return new(devices,Endpoint(_output),Endpoint(_microphone),values,_sessionError);
    }
    public void Write(AudioTarget target,int? volume,bool? mute)
    {
        if(_disposed)throw new ObjectDisposedException(nameof(CoreAudioBackend));
        var device=_devices.FirstOrDefault(d=>d.ID==target.DeviceId);
        if(device==null||device.State!=DeviceState.Active)throw new InvalidOperationException("目标设备已断开，请刷新设备列表。");
        if(target.SessionId!=null)
        {
            if(_output?.ID!=target.DeviceId||!_sessions.TryGetValue(target.SessionId,out var session))throw new InvalidOperationException("音频会话已变化，请重新选择。");
            var control=session.SimpleAudioVolume;if(volume.HasValue)control.Volume=Math.Clamp(volume.Value,0,100)/100f;if(mute.HasValue)control.Mute=mute.Value;
        }
        else
        {
            var control=device.AudioEndpointVolume;if(volume.HasValue)control.MasterVolumeLevelScalar=Math.Clamp(volume.Value,0,100)/100f;if(mute.HasValue)control.Mute=mute.Value;
        }
    }
    public void Stop()
    {
        _changed=null;try{_notifications?.Dispose();}finally{_notifications=null;}
        ClearDevices();_enumerator?.Dispose();_enumerator=null;
    }
    public void Dispose(){if(_disposed)return;Stop();_disposed=true;}
    public void OnVolumeChanged(float volume,bool isMuted)=>Notify(AudioChange.Values);
    public void OnDisplayNameChanged(string displayName)=>Notify(AudioChange.Values);
    public void OnIconPathChanged(string iconPath){}
    public void OnChannelVolumeChanged(uint channelCount,IntPtr newVolumes,uint channelIndex)=>Notify(AudioChange.Values);
    public void OnGroupingParamChanged(ref Guid groupingId){}
    public void OnStateChanged(AudioSessionState state)=>Notify(AudioChange.Sessions);
    public void OnSessionDisconnected(AudioSessionDisconnectReason reason)=>Notify(AudioChange.Devices);
}
