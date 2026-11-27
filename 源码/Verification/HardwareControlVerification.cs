#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using FileDockDemo.Features.ControlCenter;

namespace FileDockDemo;

internal static class HardwareControlVerification
{
    internal static async Task Run(MainWindow window, Action<bool, string> check)
    {
        VerifyVirtualGpuFilter(check);
        VerifyVendorClassificationAndCapabilities(check);
        VerifyServiceProtocolAndPresets(window, check);
        await VerifyControllerAndView(window, check);
        await VerifyControllableTuningAndPresetsUi(window, check);
    }

    private static void VerifyServiceProtocolAndPresets(MainWindow window, Action<bool, string> check)
    {
        var parsed = HardwareFeatureCatalog.ParseResponse(
            "OK|STATE|1|Lenovo Legion WMI|255|1|1|2500|2700|5200|1|1|已更新。|0102FF00,cpu-pl1,30,115,5,65,80;0202FF00,gpu-ctgp,55,140,5,115,130");
        check(parsed.Ready && parsed.ThermalMode == 255 && parsed.Fan1Rpm == 2500 && parsed.Fan2Rpm == 2700 &&
              parsed.FanFullSpeed == true && parsed.Features.Count == 2 &&
              parsed.Features[0].CurrentValue == 80 && parsed.Features[1].CurrentValue == 130,
            "Hardware service client parses ready state, fan telemetry and BIOS feature limits");

        bool rejectedErr = false;
        try { HardwareFeatureCatalog.ParseResponse("ERR|电池供电下禁止切换"); }
        catch (InvalidOperationException ex) { rejectedErr = ex.Message.Contains("电池供电"); }
        check(rejectedErr, "Hardware service client surfaces structured ERR responses cleanly");

        var validPreset = new HardwarePerformancePreset
        {
            Name = "游戏控温",
            VendorFamily = "Lenovo Legion WMI",
            ThermalMode = HardwareFeatureCatalog.ModeCustom,
            FanFullSpeed = false,
            FeatureValues = new() { ["0102FF00"] = 75, ["0202FF00"] = 120 }
        };
        var invalidPreset = new HardwarePerformancePreset
        {
            Name = "非法预设",
            VendorFamily = "Lenovo Legion WMI",
            ThermalMode = 99
        };
        check(validPreset.Valid() && !invalidPreset.Valid(),
            "Hardware performance preset validation accepts valid BIOS-bounded modes and rejects invalid mode codes");
    }

    private static void VerifyVirtualGpuFilter(Action<bool, string> check)
    {
        check(HardwareCapabilityEvaluator.IsVirtualDisplayAdapter("GameViewer Virtual Display Adapter", "GameViewer") &&
              HardwareCapabilityEvaluator.IsVirtualDisplayAdapter("Microsoft Basic Display Adapter", "Microsoft") &&
              HardwareCapabilityEvaluator.IsVirtualDisplayAdapter("Parsec Virtual Display Adapter", "Parsec") &&
              !HardwareCapabilityEvaluator.IsVirtualDisplayAdapter("NVIDIA GeForce RTX 4060 Laptop GPU", "NVIDIA") &&
              !HardwareCapabilityEvaluator.IsVirtualDisplayAdapter("AMD Radeon(TM) Graphics", "Advanced Micro Devices, Inc."),
            "Hardware probe filters virtual display adapters while keeping physical GPUs");
    }

