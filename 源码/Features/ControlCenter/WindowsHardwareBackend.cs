#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace FileDockDemo.Features.ControlCenter;

// Read-only hardware identity, vendor WMI class detection and NVIDIA NVML telemetry.
// Runs lazily on the hardware ControlWorkQueue without elevated privileges or hardware writes.
internal sealed class WindowsHardwareBackend : IHardwareBackend
{
    private static readonly string[] KnownVendorWmiClasses =
    [
        "LENOVO_GAMEZONE_DATA", "LENOVO_OTHER_METHOD", "LENOVO_FAN_METHOD",
        "LENOVO_FAN_TABLE_DATA", "LENOVO_CAPABILITY_DATA_00", "LENOVO_CAPABILITY_DATA_01",
        "LENOVO_CPU_OVERCLOCKING_DATA", "LENOVO_GPU_OVERCLOCKING_DATA", "LENOVO_CPU_METHOD",
        "AsusAtkWmi_WMNB", "hpqBIntM", "AWCCWmiMethodFunction", "MSI_ACPI"
    ];

    private readonly bool _native;
    private readonly NvmlPowerProbe _nvml = new();
    private HardwareIdentity? _cachedIdentity;
    private VendorInterfaceProbe? _cachedVendor;
    private bool _disposed;

    internal WindowsHardwareBackend(bool native = true) => _native = native;

    public void Start(Action changed) { }
    public void Stop() { }

    public HardwareSnapshot Read()
    {
        if (_disposed || !_native)
        {
            var emptyId = HardwareIdentity.Unknown();
            var emptyVendor = VendorInterfaceProbe.None();
            return new HardwareSnapshot(emptyId, null, emptyVendor,
                HardwareCapabilityEvaluator.Evaluate(emptyId, null, emptyVendor),
                DateTimeOffset.UtcNow, _native ? null : "自检模式下未读取本机硬件。");
        }

        _cachedIdentity ??= ReadIdentity();
        _cachedVendor ??= ProbeVendorInterfaces();
        var telemetry = _nvml.Read();
        var capabilities = HardwareCapabilityEvaluator.Evaluate(_cachedIdentity, telemetry, _cachedVendor);
        return new HardwareSnapshot(_cachedIdentity, telemetry, _cachedVendor, capabilities, DateTimeOffset.UtcNow);
    }

    private static HardwareIdentity ReadIdentity()
    {
        string manufacturer = "", model = "", family = "";
        HardwareFormFactor formFactor = HardwareFormFactor.Unknown;
        bool hasBattery = false;
        QuerySingle("root\\cimv2", "SELECT Manufacturer, Model, SystemFamily, PCSystemType FROM Win32_ComputerSystem", obj =>
        {
            manufacturer = ReadStr(obj, "Manufacturer");
            model = ReadStr(obj, "Model");
            family = ReadStr(obj, "SystemFamily");
            int? type = ReadInt(obj, "PCSystemType");
            formFactor = type switch
            {
                2 or 8 or 9 or 10 or 14 => HardwareFormFactor.Laptop,
                1 or 3 or 4 or 6 or 7 => HardwareFormFactor.Desktop,
                _ => HardwareFormFactor.Unknown
            };
        });

        QuerySingle("root\\cimv2", "SELECT DeviceID FROM Win32_Battery", _ => hasBattery = true);
        if (formFactor == HardwareFormFactor.Unknown && hasBattery)
            formFactor = HardwareFormFactor.Laptop;

        string boardMfg = "", boardProduct = "", biosVersion = "";
        QuerySingle("root\\cimv2", "SELECT Manufacturer, Product FROM Win32_BaseBoard", obj =>
        {
            boardMfg = ReadStr(obj, "Manufacturer");
            boardProduct = ReadStr(obj, "Product");
        });
        QuerySingle("root\\cimv2", "SELECT SMBIOSBIOSVersion FROM Win32_BIOS", obj =>
        {
            biosVersion = ReadStr(obj, "SMBIOSBIOSVersion");
        });

        string cpuName = "", cpuVendor = "";
        int? cpuCores = null, cpuThreads = null, cpuMaxMhz = null;
        QuerySingle("root\\cimv2", "SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor", obj =>
        {
            cpuName = ReadStr(obj, "Name");
            cpuVendor = ReadStr(obj, "Manufacturer");
            cpuCores = ReadInt(obj, "NumberOfCores");
            cpuThreads = ReadInt(obj, "NumberOfLogicalProcessors");
            cpuMaxMhz = ReadInt(obj, "MaxClockSpeed");
        });

        var physicalGpus = new List<HardwareGpuInfo>();
        int filteredVirtual = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\cimv2",
                "SELECT Name, AdapterCompatibility, DriverVersion FROM Win32_VideoController");
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    string name = ReadStr(obj, "Name");
                    string vendor = ReadStr(obj, "AdapterCompatibility");
                    string driver = ReadStr(obj, "DriverVersion");
                    if (HardwareCapabilityEvaluator.IsVirtualDisplayAdapter(name, vendor))
                    {
                        filteredVirtual++;
                        continue;
                    }
                    if (!physicalGpus.Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        physicalGpus.Add(new HardwareGpuInfo(name, vendor, driver));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException or InvalidOperationException) { }

