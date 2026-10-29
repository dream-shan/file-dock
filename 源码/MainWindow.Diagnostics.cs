using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

namespace FileDockDemo;

public partial class MainWindow
{
    private Timer? _diagnosticTimer;
    private string _diagnosticSnapshot = "ui=not-sampled";
    private int _diagnosticQueued;
    private volatile bool _diagnosticsStopped;

    // Only reads state on the UI thread. No file names, paths or search text.
    internal string DiagnosticSnapshot() => FormattableString.Invariant(
        $"uiTick={Environment.TickCount64}; panelVisible={IsVisible}; collapsed={_isCollapsed}; quitting={_quitting}; dockVisible={_dock?.Visible}; dockDisposed={_dock?.IsDisposed}; dockX={_dock?.Left:0}; dockY={_dock?.Top:0}; captureActive={_captureActive}; captureSurfaces={_captureSurfaces.Count}; capturePending={Environment.TickCount64 < _capturePendingUntil}; foregroundQueued={_foregroundUpdateQueued}; moving={_movingPanel}; dragging={_dragging}; folderView={CurrentFolderPath != null}; watcher={_folderWatcher != null}; sampling={_performanceActive}; sampleBusy={_performanceBusy}");

    internal void RecordDiagnosticState(string eventName)
    {
        if (_diagnosticsStopped) return;
        try
        {
            string snapshot = DiagnosticSnapshot();
            Volatile.Write(ref _diagnosticSnapshot, snapshot);
            DiagnosticLog.Write(eventName, snapshot);
        }
        catch (Exception ex) { DiagnosticLog.Error("snapshot-error", ex); }
    }

    private void QueueDiagnosticState()
    {
        if (_diagnosticsStopped || Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _diagnosticQueued, 1) != 0) return;
        try
        {
            Dispatcher.BeginInvoke(() => {
                Interlocked.Exchange(ref _diagnosticQueued, 0);
                RecordDiagnosticState("ui-state");
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _diagnosticQueued, 0);
            DiagnosticLog.Error("snapshot-dispatch-error", ex);
        }
    }

    private void DiagnosticSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        DiagnosticLog.Write("session-switch", e.Reason.ToString()); QueueDiagnosticState();
    }
    private void DiagnosticPowerChange(object sender, PowerModeChangedEventArgs e)
    {
        DiagnosticLog.Write("power-change", e.Mode.ToString()); QueueDiagnosticState();
    }
    private void DiagnosticVisibilityChanged(object? sender, DependencyPropertyChangedEventArgs e) => RecordDiagnosticState("panel-visibility");
    private void DiagnosticDockVisibilityChanged(object? sender, EventArgs e) => RecordDiagnosticState("dock-visibility");

    private void InitializeDiagnostics()
    {
        IsVisibleChanged += DiagnosticVisibilityChanged;
        if (_dock != null) _dock.VisibleChanged += DiagnosticDockVisibilityChanged;
        RecordDiagnosticState("window-ready");
        if (App.IsSelfTest) return;
        try
        {
            SystemEvents.SessionSwitch += DiagnosticSessionSwitch;
            SystemEvents.PowerModeChanged += DiagnosticPowerChange;
        }
        catch (Exception ex) { DiagnosticLog.Error("session-observer-error", ex); }
        // A worker heartbeat continues if the UI stops responding. Only one
        // dispatcher probe can be pending, so a hang cannot grow the queue.
        _diagnosticTimer = new Timer(_ => {
            if (_diagnosticsStopped) return;
            DiagnosticLog.Write("heartbeat", Volatile.Read(ref _diagnosticSnapshot));
            QueueDiagnosticState();
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private void DisposeDiagnostics()
    {
        RecordDiagnosticState("window-dispose");
        _diagnosticsStopped = true;
        _diagnosticTimer?.Dispose(); _diagnosticTimer = null;
        SystemEvents.SessionSwitch -= DiagnosticSessionSwitch;
        SystemEvents.PowerModeChanged -= DiagnosticPowerChange;
        IsVisibleChanged -= DiagnosticVisibilityChanged;
        if (_dock != null) _dock.VisibleChanged -= DiagnosticDockVisibilityChanged;
    }
}
