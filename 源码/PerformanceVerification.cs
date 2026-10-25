using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace FileDockDemo;

internal static class PerformanceVerification
{
    internal static async Task Run(MainWindow window, string root, Action<bool, string> check)
    {
        check(Math.Abs(PerformanceService.CalculateCpuLoad(150, 400, 300, 100, 200, 100)!.Value - 87.5) < .01,
            "CPU load subtracts idle time from kernel plus user delta");
        check(PerformanceService.CalculateCpuLoad(0, 0, 0, 100, 200, 100) == null,
            "Reset CPU counters do not produce false utilization");
        check(PerformanceService.ByteRate(5000, 1000, 2) == 2000 && PerformanceService.ByteRate(500, 1000, 2) == null,
            "Network rates use elapsed time and reject counter resets");
        check(PerformanceService.ValidTemperature(0) == null && PerformanceService.ValidTemperature(double.NaN) == null && PerformanceService.ValidTemperature(999) == null,
            "Invalid temperatures stay unavailable rather than showing fabricated readings");
        check(NetworkTraffic.IsPhysicalTrafficInterface(1, 71, 1) && !NetworkTraffic.IsPhysicalTrafficInterface(3, 71, 1) && !NetworkTraffic.IsPhysicalTrafficInterface(0, 6, 1), "Network counters exclude filter and virtual interfaces");
        string legacy = Path.Combine(root, "legacy-state"); Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "state.json"), "{\"Version\":1,\"State\":{\"Pins\":[{\"Id\":\"kept\",\"Path\":\"C:\\\\example.txt\",\"DisplayName\":\"kept\",\"Order\":0}],\"History\":[],\"HiddenRecent\":[],\"DockSide\":\"Left\",\"DockTop\":120,\"KeepOpen\":true}}");
        var storage = new StorageService(legacy); var state = storage.Load(); storage.Save(state);
        check(state.Pins.Count == 1 && state.DockSide == "Left" && state.KeepOpen && !File.ReadAllText(Path.Combine(legacy, "state.json")).Contains("History"),
            "Old configuration migrates pins and settings without retaining removed history");
        check(window.FindName("RecentSection") == null && window.FindName("DropZone") == null && window.AddButton != null,
            "Home removes recent usage and external drop zone while keeping Add");
        window.GoHome(); window.ExpandPanel(); window.EnablePerformanceForVerification();
        for (int i = 0; i < 150 && window.LastPerformanceSnapshot?.CpuLoad == null; i++) await Task.Delay(100);
        var snapshot = window.LastPerformanceSnapshot;
        check(snapshot is { CpuLoad: >= 0 and <= 100, MemoryTotal: > 0 } && window.PerformanceActive,
            "Visible home samples real CPU and memory data off the UI thread");
        File.WriteAllText(Path.Combine(root, "performance-sample.json"), JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        bool requireTemperature = Environment.GetEnvironmentVariable("FILEDOCK_REQUIRE_TEMPERATURE") == "1";
        if (requireTemperature)
            check(snapshot?.CpuTemperature is > 0 and <= 130, "Installed service returns a real CPU temperature to an unelevated UI");
        window.Width = 430; window.Height = 650; window.UpdateLayout();
        check(window.PerformanceScroll.ExtentHeight <= window.PerformanceScroll.ViewportHeight + 1,
            "All four performance cards fit the default window without scrolling");
        DemoVerification.SaveRender(window, Path.Combine(root, "performance-standard.png"));
        window.Width = 380; window.Height = 480; window.UpdateLayout();
        DemoVerification.SaveRender(window, Path.Combine(root, "performance-small.png"));
        window.Width = 430; window.Height = 650;

        window.CollapsePanel(); await Task.Delay(700); int paused = window.PerformanceSamples;
        long serviceSamples = requireTemperature ? TemperatureClient.Read(false).Samples : 0;
        await Task.Delay(2400);
        if (requireTemperature) check(serviceSamples == TemperatureClient.Read(false).Samples, "Temperature service performs no sampling while the panel is collapsed");
        check(!window.PerformanceActive && paused == window.PerformanceSamples, "Collapsed panel stops sampling");
        window.ExpandPanel(); await Task.Delay(2500);
        check(window.PerformanceSamples > paused, "Expanding home resumes sampling with a new baseline");
        await window.NavigateToFolderAsync(root); await Task.Delay(700); paused = window.PerformanceSamples; await Task.Delay(2400);
        check(!window.PerformanceActive && paused == window.PerformanceSamples, "Folder browsing pauses performance sampling");
        window.GoHome(); await Task.Delay(2400);
        check(window.PerformanceActive && window.PerformanceSamples > paused, "Returning home resumes performance sampling");
        for (int i = 0; i < 100; i++) window.CpuCard.Set(i, "test", 50, true);
        check(window.CpuCard.HistoryCount == 30, "Performance graph history remains bounded at thirty samples");
        window.CollapsePanel(); window.ExpandPanel(); await Task.Delay(2500);
    }
}



