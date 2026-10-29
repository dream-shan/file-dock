using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace FileDockDemo;

public partial class MainWindow
{
    private CancellationTokenSource? _recursiveSearchCancellation;
    private Task _recursiveSearchTask = Task.CompletedTask;
    private int _recursiveSearchVersion;
    private string? _recursiveSearchRoot;
    private string _recursiveSearchQuery = "";
    private List<EntryView> _recursiveSearchEntries = new();
    private bool _recursiveSearchPending, _recursiveSearchFailed, _recursiveSearchTruncated;
    private int _recursiveSearchSkipped;

    internal Task WaitForFolderSearchAsync() => _recursiveSearchTask;

    private void UpdateFolderSearchHint()
    {
        if (CurrentFolderPath == null) return;
        SearchPlaceholder.Text = "搜索当前文件夹及子文件夹";
        SearchBox.ToolTip = "按名称搜索当前文件夹及其子文件夹；最多显示 1000 个结果。隐藏、系统项目和子目录链接中的内容不参与搜索。";
    }

    private void StartRecursiveFolderSearch(string query)
    {
        if (CurrentFolderPath == null || (_recursiveSearchRoot == CurrentFolderPath && _recursiveSearchQuery == query)) return;
        CancelRecursiveFolderSearch();
        _recursiveSearchRoot = CurrentFolderPath;
        _recursiveSearchQuery = query;
        _recursiveSearchPending = true;
        var cancellation = _recursiveSearchCancellation = new CancellationTokenSource();
        _recursiveSearchTask = RunRecursiveFolderSearchAsync(CurrentFolderPath, query, _recursiveSearchVersion, cancellation.Token);
    }

    private async Task RunRecursiveFolderSearchAsync(string root, string query, int version, CancellationToken token)
    {
        bool IsCurrent() => !token.IsCancellationRequested && version == _recursiveSearchVersion && CurrentFolderPath == root && SearchBox.Text.Trim() == query && !_quitting;
        try
        {
            // A short pause avoids starting another disk scan for every keystroke.
            await Task.Delay(250, token);
            if (!IsCurrent()) return;
            SetStatus("正在搜索当前文件夹和子文件夹…");
            var result = await FolderSearchService.SearchAsync(root, query, token);
            if (!IsCurrent()) return;
            foreach (var item in result.Items)
            {
                var entry = MakeView(item.Path, item.Name, "", false, item.IsDirectory ? "›" : "", item.IsDirectory);
                string parent = Path.GetDirectoryName(item.Path) ?? root;
                string relative = Path.GetRelativePath(root, parent);
                entry.Subtitle = (relative == "." ? "当前文件夹" : relative) + "  ·  " + (item.IsDirectory ? "文件夹" : FormatSize(item.Length));
                _recursiveSearchEntries.Add(entry);
            }
            _recursiveSearchSkipped = result.SkippedDirectories;
            _recursiveSearchTruncated = result.Truncated;
            _recursiveSearchPending = false;
            ApplyFolderFilters();
            string details = _recursiveSearchTruncated ? " · 已达 1000 项上限，请增加关键词" : "";
            if (_recursiveSearchSkipped > 0) details += $" · 跳过 {_recursiveSearchSkipped} 个无法读取的子目录，结果可能不全";
            SetStatus($"找到 {result.Items.Count} 个项目（含子文件夹）" + details);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsCurrent()) return;
            _recursiveSearchPending = false;
            _recursiveSearchFailed = true;
            _recursiveSearchEntries.Clear();
            ApplyFolderFilters();
            SetStatus("搜索失败：" + ex.Message, true);
        }
    }

    private void ShowRecursiveFolderSearch(string filter)
    {
        if (_recursiveSearchPending && _keepFolderResultsDuringRefresh) { FolderCountText.Text = "正在同步搜索结果…"; return; }
        var visible = _recursiveSearchEntries.Where(e => filter == "全部" || e.Kind == filter).ToList();
        FolderList.ItemsSource = visible;
        ReconcileFileSelection();
        FolderEmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FolderCountText.Text = _recursiveSearchPending ? "搜索中…" : _recursiveSearchFailed ? "搜索失败" : $"{visible.Count} 个结果" + (_recursiveSearchTruncated ? "（上限）" : "");
        FolderCountText.ToolTip = _recursiveSearchPending ? "正在搜索当前文件夹及其子文件夹" : $"匹配 {_recursiveSearchEntries.Count} 项，当前筛选显示 {visible.Count} 项。" + (_recursiveSearchTruncated ? "已达 1000 项显示上限，请增加关键词缩小范围。" : "") + (_recursiveSearchSkipped > 0 ? $"跳过 {_recursiveSearchSkipped} 个无法读取的子目录，结果可能不全。" : "");
        FolderEmptyText.Text = _recursiveSearchPending ? "正在搜索当前文件夹和子文件夹…" : _recursiveSearchFailed ? "无法完成搜索\n请检查路径或连接后刷新。" : _recursiveSearchEntries.Count == 0 ? "当前文件夹和子文件夹中没有匹配项" : "没有此类型的匹配项\n试试切换到“全部”";
    }

    private void CancelRecursiveFolderSearch()
    {
        ++_recursiveSearchVersion;
        _recursiveSearchCancellation?.Cancel();
        _recursiveSearchCancellation?.Dispose();
        _recursiveSearchCancellation = null;
        _recursiveSearchRoot = null;
        _recursiveSearchQuery = "";
        _recursiveSearchEntries.Clear();
        _recursiveSearchPending = _recursiveSearchFailed = _recursiveSearchTruncated = false;
        _recursiveSearchSkipped = 0;
        FolderCountText.ToolTip = null;
    }
}
