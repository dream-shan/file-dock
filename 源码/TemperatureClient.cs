using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace FileDockDemo;

internal static class TemperatureClient
{
    internal static (double? Temperature, long Samples) Read(bool sample = true)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var pipe = new NamedPipeClientStream(".", "FileDock.Temperature.v1", PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(500);
        // Authenticate the pipe server against Windows Service Control Manager.
        // Ordinary users can query the service but cannot change its identity.
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint processId)) throw new Win32Exception();
        if (!IsServiceProcess(processId))
            throw new IOException("温度服务来源无法确认");
        pipe.WriteByte(sample ? (byte)1 : (byte)0);
        var response = new byte[16]; int received = 0;
        while (received < response.Length)
        {
            int count = pipe.ReadAsync(response.AsMemory(received), deadline.Token).AsTask().GetAwaiter().GetResult();
            if (count == 0) throw new IOException("温度服务已断开");
            received += count;
        }
        return (PerformanceService.ValidTemperature(BitConverter.ToDouble(response)), BitConverter.ToInt64(response, 8));
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