    private static void VerifyVendorClassificationAndCapabilities(Action<bool, string> check)
    {
        var legionVendor = HardwareCapabilityEvaluator.ClassifyVendorClasses([
            "LENOVO_GAMEZONE_DATA", "LENOVO_OTHER_METHOD", "LENOVO_FAN_METHOD",
            "LENOVO_CAPABILITY_DATA_00", "LENOVO_CPU_OVERCLOCKING_DATA", "LENOVO_GPU_OVERCLOCKING_DATA"
        ]);
        var legionIdentity = new HardwareIdentity("LENOVO", "82WM", "Legion R9000P ARX8", "LENOVO", "LNVNB161216", "LPCN51WW",
            HardwareFormFactor.Laptop, "AMD Ryzen 9 7945HX", "AuthenticAMD", 16, 32, 2501,
            [new HardwareGpuInfo("NVIDIA GeForce RTX 4060 Laptop GPU", "NVIDIA", "32.0.16.1714")], 1);
        var laptopGpu = new GpuPowerTelemetry("NVIDIA GeForce RTX 4060 Laptop GPU", 14.35, 55, 5, 140, 138.65, null, 61, 1890, 3105, 7001, 8001, null, 3);
        var legionCaps = HardwareCapabilityEvaluator.Evaluate(legionIdentity, laptopGpu, legionVendor);
        check(legionVendor.VendorFamily == "Lenovo Legion WMI" &&
              legionCaps.Count == 4 &&
              legionCaps.All(c => c.State == HardwareCapabilityState.RequiresElevatedService) &&
              (legionCaps.Single(c => c.Key == "power-limit").Detail.Contains("14.3") ||
               legionCaps.Single(c => c.Key == "power-limit").Detail.Contains("14.4")),
            "Lenovo Legion laptop with RTX Laptop GPU reports all four vendor-backed capabilities as requiring the elevated service");

        var desktopIdentity = new HardwareIdentity("ASUS", "Custom Desktop", "", "ASUSTeK", "PRIME Z790", "1602",
            HardwareFormFactor.Desktop, "Intel Core i7-14700K", "GenuineIntel", 20, 28, 3400,
            [new HardwareGpuInfo("NVIDIA GeForce RTX 4070", "NVIDIA", "32.0.15.6000")], 0);
        var desktopGpu = new GpuPowerTelemetry("NVIDIA GeForce RTX 4070", 42.0, 200, 100, 220, 200, 200, 48, 2475, 2800, 10501, 10501, 35, 0);
        var desktopCaps = HardwareCapabilityEvaluator.Evaluate(desktopIdentity, desktopGpu, VendorInterfaceProbe.None());
        check(desktopCaps.Single(c => c.Key == "performance-mode").State == HardwareCapabilityState.Unsupported &&
              desktopCaps.Single(c => c.Key == "power-limit").State == HardwareCapabilityState.RequiresElevatedService &&
              desktopCaps.Single(c => c.Key == "fan-control").State == HardwareCapabilityState.AvailableReadOnly &&
              desktopCaps.Single(c => c.Key == "overclock").State == HardwareCapabilityState.AvailableReadOnly,
            "Desktop NVIDIA GPU without vendor WMI separates elevated power-limit control from read-only fan/clock monitoring");

        var integratedIdentity = new HardwareIdentity("Generic", "Ultrabook", "", "OEM", "MB01", "1.0",
            HardwareFormFactor.Laptop, "Intel Core Ultra 7", "GenuineIntel", 12, 16, 2200,
            [new HardwareGpuInfo("Intel(R) Arc(TM) Graphics", "Intel", "31.0.101.0")], 0);
        var integratedCaps = HardwareCapabilityEvaluator.Evaluate(integratedIdentity, null, VendorInterfaceProbe.None());
        check(integratedCaps.Count == 4 && integratedCaps.All(c => c.State == HardwareCapabilityState.Unsupported),
            "Systems without supported vendor WMI or NVML honestly report all four hardware tuning controls as unsupported");

        var asusVendor = HardwareCapabilityEvaluator.ClassifyVendorClasses(["AsusAtkWmi_WMNB"]);
        var restrictedCaps = HardwareCapabilityEvaluator.Evaluate(integratedIdentity, null, VendorInterfaceProbe.None("策略禁止访问 WMI"));
        check(asusVendor.VendorFamily == "ASUS ATK WMI" && asusVendor.SupportsPerformanceMode && asusVendor.SupportsFanCurve &&
              restrictedCaps.Single(c => c.Key == "performance-mode").State == HardwareCapabilityState.Unavailable,
            "Other vendor WMI signatures classify cleanly and WMI probe errors surface as unavailable");
    }

