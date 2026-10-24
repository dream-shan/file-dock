using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace FileDockDemo;

public sealed class HalfWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is double width ? Math.Max(130, (width - 14) / 2) : 153d;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class EntryView
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string TimeLabel { get; set; } = "";
    public string IconGlyph { get; set; } = "FILE";
    public Brush IconBrush { get; set; } = Brushes.SlateBlue;
    public string Kind { get; set; } = "文档";
    public bool IsPinned { get; set; }
}

public partial class MainWindow : Window
{
    internal readonly StorageService Storage;
    internal AppState State;
    private readonly string _dataDirectory;
    private List<RecentEntry> _systemRecent = new();
    private List<EntryView> _displayPins = new(), _displayRecents = new();
    private bool _loaded, _quitting, _refreshing, _keepOpen, _isCollapsed, _inDialog, _menuOpen;
    private readonly Dictionary<string, bool> _directoryKinds = new(StringComparer.OrdinalIgnoreCase);
    private bool _dragging, _saveFailed;
    private Point _dragStart;
    private EntryView? _dragEntry;
    private DockTabWindow? _dock;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private HwndSource? _source;
    private readonly DispatcherTimer _fullscreenTimer;
    private Forms.Screen _screen;

    public MainWindow(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        Storage = new StorageService(dataDirectory);
        State = Storage.Load();
        _keepOpen = State.KeepOpen;
        _screen = Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
        InitializeComponent();
        InitializeFolderBrowser();
        SizeChanged += (_, _) => PinsList.MaxHeight = ActualHeight < 560 ? 82 : 158;
        SearchBox.TextChanged += (_, _) => ApplyFilters();
        FilterComboBox.SelectionChanged += (_, _) => ApplyFilters();
        KeepOpenButton.Click += (_, _) => { _keepOpen = !_keepOpen; State.KeepOpen = _keepOpen; UpdateKeepOpen(); Persist(); };
        CollapseButton.Click += (_, _) => CollapsePanel();
        RefreshButton.Click += async (_, _) => await RefreshRecentAsync();
        SettingsButton.Click += (_, _) => OpenSettings();
        PinsList.ContextMenuOpening += Items_ContextMenuOpening;
        RecentList.ContextMenuOpening += Items_ContextMenuOpening;
        PinsList.ContextMenu = new ContextMenu();
        RecentList.ContextMenu = new ContextMenu();
        PinsList.PreviewMouseMove += Pins_PreviewMouseMove;
        RecentList.PreviewMouseLeftButtonUp += Recent_SingleClick;
        var addMenu = new ContextMenu();
        AddMenu(addMenu, "添加文件…", AddFilesDialog);
        AddMenu(addMenu, "添加文件夹…", AddFolderDialog);
        AddButton.ContextMenu = addMenu;
        AddButton.Click += (_, _) => { addMenu.PlacementTarget = AddButton; addMenu.IsOpen = true; };
        addMenu.Opened += (_, _) => _menuOpen = true;
        addMenu.Closed += (_, _) => _menuOpen = false;
        SourceInitialized += (_, _) => InitializeNative();
        _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _fullscreenTimer.Tick += (_, _) =>
        {
            if (_dock == null || !_isCollapsed) return;
            bool fullscreen = NativeMethods.ForegroundIsFullscreen(new WindowInteropHelper(this).Handle, new WindowInteropHelper(_dock).Handle);
            if (fullscreen) _dock.Hide(); else if (!_dock.IsVisible) _dock.Show();
        };
        ApplyFilters();
    }

