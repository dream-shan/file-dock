#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace FileDockTemperature;

internal readonly struct CapabilitySpec
{
    internal uint Id { get; }
    internal string Key { get; }
    internal string Name { get; }
    internal string Unit { get; }
    internal bool RequiresCustomMode { get; }
    internal CapabilitySpec(uint id, string key, string name, string unit, bool requiresCustomMode)
    {
        Id = id; Key = key; Name = name; Unit = unit; RequiresCustomMode = requiresCustomMode;
    }
}

internal sealed class HardwareFeatureLimit
{
    internal uint Id { get; }
    internal string Key { get; }
    internal string Name { get; }
    internal string Unit { get; }
    internal int Min { get; }
    internal int Max { get; }
    internal int Step { get; }
    internal int DefaultValue { get; }
    internal int? CurrentValue { get; }
    internal HardwareFeatureLimit(uint id, string key, string name, string unit, int min, int max, int step, int defaultValue, int? currentValue)
    {
        Id = id; Key = key; Name = name; Unit = unit; Min = min; Max = max; Step = step; DefaultValue = defaultValue; CurrentValue = currentValue;
    }
}

internal sealed class HardwareControlState
{
    internal bool Supported { get; }
    internal string VendorFamily { get; }
    internal int? ThermalMode { get; }
    internal bool SupportsCustomMode { get; }
    internal bool OnAcPower { get; }
    internal int? Fan1Rpm { get; }
    internal int? Fan2Rpm { get; }
    internal int? FanMaxRpm { get; }
    internal bool SupportsFanFullSpeed { get; }
    internal bool? FanFullSpeed { get; }
    internal IReadOnlyList<HardwareFeatureLimit> Features { get; }
    internal string? Message { get; }
    internal HardwareControlState(bool supported, string vendorFamily, int? thermalMode, bool supportsCustomMode,
        bool onAcPower, int? fan1Rpm, int? fan2Rpm, int? fanMaxRpm, bool supportsFanFullSpeed, bool? fanFullSpeed,
        IReadOnlyList<HardwareFeatureLimit> features, string? message)
    {
        Supported = supported; VendorFamily = vendorFamily; ThermalMode = thermalMode; SupportsCustomMode = supportsCustomMode;
        OnAcPower = onAcPower; Fan1Rpm = fan1Rpm; Fan2Rpm = fan2Rpm; FanMaxRpm = fanMaxRpm;
        SupportsFanFullSpeed = supportsFanFullSpeed; FanFullSpeed = fanFullSpeed; Features = features; Message = message;
    }
}

internal interface ILenovoWmiTransport
{
    bool HasGameZone();
    bool IsAcPowerConnected();
    int? GetSmartFanMode();
    void SetSmartFanMode(int mode);
    int? GetFan1Speed();
    int? GetFan2Speed();
    int? GetFanMaxSpeed();
    IReadOnlyList<(uint Id, int Min, int Max, int Step, int DefaultValue)> ReadCapabilityData01();
    int? GetFeatureValue(uint id);
    void SetFeatureValue(uint id, int value);
}

// Strict allow-list hardware control engine. Rejects unknown IDs, out-of-range
// values, misaligned steps and unsafe battery-mode writes before touching WMI.
internal sealed class HardwareControlEngine
{
    internal const int ModeQuiet = 1;
    internal const int ModeBalance = 2;
    internal const int ModePerformance = 3;
    internal const int ModeCustom = 255;
    internal const uint FeatureFanFullSpeed = 0x04020000;
    private const uint FeatureCpuFanRpm = 0x04030001;
    private const uint FeatureGpuFanRpm = 0x04030002;

    internal static readonly IReadOnlyDictionary<uint, CapabilitySpec> AllowedFeatures =
        new Dictionary<uint, CapabilitySpec>
        {
            [0x0102FF00] = new(0x0102FF00, "cpu-pl1", "CPU 长期功耗上限 (PL1/SPL)", "W", true),
            [0x0101FF00] = new(0x0101FF00, "cpu-pl2", "CPU 短期功耗上限 (PL2/sPPT)", "W", true),
            [0x0103FF00] = new(0x0103FF00, "cpu-peak", "CPU 峰值功耗上限 (fPPT)", "W", true),
            [0x0106FF00] = new(0x0106FF00, "cpu-cross", "CPU 交叉负载功耗上限", "W", true),
            [0x0104FF00] = new(0x0104FF00, "cpu-temp", "CPU 温度上限", "°C", true),
            [0x0202FF00] = new(0x0202FF00, "gpu-ctgp", "GPU 可配置功耗上限 (cTGP)", "W", true),
            [0x0201FF00] = new(0x0201FF00, "gpu-boost", "GPU 动态加速功耗", "W", true),
            [0x0203FF00] = new(0x0203FF00, "gpu-temp", "GPU 温度上限", "°C", true),
        };

