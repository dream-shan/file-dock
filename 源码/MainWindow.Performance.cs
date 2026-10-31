using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace FileDockDemo;

public partial class MainWindow
{
    private readonly PerformanceService _performanceService = new();
    private DispatcherTimer? _performanceTimer;
    private bool _performanceActive, _performanceBusy, _performanceDisposed, _resetPerformanceBaseline, _testPerformanceEnabled;
    private int _performanceGeneration;
    private Task<PerformanceSnapshot>? _performanceWorker;
    internal int PerformanceSamples => _performanceService.SampleCount;
    internal bool PerformanceActive => _performanceActive;
    internal PerformanceSnapshot? LastPerformanceSnapshot { get; private set; }

    private void InitializePerformance()
    {
        InitializeMemory();
        _performanceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _performanceTimer.Tick += async (_, _) => await SamplePerformanceAsync();
        IsVisibleChanged += (_, _) => UpdatePerformanceActivity();
    }
    internal void EnablePerformanceForVerification() { _testPerformanceEnabled = true; UpdatePerformanceActivity(); }
    internal void UpdatePerformanceActivity()
    {
        if (_performanceTimer == null || _performanceDisposed) return;
        bool active = IsVisible && !IsFilesPage && !_quitting && !MemoryPaused && (!App.IsSelfTest || _testPerformanceEnabled);
        if (_performanceActive == active) { UpdateMemoryActivity(); return; }
        _performanceActive = active; ++_performanceGeneration;
        UpdateMemoryActivity();
        if (!active) { _performanceTimer.Stop(); return; }
        _resetPerformanceBaseline = true;
        CpuCard.Reset(); GpuCard.Reset(); MemoryCard.Reset(); VramCard.Reset();
        NetworkDownload.Text = "↓ —"; NetworkUpload.Text = "↑ —"; PerformanceHint.Text = "正在读取…";
        _performanceTimer.Start(); _ = SamplePerformanceAsync();
    }
    private async Task SamplePerformanceAsync()
    {
        if (!_performanceActive || _performanceBusy || _performanceDisposed) return;
        _performanceBusy = true;
        int generation = _performanceGeneration;
        bool reset = _resetPerformanceBaseline; _resetPerformanceBaseline = false;
        try
        {
            _performanceWorker = Task.Run(() => _performanceService.Sample(reset));
            var snapshot = await _performanceWorker;
            if (!_performanceActive || _performanceDisposed || generation != _performanceGeneration) return;
            LastPerformanceSnapshot = snapshot;
            CpuCard.Set(snapshot.CpuLoad, "", snapshot.CpuTemperature, true);
            CpuCard.ToolTip = snapshot.TemperatureStatus;
            GpuCard.Set(snapshot.GpuLoad, "", snapshot.GpuTemperature, true);
            GpuCard.ToolTip = snapshot.GpuUsageStatus;
            ApplyMemoryReading(new MemoryReading(snapshot.MemoryUsed,snapshot.MemoryTotal));
            VramCard.Set(Percent(snapshot.VramUsed, snapshot.VramTotal), Capacity(snapshot.VramUsed, snapshot.VramTotal));
            NetworkDownload.Text = "↓ " + FormatRate(snapshot.Download);
            NetworkUpload.Text = "↑ " + FormatRate(snapshot.Upload);
            NetworkBar.ToolTip = "活动物理网络接口实时收发：" + snapshot.NetworkName + "\n仅统计已有流量，不主动测速";
            PerformanceHint.Text = "实时更新 · 1 秒";
        }
        catch (Exception ex)
        {
            if (_performanceActive && generation == _performanceGeneration) { PerformanceHint.Text = "读取暂不可用"; PerformanceHint.ToolTip = ex.Message; }
        }
        finally { _performanceBusy = false; }
    }
    private static double? Percent(ulong? used, ulong? total) => used.HasValue && total is > 0 ? Math.Clamp(used.Value * 100d / total.Value, 0, 100) : null;
    private static string Capacity(ulong? used, ulong? total) => used.HasValue && total is > 0 ? $"{used.Value / 1073741824d:0.0} / {total.Value / 1073741824d:0.#} GB" : "暂不可用";
    internal static string FormatRate(double? rate) => rate switch
    {
        null => "—",
        < 1024 => $"{rate:0} B/s",
        < 1048576 => $"{rate / 1024:0.0} KB/s",
        _ => $"{rate / 1048576:0.0} MB/s"
    };
    private void DisposePerformance()
    {
        if (_performanceDisposed) return;
        DisposeMemory();
        _performanceDisposed = true; _performanceActive = false; ++_performanceGeneration;
        _performanceTimer?.Stop();
        // Close native handles only after the single in-flight sample completes.
        if (_performanceWorker is { IsCompleted: false } worker)
            _ = worker.ContinueWith(_ => _performanceService.Dispose(), TaskScheduler.Default);
        else _performanceService.Dispose();
    }
}