    private void InitializeNative()
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WindowProc);
        if (!NativeMethods.RegisterHotKey(_source!.Handle, NativeMethods.HotkeyId, 0x4003, 0x20))
        {
            SetStatus("快捷键已被占用，可通过贴边标签或托盘打开。", true);
            HotkeyHint.Text = "快捷键冲突 · 请用贴边入口";
        }
        _dock = new DockTabWindow(this);
        _trayIcon = CreateTrayIcon();
        _tray = new Forms.NotifyIcon { Icon = _trayIcon, Text = "文件坞 · Ctrl+Alt+Space", Visible = !App.IsSelfTest };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开文件坞", null, (_, _) => Dispatcher.Invoke(ExpandPanel));
        menu.Items.Add("收起到屏幕边缘", null, (_, _) => Dispatcher.Invoke(CollapsePanel));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Quit));
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(ExpandPanel); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        if (!App.IsSelfTest) _fullscreenTimer.Start();
    }

    private static System.Drawing.Icon CreateTrayIcon()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.Clear(System.Drawing.Color.Transparent);
        using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(45, 111, 236));
        foreach (int x in new[] { 3, 18 }) foreach (int y in new[] { 3, 18 }) g.FillRectangle(brush, x, y, 11, 11);
        IntPtr handle = bmp.GetHicon();
        try { using var icon = System.Drawing.Icon.FromHandle(handle); return (System.Drawing.Icon)icon.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == NativeMethods.HotkeyId) { if (IsVisible) CollapsePanel(); else ExpandPanel(); handled = true; }
        if (msg == 0x02E0) Dispatcher.BeginInvoke(() => PositionPanel());
        return IntPtr.Zero;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        UpdateKeepOpen();
        if (!App.IsSelfTest)
        {
            if (State.Pins.Count == 0 && !File.Exists(System.IO.Path.Combine(_dataDirectory, "initialized")))
            {
                AddPins(new[] { AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) });
                Directory.CreateDirectory(_dataDirectory);
                File.WriteAllText(System.IO.Path.Combine(_dataDirectory, "initialized"), "1");
            }
            PositionPanel();
            await RefreshRecentAsync();
        }
    }

    internal void AddPins(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (string raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string path;
            try { path = System.IO.Path.GetFullPath(raw); } catch { continue; }
            string key = FileService.NormalizeKey(path);
            if (State.Pins.Any(p => FileService.NormalizeKey(p.Path) == key)) continue;
            State.Pins.Add(new PinnedEntry { Id = Guid.NewGuid().ToString("N"), Path = path, DisplayName = FileName(path), Order = State.Pins.Count });
            added++;
        }
        Persist(); ApplyFilters(); SetStatus(added > 0 ? $"已固定 {added} 个项目。拖动卡片可调整顺序。" : "这些项目已在常用中。");
        _ = LoadKindsAsync(State.Pins.Select(p => p.Path).ToArray());
    }

    private static string FileName(string path)
    {
        string trimmed = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        string name = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    internal void ApplyFilters()
    {
        if (PinsList == null || RecentList == null) return;
        if (CurrentFolderPath != null) { ApplyFolderFilters(); return; }
        string query = SearchBox.Text.Trim();
        string filter = (FilterComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全部";
        bool Match(EntryView e) => (query.Length == 0 || e.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) || e.Path.Contains(query, StringComparison.OrdinalIgnoreCase)) && (filter == "全部" || e.Kind == filter);
        _displayPins = State.Pins.OrderBy(p => p.Order).Select(p => MakeView(p.Path, p.DisplayName, p.Id, true, "", "")).Where(Match).ToList();
        var hidden = new HashSet<string>(State.HiddenRecent.Select(FileService.NormalizeKey), StringComparer.Ordinal);
        _displayRecents = State.History.Concat(_systemRecent)
            .Where(r => !hidden.Contains(FileService.NormalizeKey(r.Path)))
            .GroupBy(r => FileService.NormalizeKey(r.Path))
            .Select(g => g.OrderByDescending(r => r.LastUsed).First())
            .OrderByDescending(r => r.LastUsed).Take(100)
            .Select(r => MakeView(r.Path, r.DisplayName, "", false, r.Source, r.Source == "从这里打开" ? RelativeTime(r.LastUsed) : "系统记录"))
            .Where(Match).ToList();
        PinsList.ItemsSource = _displayPins;
        RecentList.ItemsSource = _displayRecents;
        PinsEmptyText.Visibility = _displayPins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentEmptyText.Visibility = _displayRecents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PinsEmptyText.Text = query.Length > 0 ? "没有匹配的常用项目" : "拖入文件或文件夹，放进你的常用";
        RecentEmptyText.Text = query.Length > 0 ? "没有匹配的最近项目" : "暂无记录，打开文件后会显示在这里";
    }

    private EntryView MakeView(string path, string name, string id, bool pinned, string source, string time)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        string glyph = "FILE", color = "#74839B", kind = "文档";
        bool isDirectory = _directoryKinds.TryGetValue(path, out var knownKind) ? knownKind : ext == "";
        if (isDirectory) { glyph = "▰"; color = "#E9A322"; kind = "文件夹"; }
        else if (ext is ".doc" or ".docx" or ".rtf") { glyph = "W"; color = "#347BDA"; }
        else if (ext is ".xls" or ".xlsx" or ".csv") { glyph = "X"; color = "#26A176"; kind = "表格"; }
        else if (ext == ".pdf") { glyph = "PDF"; color = "#E76472"; }
        else if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg") { glyph = "IMG"; color = "#9574D6"; kind = "图片"; }
        else if (ext is ".ppt" or ".pptx") { glyph = "P"; color = "#D88244"; }
        else if (ext is ".exe" or ".lnk") { glyph = "APP"; color = "#617AD0"; }
        string parent = System.IO.Path.GetDirectoryName(path.TrimEnd('\\')) ?? path;
        return new EntryView { Id = id, Path = path, DisplayName = string.IsNullOrEmpty(name) ? FileName(path) : name, Subtitle = parent, TimeLabel = time, IconGlyph = glyph, IconBrush = (Brush)new BrushConverter().ConvertFromString(color)!, Kind = kind, IsPinned = pinned };
    }

    private async Task LoadKindsAsync(string[] paths)
    {
        var pending = paths.Distinct(StringComparer.OrdinalIgnoreCase).Where(p => !_directoryKinds.ContainsKey(p)).ToArray();
        if (pending.Length == 0) return;
        var found = await Task.Run(() => pending.Select(p => (Path: p, IsDirectory: Directory.Exists(p))).ToArray());
        foreach (var item in found) _directoryKinds[item.Path] = item.IsDirectory;
        ApplyFilters();
    }

    private static string RelativeTime(DateTime time)
    {
        double minutes = (DateTime.Now - time.ToLocalTime()).TotalMinutes;
        if (minutes < 1) return "刚刚";
        if (minutes < 60) return $"{(int)minutes} 分钟前";
        if (minutes < 1440) return $"{(int)(minutes / 60)} 小时前";
        return time.ToLocalTime().ToString("MM-dd");
    }

    internal async Task RefreshRecentAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        try
        {
            _systemRecent = await RunSta(() => RecentService.LoadSystemRecent(100));
            ApplyFilters(); if (CurrentFolderPath == null) SetStatus($"已读取 {_systemRecent.Count} 条系统记录 · 文件夹内浏览，文件单击打开");
            _ = LoadKindsAsync(_systemRecent.Select(r => r.Path).Concat(State.Pins.Select(p => p.Path)).ToArray());
        }
        catch (Exception ex) { if (CurrentFolderPath == null) SetStatus("系统记录暂不可用：" + ex.Message, true); }
        finally { _refreshing = false; RefreshButton.IsEnabled = true; }
    }

    private static Task<T> RunSta<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { completion.SetResult(action()); } catch (Exception ex) { completion.SetException(ex); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }

    private void Item_Open(object sender, RoutedEventArgs e)
    {
        if (!_dragging && sender is FrameworkElement element && (element.Tag ?? element.DataContext) is EntryView entry) OpenEntry(entry);
        e.Handled = true;
    }
    private void Recent_DoubleClick(object sender, MouseButtonEventArgs e) { e.Handled = true; }
    private void Recent_SingleClick(object sender, MouseButtonEventArgs e)
    {
        var entry = FindEntry(e.OriginalSource as DependencyObject);
        if (entry != null) { OpenEntry(entry); e.Handled = true; }
    }
    private async void OpenEntry(EntryView entry) => await OpenEntryAsync(entry);
    internal async Task OpenEntryAsync(EntryView entry)
    {
        int request = ++_openRequestVersion;
        SetStatus("正在打开：" + entry.DisplayName);
        try
        {
            bool isDirectory = await FolderBrowserService.IsDirectoryAsync(entry.Path);
            if (request != _openRequestVersion || _quitting) return;
            if (isDirectory)
            {
                await NavigateToFolderAsync(entry.Path);
                return;
            }
            await RunSta(() => { FileService.Open(entry.Path); return true; });
            string key = FileService.NormalizeKey(entry.Path);
            State.History.RemoveAll(r => FileService.NormalizeKey(r.Path) == key);
            State.HiddenRecent.RemoveAll(p => FileService.NormalizeKey(p) == key);
            State.History.Insert(0, new RecentEntry { Path = entry.Path, DisplayName = entry.DisplayName, LastUsed = DateTime.Now, Source = "从这里打开" });
            State.History = State.History.Take(100).ToList();
            Persist(); ApplyFilters();
            if (request != _openRequestVersion || _quitting) return;
            SetStatus("已请求系统打开：" + entry.DisplayName);
            if (!_keepOpen && !_saveFailed) CollapsePanel();
        }
        catch (Exception ex) { if (request == _openRequestVersion && !_quitting) SetStatus("无法打开。可右键重新定位。" + ex.Message, true); }
    }

    private static EntryView? FindEntry(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is FrameworkElement element && element.DataContext is EntryView entry) return entry;
            source = source is Visual || source is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    private void Items_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        EntryView? entry = FindEntry(e.OriginalSource as DependencyObject) ?? (sender as ListBox)?.SelectedItem as EntryView;
        if (entry == null) { e.Handled = true; return; }
        var menu = new ContextMenu();
        AddMenu(menu, entry.Kind == "文件夹" ? "在文件坞中浏览" : "打开", () => OpenEntry(entry));
        if (entry.Kind == "文件夹") AddMenu(menu, "在文件管理器中打开", () => { try { FileService.Open(entry.Path); } catch (Exception ex) { SetStatus(ex.Message, true); } });
        AddMenu(menu, "打开所在位置", () => { try { FileService.Reveal(entry.Path); } catch (Exception ex) { SetStatus(ex.Message, true); } });
        AddMenu(menu, "复制路径", () => { try { Clipboard.SetText(entry.Path); SetStatus("已复制路径"); } catch (Exception ex) { SetStatus(ex.Message, true); } });
        menu.Items.Add(new Separator());
        if (entry.IsPinned)
        {
            AddMenu(menu, "修改显示名称…", () => RenameEntry(entry));
            AddMenu(menu, "重新定位…", () => RelocateEntry(entry));
            AddMenu(menu, "移到最前", () => { var pin = State.Pins.First(p => p.Id == entry.Id); State.Pins.Remove(pin); State.Pins.Insert(0, pin); ReorderAndSave(); });
            AddMenu(menu, "从常用移除", () => { State.Pins.RemoveAll(p => p.Id == entry.Id); ReorderAndSave(); SetStatus("已移除入口，原文件保留在原位置。"); });
        }
        else
        {
            AddMenu(menu, "固定到常用", () => AddPins(new[] { entry.Path }));
            if (!ReferenceEquals(sender, FolderList)) AddMenu(menu, "隐藏这条记录", () => { State.HiddenRecent.Add(entry.Path); Persist(); ApplyFilters(); });
        }
        ShowMenu(menu, sender as UIElement); e.Handled = true;
    }
    private static void AddMenu(ContextMenu menu, string label, Action action)
    {
        var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item);
    }
    private void ShowMenu(ContextMenu menu, UIElement? target)
    {
        menu.PlacementTarget = target; _menuOpen = true;
        menu.Closed += (_, _) => { _menuOpen = false; Dispatcher.BeginInvoke(() => { if (!_keepOpen && !_inDialog && !IsActive && !App.IsSelfTest) CollapsePanel(); }); };
        menu.IsOpen = true;
    }

    private void AddFilesDialog()
    {
        _inDialog = true;
        try { var dialog = new OpenFileDialog { Multiselect = true, Title = "选择要固定的文件", CheckFileExists = true }; if (dialog.ShowDialog(this) == true) AddPins(dialog.FileNames); }
        finally { _inDialog = false; Activate(); }
    }
    private void AddFolderDialog()
    {
        _inDialog = true;
        try { var dialog = new OpenFolderDialog { Title = "选择要固定的文件夹", Multiselect = true }; if (dialog.ShowDialog(this) == true) AddPins(dialog.FolderNames); }
        finally { _inDialog = false; Activate(); }
    }
    private void RenameEntry(EntryView entry)
    {
        _inDialog = true;
        try
        {
            var input = new TextBox { Text = entry.DisplayName, Margin = new Thickness(18), Padding = new Thickness(8), FontSize = 14 };
            var ok = new Button { Content = "保存", Width = 90, Height = 32, Margin = new Thickness(18, 0, 18, 18), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
            var panel = new StackPanel(); panel.Children.Add(input); panel.Children.Add(ok);
            var dialog = new Window { Title = "修改显示名称", Content = panel, Width = 340, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Background = Brushes.White };
            ok.Click += (_, _) => dialog.DialogResult = true;
            dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(input.Text)) { State.Pins.First(p => p.Id == entry.Id).DisplayName = input.Text.Trim(); Persist(); ApplyFilters(); }
        }
        finally { _inDialog = false; }
    }
    private void RelocateEntry(EntryView entry)
    {
        _inDialog = true;
        try
        {
            string? path = null;
            if (entry.Kind == "文件夹") { var d = new OpenFolderDialog { Title = "重新选择文件夹" }; if (d.ShowDialog(this) == true) path = d.FolderName; }
            else { var d = new OpenFileDialog { Title = "重新选择文件" }; if (d.ShowDialog(this) == true) path = d.FileName; }
            if (path != null) { State.Pins.First(p => p.Id == entry.Id).Path = path; Persist(); ApplyFilters(); }
        }
        finally { _inDialog = false; }
    }

    private void OpenSettings()
    {
        var menu = new ContextMenu();
        AddMenu(menu, "添加文件夹…", AddFolderDialog);
        AddMenu(menu, "贴到左侧", () => { State.DockSide = "Left"; PositionPanel(); PositionDock(); Persist(); });
        AddMenu(menu, "贴到右侧", () => { State.DockSide = "Right"; PositionPanel(); PositionDock(); Persist(); });
        var startup = new MenuItem { Header = "开机启动", IsCheckable = true, IsChecked = StartupEnabled() };
        startup.Click += (_, _) =>
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                if (startup.IsChecked) key.SetValue("FileDockDemo", "\"" + Environment.ProcessPath + "\""); else key.DeleteValue("FileDockDemo", false);
                SetStatus(startup.IsChecked ? "已开启开机启动" : "已关闭开机启动");
            }
            catch (Exception ex) { SetStatus(ex.Message, true); }
        };
        menu.Items.Add(startup);
        AddMenu(menu, "恢复隐藏的最近记录", () => { State.HiddenRecent.Clear(); Persist(); ApplyFilters(); });
        menu.Items.Add(new Separator());
        AddMenu(menu, "使用说明", () => { _inDialog = true; MessageBox.Show(this, "拖入文件或文件夹即可固定；拖动常用卡片调整顺序。\n单击文件夹在面板内浏览，单击文件用默认应用打开。\n顶部可返回、上一级、回到常用；路径按钮可跳到祖先文件夹。\n浏览时搜索和类型筛选只作用于当前这一层。\nCtrl + Alt + Space 展开/收起；Esc 收起。\n图钉开启后，打开文件和失去焦点都保持展开。\n\n最近记录由 Windows 提供，覆盖取决于各应用。\n固定项及设置保存在程序旁的 Data 文件夹。", "文件坞 Demo", MessageBoxButton.OK, MessageBoxImage.Information); _inDialog = false; });
        AddMenu(menu, "退出文件坞", Quit);
        ShowMenu(menu, SettingsButton);
    }
    private static bool StartupEnabled()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("FileDockDemo") is string; } catch { return false; }
    }

    private void Pin_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) { _dragEntry = FindEntry(e.OriginalSource as DependencyObject); _dragStart = e.GetPosition(this); _dragging = false; }
    private void Pins_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragEntry == null || e.LeftButton != MouseButtonState.Pressed || _dragging) return;
        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragging = true;
        try { DragDrop.DoDragDrop(PinsList, new DataObject("FileDockPin", _dragEntry.Id), DragDropEffects.Move); }
        finally { _dragEntry = null; Dispatcher.BeginInvoke(() => _dragging = false, DispatcherPriority.Background); }
    }
    private void Window_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : e.Data.GetDataPresent("FileDockPin") ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; }
    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files) AddPins(files);
        else if (e.Data.GetData("FileDockPin") is string id)
        {
            var target = FindEntry(e.OriginalSource as DependencyObject);
            var pin = State.Pins.FirstOrDefault(p => p.Id == id);
            if (target?.Id == id) { e.Handled = true; return; }
            if (pin != null)
            {
                State.Pins.Remove(pin);
                int index = target == null ? State.Pins.Count : State.Pins.FindIndex(p => p.Id == target.Id);
                State.Pins.Insert(index < 0 ? State.Pins.Count : index, pin); ReorderAndSave();
            }
        }
        e.Handled = true;
    }
    private void ReorderAndSave() { for (int i = 0; i < State.Pins.Count; i++) State.Pins[i].Order = i; Persist(); ApplyFilters(); }

    public void ExpandPanel()
    {
        _isCollapsed = false; _dock?.Hide(); PositionPanel(); Show(); Activate(); SearchBox.Focus();
        if (CurrentFolderPath == null) _ = RefreshRecentAsync();
    }
    internal void CollapsePanel()
    {
        if (App.IsSelfTest) { _isCollapsed = true; Hide(); return; }
        _isCollapsed = true; Hide(); PositionDock(); _dock?.Show();
    }
    internal void DockMoved(double left, double top)
    {
        var dpi = VisualTreeHelper.GetDpi(_dock!);
        _screen = Forms.Screen.FromHandle(new WindowInteropHelper(_dock!).Handle);
        State.DockSide = left * dpi.DpiScaleX < _screen.WorkingArea.Left + _screen.WorkingArea.Width / 2 ? "Left" : "Right";
        State.DockTop = top; PositionDock(); Persist();
    }
    private Rect WorkArea(Visual? target = null)
    {
        if (!Forms.Screen.AllScreens.Any(s => s.DeviceName == _screen.DeviceName)) _screen = Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
        var bounds = _screen.WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(target ?? this);
        return new Rect(bounds.Left / dpi.DpiScaleX, bounds.Top / dpi.DpiScaleY, bounds.Width / dpi.DpiScaleX, bounds.Height / dpi.DpiScaleY);
    }
    private void PositionPanel()
    {
        if (App.IsSelfTest) return;
        Rect area = WorkArea();
        MaxHeight = Math.Max(480, area.Height - 20); Height = Math.Min(Height, MaxHeight);
        Left = State.DockSide == "Left" ? area.Left + 10 : Math.Max(area.Left, area.Right - Width - 10);
        Top = Math.Clamp(State.DockTop < 0 ? area.Top + (area.Height - Height) / 2 : State.DockTop - 30, area.Top + 6, Math.Max(area.Top + 6, area.Bottom - Height - 6));
    }
    private void PositionDock()
    {
        if (_dock == null) return;
        Rect area = WorkArea(_dock);
        _dock.Left = State.DockSide == "Left" ? area.Left : area.Right - _dock.Width;
        _dock.Top = Math.Clamp(State.DockTop < 0 ? area.Top + area.Height / 2 - _dock.Height / 2 : State.DockTop, area.Top, Math.Max(area.Top, area.Bottom - _dock.Height));
    }
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || FindButton(source) != null) return;
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
            _screen = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
            State.DockTop = Top + 30;
            Rect area = WorkArea(); State.DockSide = Left + Width / 2 < area.Left + area.Width / 2 ? "Left" : "Right";
            Persist();
        }
    }
    private static Button? FindButton(DependencyObject? source)
    {
        while (source != null) { if (source is Button button) return button; source = source is Visual ? VisualTreeHelper.GetParent(source) : null; } return null;
    }
    private void Window_Deactivated(object sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() => { if (!_keepOpen && !_inDialog && !_dragging && !IsActive && !AnyMenuOpen() && !App.IsSelfTest) CollapsePanel(); }, DispatcherPriority.Background);
    }
    private bool AnyMenuOpen() => _menuOpen || AddButton.ContextMenu?.IsOpen == true;
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleBrowserKey(e)) return;
        if (e.Key == Key.Escape) { CollapsePanel(); e.Handled = true; }
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        if (e.Key == Key.Enter) { var entry = RecentList.SelectedItem as EntryView ?? _displayPins.FirstOrDefault() ?? _displayRecents.FirstOrDefault(); if (entry != null) OpenEntry(entry); e.Handled = true; }
        if (e.Key == Key.Down && SearchBox.IsKeyboardFocused && RecentList.Items.Count > 0) { RecentList.SelectedIndex = 0; RecentList.Focus(); e.Handled = true; }
    }
    private void Window_Closing(object? sender, CancelEventArgs e) { if (!_quitting && !App.IsSelfTest) { e.Cancel = true; CollapsePanel(); } }
    private void DisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { PositionPanel(); PositionDock(); });
    private void UpdateKeepOpen() { KeepOpenButton.ToolTip = _keepOpen ? "已保持展开，点击取消" : "保持展开"; KeepOpenButton.Foreground = _keepOpen ? new SolidColorBrush(Color.FromRgb(41, 111, 235)) : Brushes.SlateGray; }
    internal void SetStatus(string text, bool error = false) { if (_saveFailed && !error) return; StatusText.Text = text; StatusText.Foreground = error ? Brushes.IndianRed : Brushes.SlateGray; StatusText.ToolTip = text; }
    internal bool Persist() { try { Storage.Save(State); _saveFailed = false; return true; } catch (Exception ex) { _saveFailed = true; SetStatus("配置保存失败：" + ex.Message, true); return false; } }
    public void Quit() { _quitting = true; Persist(); Application.Current.Shutdown(); }
    public void DisposeResources()
    {
        CancelFolderLoad();
        _fullscreenTimer.Stop();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        if (_source != null) { NativeMethods.UnregisterHotKey(_source.Handle, NativeMethods.HotkeyId); _source.RemoveHook(WindowProc); }
        _tray?.Dispose(); _trayIcon?.Dispose(); _dock?.Close();
    }
}
