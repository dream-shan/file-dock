#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FileDockDemo.Features.ControlCenter;

internal static class HardwareCapabilityEvaluator
{
    private static readonly string[] VirtualDisplayKeywords =
    [
        "gameviewer", "microsoft basic display", "microsoft remote display",
        "indirect display", "virtual display", "parsec virtual", "citrix",
        "rdp", "oray", "spacedesk", "iddsampledriver", "meta virtual",
        "luminoncore", "sunshine", "anydesk", "teamviewer", "vmware", "vbox"
    ];

    internal static bool IsVirtualDisplayAdapter(string? name, string? compatibility)
    {
        string combined = ((name ?? "") + " " + (compatibility ?? "")).Trim();
        if (combined.Length == 0) return true;
        foreach (string keyword in VirtualDisplayKeywords)
        {
            if (combined.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal static string StateLabel(HardwareCapabilityState state) => state switch
    {
        HardwareCapabilityState.AvailableControllable => "可调节",
        HardwareCapabilityState.AvailableReadOnly => "只读可用",
        HardwareCapabilityState.RequiresElevatedService => "需提权服务",
        HardwareCapabilityState.Unsupported => "本机不支持",
        _ => "暂不可读"
    };

    internal static VendorInterfaceProbe ClassifyVendorClasses(IReadOnlyCollection<string> wmiClasses, string? probeError = null)
    {
        if (wmiClasses.Count == 0)
            return VendorInterfaceProbe.None(probeError);

        var set = new HashSet<string>(wmiClasses, StringComparer.OrdinalIgnoreCase);

        // Lenovo Legion WMI BIOS classes
        string[] legionClasses =
        [
            "LENOVO_GAMEZONE_DATA", "LENOVO_OTHER_METHOD", "LENOVO_FAN_METHOD",
            "LENOVO_FAN_TABLE_DATA", "LENOVO_CAPABILITY_DATA_00", "LENOVO_CAPABILITY_DATA_01",
            "LENOVO_CPU_OVERCLOCKING_DATA", "LENOVO_GPU_OVERCLOCKING_DATA", "LENOVO_CPU_METHOD"
        ];
        var foundLegion = legionClasses.Where(set.Contains).ToArray();
        if (foundLegion.Length > 0)
        {
            bool hasGameZone = set.Contains("LENOVO_GAMEZONE_DATA");
            bool hasOther = set.Contains("LENOVO_OTHER_METHOD");
            bool hasFan = set.Contains("LENOVO_FAN_METHOD") || set.Contains("LENOVO_FAN_TABLE_DATA") || hasGameZone;
            bool hasCpuOc = set.Contains("LENOVO_CPU_OVERCLOCKING_DATA") || set.Contains("LENOVO_CPU_METHOD") || hasGameZone;
            bool hasGpuOc = set.Contains("LENOVO_GPU_OVERCLOCKING_DATA") || hasOther || hasGameZone;
            return new VendorInterfaceProbe(
                "Lenovo Legion WMI",
                foundLegion,
                SupportsPerformanceMode: hasGameZone,
                SupportsPowerControl: hasOther || set.Contains("LENOVO_CAPABILITY_DATA_00") || set.Contains("LENOVO_CAPABILITY_DATA_01"),
                SupportsFanCurve: hasFan,
                SupportsCpuOverclock: hasCpuOc,
                SupportsGpuOverclock: hasGpuOc,
                ProbeError: probeError);
        }

        if (set.Contains("AsusAtkWmi_WMNB"))
        {
            return new VendorInterfaceProbe("ASUS ATK WMI", ["AsusAtkWmi_WMNB"],
                SupportsPerformanceMode: true, SupportsPowerControl: true, SupportsFanCurve: true,
                SupportsCpuOverclock: false, SupportsGpuOverclock: false, ProbeError: probeError);
        }

        if (set.Contains("hpqBIntM"))
        {
            return new VendorInterfaceProbe("HP OMEN WMI", ["hpqBIntM"],
                SupportsPerformanceMode: true, SupportsPowerControl: false, SupportsFanCurve: true,
                SupportsCpuOverclock: false, SupportsGpuOverclock: false, ProbeError: probeError);
        }

        if (set.Contains("AWCCWmiMethodFunction"))
        {
            return new VendorInterfaceProbe("Dell / Alienware WMI", ["AWCCWmiMethodFunction"],
                SupportsPerformanceMode: true, SupportsPowerControl: false, SupportsFanCurve: true,
                SupportsCpuOverclock: false, SupportsGpuOverclock: false, ProbeError: probeError);
        }

        if (set.Contains("MSI_ACPI"))
        {
            return new VendorInterfaceProbe("MSI ACPI WMI", ["MSI_ACPI"],
                SupportsPerformanceMode: true, SupportsPowerControl: false, SupportsFanCurve: true,
                SupportsCpuOverclock: false, SupportsGpuOverclock: false, ProbeError: probeError);
        }

        return VendorInterfaceProbe.None(probeError);
    }

    internal static IReadOnlyList<HardwareCapabilityItem> Evaluate(
        HardwareIdentity identity,
        GpuPowerTelemetry? gpu,
        VendorInterfaceProbe vendor,
        HardwareControlSnapshot? control = null)
    {
        var items = new List<HardwareCapabilityItem>(4);
        bool serviceReady = control?.Ready == true;
        bool desktopNvmlPower = identity.FormFactor != HardwareFormFactor.Laptop &&
            gpu is { ManagementLimitWatts: not null, MinLimitWatts: not null, MaxLimitWatts: not null };

        // 1. Vendor performance mode
        if (serviceReady && control!.ThermalMode != null)
        {
            items.Add(new HardwareCapabilityItem(
                "performance-mode",
                "厂商性能模式",
                HardwareCapabilityState.AvailableControllable,
                $"{control.VendorFamily} · 当前 {HardwareFeatureCatalog.ModeLabel(control.ThermalMode)}模式",
                "已连接本机提权服务，可直接切换安静 / 均衡 / 野兽 / 自定义模式（电池供电时自动阻止野兽与自定义模式）。"));
        }
        else if (vendor.SupportsPerformanceMode)
        {
            items.Add(new HardwareCapabilityItem(
                "performance-mode",
                "厂商性能模式",
                HardwareCapabilityState.RequiresElevatedService,
                $"检测到 {vendor.VendorFamily}",
                $"本机已检测到 {vendor.VendorFamily} 性能模式接口（如安静 / 均衡 / 野兽 / 自定义）；安装或更新本机提权服务后即可直接切换。"));
        }
        else if (!string.IsNullOrWhiteSpace(vendor.ProbeError))
        {
            items.Add(new HardwareCapabilityItem(
                "performance-mode",
                "厂商性能模式",
                HardwareCapabilityState.Unavailable,
                "接口探测受限",
                vendor.ProbeError!));
        }
        else
        {
            items.Add(new HardwareCapabilityItem(
                "performance-mode",
                "厂商性能模式",
                HardwareCapabilityState.Unsupported,
                "未检测到厂商模式接口",
                "未检测到受支持的品牌 WMI 性能模式接口；仍可在上方「电源」调节系统电源模式与电脑已有电源计划。"));
        }

        // 2. Power limit / telemetry
        if (serviceReady && control!.Features.Count > 0)
        {
            string rangeNote = FormatGpuPowerNote(gpu);
            items.Add(new HardwareCapabilityItem(
                "power-limit",
                "功耗与温控上限（CPU / GPU）",
                HardwareCapabilityState.AvailableControllable,
                $"已开放 {control.Features.Count} 项 BIOS 安全范围参数",
                $"所有写入均按本机 BIOS 安全上下限与步长二次校验，可在自定义模式下调节并保存为方案。" +
                (rangeNote.Length > 0 ? $"当前 GPU：{rangeNote}。" : "")));
        }
        else if (vendor.SupportsPowerControl)
        {
            string rangeNote = FormatGpuPowerNote(gpu);
            items.Add(new HardwareCapabilityItem(
                "power-limit",
                "功耗控制（CPU / GPU）",
                HardwareCapabilityState.RequiresElevatedService,
                $"检测到 {vendor.VendorFamily} 功耗接口",
                $"已检测到厂商自定义功耗与安全上下限接口（{string.Join(" / ", vendor.DetectedClasses.Take(3))}）。" +
                (rangeNote.Length > 0 ? $"当前 {rangeNote}。" : "") +
                "安装或更新本机提权服务后，将按 BIOS 安全上下限受控调节。"));
        }
        else if (desktopNvmlPower)
        {
            items.Add(new HardwareCapabilityItem(
                "power-limit",
                "功耗控制（GPU）",
                HardwareCapabilityState.RequiresElevatedService,
                $"NVIDIA 显卡可调范围 {gpu!.MinLimitWatts:F0}–{gpu.MaxLimitWatts:F0} W",
                $"当前已只读监测 {gpu.GpuName} 实时功耗；修改桌面显卡功耗墙需要本机提权服务授权。"));
        }
        else if (gpu?.PowerDrawWatts != null || gpu?.EnforcedLimitWatts != null)
        {
            string rangeNote = FormatGpuPowerNote(gpu);
            items.Add(new HardwareCapabilityItem(
                "power-limit",
                "功耗监测与控制",
                HardwareCapabilityState.AvailableReadOnly,
                rangeNote.Length > 0 ? rangeNote : "已支持 GPU 实时功耗只读监测",
                "当前可通过驱动接口只读监测 GPU 功耗，但本机未检测到公开的独立功耗墙写入接口。"));
        }
        else
        {
            items.Add(new HardwareCapabilityItem(
                "power-limit",
                "功耗控制",
                HardwareCapabilityState.Unsupported,
                "本机未检测到功耗接口",
                "当前硬件或驱动未暴露可读取或可调节的功耗接口。"));
        }

        // 3. Fan control
        if (serviceReady && (control!.SupportsFanFullSpeed || control.Fan1Rpm != null || control.Fan2Rpm != null))
        {
            string rpmText = FormatFanRpm(control);
            items.Add(new HardwareCapabilityItem(
                "fan-control",
                "风扇转速与散热控制",
                control.SupportsFanFullSpeed ? HardwareCapabilityState.AvailableControllable : HardwareCapabilityState.AvailableReadOnly,
                rpmText.Length > 0 ? rpmText : "支持全速散热切换",
                control.SupportsFanFullSpeed
                    ? "已实时读取 EC 风扇转速并支持切换全速散热开关。"
                    : "已通过本机提权服务实时读取 EC 风扇转速。"));
        }
        else if (vendor.SupportsFanCurve)
        {
            items.Add(new HardwareCapabilityItem(
                "fan-control",
                "风扇转速与曲线",
                HardwareCapabilityState.RequiresElevatedService,
                $"检测到 {vendor.VendorFamily} 风扇接口",
                identity.FormFactor == HardwareFormFactor.Laptop
                    ? "笔记本风扇由 EC/BIOS 托管，已检测到厂商风扇接口，安装或更新本机提权服务后可读取实时转速并控制散热。"
                    : "已检测到厂商风扇接口，需通过本机提权服务进行受控读写。"));
        }
        else if (gpu?.FanPercent != null)
        {
            items.Add(new HardwareCapabilityItem(
                "fan-control",
                "风扇转速与控制",
                HardwareCapabilityState.AvailableReadOnly,
                $"GPU 风扇当前 {gpu.FanPercent}%",
                "当前仅只读展示独立显卡风扇转速；自定义风扇曲线需要硬件或厂商提权接口支持。"));
        }
        else
        {
            items.Add(new HardwareCapabilityItem(
                "fan-control",
                "风扇控制",
                HardwareCapabilityState.Unsupported,
                "未检测到可控风扇接口",
                identity.FormFactor == HardwareFormFactor.Laptop
                    ? "笔记本风扇由 EC 托管且未检测到受支持的厂商风扇接口。"
                    : "未检测到受支持的主板或显卡风扇控制接口。"));
        }

        // 4. Clock & overclocking
        if (vendor.SupportsCpuOverclock || vendor.SupportsGpuOverclock)
        {
            string scope = vendor.SupportsCpuOverclock && vendor.SupportsGpuOverclock ? "CPU / GPU" :
                vendor.SupportsCpuOverclock ? "CPU" : "GPU";
            string clockNote = FormatGpuClockNote(gpu);
            items.Add(new HardwareCapabilityItem(
                "overclock",
                $"频率与超频（{scope}）",
                HardwareCapabilityState.RequiresElevatedService,
                $"检测到 {vendor.VendorFamily} 超频接口（当前未开放写入）",
                (clockNote.Length > 0 ? $"当前 {clockNote}。" : "") +
                "6.2 优先开放安全的模式、功耗与散热控制；高风险超频偏移暂不写入。"));
        }
        else if (gpu?.GraphicsClockMhz != null)
        {
            items.Add(new HardwareCapabilityItem(
                "overclock",
                "频率监测与超频",
                HardwareCapabilityState.AvailableReadOnly,
                FormatGpuClockNote(gpu),
                "当前支持只读查看 GPU 实时频率与 P-State；本机未检测到受支持的超频调节接口。"));
        }
        else
        {
            items.Add(new HardwareCapabilityItem(
                "overclock",
                "频率与超频",
                HardwareCapabilityState.Unsupported,
                "未检测到超频接口",
                "当前硬件或厂商未暴露受支持的超频控制接口。"));
        }

        return items;
    }

    internal static string FormatFanRpm(HardwareControlSnapshot? control)
    {
        if (control == null) return "";
        var parts = new List<string>(2);
        if (control.Fan1Rpm is { } f1) parts.Add($"风扇1 {f1} RPM");
        if (control.Fan2Rpm is { } f2) parts.Add($"风扇2 {f2} RPM");
        return string.Join(" · ", parts);
    }

    internal static string FormatGpuPowerNote(GpuPowerTelemetry? gpu)
    {
        if (gpu == null) return "";
        var parts = new List<string>(3);
        if (gpu.PowerDrawWatts is { } draw)
            parts.Add($"实时 {draw.ToString("F1", CultureInfo.InvariantCulture)} W");
        if (gpu.EnforcedLimitWatts is { } enforced)
            parts.Add($"生效上限 {enforced.ToString("F0", CultureInfo.InvariantCulture)} W");
        else if (gpu.ManagementLimitWatts is { } limit)
            parts.Add($"功耗墙 {limit.ToString("F0", CultureInfo.InvariantCulture)} W");
        if (gpu.MinLimitWatts is { } min && gpu.MaxLimitWatts is { } max)
            parts.Add($"范围 {min.ToString("F0", CultureInfo.InvariantCulture)}–{max.ToString("F0", CultureInfo.InvariantCulture)} W");
        return string.Join(" · ", parts);
    }

    internal static string FormatGpuClockNote(GpuPowerTelemetry? gpu)
    {
        if (gpu == null) return "";
        var parts = new List<string>(3);
        if (gpu.GraphicsClockMhz is { } gr)
            parts.Add(gpu.MaxGraphicsClockMhz is { } maxGr ? $"核心 {gr}/{maxGr} MHz" : $"核心 {gr} MHz");
        if (gpu.MemoryClockMhz is { } mem)
            parts.Add($"显存 {mem} MHz");
        if (gpu.PerformanceState is { } ps)
            parts.Add($"P{ps}");
        return string.Join(" · ", parts);
    }
}
