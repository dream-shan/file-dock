using System;
using System.Diagnostics;

namespace FileDockTemperature;

internal interface ITemperatureHardware
{
    void Open();
    double Read();
    void Close();
}

// All methods belong to the single pipe worker. Time limits gate future
// requests; they do not interrupt an in-flight native hardware call.
internal sealed class CpuTemperatureSampler : IDisposable
{
    private static readonly int[] RetryMilliseconds = { 2000, 5000, 15000, 30000 };
    private readonly Func<ITemperatureHardware> _create;
    private readonly Action<string, Exception> _report;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Func<long> _milliseconds;
    private ITemperatureHardware? _hardware;
    private long _nextAttempt;
    private int _failures;
    private bool _disposed;

    internal CpuTemperatureSampler(Func<ITemperatureHardware> create,
        Action<string, Exception> report, Func<long>? milliseconds = null)
    {
        _create = create;
        _report = report;
        _milliseconds = milliseconds ?? (() => _clock.ElapsedMilliseconds);
    }

    internal double Temperature { get; private set; } = double.NaN;
    internal long Samples { get; private set; }

    internal void Sample()
    {
        if (_disposed || _milliseconds() < _nextAttempt) return;
        string stage = "initialize";
        ITemperatureHardware? candidate = null;
        try
        {
            if (_hardware == null)
            {
                candidate = _create();
                candidate.Open();
                _hardware = candidate;
                candidate = null;
            }
            stage = "update";
            double value = _hardware.Read();
            Temperature = value > 0 && value <= 130 ? value : double.NaN;
            _failures = 0;
            _nextAttempt = _milliseconds() + 1000;
        }
        catch (Exception error)
        {
            Temperature = double.NaN;
            Report(stage, error);
            Close(candidate);
            ResetHardware();
            _failures = Math.Min(_failures + 1, RetryMilliseconds.Length);
            _nextAttempt = _milliseconds() + RetryMilliseconds[_failures - 1];
        }
        finally { Samples++; }
    }

    private void ResetHardware()
    {
        var previous = _hardware;
        _hardware = null;
        Close(previous);
    }

    private void Close(ITemperatureHardware? hardware)
    {
        try { hardware?.Close(); }
        catch (Exception error) { Report("close", error); }
    }

    private void Report(string stage, Exception error)
    {
        try { _report(stage, error); }
        catch { /* Diagnostics must not prevent cleanup or a later retry. */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Temperature = double.NaN;
        ResetHardware();
    }
}
