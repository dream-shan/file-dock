using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FileDockDemo;

internal sealed record PerformanceSnapshot(double? CpuLoad, double? CpuTemperature, string CpuName,
    double? GpuLoad, double? GpuTemperature, string GpuName, ulong MemoryUsed, ulong MemoryTotal,
    ulong? VramUsed, ulong? VramTotal, double? Download, double? Upload, string NetworkName, string TemperatureStatus);

// Owned by one sampling worker. No polling threads, child processes or logs.
internal sealed class PerformanceService : IDisposable
{
    private bool _initialized;
    private string _temperatureStatus = "", _cpuName = "CPU";
    private readonly NvidiaMetrics _nvidia = new();
    private ulong _idle, _kernel, _user;
    private bool _cpuBaseline;
    private long _networkTick;
    private Dictionary<string, (long Received, long Sent)> _networkBytes = new();
    internal int SampleCount { get; private set; }

    internal PerformanceSnapshot Sample(bool resetBaseline)
    {
        if (!_initialized)
        {
            _initialized = true;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                _cpuName = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
            }
            catch (Exception ex) { _temperatureStatus = "CPU 温度暂不可用：" + ex.Message; }
        }
        if (resetBaseline) { _cpuBaseline = false; _networkTick = 0; _networkBytes.Clear(); }
        double? load = ReadCpuLoad(), temperature = null;
        try
        {
            temperature = TemperatureClient.Read().Temperature;
            _temperatureStatus = temperature.HasValue ? "CPU 封装温度 · 专用温度服务" : "传感器暂未返回有效温度";
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { _temperatureStatus = "CPU 温度服务暂不可用：" + ex.Message; }
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory)) memory = default;
        var gpu = _nvidia.Read();
        var network = ReadNetwork();
        SampleCount++;
        return new PerformanceSnapshot(load, temperature, _cpuName, gpu.Load, gpu.Temperature, gpu.Name,
            memory.TotalPhysical >= memory.AvailablePhysical ? memory.TotalPhysical - memory.AvailablePhysical : 0,
            memory.TotalPhysical, gpu.Used, gpu.Total, network.Down, network.Up, network.Name, _temperatureStatus);
    }

    internal static double? CalculateCpuLoad(ulong idle, ulong kernel, ulong user, ulong oldIdle, ulong oldKernel, ulong oldUser)
    {
        if (idle < oldIdle || kernel < oldKernel || user < oldUser) return null;
        double total = (double)(kernel - oldKernel) + (user - oldUser);
        return total <= 0 ? null : Math.Clamp(100 * (1 - (idle - oldIdle) / total), 0, 100);
    }
    internal static double? ValidTemperature(double? value) => value is > 0 and <= 130 && double.IsFinite(value.Value) ? value : null;
    internal static double? ByteRate(long current, long previous, double seconds) => seconds > 0 && current >= previous ? (current - previous) / seconds : null;
    private double? ReadCpuLoad()
    {
        if (!GetSystemTimes(out ulong idle, out ulong kernel, out ulong user)) return null;
        double? result = _cpuBaseline ? CalculateCpuLoad(idle, kernel, user, _idle, _kernel, _user) : null;
        _idle = idle; _kernel = kernel; _user = user; _cpuBaseline = true;
        return result;
    }
    private (double? Down, double? Up, string Name) ReadNetwork()
    {
        long tick = Environment.TickCount64;
        double seconds = (tick - _networkTick) / 1000d;
        var current = new Dictionary<string, (long Received, long Sent)>();
        var names = new List<string>();
        double down = 0, up = 0; bool hasDelta = false;
        foreach (var adapter in NetworkTraffic.Read())
        {
            current[adapter.Id] = (adapter.Received, adapter.Sent);
            names.Add(adapter.Name);
            if (_networkTick != 0 && _networkBytes.TryGetValue(adapter.Id, out var previous))
            {
                double? receive = ByteRate(adapter.Received, previous.Received, seconds), send = ByteRate(adapter.Sent, previous.Sent, seconds);
                if (receive.HasValue && send.HasValue) { down += receive.Value; up += send.Value; hasDelta = true; }
            }
        }
        _networkTick = tick; _networkBytes = current;
        return (hasDelta ? down : null, hasDelta ? up : null, names.Count == 0 ? "未检测到活动网络" : string.Join("、", names));
    }
    public void Dispose() { _nvidia.Dispose(); }

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
}

internal sealed class NvidiaMetrics : IDisposable
{
    private IntPtr _library, _device;
    private bool _attempted;
    private string _name = "GPU";
    private NvmlSimple? _shutdown;
    private NvmlUtilization? _utilization;
    private NvmlTemperature? _temperature;
    private NvmlMemory? _memory;
    internal (double? Load, double? Temperature, string Name, ulong? Used, ulong? Total) Read()
    {
        if (!_attempted)
        {
            _attempted = true;
            try
            {
                _library = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "nvml.dll"));
                if (Get<NvmlSimple>("nvmlInit_v2")() != 0) { Dispose(); return (null, null, _name, null, null); }
                _shutdown = Get<NvmlSimple>("nvmlShutdown");
                if (Get<NvmlDevice>("nvmlDeviceGetHandleByIndex_v2")(0, out _device) != 0) { Dispose(); return (null, null, _name, null, null); }
                var name = new System.Text.StringBuilder(128);
                if (Get<NvmlName>("nvmlDeviceGetName")(_device, name, (uint)name.Capacity) == 0) _name = name.ToString();
                _utilization = Get<NvmlUtilization>("nvmlDeviceGetUtilizationRates");
                _temperature = Get<NvmlTemperature>("nvmlDeviceGetTemperature");
                _memory = Get<NvmlMemory>("nvmlDeviceGetMemoryInfo");
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { Dispose(); }
        }
        if (_device == IntPtr.Zero || _utilization == null || _temperature == null || _memory == null) return (null, null, _name, null, null);
        double? load = _utilization(_device, out var utilization) == 0 ? Math.Clamp(utilization.Gpu, 0, 100) : null;
        double? temperature = _temperature(_device, 0, out uint degrees) == 0 ? PerformanceService.ValidTemperature(degrees) : null;
        bool hasMemory = _memory(_device, out var memory) == 0 && memory.Total > 0;
        return (load, temperature, _name, hasMemory ? memory.Used : null, hasMemory ? memory.Total : null);
    }
    private T Get<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
    public void Dispose()
    {
        _shutdown?.Invoke(); _shutdown = null; _device = IntPtr.Zero;
        if (_library != IntPtr.Zero) { NativeLibrary.Free(_library); _library = IntPtr.Zero; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Utilization { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct Memory { public ulong Total, Free, Used; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlSimple();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlDevice(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate int NvmlName(IntPtr device, System.Text.StringBuilder name, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlUtilization(IntPtr device, out Utilization utilization);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlTemperature(IntPtr device, uint sensor, out uint temperature);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlMemory(IntPtr device, out Memory memory);
}



