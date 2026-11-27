#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;

namespace FileDockDemo.Features.ControlCenter;

internal enum HardwareServiceStatus
{
    NotApplicable,
    ServiceUnavailable,
    ConnectedUnsupported,
    ConnectedReady
}

internal sealed record HardwareControlFeature(
    uint Id,
    string HexId,
    string Key,
    string Name,
    string Unit,
    int Min,
    int Max,
    int Step,
    int DefaultValue,
    int? CurrentValue);

internal sealed record HardwareControlSnapshot(
    HardwareServiceStatus Status,
    string VendorFamily,
    int? ThermalMode,
    bool SupportsCustomMode,
    bool OnAcPower,
    int? Fan1Rpm,
    int? Fan2Rpm,
    int? FanMaxRpm,
    bool SupportsFanFullSpeed,
    bool? FanFullSpeed,
    IReadOnlyList<HardwareControlFeature> Features,
    string? Message = null)
{
    internal bool Ready => Status == HardwareServiceStatus.ConnectedReady;
    internal static HardwareControlSnapshot Unavailable(string? message = null) =>
        new(HardwareServiceStatus.ServiceUnavailable, "", null, false, true, null, null, null, false, null, [], message);
    internal static HardwareControlSnapshot NotApplicable() =>
        new(HardwareServiceStatus.NotApplicable, "", null, false, true, null, null, null, false, null, []);
}

public sealed class HardwarePerformancePreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string VendorFamily { get; set; } = "";
    public int ThermalMode { get; set; } = 2;
    public bool? FanFullSpeed { get; set; }
    public Dictionary<string, int> FeatureValues { get; set; } = [];

    internal HardwarePerformancePreset Copy() => new()
    {
        Id = Id,
        Name = Name,
        VendorFamily = VendorFamily,
        ThermalMode = ThermalMode,
        FanFullSpeed = FanFullSpeed,
        FeatureValues = new Dictionary<string, int>(FeatureValues, StringComparer.OrdinalIgnoreCase)
    };

    internal bool Valid()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 80 ||
            string.IsNullOrWhiteSpace(Name) || Name.Length > 60 ||
            string.IsNullOrWhiteSpace(VendorFamily) || VendorFamily.Length > 80 ||
            ThermalMode is not (1 or 2 or 3 or 255) ||
            FeatureValues == null || FeatureValues.Count > 32)
            return false;
        foreach (var pair in FeatureValues)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length != 8 ||
                !uint.TryParse(pair.Key, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) ||
                pair.Value < 0 || pair.Value > 1000)
                return false;
        }
        return true;
    }

    internal static void Validate(AppState state)
    {
        state.HardwarePresets ??= [];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (state.HardwarePresets.Count > 32 ||
            state.HardwarePresets.Any(p => p == null || !p.Valid() || !ids.Add(p.Id)))
            throw new InvalidDataException("硬件性能预设配置无效。");
    }
}

internal interface IHardwareServiceClient
{
    HardwareControlSnapshot Read();
    HardwareControlSnapshot SetMode(int mode);
    HardwareControlSnapshot SetFeature(uint featureId, int value);
    HardwareControlSnapshot ResetDefaults();
}

internal static class HardwareFeatureCatalog
{
    internal const int ModeQuiet = 1;
    internal const int ModeBalance = 2;
    internal const int ModePerformance = 3;
    internal const int ModeCustom = 255;
    internal const uint FeatureFanFullSpeed = 0x04020000;

    internal static readonly IReadOnlyDictionary<uint, (string Key, string Name, string Unit)> KnownFeatures =
        new Dictionary<uint, (string, string, string)>
        {
            [0x0102FF00] = ("cpu-pl1", "CPU 长期功耗上限 (PL1/SPL)", "W"),
            [0x0101FF00] = ("cpu-pl2", "CPU 短期功耗上限 (PL2/sPPT)", "W"),
            [0x0103FF00] = ("cpu-peak", "CPU 峰值功耗上限 (fPPT)", "W"),
            [0x0106FF00] = ("cpu-cross", "CPU 交叉负载功耗上限", "W"),
            [0x0104FF00] = ("cpu-temp", "CPU 温度上限", "°C"),
            [0x0202FF00] = ("gpu-ctgp", "GPU 可配置功耗上限 (cTGP)", "W"),
            [0x0201FF00] = ("gpu-boost", "GPU 动态加速功耗", "W"),
            [0x0203FF00] = ("gpu-temp", "GPU 温度上限", "°C")
        };