    private static async Task VerifyControllerAndView(MainWindow window, Action<bool, string> check)
    {
        window.SelectPage("Control", false);
        window.UpdateLayout();
        check(DisplayPowerViewVerification.Children<Button>(window.ControlSection)
            .Any(button => AutomationProperties.GetName(button) == "硬件与性能详情"),
            "Control center exposes a dedicated hardware-and-performance detail entry");

        var fake = new FakeHardwareBackend();
        using var audio = new AudioController(() => new SilentAudioBackend(), window.Dispatcher);
        using var hardware = new HardwareController(() => fake, window.Dispatcher);
        using var view = new ControlCenterView(audio, hardware: hardware);
        var host = new Window
        {
            Content = view, Width = 380, Height = 500, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000
        };
        try
        {
            check(fake.Reads == 0, "Hardware backend performs no probe before the control center becomes visible");
            host.Show();
            await hardware.DrainAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            host.UpdateLayout();

            var texts = DisplayPowerViewVerification.Children<TextBlock>(view).Where(t => t.IsVisible).Select(t => t.Text).ToArray();
            check(fake.Reads == 1 && texts.Any(t => t.Contains("Legion R9000P ARX8")) && texts.Any(t => t.Contains("18.5")),
                "Activating control center probes hardware once and renders compact model and GPU power summary");

            var detailButton = DisplayPowerViewVerification.Children<Button>(view)
                .Single(b => b.IsVisible && AutomationProperties.GetName(b) == "硬件与性能详情");
            detailButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            host.UpdateLayout();
            var detailTexts = DisplayPowerViewVerification.Children<TextBlock>(view).Where(t => t.IsVisible).Select(t => t.Text).ToArray();
            check(detailTexts.Any(t => t.Contains("AMD Ryzen 9 7945HX")) &&
                  detailTexts.Any(t => t.Contains("已自动过滤 1 个虚拟显示适配器")) &&
                  detailTexts.Any(t => t.Contains("Lenovo Legion WMI")) &&
                  detailTexts.Any(t => t.Contains("需提权服务")),
                "Hardware detail view shows profile, virtual-GPU filtering note, NVML telemetry and capability badges");

            fake.Watts = 32.4;
            var refreshButton = DisplayPowerViewVerification.Children<Button>(view)
                .Single(b => b.IsVisible && Equals(b.Content, "刷新"));
            refreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await hardware.DrainAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            host.UpdateLayout();
            check(DisplayPowerViewVerification.Children<TextBlock>(view).Any(t => t.IsVisible && t.Text.Contains("32.40 W")),
                "Manual refresh updates live GPU power telemetry");

            fake.ThrowOnRead = true;
            refreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await hardware.DrainAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            host.UpdateLayout();
            check(DisplayPowerViewVerification.Children<TextBlock>(view).Any(t => t.IsVisible && t.Text.Contains("读取失败")),
                "Hardware read failure reports cleanly without crashing the control center");

            view.SetPaused(true);
            await hardware.DrainAsync();
            check(!hardware.Active && fake.Stops >= 1, "Pausing the control center deactivates the hardware channel");
        }
        finally
        {
            host.Close();
        }
    }

    private sealed class FakeHardwareBackend : IHardwareBackend
    {
        internal int Reads { get; private set; }
        internal int Stops { get; private set; }
        internal double Watts { get; set; } = 18.5;
        internal bool ThrowOnRead { get; set; }

        public void Start(Action changed) { }
        public void Stop() => Stops++;
        public void Dispose() { }

        public HardwareSnapshot Read()
        {
            Reads++;
            if (ThrowOnRead) throw new InvalidOperationException("simulated hardware read failure");
            var identity = new HardwareIdentity("LENOVO", "82WM", "Legion R9000P ARX8", "LENOVO", "LNVNB161216", "LPCN51WW",
                HardwareFormFactor.Laptop, "AMD Ryzen 9 7945HX", "AuthenticAMD", 16, 32, 2501,
                [new HardwareGpuInfo("NVIDIA GeForce RTX 4060 Laptop GPU", "NVIDIA", "32.0.16.1714")], 1);
            var gpu = new GpuPowerTelemetry("NVIDIA GeForce RTX 4060 Laptop GPU", Watts, 55, 5, 140, 138.6, null, 58, 1920, 3105, 7001, 8001, null, 3);
            var vendor = HardwareCapabilityEvaluator.ClassifyVendorClasses(["LENOVO_GAMEZONE_DATA", "LENOVO_OTHER_METHOD", "LENOVO_FAN_METHOD"]);
            var caps = HardwareCapabilityEvaluator.Evaluate(identity, gpu, vendor);
            return new HardwareSnapshot(identity, gpu, vendor, caps, DateTimeOffset.UtcNow);
        }

