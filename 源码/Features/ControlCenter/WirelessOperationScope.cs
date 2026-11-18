using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileDockDemo.Features.ControlCenter;

// Owned by the UI thread. Hide immediately cancels the lifetime, while an
// accepted OS request can finish on its own. Reopening never queues old work.
internal sealed class WirelessOperationScope:IDisposable
{
    private CancellationTokenSource? _lifetime;
    private bool _disposed;
    internal bool Active=>_lifetime!=null;
    internal bool Busy{get;private set;}
    internal event Action? Changed;
    internal string? Message{get;private set;}

    internal void SetActive(bool active)
    {
        if(_disposed||active==Active)return;
        if(active)_lifetime=new CancellationTokenSource();
        else{var previous=_lifetime;_lifetime=null;previous?.Cancel();previous?.Dispose();}
        Message=null;
        Changed?.Invoke();
    }

    internal async Task RunAsync(Func<CancellationToken,Task<string?>> operation,CancellationToken detail=default,bool preserveMessage=false)
    {
        if(_lifetime==null||Busy)return;
        using var request=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token,detail);
        var token=request.Token;
        if(token.IsCancellationRequested)return;
        Busy=true;if(!preserveMessage)Message=null;Changed?.Invoke();
        try
        {
            var result=await operation(token);
            if(!token.IsCancellationRequested&&(!preserveMessage||result!=null))Message=result;
        }
        catch(Exception error)
        {
            if(!token.IsCancellationRequested)Message=error is WirelessException?error.Message:"Windows 无线操作暂不可用，请刷新或打开系统设置。";
        }
        finally{Busy=false;Changed?.Invoke();}
    }

    internal void ShowError(string text){if(_lifetime!=null){Message=text;Changed?.Invoke();}}

    public void Dispose(){SetActive(false);_disposed=true;Changed=null;}
}