    internal static string ModeLabel(int? mode) => mode switch
    {
        ModeQuiet => "安静",
        ModeBalance => "均衡",
        ModePerformance => "野兽",
        ModeCustom => "自定义",
        _ => "未知"
    };

    internal static HardwareControlSnapshot ParseResponse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            throw new InvalidDataException("提权服务未返回有效数据。");
        string trimmed = line.Trim();
        if (trimmed.StartsWith("ERR|", StringComparison.Ordinal))
            throw new InvalidOperationException(trimmed[4..]);
        if (!trimmed.StartsWith("OK|STATE|", StringComparison.Ordinal))
            throw new InvalidDataException("提权服务返回格式无法识别。");

        string[] fields = trimmed.Split('|');
        if (fields.Length < 14)
            throw new InvalidDataException("提权服务返回字段不完整。");

        bool supported = fields[2] == "1";
        string vendorFamily = fields[3];
        int? mode = TryInt(fields[4]);
        bool supportsCustom = fields[5] == "1";
        bool onAc = fields[6] == "1";
        int? fan1 = TryInt(fields[7]);
        int? fan2 = TryInt(fields[8]);
        int? fanMax = TryInt(fields[9]);
        bool supportsFullSpeed = fields[10] == "1";
        bool? fullSpeed = fields[11].Length == 0 ? null : fields[11] == "1";
        string? message = fields[12].Length == 0 ? null : fields[12];
        var features = new List<HardwareControlFeature>();

        if (fields[13].Length > 0)
        {
            foreach (string token in fields[13].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = token.Split(',');
                if (parts.Length < 7) continue;
                if (!uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id)) continue;
                if (!KnownFeatures.TryGetValue(id, out var meta)) continue;
                int? min = TryInt(parts[2]), max = TryInt(parts[3]), step = TryInt(parts[4]), def = TryInt(parts[5]);
                if (min == null || max == null || step == null || def == null || max < min) continue;
                features.Add(new HardwareControlFeature(
                    id,
                    id.ToString("X8", CultureInfo.InvariantCulture),
                    meta.Key,
                    meta.Name,
                    meta.Unit,
                    min.Value,
                    max.Value,
                    Math.Max(1, step.Value),
                    def.Value,
                    TryInt(parts[6])));
            }
        }

        var status = supported ? HardwareServiceStatus.ConnectedReady : HardwareServiceStatus.ConnectedUnsupported;
        return new HardwareControlSnapshot(status, vendorFamily, mode, supportsCustom, onAc,
            fan1, fan2, fanMax, supportsFullSpeed, fullSpeed, features, message);
    }

    private static int? TryInt(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
}

internal sealed class NamedPipeHardwareServiceClient : IHardwareServiceClient
{
    internal const string PipeName = "FileDock.HardwareControl.v1";
    private static readonly UTF8Encoding Utf8 = new(false);

    public HardwareControlSnapshot Read()
    {
        try { return HardwareFeatureCatalog.ParseResponse(Send("READ")); }
        catch (TimeoutException) { return HardwareControlSnapshot.Unavailable("本机提权服务未安装或尚未更新到 6.2。"); }
        catch (IOException) { return HardwareControlSnapshot.Unavailable("无法连接到本机提权服务，请确认服务已更新并运行。"); }
        catch (UnauthorizedAccessException) { return HardwareControlSnapshot.Unavailable("本机提权服务访问被拒绝。"); }
    }

    public HardwareControlSnapshot SetMode(int mode) =>
        HardwareFeatureCatalog.ParseResponse(Send($"SET_MODE {mode.ToString(CultureInfo.InvariantCulture)}"));

    public HardwareControlSnapshot SetFeature(uint featureId, int value) =>
        HardwareFeatureCatalog.ParseResponse(Send($"SET_FEATURE {featureId.ToString("X8", CultureInfo.InvariantCulture)} {value.ToString(CultureInfo.InvariantCulture)}"));

    public HardwareControlSnapshot ResetDefaults() =>
        HardwareFeatureCatalog.ParseResponse(Send("RESET_DEFAULTS"));

    private static string Send(string command)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None);
        pipe.Connect(1200);
        byte[] request = Utf8.GetBytes(command + "\n");
        pipe.Write(request, 0, request.Length);
        pipe.Flush();
        using var reader = new StreamReader(pipe, Utf8, false, 1024, leaveOpen: true);
        string? line = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(line))
            throw new IOException("提权服务未返回响应。");
        return line;
    }
}
