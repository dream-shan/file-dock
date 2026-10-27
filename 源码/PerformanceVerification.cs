using System;
using System.IO;
using System.Linq;
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
        const string adapter = "luid_0x00000000_0x000108f9";
        check(GpuEngineMetrics.IsPhysicalAddress(1, 0, 0) && !GpuEngineMetrics.IsPhysicalAddress(uint.MaxValue, 65535, 65535),
            "Remote display aliases with a sentinel PCI address cannot impersonate the physical GPU");
        string Instance(int pid, int engine, string type, string identity = adapter) => $"pid_{pid}_{identity}_phys_0_eng_{engine}_engtype_{type}";
        var gpu = GpuEngineMetrics.Aggregate(new[] {
            new GpuEngineMetrics.Reading(Instance(1, 0, "3D"), 12),
            new GpuEngineMetrics.Reading(Instance(2, 0, "3D"), 13),
            new GpuEngineMetrics.Reading(Instance(1, 6, "VideoEncode"), 88),
            new GpuEngineMetrics.Reading(Instance(3, 1, "3D", "luid_0x00000000_0x000124f0"), 99)
        }, adapter);
        check(gpu.Percent == 88 && gpu.Engine == "VideoEncode", "GPU includes encoding engines, selects busiest engine, and excludes other adapters");
        gpu = GpuEngineMetrics.Aggregate(new[] {
            new GpuEngineMetrics.Reading(Instance(1, 0, "3D"), 35),
            new GpuEngineMetrics.Reading(Instance(2, 0, "3D"), 40),
            new GpuEngineMetrics.Reading(Instance(1, 6, "VideoEncode"), 30)
        }, adapter);
        check(gpu.Percent == 75, "GPU sums processes sharing an engine without summing independent engines");
        gpu = GpuEngineMetrics.Aggregate(new[] {
            new GpuEngineMetrics.Reading(Instance(1, 2, "Copy"), 60),
            new GpuEngineMetrics.Reading(Instance(1, 3, "Copy"), 55)
        }, adapter);
        check(gpu.Percent == 60, "Distinct engines with the same type stay separate");
        check(GpuEngineMetrics.Aggregate(new[] {new GpuEngineMetrics.Reading(Instance(1, 0, "3D"), 0)}, adapter).Percent == 0,
            "A valid idle GPU remains zero rather than unavailable");
        check(GpuEngineMetrics.Aggregate(new[] {
            new GpuEngineMetrics.Reading(Instance(1, 0, "3D"), double.NaN),
            new GpuEngineMetrics.Reading(Instance(2, 0, "3D"), -1),
            new GpuEngineMetrics.Reading(Instance(3, 0, "3D"), 99, 0xc0000bc6)
        }, adapter).Percent == null, "Missing or invalid GPU counters do not fabricate a reading or fall back to a different metric");
        check(GpuEngineMetrics.Aggregate(new[] {new GpuEngineMetrics.Reading(Instance(1, 0, "3D"), 120, 1)}, adapter).Percent == 100,
            "New valid GPU counter data is accepted and aggregate is bounded");
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
        int refreshBaseline = window.PerformanceSamples;
        await Task.Delay(2200);
        check(window.PerformanceSamples - refreshBaseline is >= 2 and <= 3, "Visible metrics refresh once per second without blocking the UI");
        if (Environment.GetEnvironmentVariable("FILEDOCK_REQUIRE_GPU") == "1")
            check(window.LastPerformanceSnapshot is { GpuLoad: >= 0 and <= 100 } && window.LastPerformanceSnapshot.GpuUsageStatus.Contains("Windows GPU 最繁忙引擎"),
                "Real GPU load comes from matched Windows engine counters");
        window.Width = 430; window.Height = 650; window.UpdateLayout();
        check(window.PerformanceScroll.ExtentHeight <= window.PerformanceScroll.ViewportHeight + 1,
            "All four performance cards fit the default window without scrolling");
        check(window.MemoryCard.Ring && window.VramCard.Ring && !window.CpuCard.Ring && !window.GpuCard.Ring,
            "Only memory and VRAM use the ring layout; CPU and GPU retain their trend cards");
        check(window.MemoryCard.RingChart.Percent is >= 0 and <= 100 && window.MemoryCard.DetailText.Contains("GB") && window.MemoryCard.CapacityFits && window.VramCard.CapacityFits,
            "Memory ring receives real utilization and both capacity labels fit the default card width");
        check(window.CpuCard.Graph.ActualHeight is >= 18 and <= 60 && window.GpuCard.Graph.ActualHeight == window.CpuCard.Graph.ActualHeight,
            "CPU and GPU use equally enlarged charts within the existing default card layout");
        var savedPins=window.State.Pins;
        window.State.Pins=savedPins.Take(2).ToList(); window.ApplyFilters(); window.UpdateLayout();
        check(window.CpuCard.Graph.ActualHeight is >= 40 and <= 60 && window.PerformanceScroll.ExtentHeight <= window.PerformanceScroll.ViewportHeight+1,
            "One row of favorites gives the enlarged chart forty-to-sixty pixels without enlarging the window");
        window.State.Pins=savedPins; window.ApplyFilters(); window.UpdateLayout();
        check(window.CpuCard.DetailText == "最近 60 秒" && window.GpuCard.DetailText == "最近 60 秒" &&
            !System.Windows.Automation.AutomationProperties.GetName(window.CpuCard).Contains(snapshot!.CpuName),
            "CPU and GPU replace model text with a sixty-second caption and accessible readings");
        DemoVerification.SaveRender(window, Path.Combine(root, "performance-standard.png"));
        window.Width = 380; window.Height = 480; window.UpdateLayout();
        check(window.CpuCard.Graph.ActualHeight >= 12 && window.GpuCard.Graph.ActualHeight >= 12 &&
            window.MemoryCard.ValueText != "—", "Compact cards keep a visible chart and memory readout");
        check(window.MemoryCard.CapacityFits && window.VramCard.CapacityFits && window.MemoryCard.RingChart.ActualWidth <= 50,
            "Compact ring cards keep their complete capacity text without expanding the cards");
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
        for (int i = 0; i < 200; i++) window.CpuCard.Set(i, "test", 50, true);
        check(window.CpuCard.HistoryCount == PerformanceGraph.MaximumSamples, "Performance graph history stays bounded at one minute of one-second samples");
        var graph = new PerformanceGraph { Trend=true, MetricLabel="CPU" };
        graph.Add(10); graph.Add(null); graph.Add(70);
        check(graph.HoverText(59,59).Contains("70%") && graph.HoverText(58,59).Contains("暂不可用") && graph.HoverText(0,59).Contains("暂无采样"),
            "Hover maps historical slots to exact values, gaps and not-yet-sampled periods");
        graph.Add(double.NaN); graph.Add(double.PositiveInfinity);
        check(graph.HoverText(59,59).Contains("暂不可用"), "Non-finite samples create gaps rather than misleading chart points");
        graph.Add(150); check(graph.HoverText(59,59).Contains("100%"), "Out-of-range graph samples are bounded to the percent scale");
        check(PerformanceGraph.PlotY(0,60) == 57 && PerformanceGraph.PlotY(50,60) == 30 && PerformanceGraph.PlotY(100,60) == 3,
            "Chart coordinates use a fixed zero-to-one-hundred scale");
        graph.Clear(); check(graph.HistoryCount == 0 && graph.HoverText(59,59).Contains("暂无采样"), "Reset clears chart history and stale hover values");
        var ring = new PerformanceRing { Width=50,Height=50,Accent=System.Windows.Media.Brushes.Red,Track=System.Windows.Media.Brushes.Blue };
        ring.Measure(new Size(50,50)); ring.Arrange(new Rect(0,0,50,50));
        byte[] RingPixels(double? percent)
        {
            ring.Set(percent); ring.UpdateLayout();
            var image=new System.Windows.Media.Imaging.RenderTargetBitmap(50,50,96,96,System.Windows.Media.PixelFormats.Pbgra32);
            image.Render(ring); var pixels=new byte[50*50*4]; image.CopyPixels(pixels,50*4,0); return pixels;
        }
        bool IsRed(byte[] pixels,int x,int y) {int p=(y*50+x)*4; return pixels[p+2]>180 && pixels[p]<50;}
        var quarter=RingPixels(25);
        check(IsRed(quarter,25,3) && IsRed(quarter,47,25) && !IsRed(quarter,25,47) && !IsRed(quarter,3,25),
            "Quarter ring starts at the top and advances clockwise to the right");
        var empty=RingPixels(0); var full=RingPixels(100);
        check(!IsRed(empty,25,3) && IsRed(full,25,3) && IsRed(full,25,47) && IsRed(full,3,25),
            "Zero utilization has no accent arc and full utilization draws a complete circle");
        ring.Set(double.NaN); check(ring.Percent == null && PerformanceRing.Normalize(double.PositiveInfinity) == null,
            "Unavailable ring values remain unknown instead of displaying a false zero");
        var unavailable=new PerformanceCard { Ring=true }; unavailable.Set(null,"暂不可用");
        check(unavailable.ValueText == "—" && unavailable.RingChart.Percent == null && unavailable.DetailText == "暂不可用",
            "Missing memory data keeps the dash and unavailable capacity text");
        window.CollapsePanel(); window.ExpandPanel(); await Task.Delay(2500);
        string originalTheme=window.State.ThemeMode;
        window.State.Pins=savedPins.Take(2).ToList(); window.ApplyFilters(); window.UpdateLayout();
        window.CpuCard.Reset(); window.GpuCard.Reset();
        for(int i=0;i<60;i++)
        {
            window.CpuCard.Set(12+7*Math.Sin(i*.35), "hidden model", 72,true);
            window.GpuCard.Set(20+(i%20)*3.5,"hidden model",58,true);
        }
        foreach(string theme in new[]{"Light","Dark"})
        {
            window.SetThemeMode(theme); window.UpdateLayout();
            DemoVerification.SaveRender(window,Path.Combine(root,"trends-"+theme.ToLowerInvariant()+".png"));
        }
        window.SetThemeMode(originalTheme);
        window.State.Pins=savedPins; window.ApplyFilters();
        window.CollapsePanel(); window.ExpandPanel(); await Task.Delay(1200);
    }
}



