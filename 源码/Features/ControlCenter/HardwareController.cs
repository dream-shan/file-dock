#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace FileDockDemo.Features.ControlCenter;

internal enum HardwareFormFactor { Unknown, Desktop, Laptop }

internal enum HardwareCapabilityState
{
    AvailableControllable,
    AvailableReadOnly,
    RequiresElevatedService,
    Unsupported,
    Unavailable
}

internal sealed record HardwareGpuInfo(string Name, string Vendor, string DriverVersion);

internal sealed record HardwareIdentity(
    string Manufacturer,
    string Model,
    string SystemFamily,
    string BoardManufacturer,
    string BoardProduct,
    string BiosVersion,
    HardwareFormFactor FormFactor,
    string CpuName,
    string CpuVendor,
    int? CpuCores,
    int? CpuThreads,
    int? CpuMaxClockMhz,
    IReadOnlyList<HardwareGpuInfo> PhysicalGpus,
    int FilteredVirtualGpuCount)
{
    internal string DisplayTitle
    {
        get
        {
            string brand = Manufacturer.Trim();
            string primary = !string.IsNullOrWhiteSpace(SystemFamily) &&
                !SystemFamily.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase) &&
                !SystemFamily.Equals("Default string", StringComparison.OrdinalIgnoreCase)
                ? SystemFamily.Trim()
                : Model.Trim();
            if (string.IsNullOrWhiteSpace(primary)) return brand.Length > 0 ? brand : "未识别机型";
            if (brand.Length > 0 && !primary.StartsWith(brand, StringComparison.OrdinalIgnoreCase))
                return $"{brand} {primary}";
            return primary;
        }
    }

    internal string FormFactorLabel => FormFactor switch
    {
        HardwareFormFactor.Laptop => "笔记本",
        HardwareFormFactor.Desktop => "台式机",
        _ => "形态未知"
    };

    internal static HardwareIdentity Unknown() => new("", "", "", "", "", "", HardwareFormFactor.Unknown,
        "", "", null, null, null, [], 0);
}

internal sealed record GpuPowerTelemetry(
    string GpuName,
    double? PowerDrawWatts,
    double? DefaultLimitWatts,
    double? MinLimitWatts,
    double? MaxLimitWatts,
    double? EnforcedLimitWatts,
    double? ManagementLimitWatts,
    int? TemperatureCelsius,
    int? GraphicsClockMhz,
    int? MaxGraphicsClockMhz,
    int? MemoryClockMhz,
    int? MaxMemoryClockMhz,
    int? FanPercent,
    int? PerformanceState);

internal sealed record VendorInterfaceProbe(
    string VendorFamily,
    IReadOnlyList<string> DetectedClasses,
    bool SupportsPerformanceMode,
    bool SupportsPowerControl,
    bool SupportsFanCurve,
    bool SupportsCpuOverclock,
    bool SupportsGpuOverclock,
    string? ProbeError = null)
{
    internal bool HasAnyVendorInterface => DetectedClasses.Count > 0;
    internal static VendorInterfaceProbe None(string? error = null) =>
        new("", [], false, false, false, false, false, error);
}

internal sealed record HardwareCapabilityItem(
    string Key,
    string Title,
    HardwareCapabilityState State,
    string Summary,
    string Detail);

internal sealed record HardwareSnapshot(
    HardwareIdentity Identity,
    GpuPowerTelemetry? GpuTelemetry,
    VendorInterfaceProbe VendorProbe,
    IReadOnlyList<HardwareCapabilityItem> Capabilities,
    DateTimeOffset? UpdatedAt = null,
    string? Error = null,
    bool Loading = false,
    HardwareControlSnapshot? Control = null)
{
    internal static HardwareSnapshot Empty(string? status = null) => new(
        HardwareIdentity.Unknown(),
        null,
        VendorInterfaceProbe.None(),
        [],
        null,
        status == "正在读取…" ? null : status,
        Loading: status == "正在读取…",
        Control: null);
}

internal interface IHardwareBackend : IControlBackend<HardwareSnapshot>
{
    void SetThermalMode(int mode);
    void SetFeature(uint featureId, int value);
    void ApplyFeatureValues(IReadOnlyDictionary<uint, int> featureValues, int targetMode, bool? fanFullSpeed);
    void ResetDefaults();
}

internal sealed class HardwareController : IDisposable
{
    private readonly ControlChannel<IHardwareBackend, HardwareSnapshot> _channel;
    internal HardwareSnapshot Current => _channel.Current;
    internal event Action<HardwareSnapshot>? Changed { add => _channel.Changed += value; remove => _channel.Changed -= value; }
    internal bool Active => _channel.Active;

    internal HardwareController(Func<IHardwareBackend> factory, Dispatcher dispatcher) =>
        _channel = new("FileDock hardware", factory, dispatcher, HardwareSnapshot.Empty,
            (snapshot, error) => snapshot with { Error = error, Loading = false });

    internal void SetActive(bool active) => _channel.SetActive(active);
    internal void Refresh() => _channel.Refresh();
    internal void SetThermalMode(int mode) => _channel.Command(backend => backend.SetThermalMode(mode));
    internal void SetFeature(uint featureId, int value) => _channel.Command(backend => backend.SetFeature(featureId, value));
    internal void ApplyFeatureValues(IReadOnlyDictionary<uint, int> featureValues, int targetMode, bool? fanFullSpeed) =>
        _channel.Command(backend => backend.ApplyFeatureValues(featureValues, targetMode, fanFullSpeed));
    internal void ResetDefaults() => _channel.Command(backend => backend.ResetDefaults());
    internal Task DrainAsync() => _channel.DrainAsync();
    public void Dispose() => _channel.Dispose();
}
