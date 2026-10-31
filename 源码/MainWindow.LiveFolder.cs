using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace FileDockDemo;

public partial class MainWindow
{
    private FileSystemWatcher? _folderWatcher;
    private DispatcherTimer? _folderRefreshTimer;
    private CancellationTokenSource? _liveRefreshCancellation;
    private int _watchGeneration;
    private int _watchNotificationQueued;
    private bool _liveDirty, _liveBusy, _keepFolderResultsDuringRefresh;
    internal bool LiveFolderUpdatesEnabled = !App.IsSelfTest;
    internal bool FolderWatcherActive => _folderWatcher != null;
    internal int LiveFolderRefreshCount { get; private set; }

    private void InitializeLiveFolder()
    {
        _folderRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _folderRefreshTimer.Tick += async (_, _) => await RefreshLiveFolderAsync();
        IsVisibleChanged += (_, _) => UpdateFolderWatcher(refresh: IsVisible);
        Activated += (_, _) => UpdateFolderWatcher(refresh: true);
        SearchBox.TextChanged += (_, _) => UpdateFolderWatcher();
    }

    private void StopFolderWatcher()
    {
        ++_watchGeneration;
        Interlocked.Exchange(ref _watchNotificationQueued,0);
        _folderWatcher?.Dispose(); _folderWatcher = null;
        _folderRefreshTimer?.Stop(); _liveDirty = false;
        _liveRefreshCancellation?.Cancel();
    }

    private void UpdateFolderWatcher(bool refresh = false)
    {
        if (!LiveFolderUpdatesEnabled || _quitting || !IsVisible || !IsFilesPage || CurrentFolderPath == null)
        { StopFolderWatcher(); return; }
        bool recursive = SearchBox.Text.Trim().Length > 0;
        if (_folderWatcher == null || _folderWatcher.Path != CurrentFolderPath || _folderWatcher.IncludeSubdirectories != recursive)
        {
            StopFolderWatcher();
            try
            {
                int generation = _watchGeneration;
                var watcher = new FileSystemWatcher(CurrentFolderPath) {
                    IncludeSubdirectories = recursive,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes
                };
                _folderWatcher = watcher;
                void Changed(object? sender, FileSystemEventArgs e)
                {
                    if (Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _watchNotificationQueued,1) != 0) return;
                    Dispatcher.BeginInvoke(() => {
                        Interlocked.Exchange(ref _watchNotificationQueued,0);
                        if (generation == _watchGeneration && !_quitting) QueueFolderRefresh();
                    }, DispatcherPriority.Background);
                }
                watcher.Created += Changed; watcher.Deleted += Changed; watcher.Changed += Changed; watcher.Renamed += (s,e) => Changed(s,e);
                watcher.Error += (_, _) => Dispatcher.BeginInvoke(() => {
                    if (generation != _watchGeneration || _quitting) return;
                    StopFolderWatcher(); UpdateFolderWatcher(); QueueFolderRefresh();
                }, DispatcherPriority.Background);
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _folderWatcher?.Dispose(); _folderWatcher = null;
                SetStatus("此目录暂不能自动同步，可使用刷新按钮或 F5。", true);
            }
        }
        if (refresh) QueueFolderRefresh();
    }

    private void QueueFolderRefresh()
    {
        _liveDirty = true;
        _folderRefreshTimer?.Start(); // Do not reset: continuous writes must not postpone updates forever.
    }

    private async Task RefreshLiveFolderAsync()
    {
        if (!_liveDirty) { _folderRefreshTimer?.Stop(); return; }
        if (_liveBusy || _folderLoading || _dragging || _dragEntry != null || _movingPanel) return;
        if (!LiveFolderUpdatesEnabled || !IsVisible || !IsFilesPage || CurrentFolderPath == null || _quitting) { StopFolderWatcher(); return; }
        _liveDirty = false; _liveBusy = true;
        int generation = _watchGeneration;
        string root = CurrentFolderPath, query = SearchBox.Text.Trim();
        using var cancellation = new CancellationTokenSource();
        _liveRefreshCancellation = cancellation;
        var token = cancellation.Token;
        bool Current() => !token.IsCancellationRequested && generation == _watchGeneration && CurrentFolderPath == root && SearchBox.Text.Trim() == query && IsVisible && IsFilesPage && !_quitting;
        var scroll = VisualChildren(FolderList).OfType<ScrollViewer>().FirstOrDefault();
        double offset = scroll?.VerticalOffset ?? 0;
        try
        {
            var items = await FolderBrowserService.LoadAsync(root, token);
            if (!Current()) return;
            ImageLoader.InvalidateThumbnails();
            _folderEntries = items.Select(item => {
                var view = MakeView(item.Path,item.Name,"",false,item.IsDirectory ? "›" : "",item.IsDirectory);
                view.Subtitle = item.IsDirectory ? "文件夹 · 点击进入" : FormatSize(item.Length);
                if (!item.IsDirectory && item.LastModified.HasValue) view.Subtitle += "  ·  " + item.LastModified.Value.ToString("yyyy-MM-dd HH:mm");
                return view;
            }).ToList();
            _folderFailed = false;
            if (query.Length > 0)
            {
                _keepFolderResultsDuringRefresh = true;
                CancelRecursiveFolderSearch(); ApplyFolderFilters();
                await _recursiveSearchTask;
            }
            else { ApplyFolderFilters(); SetStatus($"{items.Count} 个项目 · 已同步文件夹变化"); }
            if (!Current()) return;
            LiveFolderRefreshCount++;
            _ = Dispatcher.BeginInvoke(() => { if (Current()) scroll?.ScrollToVerticalOffset(offset); },DispatcherPriority.Loaded);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!Current()) return;
            CancelRecursiveFolderSearch(); _folderEntries.Clear(); _folderFailed = true; ApplyFolderFilters();
            FolderEmptyText.Text = "此文件夹已删除、移动或暂时无法访问。\n可返回上一级，或点击刷新重试。";
            SetStatus("文件夹不可访问：" + ex.Message,true);
        }
        finally
        {
            _keepFolderResultsDuringRefresh = false; _liveBusy = false;
            if (ReferenceEquals(_liveRefreshCancellation,cancellation)) _liveRefreshCancellation = null;
        }
    }
}
