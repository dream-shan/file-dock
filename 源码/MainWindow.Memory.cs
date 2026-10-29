using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FileDockDemo;

public partial class MainWindow
{
    private DispatcherTimer? _memoryTimer;
    private bool _memorySessionLocked, _memoryDisconnected, _memorySuspended, _memoryDisposed, _memoryTestEnabled;
    private readonly MemoryActionGate _memoryAction = new();
    private readonly CancellationTokenSource _memoryCleanupCancellation = new();
    private SolidColorBrush? _memoryBrush;
    private double? _memoryPercent;
    private bool _memoryWarning;
    private uint _memoryProtectedPid;
    internal int MemoryReadCount { get; private set; }
    internal bool MemoryOnlyActive => _memoryTimer?.IsEnabled == true;
    internal bool MemoryPaused => _memorySessionLocked || _memoryDisconnected || _memorySuspended;
    internal Func<Task<MemoryCleanupResult>>? MemoryCleanupForVerification;

    private void InitializeMemory()
    {
        RememberMemoryForeground();
        MemoryCard.IsAction = true;
        MemoryCard.ActionRequested += async (_,_) => await OrganizeMemoryAsync();
        _memoryTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval=TimeSpan.FromSeconds(2) };
        _memoryTimer.Tick += (_,_) => ReadMemoryOnly();
        if (!App.IsSelfTest)
        {
            SystemEvents.SessionSwitch += MemorySessionSwitch;
            SystemEvents.PowerModeChanged += MemoryPowerChanged;
        }
        UpdateMemoryActionHint();
    }
    private void MemorySessionSwitch(object sender, SessionSwitchEventArgs e) => Dispatcher.BeginInvoke(() => {
        if (_memoryDisposed) return;
        if (e.Reason == SessionSwitchReason.SessionLock) _memorySessionLocked=true;
        if (e.Reason == SessionSwitchReason.SessionUnlock) _memorySessionLocked=false;
        if (e.Reason is SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.SessionLogoff) _memoryDisconnected=true;
        if (e.Reason is SessionSwitchReason.RemoteConnect or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.SessionLogon) _memoryDisconnected=false;
        UpdatePerformanceActivity();
    });
    private void MemoryPowerChanged(object sender, PowerModeChangedEventArgs e) => Dispatcher.BeginInvoke(() => {
        if (_memoryDisposed) return;
        if (e.Mode == PowerModes.Suspend) _memorySuspended=true;
        if (e.Mode == PowerModes.Resume) _memorySuspended=false;
        UpdatePerformanceActivity();
    });
    internal void SetMemoryPauseForVerification(bool paused)
    {
        _memorySessionLocked=paused; UpdatePerformanceActivity();
    }
    internal void EnableMemoryForVerification() { _memoryTestEnabled=true; UpdateMemoryActivity(); }
    private void UpdateMemoryActivity()
    {
        if (_memoryTimer==null || _memoryDisposed) return;
        bool active = !_quitting && !MemoryPaused && !_performanceActive && (!App.IsSelfTest || _memoryTestEnabled);
        if (active == _memoryTimer.IsEnabled) return;
        if (active) { ReadMemoryOnly(); _memoryTimer.Start(); } else _memoryTimer.Stop();
    }
    private void ReadMemoryOnly()
    {
        if (_memoryDisposed || MemoryPaused || _quitting) return;
        var memory=MemoryTools.Read(); MemoryReadCount++;
        ApplyMemoryReading(memory);
    }
    internal void ApplyMemoryReading(MemoryReading memory)
    {
        _memoryPercent=memory.Percent;
        _memoryWarning=MemoryPressure.Warning(_memoryWarning,_memoryPercent);
        MemoryCard.Set(_memoryPercent, Capacity(memory.Used,memory.Total));
        ApplyMemoryAppearance();
    }
    private void ApplyMemoryAppearance(bool animate=true)
    {
        if (MemoryCard==null) return;
        Color next=_memoryPercent.HasValue ? MemoryPressure.ColorFor(_memoryPercent.Value,IsDarkTheme) : ThemePalette.Brush("MemoryAccent").Color;
        if (_memoryBrush==null) { _memoryBrush=new SolidColorBrush(next); }
        else if (_memoryBrush.Color != next)
        {
            Color current=_memoryBrush.Color;
            _memoryBrush.BeginAnimation(SolidColorBrush.ColorProperty,null);
            _memoryBrush.Color=next;
            if (animate && MemoryCard.IsVisible) _memoryBrush.BeginAnimation(SolidColorBrush.ColorProperty,new ColorAnimation(current,next,TimeSpan.FromMilliseconds(300)) { FillBehavior=FillBehavior.Stop });
        }
        MemoryCard.Accent=_memoryBrush;
        MemoryCard.Notice=_memoryWarning ? "占用偏高" : "";
        _dock?.SetMemoryPressure(_memoryPercent,animate);
        UpdateMemoryActionHint();
    }
    private void UpdateMemoryActionHint()
    {
        MemoryCard.ActionAvailable=!_memoryAction.Busy && !MemoryPaused && !_quitting && !_captureActive;
        MemoryCard.ActionHint=_memoryAction.Busy ? "正在整理…" : "点击整理内存";
        MemoryCard.ToolTip=MemoryCard.ActionHint+"\n清理图片缓存并尝试整理当前用户后台应用工作集。\n可能短暂变慢，实际占用可能回升；不会关闭应用。";
    }
    internal bool HandleMemoryActionKey(KeyEventArgs e)
    {
        if (!MemoryCard.IsKeyboardFocused || e.Key is not (Key.Enter or Key.Space)) return false;
        MemoryCard.RequestAction(); e.Handled=true; return true;
    }
    private void RememberMemoryForeground()
    {
        uint foreground=MemoryTools.ForegroundPid();
        if (foreground!=0 && foreground!=(uint)Environment.ProcessId) _memoryProtectedPid=foreground;
    }
    internal async Task OrganizeMemoryAsync()
    {
        if (_quitting || _memoryDisposed || MemoryPaused || _captureActive || !_memoryAction.TryStart()) return;
        UpdateMemoryActionHint(); SetStatus("正在整理内存…");
        try
        {
            await MemoryCard.RingChart.BeginOperationAsync(_memoryCleanupCancellation.Token);
            ImageLoader.InvalidateThumbnails();
            uint protectedPid=_memoryProtectedPid;
            var result = MemoryCleanupForVerification!=null ? await MemoryCleanupForVerification() :
                await Task.Run(()=>MemoryTools.Cleanup(protectedPid,_memoryCleanupCancellation.Token));
            if (_memoryDisposed || _quitting) return;
            ApplyMemoryReading(result.After);
            string change=result.Before.Percent.HasValue && result.After.Percent.HasValue ? $"{result.Before.Percent:0.#}% → {result.After.Percent:0.#}%" : "读数暂不可用";
            SetStatus($"整理后内存：{change} · 已整理 {result.Trimmed} 个后台进程，效果可能回升");
            DiagnosticLog.Write("memory-organized",$"trimmed={result.Trimmed}; skipped={result.Skipped}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { DiagnosticLog.Error("memory-organize-error",ex); if (!_memoryDisposed) SetStatus("内存整理未完成，可稍后重试。",true); }
        finally
        {
            try {
                if (!_memoryDisposed) await MemoryCard.RingChart.FinishOperationAsync(_memoryCleanupCancellation.Token);
                else MemoryCard.RingChart.AbortOperation();
            }
            catch (OperationCanceledException) { MemoryCard.RingChart.AbortOperation(); }
            finally { _memoryAction.Complete(); if (!_memoryDisposed) UpdateMemoryActionHint(); }
        }
    }
    private void DisposeMemory()
    {
        _memoryDisposed=true; _memoryTimer?.Stop();
        _memoryCleanupCancellation.Cancel();
        SystemEvents.SessionSwitch-=MemorySessionSwitch; SystemEvents.PowerModeChanged-=MemoryPowerChanged;
    }
}
