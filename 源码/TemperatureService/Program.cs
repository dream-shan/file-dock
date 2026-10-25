using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;

namespace FileDockTemperature;

// Fixed, read-only IPC: request 1 samples CPU temperature, request 0 returns
// the last reading and sample counter. No paths, commands or hardware writes
// can be supplied by clients. The service has no periodic sampling timer.
internal sealed class TemperatureService : ServiceBase
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private Computer? _computer;
    private IHardware[] _cpus = Array.Empty<IHardware>();
    private double _temperature = double.NaN;
    private long _samples;
    private readonly Stopwatch _sinceSample = Stopwatch.StartNew();

    public TemperatureService()
    {
        ServiceName = "FileDockTemperature";
        CanStop = true; CanShutdown = true; AutoLog = false;
    }
    public static void Main() => Run(new TemperatureService());
    protected override void OnStart(string[] args) => _worker = Task.Run(Serve);
    protected override void OnShutdown() => OnStop();
    protected override void OnStop()
    {
        _stop.Cancel();
        try { _worker?.Wait(5000); } catch (AggregateException) { }
    }
    private async Task Serve()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream("FileDock.Temperature.v1", PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 256, 256, security);
                    // .NET Framework does not reliably cancel an already pending
                    // pipe read from its token alone. Closing the handle does.
                    using var stopRegistration = _stop.Token.Register(() => pipe.Dispose());
                    await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    using var timeoutRegistration = deadline.Token.Register(() => pipe.Dispose());
                    deadline.CancelAfter(2000);
                    var request = new byte[1];
                    if (await pipe.ReadAsync(request, 0, 1, deadline.Token).ConfigureAwait(false) != 1 || request[0] > 1) continue;
                    deadline.CancelAfter(8000);
                    if (request[0] == 1 && (_samples == 0 || _sinceSample.ElapsedMilliseconds >= 1000)) Sample();
                    var response = new byte[16];
                    Buffer.BlockCopy(BitConverter.GetBytes(_temperature), 0, response, 0, 8);
                    Buffer.BlockCopy(BitConverter.GetBytes(_samples), 0, response, 8, 8);
                    await pipe.WriteAsync(response, 0, response.Length, deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                catch (IOException) { if (!_stop.IsCancellationRequested) await Task.Delay(100, _stop.Token).ConfigureAwait(false); }
                catch (UnauthorizedAccessException) { await Task.Delay(1000, _stop.Token).ConfigureAwait(false); }
            }
        }
        catch (OperationCanceledException) { }
        finally { _computer?.Close(); }
    }
    private void Sample()
    {
        try
        {
            if (_computer == null)
            {
                _computer = new Computer { IsCpuEnabled = true };
                _computer.Open();
                _cpus = _computer.Hardware.Where(h => h.HardwareType == HardwareType.Cpu).ToArray();
            }
            foreach (var cpu in _cpus) cpu.Update();
            var temperatures = _cpus.SelectMany(h => h.Sensors)
                .Where(s => s.SensorType == SensorType.Temperature && s.Value > 0 && s.Value <= 130).ToArray();
            var sensor = temperatures.FirstOrDefault(s => s.Name.IndexOf("Tctl/Tdie", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? temperatures.FirstOrDefault(s => s.Name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? temperatures.FirstOrDefault(s => s.Name.IndexOf("Tdie", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? temperatures.FirstOrDefault();
            _temperature = sensor?.Value ?? double.NaN;
        }
        catch { _temperature = double.NaN; }
        finally { _samples++; _sinceSample.Restart(); }
    }
}