        return new HardwareIdentity(manufacturer, model, family, boardMfg, boardProduct, biosVersion,
            formFactor, cpuName, cpuVendor, cpuCores, cpuThreads, cpuMaxMhz, physicalGpus, filteredVirtual);
    }

    private static VendorInterfaceProbe ProbeVendorInterfaces()
    {
        var found = new List<string>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\wmi");
            scope.Connect();
            foreach (string name in KnownVendorWmiClasses)
            {
                try
                {
                    using var cls = new ManagementClass(scope, new ManagementPath(name), new ObjectGetOptions());
                    cls.Get();
                    found.Add(name);
                }
                catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.NotFound or ManagementStatus.InvalidClass) { }
            }
            return HardwareCapabilityEvaluator.ClassifyVendorClasses(found);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            return VendorInterfaceProbe.None("无法查询本机 WMI 厂商接口定义（可能受权限或系统策略限制）。");
        }
    }

    private static void QuerySingle(string scope, string query, Action<ManagementBaseObject> reader)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, query);
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            {
                using (obj) { reader(obj); break; }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException or InvalidOperationException) { }
    }

    private static string ReadStr(ManagementBaseObject obj, string property)
    {
        try { return obj[property]?.ToString()?.Trim() ?? ""; }
        catch (ManagementException) { return ""; }
    }

    private static int? ReadInt(ManagementBaseObject obj, string property)
    {
        try
        {
            object? value = obj[property];
            return value == null ? null : Convert.ToInt32(value);
        }
        catch (Exception ex) when (ex is ManagementException or FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _nvml.Dispose();
    }

    private sealed class NvmlPowerProbe : IDisposable
    {
        private IntPtr _library, _device;
        private bool _attempted;
        private string _name = "";
        private NvmlSimple? _shutdown;
        private NvmlUIntOut? _powerUsage;
        private NvmlUIntOut? _defaultLimit;
        private NvmlUIntOut? _enforcedLimit;
        private NvmlUIntOut? _managementLimit;
        private NvmlConstraints? _constraints;
        private NvmlTemperature? _temperature;
        private NvmlClock? _clockInfo;
        private NvmlClock? _maxClockInfo;
        private NvmlUIntOut? _fanSpeed;
        private NvmlUIntOut? _pstate;

        internal GpuPowerTelemetry? Read()
        {
            if (!_attempted)
            {
                _attempted = true;
                try
                {
                    _library = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "nvml.dll"));
                    var init = TryGet<NvmlSimple>("nvmlInit_v2");
                    var getDevice = TryGet<NvmlDevice>("nvmlDeviceGetHandleByIndex_v2");
                    if (init == null || getDevice == null || init() != 0) { Dispose(); return null; }
                    _shutdown = TryGet<NvmlSimple>("nvmlShutdown");
                    if (getDevice(0, out _device) != 0 || _device == IntPtr.Zero) { Dispose(); return null; }

                    var getName = TryGet<NvmlName>("nvmlDeviceGetName");
                    if (getName != null)
                    {
                        var buffer = new StringBuilder(128);
                        if (getName(_device, buffer, (uint)buffer.Capacity) == 0) _name = buffer.ToString().Trim();
                    }
                    _powerUsage = TryGet<NvmlUIntOut>("nvmlDeviceGetPowerUsage");
                    _defaultLimit = TryGet<NvmlUIntOut>("nvmlDeviceGetPowerManagementDefaultLimit");
                    _enforcedLimit = TryGet<NvmlUIntOut>("nvmlDeviceGetEnforcedPowerLimit");
                    _managementLimit = TryGet<NvmlUIntOut>("nvmlDeviceGetPowerManagementLimit");
                    _constraints = TryGet<NvmlConstraints>("nvmlDeviceGetPowerManagementLimitConstraints");
                    _temperature = TryGet<NvmlTemperature>("nvmlDeviceGetTemperature");
                    _clockInfo = TryGet<NvmlClock>("nvmlDeviceGetClockInfo");
                    _maxClockInfo = TryGet<NvmlClock>("nvmlDeviceGetMaxClockInfo");
                    _fanSpeed = TryGet<NvmlUIntOut>("nvmlDeviceGetFanSpeed");
                    _pstate = TryGet<NvmlUIntOut>("nvmlDeviceGetPerformanceState");
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                {
                    Dispose();
                }
            }
            if (_device == IntPtr.Zero) return null;

            double? Milliwatts(NvmlUIntOut? call) =>
                call != null && call(_device, out uint mw) == 0 && mw > 0 && mw < 2_000_000 ? mw / 1000.0 : null;

            double? minW = null, maxW = null;
            if (_constraints != null && _constraints(_device, out uint minMw, out uint maxMw) == 0 &&
                minMw > 0 && maxMw >= minMw && maxMw < 2_000_000)
            {
                minW = minMw / 1000.0;
                maxW = maxMw / 1000.0;
            }

            int? temp = _temperature != null && _temperature(_device, 0, out uint deg) == 0 && deg <= 130 ? (int)deg : null;
            int? grClock = _clockInfo != null && _clockInfo(_device, 0, out uint gr) == 0 ? (int)gr : null;
            int? maxGrClock = _maxClockInfo != null && _maxClockInfo(_device, 0, out uint mgr) == 0 ? (int)mgr : null;
            int? memClock = _clockInfo != null && _clockInfo(_device, 2, out uint mem) == 0 ? (int)mem : null;
            int? maxMemClock = _maxClockInfo != null && _maxClockInfo(_device, 2, out uint mmem) == 0 ? (int)mmem : null;
            int? fan = _fanSpeed != null && _fanSpeed(_device, out uint f) == 0 && f <= 100 ? (int)f : null;
            int? pstate = _pstate != null && _pstate(_device, out uint p) == 0 && p <= 15 ? (int)p : null;

            return new GpuPowerTelemetry(
                _name.Length > 0 ? _name : "NVIDIA GPU",
                Milliwatts(_powerUsage),
                Milliwatts(_defaultLimit),
                minW,
                maxW,
                Milliwatts(_enforcedLimit),
                Milliwatts(_managementLimit),
                temp,
                grClock,
                maxGrClock,
                memClock,
                maxMemClock,
                fan,
                pstate);
        }

        private T? TryGet<T>(string name) where T : Delegate =>
            NativeLibrary.TryGetExport(_library, name, out IntPtr address)
                ? Marshal.GetDelegateForFunctionPointer<T>(address)
                : null;

        public void Dispose()
        {
            _shutdown?.Invoke();
            _shutdown = null;
            _device = IntPtr.Zero;
            if (_library != IntPtr.Zero)
            {
                NativeLibrary.Free(_library);
                _library = IntPtr.Zero;
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlSimple();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlDevice(uint index, out IntPtr device);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate int NvmlName(IntPtr device, StringBuilder name, uint length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlUIntOut(IntPtr device, out uint value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlConstraints(IntPtr device, out uint minLimit, out uint maxLimit);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlTemperature(IntPtr device, uint sensor, out uint temperature);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlClock(IntPtr device, uint clockType, out uint mhz);
    }
}
