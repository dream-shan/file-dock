using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace FileDockDemo.Features.ControlCenter;

internal interface IControlBackend<T>:IDisposable
{
    void Start(Action changed);
    T Read();
    void Stop();
}

// A fixed, lazy worker per subsystem. A stuck display driver cannot consume
// additional threads or block the audio worker or the dispatcher.
internal sealed class ControlWorkQueue:IDisposable
{
    private readonly BlockingCollection<Action> _commands=new();
    private readonly object _gate=new();
    private readonly Action _cleanup;
    private readonly string _name;
    private Thread? _thread;
    private bool _disposed;
    internal ControlWorkQueue(string name,Action cleanup){_name=name;_cleanup=cleanup;}
    internal bool Post(Action action)
    {
        lock(_gate)
        {
            if(_disposed)return false;
            if(_thread==null)
            {
                _thread=new Thread(()=>{
                    try{foreach(var command in _commands.GetConsumingEnumerable())try{command();}catch(Exception error){DiagnosticLog.Error("control-command",error);}}
                    finally{try{_cleanup();}catch(Exception error){DiagnosticLog.Error("control-dispose",error);}_commands.Dispose();}
                }){Name=_name,IsBackground=true};
                _thread.SetApartmentState(ApartmentState.MTA);_thread.Start();
            }
            _commands.Add(action);return true;
        }
    }
    internal Task DrainAsync()
    {
        lock(_gate){if(_thread==null||_disposed)return Task.CompletedTask;}
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!Post(()=>done.TrySetResult()))done.TrySetResult();return done.Task;
    }
    public void Dispose()
    {
        lock(_gate){if(_disposed)return;_disposed=true;if(_thread==null){_commands.Dispose();return;}_commands.CompleteAdding();}
        if(_thread!=Thread.CurrentThread)_thread.Join(300);
    }
}

internal sealed class ControlChannel<TBackend,TSnapshot>:IDisposable where TBackend:IControlBackend<TSnapshot>
{
    private readonly Func<TBackend> _factory;
    private readonly Dispatcher _dispatcher;
    private readonly Func<string?,TSnapshot> _empty;
    private readonly Func<TSnapshot,string,TSnapshot> _withError;
    private readonly ControlWorkQueue _work;
    private readonly object _gate=new();
    private TBackend? _backend;
    private bool _active,_disposed;
    private int _generation,_pending;
    internal TSnapshot Current{get;private set;}
    internal event Action<TSnapshot>? Changed;
    internal bool Active{get{lock(_gate)return _active;}}
    internal ControlChannel(string name,Func<TBackend> factory,Dispatcher dispatcher,Func<string?,TSnapshot> empty,Func<TSnapshot,string,TSnapshot> withError)
    {
        _factory=factory;_dispatcher=dispatcher;_empty=empty;_withError=withError;Current=empty(null);
        _work=new(name,()=>_backend?.Dispose());
    }
    private bool Valid(int generation){lock(_gate)return !_disposed&&_active&&_generation==generation;}
    internal void SetActive(bool active)
    {
        int generation;lock(_gate){if(_disposed||_active==active)return;_active=active;generation=++_generation;}
        if(!active){_work.Post(()=>_backend?.Stop());return;}
        Current=_empty("正在读取…");Changed?.Invoke(Current);
        _work.Post(()=>Read(generation,true));
    }
    internal void Refresh(){int generation;lock(_gate)generation=_generation;_work.Post(()=>Read(generation,true));}
    private void Notify(int generation)
    {
        lock(_gate){if(!Valid(generation)||_pending==generation)return;_pending=generation;}
        _work.Post(()=>{lock(_gate){if(_pending==generation)_pending=0;}Read(generation,false);});
    }
    private void Read(int generation,bool restart)
    {
        if(!Valid(generation))return;
        try
        {
            _backend??=_factory();if(restart)_backend.Start(()=>Notify(generation));
            if(!Valid(generation))return;
            Publish(_backend.Read(),generation);
        }
        catch(Exception error){Publish(_empty("读取失败："+error.Message),generation);}
    }
    internal void Command(Action<TBackend> action)
    {
        int generation;lock(_gate)generation=_generation;
        _work.Post(()=>{
            if(!Valid(generation)||_backend==null)return;
            string? failure=null;try{action(_backend);}catch(Exception error){failure="操作失败："+error.Message;}
            if(!Valid(generation))return;
            try{var snapshot=_backend.Read();Publish(failure==null?snapshot:_withError(snapshot,failure),generation);}
            catch(Exception error){Publish(_empty(failure??"读取失败："+error.Message),generation);}
        });
    }
    private void Publish(TSnapshot snapshot,int generation)
    {
        if(_dispatcher.HasShutdownStarted)return;
        _dispatcher.BeginInvoke(()=>{if(!Valid(generation))return;Current=snapshot;Changed?.Invoke(snapshot);});
    }
    internal Task DrainAsync()=>_work.DrainAsync();
    public void Dispose(){lock(_gate){if(_disposed)return;_disposed=true;_active=false;_generation++;Changed=null;}_work.Dispose();}
}