        public void SetThermalMode(int mode) { }
        public void SetFeature(uint featureId, int value) { }
        public void ApplyFeatureValues(System.Collections.Generic.IReadOnlyDictionary<uint, int> featureValues, int targetMode, bool? fanFullSpeed) { }
        public void ResetDefaults() { }
    }

    private static async Task VerifyControllableTuningAndPresetsUi(MainWindow window, Action<bool, string> check)
    {
        var controllable = new ControllableHardwareBackend();
        using var audio = new AudioController(() => new SilentAudioBackend(), window.Dispatcher);
        using var hardware = new HardwareController(() => controllable, window.Dispatcher);
        var savedPresets = new System.Collections.Generic.List<HardwarePerformancePreset>();
        using var view = new ControlCenterView(audio, hardware: hardware,
            getHardwarePresets: () => savedPresets,
            saveHardwarePresets: next => {
                savedPresets = next.Select(p => p.Copy()).ToList();
                return window.SaveHardwarePresetsAsync(savedPresets);
            });
        var host = new Window
        {
            Content = view, Width = 380, Height = 560, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000
        };
        try
        {
            host.Show();
            await hardware.DrainAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            host.UpdateLayout();

            // Switch from home strip to Custom mode
            var customHomeBtn = DisplayPowerViewVerification.Children<Button>(view)
                .Single(b => b.IsVisible && AutomationProperties.GetName(b) == "主页硬件模式：自定义");
            customHomeBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await hardware.DrainAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(controllable.Mode == HardwareFeatureCatalog.ModeCustom,
                "Home hardware mode strip switches the vendor thermal mode through the hardware channel");

            // Open detail view
            DisplayPowerViewVerification.Children<Button>(view)
                .Single(b => b.IsVisible && AutomationProperties.GetName(b) == "硬件与性能详情")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            host.UpdateLayout();

            var pl1Slider = DisplayPowerViewVerification.Children<Slider>(view)
                .Single(s => s.IsVisible && AutomationProperties.GetName(s) == "CPU 长期功耗上限 (PL1/SPL)");
            pl1Slider.Value = 85;
            DisplayPowerViewVerification.Children<Button>(view)
                .Single(b => b.IsVisible && AutomationProperties.GetName(b) == "应用自定义硬件参数")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await hardware.DrainAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(controllable.Values[0x0102FF00] == 85,
                "Applying custom sliders writes BIOS-bounded feature values and refreshes readback");

            var nameBox = DisplayPowerViewVerification.Children<TextBox>(view)
                .Single(t => t.IsVisible && AutomationProperties.GetName(t) == "性能方案名称");
            nameBox.Text = "静音渲染";
            DisplayPowerViewVerification.Children<Button>(view)
                .Single(b => b.IsVisible && AutomationProperties.GetName(b) == "保存当前硬件方案")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.WaitForSavesAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            host.UpdateLayout();
            check(window.Storage.Load().HardwarePresets.Any(p => p.Name == "静音渲染" && p.FeatureValues["0102FF00"] == 85),
                "Saving a hardware performance preset persists it to disk with its custom power parameters");

            // Reset BIOS defaults
            DisplayPowerViewVerification.Children<Button>(view)
                .Single(b => b.IsVisible && AutomationProperties.GetName(b) == "恢复 BIOS 默认")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await hardware.DrainAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(controllable.Mode == HardwareFeatureCatalog.ModeBalance && controllable.Values[0x0102FF00] == 65,
                "Reset BIOS defaults restores default feature values and returns to Balance mode");

            // Apply saved preset back
            DisplayPowerViewVerification.Children<Button>(view)
                .Single(b => b.IsVisible && AutomationProperties.GetName(b) == "应用方案：静音渲染")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await hardware.DrainAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(controllable.Mode == HardwareFeatureCatalog.ModeCustom && controllable.Values[0x0102FF00] == 85,
                "Applying a saved hardware preset restores its mode and custom power values");

            // Delete saved preset to leave test state clean
            DisplayPowerViewVerification.Children<Button>(view)
                .Single(b => b.IsVisible && AutomationProperties.GetName(b) == "删除方案：静音渲染")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.WaitForSavesAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(window.Storage.Load().HardwarePresets.Count == 0,
                "Deleting a saved hardware preset removes it cleanly from disk");
        }
        finally
        {
            host.Close();
            await window.SaveHardwarePresetsAsync([]);
        }
    }