    private static readonly uint[] OrderedFeatureIds =
    [
        0x0102FF00, 0x0101FF00, 0x0103FF00, 0x0106FF00, 0x0104FF00,
        0x0202FF00, 0x0201FF00, 0x0203FF00
    ];

    private readonly ILenovoWmiTransport _wmi;
    private readonly object _gate = new();

    internal HardwareControlEngine(ILenovoWmiTransport wmi) => _wmi = wmi;

    internal string HandleCommand(string rawCommand)
    {
        if (string.IsNullOrWhiteSpace(rawCommand) || rawCommand.Length > 512)
            return ErrorResponse("命令无效或超出长度限制。");

        string[] parts = rawCommand.Trim().Split([' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return ErrorResponse("命令为空。");

        lock (_gate)
        {
            try
            {
                return parts[0].ToUpperInvariant() switch
                {
                    "PING" => "OK|PONG|v1",
                    "READ" when parts.Length == 1 => FormatState(ReadState(null)),
                    "SET_MODE" when parts.Length == 2 => HandleSetMode(parts[1]),
                    "SET_FEATURE" when parts.Length == 3 => HandleSetFeature(parts[1], parts[2]),
                    "RESET_DEFAULTS" when parts.Length == 1 => HandleResetDefaults(),
                    _ => ErrorResponse("不支持的硬件控制命令。")
                };
            }
            catch (Exception ex)
            {
                return ErrorResponse("硬件服务执行失败：" + ex.GetType().Name);
            }
        }
    }

    private string HandleSetMode(string modeToken)
    {
        if (!int.TryParse(modeToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mode) ||
            mode is not (ModeQuiet or ModeBalance or ModePerformance or ModeCustom))
            return ErrorResponse("性能模式值不在允许范围内。");

        if (!_wmi.HasGameZone())
            return ErrorResponse("本机未检测到受支持的 Lenovo Legion WMI 接口。");

        bool onAc = _wmi.IsAcPowerConnected();
        if ((mode == ModePerformance || mode == ModeCustom) && !onAc)
            return ErrorResponse("电池供电下禁止切换到野兽模式或自定义模式，请先接通交流电源。");

        var caps = _wmi.ReadCapabilityData01();
        if (mode == ModeCustom && !caps.Any(c => AllowedFeatures.ContainsKey(c.Id)))
            return ErrorResponse("当前 BIOS 未提供自定义功耗与温控上限表，不支持自定义模式。");

        _wmi.SetSmartFanMode(mode);
        return FormatState(ReadState("性能模式已切换。"));
    }

    private string HandleSetFeature(string idToken, string valueToken)
    {
        string hex = idToken.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? idToken.Substring(2) : idToken;
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id) ||
            !int.TryParse(valueToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            return ErrorResponse("参数标识或数值格式无效。");

        if (!_wmi.HasGameZone())
            return ErrorResponse("本机未检测到受支持的 Lenovo Legion WMI 接口。");

        if (id == FeatureFanFullSpeed)
        {
            if (value is not (0 or 1))
                return ErrorResponse("全速散热开关仅允许 0 或 1。");
            if (_wmi.GetFeatureValue(FeatureFanFullSpeed) == null)
                return ErrorResponse("当前机型不支持全速散热开关。");
            _wmi.SetFeatureValue(FeatureFanFullSpeed, value);
            return FormatState(ReadState(value == 1 ? "已开启全速散热。" : "已关闭全速散热。"));
        }

        if (!AllowedFeatures.TryGetValue(id, out var spec))
            return ErrorResponse("该硬件特征不在安全白名单内，已拒绝写入。");

        if (!_wmi.IsAcPowerConnected())
            return ErrorResponse("电池供电下不允许修改自定义功耗或温度上限，请先接通交流电源。");

        int? currentMode = _wmi.GetSmartFanMode();
        if (spec.RequiresCustomMode && currentMode != ModeCustom)
            return ErrorResponse("修改自定义功耗或温度上限前，请先切换到「自定义」模式。");

        var cap = _wmi.ReadCapabilityData01().FirstOrDefault(item => item.Id == id);
        if (cap.Id == 0 || cap.Max < cap.Min)
            return ErrorResponse($"当前 BIOS 未声明「{spec.Name}」的安全范围，已拒绝写入。");

        if (value < cap.Min || value > cap.Max)
            return ErrorResponse($"「{spec.Name}」目标值 {value} 超出 BIOS 安全范围 [{cap.Min}, {cap.Max}]。");

        int step = cap.Step <= 0 ? 1 : cap.Step;
        if ((value - cap.Min) % step != 0)
            return ErrorResponse($"「{spec.Name}」目标值 {value} 未按 BIOS 步长 {step} 对齐。");

        _wmi.SetFeatureValue(id, value);
        return FormatState(ReadState($"已更新 {spec.Name} 为 {value}{spec.Unit}。"));
    }

    private string HandleResetDefaults()
    {
        if (!_wmi.HasGameZone())
            return ErrorResponse("本机未检测到受支持的 Lenovo Legion WMI 接口。");

        if (_wmi.GetFeatureValue(FeatureFanFullSpeed) is { } fullSpeed && fullSpeed != 0)
        {
            try { _wmi.SetFeatureValue(FeatureFanFullSpeed, 0); }
            catch { }
        }

        var caps = _wmi.ReadCapabilityData01();
        int? mode = _wmi.GetSmartFanMode();
        if (mode == ModeCustom)
        {
            foreach (var cap in caps)
            {
                if (!AllowedFeatures.ContainsKey(cap.Id)) continue;
                if (cap.DefaultValue < cap.Min || cap.DefaultValue > cap.Max) continue;
                try { _wmi.SetFeatureValue(cap.Id, cap.DefaultValue); }
                catch { }
            }
        }

        _wmi.SetSmartFanMode(ModeBalance);
        return FormatState(ReadState("已恢复 BIOS 默认参数并切回均衡模式。"));
    }

    internal HardwareControlState ReadState(string? message)
    {
        if (!_wmi.HasGameZone())
        {
            return new HardwareControlState(false, "", null, false, _wmi.IsAcPowerConnected(),
                null, null, null, false, null, [], message ?? "未检测到受支持的 Lenovo Legion WMI 接口。");
        }

        int? mode = _wmi.GetSmartFanMode();
        bool onAc = _wmi.IsAcPowerConnected();
        int? fan1 = _wmi.GetFan1Speed() ?? _wmi.GetFeatureValue(FeatureCpuFanRpm);
        int? fan2 = _wmi.GetFan2Speed() ?? _wmi.GetFeatureValue(FeatureGpuFanRpm);
        int? fanMax = _wmi.GetFanMaxSpeed();
        int? fullSpeedRaw = _wmi.GetFeatureValue(FeatureFanFullSpeed);
        bool supportsFullSpeed = fullSpeedRaw is 0 or 1;
        bool? fullSpeed = supportsFullSpeed ? fullSpeedRaw == 1 : null;

        var rawCaps = _wmi.ReadCapabilityData01().ToDictionary(c => c.Id, c => c);
        var features = new List<HardwareFeatureLimit>();
        foreach (uint id in OrderedFeatureIds)
        {
            if (!rawCaps.TryGetValue(id, out var cap) || cap.Max < cap.Min || cap.Max <= 0) continue;
            var spec = AllowedFeatures[id];
            int step = cap.Step <= 0 ? 1 : cap.Step;
            int? current = _wmi.GetFeatureValue(id);
            features.Add(new HardwareFeatureLimit(id, spec.Key, spec.Name, spec.Unit,
                cap.Min, cap.Max, step, cap.DefaultValue, current));
        }

        return new HardwareControlState(
            true,
            "Lenovo Legion WMI",
            mode,
            features.Count > 0,
            onAc,
            NormalizeRpm(fan1),
            NormalizeRpm(fan2),
            NormalizeRpm(fanMax),
            supportsFullSpeed,
            fullSpeed,
            features,
            message);
    }

    private static int? NormalizeRpm(int? rpm) => rpm is > 0 and <= 20000 ? rpm : null;

    private static string Escape(string? text) =>
        (text ?? "").Replace("\\", "\\\\").Replace("|", "/").Replace("\r", " ").Replace("\n", " ");

    private static string ErrorResponse(string message) => "ERR|" + Escape(message);

    internal static string FormatState(HardwareControlState state)
    {
        var sb = new StringBuilder(512);
        sb.Append("OK|STATE|");
        sb.Append(state.Supported ? "1" : "0").Append('|');
        sb.Append(Escape(state.VendorFamily)).Append('|');
        sb.Append(state.ThermalMode?.ToString(CultureInfo.InvariantCulture) ?? "").Append('|');
        sb.Append(state.SupportsCustomMode ? "1" : "0").Append('|');
        sb.Append(state.OnAcPower ? "1" : "0").Append('|');
        sb.Append(state.Fan1Rpm?.ToString(CultureInfo.InvariantCulture) ?? "").Append('|');
        sb.Append(state.Fan2Rpm?.ToString(CultureInfo.InvariantCulture) ?? "").Append('|');
        sb.Append(state.FanMaxRpm?.ToString(CultureInfo.InvariantCulture) ?? "").Append('|');
        sb.Append(state.SupportsFanFullSpeed ? "1" : "0").Append('|');
        sb.Append(state.FanFullSpeed.HasValue ? (state.FanFullSpeed.Value ? "1" : "0") : "").Append('|');
        sb.Append(Escape(state.Message)).Append('|');
        for (int i = 0; i < state.Features.Count; i++)
        {
            if (i > 0) sb.Append(';');
            var f = state.Features[i];
            sb.Append(f.Id.ToString("X8", CultureInfo.InvariantCulture)).Append(',')
              .Append(f.Key).Append(',')
              .Append(f.Min.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(f.Max.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(f.Step.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(f.DefaultValue.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(f.CurrentValue?.ToString(CultureInfo.InvariantCulture) ?? "");
        }
        return sb.ToString();
    }
}

internal sealed class WindowsLenovoWmiTransport : ILenovoWmiTransport
{
    private const string WmiScope = @"\\.\root\wmi";

    public bool HasGameZone()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(WmiScope, "SELECT InstanceName FROM LENOVO_GAMEZONE_DATA");
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            {
                using (obj) return true;
            }
            return false;
        }
        catch { return false; }
    }

    public bool IsAcPowerConnected()
    {
        if (GetSystemPowerStatus(out var status))
            return status.ACLineStatus == 1;
        return false;
    }

    public int? GetSmartFanMode() => InvokeGameZoneOut("GetSmartFanMode");
    public int? GetFan1Speed() => InvokeGameZoneOut("GetFan1Speed");
    public int? GetFan2Speed() => InvokeGameZoneOut("GetFan2Speed");
    public int? GetFanMaxSpeed() => InvokeGameZoneOut("GetFanMaxSpeed");

    public void SetSmartFanMode(int mode)
    {
        using var instance = GetFirstInstance("LENOVO_GAMEZONE_DATA")
            ?? throw new InvalidOperationException("LENOVO_GAMEZONE_DATA unavailable.");
        using var parameters = instance.GetMethodParameters("SetSmartFanMode");
        parameters["Data"] = (uint)mode;
        using var _ = instance.InvokeMethod("SetSmartFanMode", parameters, null);
    }

    public IReadOnlyList<(uint Id, int Min, int Max, int Step, int DefaultValue)> ReadCapabilityData01()
    {
        var list = new List<(uint, int, int, int, int)>();
        try
        {
            using var searcher = new ManagementObjectSearcher(WmiScope,
                "SELECT IDs, MinValue, MaxValue, Step, DefaultValue FROM LENOVO_CAPABILITY_DATA_01");
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    uint id = ToUInt(obj["IDs"]);
                    int min = ToInt(obj["MinValue"]);
                    int max = ToInt(obj["MaxValue"]);
                    int step = ToInt(obj["Step"]);
                    int def = ToInt(obj["DefaultValue"]);
                    if (id != 0) list.Add((id, min, max, step, def));
                }
            }
        }
        catch { }
        return list;
    }

    public int? GetFeatureValue(uint id)
    {
        try
        {
            using var instance = GetFirstInstance("LENOVO_OTHER_METHOD");
            if (instance == null) return null;
            using var parameters = instance.GetMethodParameters("GetFeatureValue");
            parameters["IDs"] = id;
            using var output = instance.InvokeMethod("GetFeatureValue", parameters, null);
            return output?["value"] == null ? null : Convert.ToInt32(output["value"], CultureInfo.InvariantCulture);
        }
        catch { return null; }
    }

    public void SetFeatureValue(uint id, int value)
    {
        using var instance = GetFirstInstance("LENOVO_OTHER_METHOD")
            ?? throw new InvalidOperationException("LENOVO_OTHER_METHOD unavailable.");
        using var parameters = instance.GetMethodParameters("SetFeatureValue");
        parameters["IDs"] = id;
        parameters["value"] = (uint)value;
        using var _ = instance.InvokeMethod("SetFeatureValue", parameters, null);
    }

    private static int? InvokeGameZoneOut(string methodName)
    {
        try
        {
            using var instance = GetFirstInstance("LENOVO_GAMEZONE_DATA");
            if (instance == null) return null;
            using var output = instance.InvokeMethod(methodName, null, null);
            return output?["Data"] == null ? null : Convert.ToInt32(output["Data"], CultureInfo.InvariantCulture);
        }
        catch { return null; }
    }

    private static ManagementObject? GetFirstInstance(string className)
    {
        using var searcher = new ManagementObjectSearcher(WmiScope, $"SELECT * FROM {className}");
        using var results = searcher.Get();
        foreach (ManagementBaseObject obj in results)
        {
            if (obj is ManagementObject mo) return mo;
            obj.Dispose();
        }
        return null;
    }

    private static uint ToUInt(object? value) =>
        value == null ? 0u : Convert.ToUInt32(value, CultureInfo.InvariantCulture);
    private static int ToInt(object? value) =>
        value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
