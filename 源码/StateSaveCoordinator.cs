using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileDockDemo;

internal enum SaveExitChoice { Retry, Discard, Cancel }

// Calls and continuations belong to the UI context. Only a detached snapshot
// crosses into Task.Run; every write, including explicit flushes, shares one gate.
internal sealed class StateSaveCoordinator : IDisposable
{
    private readonly AppState _state;
    private readonly Action<AppState> _write;
    private readonly Action<Exception?> _report;
    private readonly SemaphoreSlim _gate = new(1);
    private CancellationTokenSource? _debounce;
    private long _requested, _saved;
    private bool _disposed;
    internal Task Pending { get; private set; } = Task.CompletedTask;
    internal bool HasPendingSettings => _requested > _saved;

    internal StateSaveCoordinator(AppState state, Action<AppState> write, Action<Exception?> report)
    { _state = state; _write = write; _report = report; }

    internal void RequestSettingsSave()
    {
        if (_disposed) return;
        ++_requested;
        CancelDebounce();
        var cancellation = _debounce = new CancellationTokenSource();
        Pending = SaveAfterDelayAsync(cancellation);
    }

    private async Task SaveAfterDelayAsync(CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            await Task.Delay(400, token);
            await _gate.WaitAsync(token);
            try { if (!_disposed && HasPendingSettings) await WriteAsync(_state.Copy(), _requested); }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!_disposed) _report(error); }
        finally
        {
            if (ReferenceEquals(_debounce, cancellation)) _debounce = null;
            cancellation.Dispose();
        }
    }

    internal async Task<bool> CommitAsync(Action<AppState> change, Action<AppState> apply)
    {
        if (_disposed) return false;
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return false;
            var candidate = _state.Copy();
            change(candidate);
            if (!await WriteAsync(candidate, _requested)) return false;
            // Merge only this operation's business fields; newer UI settings
            // must survive a write that started before those settings changed.
            if (_disposed) return false;
            apply(candidate);
            return true;
        }
        catch (Exception error) { if (!_disposed) _report(error); return false; }
        finally { _gate.Release(); }
    }

    internal async Task<bool> FlushAsync()
    {
        if (_disposed) return false;
        CancelDebounce();
        await _gate.WaitAsync();
        try { return !_disposed && await WriteAsync(_state.Copy(), _requested); }
        catch (Exception error) { if (!_disposed) _report(error); return false; }
        finally { _gate.Release(); }
    }

    internal async Task<bool> FlushForExitAsync(Func<SaveExitChoice> choose)
    {
        while(!await FlushAsync())
        {
            if(_disposed)return false;
            switch(choose()){case SaveExitChoice.Discard:return true;case SaveExitChoice.Cancel:return false;}
        }
        return true;
    }

    private async Task<bool> WriteAsync(AppState snapshot, long version)
    {
        try
        {
            await Task.Run(() => _write(snapshot));
            _saved = Math.Max(_saved, version);
            if (!_disposed && !HasPendingSettings) _report(null);
            return true;
        }
        catch (Exception error) { if (!_disposed) _report(error); return false; }
    }

    private void CancelDebounce()
    {
        var previous = _debounce; _debounce = null;
        previous?.Cancel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelDebounce();
    }
}
