using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FileDockDemo;

public partial class MainWindow
{
    internal string? CurrentFolderPath { get; private set; }
    private readonly Stack<string> _folderBack = new();
    private List<EntryView> _folderEntries = new();
    private CancellationTokenSource? _folderCancellation;
    private int _folderRequestVersion, _openRequestVersion;
    private bool _folderLoading, _folderFailed;
    private string _homeSearch = "";

    private void InitializeFolderBrowser()
    {
        SizeChanged += (_, _) => BreadcrumbScroll.Visibility = ActualHeight < 560 ? Visibility.Collapsed : Visibility.Visible;
        FolderBackButton.Click += async (_, _) => await GoBackAsync();
        FolderUpButton.Click += async (_, _) => await GoUpAsync();
        FolderHomeButton.Click += (_, _) => GoHome();
        FolderRefreshButton.Click += async (_, _) =>
        {
            if (CurrentFolderPath != null) await NavigateToFolderAsync(CurrentFolderPath, false, true);
        };
        FolderFilterComboBox.SelectionChanged += (_, _) => ApplyFolderFilters();
        FolderList.ContextMenu = new ContextMenu();
        FolderList.ContextMenuOpening += Items_ContextMenuOpening;
    }

    internal async Task NavigateToFolderAsync(string path, bool addHistory = true, bool preserveSearch = false)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception ex) { SetStatus("无法浏览此路径：" + ex.Message, true); return; }
        ClearFileSelection();
        ++_openRequestVersion;
        CancelFolderLoad();
        int request = _folderRequestVersion;
        var cancellation = _folderCancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        if (CurrentFolderPath == null) _homeSearch = SearchBox.Text;
        else if (addHistory && FileService.NormalizeKey(CurrentFolderPath) != FileService.NormalizeKey(fullPath)) _folderBack.Push(CurrentFolderPath);
        CurrentFolderPath = fullPath;
        UpdatePerformanceActivity();
        _folderLoading = true;
        _folderFailed = false;
        _folderEntries.Clear();
        PinsSection.Visibility = PerformanceSection.Visibility = Visibility.Collapsed;
        BrowserPanel.Visibility = Visibility.Visible;
        UpdateFolderSearchHint();
        FolderTitle.Text = FileName(fullPath);
        FolderPathText.Text = fullPath;
        FolderUpButton.IsEnabled = ParentPath(fullPath) != null;
        FolderRefreshButton.IsEnabled = false;
        BuildBreadcrumbs(fullPath);
        if (!preserveSearch) { SearchBox.Clear(); FolderFilterComboBox.SelectedIndex = 0; }
        ApplyFolderFilters();
        SetStatus("正在读取文件夹内容…");
        try
        {
            var items = await FolderBrowserService.LoadAsync(fullPath, token);
            if (token.IsCancellationRequested || request != _folderRequestVersion || _quitting) return;
            foreach (var item in items)
            {
                var view = MakeView(item.Path, item.Name, "", false, item.IsDirectory ? "›" : "", item.IsDirectory);
                view.Subtitle = item.IsDirectory ? "文件夹 · 点击进入" : FormatSize(item.Length);
                if (!item.IsDirectory && item.LastModified.HasValue) view.Subtitle += "  ·  " + item.LastModified.Value.ToString("yyyy-MM-dd HH:mm");
                _folderEntries.Add(view);
            }
            _folderLoading = false;
            ApplyFolderFilters();
            SetStatus($"{items.Count} 个项目 · 单击文件夹进入，单击文件打开");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || request != _folderRequestVersion) return;
            _folderLoading = false;
            _folderFailed = true;
            _folderEntries.Clear();
            ApplyFolderFilters();
            FolderEmptyText.Text = ex is UnauthorizedAccessException
                ? "没有权限读取此文件夹。\n可以返回，或选择其他文件夹。"
                : "无法读取此文件夹。\n请检查路径或连接后点击刷新。";
            SetStatus("读取失败：" + ex.Message, true);
        }
        finally
        {
            if (request == _folderRequestVersion) FolderRefreshButton.IsEnabled = true;
        }
    }

    private void ApplyFolderFilters()
    {
        if (CurrentFolderPath == null || FolderList == null) return;
        string query = SearchBox.Text.Trim();
        string filter = (FolderFilterComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全部";
        if (query.Length > 0 && !_folderLoading && !_folderFailed)
        {
            StartRecursiveFolderSearch(query);
            ShowRecursiveFolderSearch(filter);
            return;
        }
        bool wasRecursiveSearch = _recursiveSearchRoot != null;
        CancelRecursiveFolderSearch();
        var visible = _folderEntries.Where(e => (query.Length == 0 || e.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)) && (filter == "全部" || e.Kind == filter)).ToList();
        FolderList.ItemsSource = visible;
        ReconcileFileSelection();
        FolderCountText.Text = _folderLoading ? "正在读取…" : _folderFailed ? "读取失败" : query.Length > 0 || filter != "全部" ? $"{visible.Count} / {_folderEntries.Count} 个项目" : $"{visible.Count} 个项目";
        FolderEmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_folderFailed) return;
        FolderEmptyText.Text = _folderLoading ? "正在读取文件夹内容…" : _folderEntries.Count == 0 ? "这个文件夹是空的" : "没有匹配的项目\n试试其他关键词，或切换到“全部”";
        if (wasRecursiveSearch && !_folderLoading) SetStatus($"当前层显示 {visible.Count} 个项目");
    }

    internal async Task GoBackAsync()
    {
        if (_folderBack.TryPop(out string? previous)) await NavigateToFolderAsync(previous, false);
        else GoHome();
    }

    internal async Task GoUpAsync()
    {
        if (CurrentFolderPath != null && ParentPath(CurrentFolderPath) is string parent) await NavigateToFolderAsync(parent);
    }

    internal void GoHome()
    {
        if (CurrentFolderPath == null) return;
        ClearFileSelection();
        ++_openRequestVersion;
        CancelFolderLoad();
        CurrentFolderPath = null;
        UpdatePerformanceActivity();
        _folderBack.Clear();
        _folderEntries.Clear();
        FolderList.ItemsSource = null;
        BrowserPanel.Visibility = Visibility.Collapsed;
        PinsSection.Visibility = PerformanceSection.Visibility = Visibility.Visible;
        SearchPlaceholder.Text = "搜索常用文件或路径";
        SearchBox.ToolTip = null;
        SearchBox.Text = _homeSearch;
        ApplyFilters();
        SetStatus("文件夹内浏览 · 文件单击打开");
    }

    private void CancelFolderLoad()
    {
        CancelRecursiveFolderSearch();
        ++_folderRequestVersion;
        _folderCancellation?.Cancel();
        _folderCancellation?.Dispose();
        _folderCancellation = null;
    }

    private static string? ParentPath(string path) => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));

    private void BuildBreadcrumbs(string path)
    {
        Breadcrumbs.Children.Clear();
        var ancestors = new Stack<string>();
        for (string? item = path; item != null; item = ParentPath(item)) ancestors.Push(item);
        while (ancestors.TryPop(out string? item))
        {
            string target = item;
            var button = new Button
            {
                Content = FileName(item), ToolTip = item, Style = (Style)FindResource("QuietButton"),
                FontSize = 10, Padding = new Thickness(4, 2, 4, 2),
                IsEnabled = FileService.NormalizeKey(item) != FileService.NormalizeKey(path)
            };
            button.Click += async (_, _) => await NavigateToFolderAsync(target);
            Breadcrumbs.Children.Add(button);
            if (ancestors.Count > 0) Breadcrumbs.Children.Add(new TextBlock { Text = "›", Foreground = System.Windows.Media.Brushes.LightSlateGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) });
        }
        Dispatcher.BeginInvoke(() => BreadcrumbScroll.ScrollToRightEnd(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static string FormatSize(long? bytes) => bytes switch
    {
        null => "文件",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
        _ => $"{bytes / (1024d * 1024 * 1024):0.#} GB"
    };

    private bool HandleBrowserKey(KeyEventArgs e)
    {
        if (CurrentFolderPath == null) return false;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if ((Keyboard.Modifiers == ModifierKeys.Alt && key == Key.Left) || (key == Key.Back && Keyboard.Modifiers == ModifierKeys.None && Keyboard.FocusedElement is not TextBox))
        { _ = GoBackAsync(); e.Handled = true; return true; }
        if (Keyboard.Modifiers == ModifierKeys.Alt && key == Key.Up) { _ = GoUpAsync(); e.Handled = true; return true; }
        if (key == Key.F5) { _ = NavigateToFolderAsync(CurrentFolderPath, false, true); e.Handled = true; return true; }
        if (key == Key.Enter)
        {
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase || FolderFilterComboBox.IsKeyboardFocusWithin) return true;
            var entry = FolderList.SelectedItem as EntryView ?? FolderList.Items.Cast<EntryView>().FirstOrDefault();
            if (entry != null) OpenEntry(entry);
            e.Handled = true; return true;
        }
        if (key == Key.Down && SearchBox.IsKeyboardFocused)
        {
            if (FolderList.Items.Count > 0) { FolderList.SelectedIndex = 0; FolderList.Focus(); }
            e.Handled = true; return true;
        }
        return false;
    }
}