    private sealed class ControllableHardwareBackend : IHardwareBackend
    {
        internal int Mode = HardwareFeatureCatalog.ModeBalance;
        internal bool FullSpeed;
        internal readonly System.Collections.Generic.Dictionary<uint, int> Values = new()
        {
            [0x0102FF00] = 65,
            [0x0202FF00] = 115
        };

        public void Start(Action changed) { }
        public void Stop() { }
        public void Dispose() { }

        public HardwareSnapshot Read()
        {
            var identity = new HardwareIdentity("LENOVO", "82WM", "Legion R9000P ARX8", "LENOVO", "LNVNB161216", "LPCN51WW",
                HardwareFormFactor.Laptop, "AMD Ryzen 9 7945HX", "AuthenticAMD", 16, 32, 2501,
                [new HardwareGpuInfo("NVIDIA GeForce RTX 4060 Laptop GPU", "NVIDIA", "32.0.16.1714")], 1);
            var gpu = new GpuPowerTelemetry("NVIDIA GeForce RTX 4060 Laptop GPU", 22.0, 55, 5, 140, 138.6, null, 60, 1950, 3105, 7001, 8001, null, 2);
            var vendor = HardwareCapabilityEvaluator.ClassifyVendorClasses(["LENOVO_GAMEZONE_DATA", "LENOVO_OTHER_METHOD", "LENOVO_FAN_METHOD", "LENOVO_CAPABILITY_DATA_01"]);
            var control = new HardwareControlSnapshot(
                HardwareServiceStatus.ConnectedReady,
                "Lenovo Legion WMI",
                Mode,
                SupportsCustomMode: true,
                OnAcPower: true,
                Fan1Rpm: 2400,
                Fan2Rpm: 2600,
                FanMaxRpm: 5200,
                SupportsFanFullSpeed: true,
                FanFullSpeed: FullSpeed,
                Features:
                [
                    new HardwareControlFeature(0x0102FF00, "0102FF00", "cpu-pl1", "CPU 长期功耗上限 (PL1/SPL)", "W", 30, 115, 5, 65, Values[0x0102FF00]),
                    new HardwareControlFeature(0x0202FF00, "0202FF00", "gpu-ctgp", "GPU 可配置功耗上限 (cTGP)", "W", 55, 140, 5, 115, Values[0x0202FF00])
                ]);
            var caps = HardwareCapabilityEvaluator.Evaluate(identity, gpu, vendor, control);
            return new HardwareSnapshot(identity, gpu, vendor, caps, DateTimeOffset.UtcNow, Control: control);
        }

        public void SetThermalMode(int mode) => Mode = mode;
        public void SetFeature(uint featureId, int value)
        {
            if (featureId == HardwareFeatureCatalog.FeatureFanFullSpeed) FullSpeed = value == 1;
            else Values[featureId] = value;
        }
        public void ApplyFeatureValues(System.Collections.Generic.IReadOnlyDictionary<uint, int> featureValues, int targetMode, bool? fanFullSpeed)
        {
            Mode = targetMode;
            if (targetMode == HardwareFeatureCatalog.ModeCustom)
                foreach (var pair in featureValues) Values[pair.Key] = pair.Value;
            if (fanFullSpeed.HasValue) FullSpeed = fanFullSpeed.Value;
        }
        public void ResetDefaults()
        {
            Mode = HardwareFeatureCatalog.ModeBalance;
            FullSpeed = false;
            Values[0x0102FF00] = 65;
            Values[0x0202FF00] = 115;
        }
    }

    private sealed class SilentAudioBackend : IAudioBackend
    {
        public void Start(Action<AudioChange> changed) { }
        public AudioSnapshot Read(string? outputId, string? micId, AudioChange change) => AudioSnapshot.Empty();
        public void Write(AudioTarget target, int? volume, bool? mute) { }
        public void Stop() { }
        public void Dispose() { }
    }
}
