using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace FileDockDemo;

internal static class TemperatureClient
{
    internal static async Task<(double? Temperature, long Samples)> ReadAsync(
        bool sample = true, CancellationToken cancellationToken = default)
    {
        using var pipe = new NamedPipeClientStream(".", "FileDock.Temperature.v1", PipeDirection.InOut, PipeOptions.Asynchronous);
        return await ReadFromPipeAsync(pipe, sample, cancellationToken, AuthenticateService,
            TimeSpan.FromSeconds(8)).ConfigureAwait(false);
    }

    // Tests supply a private pipe and verifier; production always authenticates
    // the server against SCM before sending even the read-only request byte.
    internal static async Task<(double? Temperature, long Samples)> ReadFromPipeAsync(
        NamedPipeClientStream pipe, bool sample, CancellationToken cancellationToken,
        Action<NamedPipeClientStream> authenticate, TimeSpan timeout)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            authenticate(pipe);
            deadline.Token.ThrowIfCancellationRequested();
            await pipe.WriteAsync(new byte[] { sample ? (byte)1 : (byte)0 }, deadline.Token).ConfigureAwait(false);
            var response = new byte[16];
            await pipe.ReadExactlyAsync(response, deadline.Token).ConfigureAwait(false);
            return (PerformanceService.ValidTemperature(BitConverter.ToDouble(response)), BitConverter.ToInt64(response, 8));
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("温度服务响应超时", error);
        }
    }

    private static void AuthenticateService(NamedPipeClientStream pipe)
    {
        // Authenticate the pipe server against Windows Service Control Manager.
        // Ordinary users can query the service but cannot change its identity.
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint processId)) throw new Win32Exception();
        if (!IsServiceProcess(processId))
            throw new IOException("温度服务来源无法确认");
    }
    private static bool IsServiceProcess(uint processId)
    {
        IntPtr manager = OpenSCManager(null, null, 1), service = IntPtr.Zero;
        if (manager == IntPtr.Zero) return false;
        try
        {
            service = OpenService(manager, "FileDockTemperature", 4);
            return service != IntPtr.Zero && QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _) &&
                status.State == 4 && status.ProcessId != 0 && status.ProcessId == processId;
        }
        finally { if (service != IntPtr.Zero) CloseServiceHandle(service); CloseServiceHandle(manager); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
    {
        public uint Type, State, Controls, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint, ProcessId, Flags;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll")] private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatus status, int size, out int needed);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
}
