using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace FileDockDemo;

internal static class StabilityVerification
{
    internal static async Task Run(MainWindow window, string root, Action<bool, string> check)
    {
        string data = Path.Combine(root, "future-version");
        var storage = new StorageService(data);
        storage.Save(new AppState()); storage.Save(new AppState());
        string path = Path.Combine(data, "state.json");
        string future = "{\"Version\":2,\"State\":{\"Pins\":[]}}";
        File.WriteAllText(path, future);
        bool refused = false;
        try { storage.Load(); } catch (NotSupportedException) { refused = true; }
        check(refused && File.ReadAllText(path) == future,
            "Newer configuration versions are refused without restoring an older backup over them");
        File.WriteAllText(path, "{broken");
        check(storage.Load().Pins.Count == 0, "Corrupt current configuration still recovers from its valid backup");

        var original = window.State.Pins;
        try
        {
            window.State.Pins = new() { new PinnedEntry { Path = root, DisplayName = "fixture" } };
            await window.LoadKindsAsync(new[] { root });
            check(window.CachedPathKindCount == 1, "Metadata cache contains only active favorite paths");
            window.State.Pins.Clear();
            await window.LoadKindsAsync(Array.Empty<string>());
            check(window.CachedPathKindCount == 0, "Removing all favorites evicts metadata even when no new paths need probing");
        }
        finally { window.State.Pins = original; window.ApplyFilters(); }

        window.GoHome(); window.CollapsePanel();
        await Task.Delay(1500);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        long privateBefore = process.PrivateMemorySize64;
        int handlesBefore = process.HandleCount;
        long retainedBefore = GC.GetTotalMemory(true);
        for (int i = 0; i < 60; i++)
        {
            window.ExpandPanel();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.CollapsePanel();
            await Task.Delay(40);
            if (i == 29)
            {
                await Task.Delay(1500);
                process.Refresh();
                privateBefore = process.PrivateMemorySize64;
                handlesBefore = process.HandleCount;
                retainedBefore = GC.GetTotalMemory(true);
            }
        }
        await Task.Delay(1500);
        int stopped = window.PerformanceSamples;
        process.Refresh(); var cpuBefore = process.TotalProcessorTime; var elapsed = Stopwatch.StartNew();
        await Task.Delay(5000);
        process.Refresh();
        double cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds / elapsed.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
        check(!window.PerformanceActive && stopped == window.PerformanceSamples,
            "Sixty expand-collapse cycles leave no background performance sampling active");
        check(ImageLoader.CacheCount <= 64, "Image cache remains bounded after lifecycle stress");
        long retainedAfter = GC.GetTotalMemory(true);
        File.WriteAllText(Path.Combine(root, "resource-check.txt"),
            $"Warmup cycles: 30; measured cycles: 30\nCollapsed sample seconds: {elapsed.Elapsed.TotalSeconds:F2}\nCPU percent of whole machine: {cpu:F4}\n" +
            $"Private bytes before/after: {privateBefore}/{process.PrivateMemorySize64}\n" +
            $"Handles before/after: {handlesBefore}/{process.HandleCount}\nManaged retained bytes before/after: {retainedBefore}/{retainedAfter}\n" +
            "Short controlled sample, not a long-term leak proof. Managed retained memory uses explicit collections only in this test.\n");
        window.ExpandPanel();
    }
}
