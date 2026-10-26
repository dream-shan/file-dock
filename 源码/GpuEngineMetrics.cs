using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace FileDockDemo;

// Windows GPU Engine counters come from the WDDM scheduler. Sum processes on
// each physical engine, then take the busiest engine on the selected adapter.
// Never sum independent engines, average them, or mix adapters.
internal sealed class GpuEngineMetrics : IDisposable
{
    internal readonly record struct Reading(string Instance, double Percent, uint Status = 0);
    internal readonly record struct Usage(double? Percent, string Engine);
    private static readonly Regex InstancePattern = new(
        @"^pid_\d+_luid_0x([0-9a-f]{8})_0x([0-9a-f]{8})_phys_(\d+)_eng_(\d+)_engtype_(.+?)(?:#\d+)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private IntPtr _query, _counter, _buffer;
    private uint _bufferSize;
    private string? _adapter;
    private bool _baseline;
    private long _nextAttempt;
    internal string Status { get; private set; } = "Windows GPU 引擎正在建立采样基线";
    internal string? AdapterKey => _adapter;

    internal Usage Read(string deviceName, bool resetBaseline)
    {
        if (resetBaseline) _baseline = false;
        if (_query == IntPtr.Zero)
        {
            if (Environment.TickCount64 < _nextAttempt) return new(null, "");
            _nextAttempt = Environment.TickCount64 + 30000;
            _adapter = FindAdapter(deviceName);
            if (_adapter == null) { Status = "无法唯一匹配这块显卡的 Windows 标识"; return new(null, ""); }
            uint error = PdhOpenQueryW(null, UIntPtr.Zero, out _query);
            if (error == 0) error = PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", UIntPtr.Zero, out _counter);
            if (error != 0) { Fail(error); return new(null, ""); }
        }
        uint collect = PdhCollectQueryData(_query);
        if (collect != 0) { Fail(collect); return new(null, ""); }
        if (!_baseline)
        {
            _baseline = true;
            Status = "Windows GPU 引擎正在建立采样基线";
            return new(null, "");
        }
        const uint format = 0x200 | 0x8000; // DOUBLE | NOCAP100; clamp after aggregation.
        // Re-query size if processes appear between calls. Reuse the allocation
        // across samples and impose an upper bound on a malformed provider.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint bytes = _bufferSize;
            uint error = PdhGetFormattedCounterArrayW(_counter, format, ref bytes, out uint count, _buffer);
            if (error == 0)
            {
                int stride = Marshal.SizeOf<CounterItem>();
                if (count > _bufferSize / stride) { Status = "Windows GPU 计数器数据无效"; return new(null, ""); }
                var readings = new List<Reading>((int)count);
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<CounterItem>(IntPtr.Add(_buffer, i * stride));
                    readings.Add(new(Marshal.PtrToStringUni(item.Name) ?? "", item.Value.Number, item.Value.Status));
                }
                var usage = Aggregate(readings, _adapter!);
                Status = usage.Percent.HasValue
                    ? "Windows GPU 最繁忙引擎 · " + usage.Engine + "\n按显卡和物理引擎汇总进程；与任务管理器采用相同统计原则"
                    : "Windows 暂未返回这块显卡的有效引擎数据";
                return usage;
            }
            if (error != 0x800007D2) { Status = $"Windows GPU 计数器暂不可用 (0x{error:X8})"; _baseline = false; return new(null, ""); }
            bytes = 0;
            error = PdhGetFormattedCounterArrayW(_counter, format, ref bytes, out _, IntPtr.Zero);
            if (error != 0x800007D2 || bytes == 0 || bytes > 16 * 1024 * 1024)
            { Status = "Windows GPU 计数器缓冲区暂不可用"; return new(null, ""); }
            _buffer = Marshal.ReAllocHGlobal(_buffer, (IntPtr)bytes); _bufferSize = bytes;
        }
        Status = "Windows GPU 计数器实例正在变化";
        return new(null, "");
    }

    internal static Usage Aggregate(IEnumerable<Reading> readings, string adapter)
    {
        var engines = new Dictionary<string, (double Total, string Type)>(StringComparer.OrdinalIgnoreCase);
        foreach (var reading in readings)
        {
            if (reading.Status > 1 || !double.IsFinite(reading.Percent) || reading.Percent < 0) continue;
            var match = InstancePattern.Match(reading.Instance);
            if (!match.Success) continue;
            string identity = "luid_0x" + match.Groups[1].Value + "_0x" + match.Groups[2].Value;
            if (!string.Equals(identity, adapter, StringComparison.OrdinalIgnoreCase)) continue;
            // The physical engine index, not its engine type, is the identity:
            // multiple Copy/Compute engines must not be merged into one.
            string key = match.Groups[3].Value + ":" + match.Groups[4].Value;
            engines.TryGetValue(key, out var previous);
            engines[key] = (previous.Total + reading.Percent, match.Groups[5].Value);
        }
        double? busiest = null; string type = "";
        foreach (var engine in engines.Values)
            if (!busiest.HasValue || engine.Total > busiest.Value) { busiest = engine.Total; type = engine.Type; }
        return new(busiest.HasValue ? Math.Clamp(busiest.Value, 0, 100) : null, type);
    }

    private void Fail(uint error)
    {
        Status = $"Windows GPU 引擎暂不可用 (0x{error:X8})";
        Dispose(); _nextAttempt = Environment.TickCount64 + 30000;
    }

    // Match the driver's exact model name to DXGI, rejecting ambiguous matches
    // instead of assuming NVIDIA index 0 means Windows GPU 0.
    private static string? FindAdapter(string deviceName)
    {
        IntPtr factory = IntPtr.Zero;
        try
        {
            var iid = new Guid("770AAE78-F26F-4DBA-A829-253C83D1B387");
            if (CreateDXGIFactory1(ref iid, out factory) < 0) return null;
            var enumerate = Method<EnumAdapters>(factory, 12);
            string? result = null;
            for (uint i = 0; i < 64; i++)
            {
                if (enumerate(factory, i, out IntPtr adapter) < 0) break;
                try
                {
                    if (Method<GetDescription>(adapter, 10)(adapter, out var description) < 0 || (description.Flags & 2) != 0) continue;
                    if (!string.Equals(description.Description.Trim(), deviceName.Trim(), StringComparison.OrdinalIgnoreCase)
                        || !HasPhysicalAddress(description)) continue;
                    if (result != null) return null;
                    result = string.Format(CultureInfo.InvariantCulture, "luid_0x{0:x8}_0x{1:x8}", description.LuidHigh, description.LuidLow);
                }
                finally { Marshal.Release(adapter); }
            }
            return result;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException) { return null; }
        finally { if (factory != IntPtr.Zero) Marshal.Release(factory); }
    }
    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    // Remote/indirect display adapters can reuse the physical GPU's model name.
    // They report a sentinel PCI address and must not impersonate that GPU.
    internal static bool IsPhysicalAddress(uint bus, uint device, uint function) => bus <= 255 && device <= 31 && function <= 7;
    private static bool HasPhysicalAddress(AdapterDescription description)
    {
        var open = new OpenAdapter { Low = description.LuidLow, High = description.LuidHigh };
        if (D3DKMTOpenAdapterFromLuid(ref open) < 0) return false;
        IntPtr data = Marshal.AllocHGlobal(12);
        try
        {
            var query = new QueryAdapter { Handle = open.Handle, Type = 6, Data = data, Size = 12 };
            return D3DKMTQueryAdapterInfo(ref query) >= 0 && IsPhysicalAddress(
                unchecked((uint)Marshal.ReadInt32(data)), unchecked((uint)Marshal.ReadInt32(data, 4)), unchecked((uint)Marshal.ReadInt32(data, 8)));
        }
        finally { Marshal.FreeHGlobal(data); D3DKMTCloseAdapter(ref open.Handle); }
    }
    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _query = _counter = _buffer = IntPtr.Zero; _bufferSize = 0; _baseline = false;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)] private struct CounterValue
    { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Number; }
    [StructLayout(LayoutKind.Sequential)] private struct CounterItem { public IntPtr Name; public CounterValue Value; }
    [StructLayout(LayoutKind.Sequential)] private struct OpenAdapter { public uint Low, High, Handle; }
    [StructLayout(LayoutKind.Sequential)] private struct QueryAdapter { public uint Handle, Type; public IntPtr Data; public uint Size; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubsystemId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow, LuidHigh, Flags;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapters(IntPtr factory, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDescription(IntPtr adapter, out AdapterDescription description);
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    [DllImport("gdi32.dll")] private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapter open);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapter query);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref uint handle);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? source, UIntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, UIntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr items);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
