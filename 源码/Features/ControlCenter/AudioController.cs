using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace FileDockDemo.Features.ControlCenter;

[Flags]internal enum AudioChange { Values=1,Sessions=2,Devices=4,All=7 }
internal sealed record AudioDevice(string Id,string Name,bool Input,bool Default)
{
    public string Label=>Name+(Default?"（系统默认）":"");
}
internal sealed record AudioTarget(string DeviceId,string? SessionId=null);
internal sealed record AudioLevel(AudioTarget Target,string Name,int Volume,bool Muted,string? Error=null);
internal sealed record AudioSnapshot(IReadOnlyList<AudioDevice> Devices,AudioLevel? Output,AudioLevel? Microphone,IReadOnlyList<AudioLevel> Sessions,string? Error=null)
{
    internal static AudioSnapshot Empty(string? error=null)=>new([],null,null,[],error);
}
internal interface IAudioBackend : IDisposable
{
    void Start(Action<AudioChange> changed);
    AudioSnapshot Read(string? output,string? microphone,AudioChange change);
    void Write(AudioTarget target,int? volume,bool? mute);
    void Stop();
}

// One lazy, idle-blocking MTA thread owns all audio COM objects. No audio sampling.
internal sealed class AudioController : IDisposable
{
    private readonly Func<IAudioBackend> _factory;
    private readonly Dispatcher _dispatcher;
    private readonly BlockingCollection<Action> _queue=new();
    private readonly object _gate=new();
    private Thread? _thread;
    private IAudioBackend? _backend;
    private bool _active,_disposed;
    private int _generation,_pendingFlags,_pendingGeneration;
    private string? _output,_microphone;
    internal event Action<AudioSnapshot>? Changed;
    internal AudioSnapshot Current {get;private set;}=AudioSnapshot.Empty();
    internal bool Active {get{lock(_gate)return _active;}}
    internal AudioController(Func<IAudioBackend> factory,Dispatcher dispatcher){_factory=factory;_dispatcher=dispatcher;}
    private void EnsureThread()
    {
        if(_thread!=null)return;
        _thread=new Thread(()=>{
            try{foreach(var action in _queue.GetConsumingEnumerable()){try{action();}catch{/* Each command reports its own generation-scoped error. */}}}
            finally{try{_backend?.Dispose();}catch{} _backend=null;}
        }){IsBackground=true,Name="FileDock audio control"};
        _thread.SetApartmentState(ApartmentState.MTA);_thread.Start();
    }
    private int Generation{get{lock(_gate)return _generation;}}
    private bool IsCurrent(int generation){lock(_gate)return !_disposed&&_active&&generation==_generation;}
    private void Enqueue(Action action){lock(_gate){if(_disposed)return;EnsureThread();_queue.Add(action);}}
    internal void SetActive(bool active)
    {
        int generation;
        lock(_gate){if(_disposed||_active==active)return;_active=active;generation=++_generation;}
        if(!active){Enqueue(()=>{try{_backend?.Stop();}catch{} });return;}
        Current=AudioSnapshot.Empty("正在读取音频设备…");Changed?.Invoke(Current);
        Enqueue(()=>{
            if(!IsCurrent(generation))return;
            try{_backend??=_factory();_backend.Start(change=>Invalidate(change,generation));Publish(_backend.Read(_output,_microphone,AudioChange.All),generation);}
            catch(Exception error){try{_backend?.Stop();}catch{}Publish(AudioSnapshot.Empty("音频设备不可用："+error.Message),generation);}
        });
    }
    private void Invalidate(AudioChange change,int generation)
    {
        if(!IsCurrent(generation))return;
        lock(_gate)
        {
            if(_disposed)return;
            if(_pendingGeneration!=generation){_pendingGeneration=generation;_pendingFlags=0;}
            bool schedule=_pendingFlags==0;_pendingFlags|=(int)change;
            if(schedule)_queue.Add(()=>{
                AudioChange flags;lock(_gate){if(_pendingGeneration!=generation)return;flags=(AudioChange)_pendingFlags;_pendingFlags=0;}
                if(!IsCurrent(generation)||_backend==null)return;
                try{Publish(_backend.Read(_output,_microphone,flags),generation);}catch(Exception error){Publish(AudioSnapshot.Empty("刷新失败："+error.Message),generation);}
            });
        }
    }
    internal void Refresh()
    {
        int generation=Generation;Enqueue(()=>{
            if(!IsCurrent(generation))return;
            try{_backend??=_factory();_backend.Start(change=>Invalidate(change,generation));Publish(_backend.Read(_output,_microphone,AudioChange.All),generation);}
            catch(Exception error){Publish(AudioSnapshot.Empty("刷新失败："+error.Message),generation);}
        });
    }
    internal void SelectDevice(bool input,string? id)
    {
        int generation=Generation;Enqueue(()=>{
            if(!IsCurrent(generation)||_backend==null)return;
            if(input)_microphone=id;else _output=id;
            try{Publish(_backend.Read(_output,_microphone,AudioChange.All),generation);}catch(Exception error){Publish(AudioSnapshot.Empty("设备选择失败："+error.Message),generation);}
        });
    }
    internal void SetLevel(AudioTarget target,int? volume=null,bool? mute=null)
    {
        int generation=Generation;Enqueue(()=>{
            if(!IsCurrent(generation)||_backend==null)return;
            string? error=null;
            try{_backend.Write(target,volume.HasValue?Math.Clamp(volume.Value,0,100):null,mute);}
            catch(Exception exception){error="调节失败："+exception.Message;}
            try{var snapshot=_backend.Read(_output,_microphone,error==null?AudioChange.Values:AudioChange.All);Publish(error==null?snapshot:snapshot with{Error=error},generation);}
            catch(Exception exception){Publish(AudioSnapshot.Empty(error??"读取失败："+exception.Message),generation);}
        });
    }
    private void Publish(AudioSnapshot snapshot,int generation)
    {
        if(_dispatcher.HasShutdownStarted)return;
        _dispatcher.BeginInvoke(()=>{if(!IsCurrent(generation))return;Current=snapshot;Changed?.Invoke(snapshot);});
    }
    internal Task DrainAsync()
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(_gate){if(_disposed||_thread==null)return Task.CompletedTask;_queue.Add(()=>done.TrySetResult());}return done.Task;
    }
    public void Dispose()
    {
        lock(_gate){if(_disposed)return;_disposed=true;_active=false;_generation++;_queue.CompleteAdding();Changed=null;}
        if(_thread!=null&&_thread!=Thread.CurrentThread)_thread.Join(1500);
    }
}
