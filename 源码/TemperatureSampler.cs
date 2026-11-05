using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileDockDemo;

internal sealed record TemperatureReading(double? Value, string Status);

// The existing performance timer requests work; this cache owns no timer.
// One request may run independently of all other performance metrics.
internal sealed class TemperatureSampler : IDisposable
{
    private static readonly int[] RetryMilliseconds = { 2000, 5000, 15000, 30000 };
    private readonly object _gate = new();
    private readonly Func<CancellationToken, Task<(double? Temperature, long Samples)>> _read;
    private readonly Func<long> _milliseconds;
    private CancellationTokenSource? _request;
    private Task? _worker;
    private bool _active, _disposed;
    private int _generation, _failures;
    private long _nextAttempt, _sampledAt;
    private double? _value;
    private string _status = "温度采样已暂停";

    internal TemperatureSampler(
        Func<CancellationToken, Task<(double? Temperature, long Samples)>>? read = null,
        Func<long>? milliseconds = null)
    {
        _read = read ?? (token => TemperatureClient.ReadAsync(cancellationToken: token));
        _milliseconds = milliseconds ?? (() => Environment.TickCount64);
    }

    internal Task Pending { get { lock (_gate) return _worker ?? Task.CompletedTask; } }

    internal void SetActive(bool active)
    {
        CancellationTokenSource? cancel;
        lock (_gate)
        {
            if (_disposed || _active == active) return;
            _active = active;
            ++_generation;
            _value = null;
            _status = active ? "正在读取 CPU 温度…" : "温度采样已暂停";
            cancel = _request;
            // Keep the worker slot until cancellation finishes. Rapid hide/show
            // must not overlap a previous request, even if it ignores cancellation.
        }
        Cancel(cancel);
    }

    internal void Poll()
    {
        lock (_gate)
        {
            if (_disposed || !_active || _worker != null || _milliseconds() < _nextAttempt) return;
            var request = _request = new CancellationTokenSource();
            int generation = _generation;
            long startedAt = _milliseconds();
            _worker = Task.Run(() => RunAsync(generation, request, startedAt));
        }
    }

    internal TemperatureReading ReadCached()
    {
        lock (_gate)
        {
            if (_value.HasValue && _milliseconds() - _sampledAt >= 3000)
                return new(null, "温度读数已过期，等待服务响应");
            return new(_value, _status);
        }
    }

    private async Task RunAsync(int generation, CancellationTokenSource request, long startedAt)
    {
        try
        {
            var result = await _read(request.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (!Current()) return;
                _value = PerformanceService.ValidTemperature(result.Temperature);
                _sampledAt = _milliseconds();
                _status = _value.HasValue ? "CPU Tctl/Tdie 或封装温度 · 独立采样 · 专用温度服务" : "传感器暂未返回有效温度";
                _failures = 0;
                // Anchor normal cadence to request start. Measuring from response
                // completion would skip every other one-second UI tick.
                _nextAttempt = startedAt + 1000;
            }
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                if (!Current()) return;
                _value = null;
                _failures = Math.Min(_failures + 1, RetryMilliseconds.Length);
                int delay = RetryMilliseconds[_failures - 1];
                _nextAttempt = _milliseconds() + delay;
                _status = (error is TimeoutException ? "CPU 温度服务响应超时" : "CPU 温度服务暂不可用") +
                    $" · {delay / 1000} 秒后重试";
            }
        }
        finally
        {
            lock (_gate) { _request = null; _worker = null; }
            request.Dispose();
        }

        bool Current() => !_disposed && _active && generation == _generation && !request.IsCancellationRequested;
    }

    private static void Cancel(CancellationTokenSource? request)
    {
        try { request?.Cancel(); }
        catch (ObjectDisposedException) { /* The worker completed before cancellation. */ }
    }

    public void Dispose()
    {
        CancellationTokenSource? cancel;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _active = false;
            ++_generation;
            _value = null;
            _status = "温度采样已暂停";
            cancel = _request;
        }
        Cancel(cancel);
    }
}
