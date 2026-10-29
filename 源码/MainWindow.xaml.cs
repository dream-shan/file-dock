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

public sealed class EntryView : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool _isChosen;
    public bool IsChosen { get => _isChosen; set { if (_isChosen != value) { _isChosen = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChosen))); } } }
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
    private List<EntryView> _displayPins = new();
    private bool _loaded, _quitting, _keepOpen, _isCollapsed, _inDialog, _menuOpen;
    private readonly Dictionary<string, bool> _directoryKinds = new(StringComparer.OrdinalIgnoreCase);
    internal int CachedPathKindCount => _directoryKinds.Count;
    private bool _dragging, _saveFailed;
    private Point _dragStart;
    private EntryView? _dragEntry;
    private DockTabWindow? _dock;
    internal DockTabWindow DockEntry => _dock!;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private HwndSource? _source;
    private NativeMethods.WinEventCallback? _foregroundCallback;
    private IntPtr _foregroundHook, _locationHook, _reorderHook;
    private bool _foregroundUpdateQueued;
    private static readonly Dictionary<string, Brush> IconBrushes = new();
    private Forms.Screen _screen;

    public MainWindow(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        Storage = new StorageService(dataDirectory);
        State = Storage.Load();
        _keepOpen = State.KeepOpen;
        _screen = Forms.Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName == State.PanelScreen) ?? Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
        State.ThemeMode = ThemePalette.Normalize(State.ThemeMode);
        ThemePalette.Apply(ThemePalette.Resolve(State.ThemeMode, ThemePalette.SystemAppsDark()));
        InitializeComponent();
        InitializeMovement();
        InitializeFolderBrowser();
        InitializeLiveFolder();
        InitializeTransfers();
        InitializePerformance();
        ApplyTheme();
        SizeChanged += (_, _) => PinsList.MaxHeight = ActualHeight < 560 ? 82 : 158;
        SearchBox.TextChanged += (_, _) => ApplyFilters();
        KeepOpenButton.Click += (_, _) => { _keepOpen = !_keepOpen; State.KeepOpen = _keepOpen; UpdateKeepOpen(); Persist(); };
        CollapseButton.Click += (_, _) => CollapsePanel();
        SettingsButton.Click += (_, _) => OpenSettings();
        PinsList.ContextMenuOpening += Items_ContextMenuOpening;
        PinsList.ContextMenu = new ContextMenu();


        var addMenu = new ContextMenu();
        AddMenu(addMenu, "添加文件…", AddFilesDialog);
        AddMenu(addMenu, "添加文件夹…", AddFolderDialog);
        AddButton.ContextMenu = addMenu;
        AddButton.Click += (_, _) => { addMenu.PlacementTarget = AddButton; addMenu.IsOpen = true; };
        addMenu.Opened += (_, _) => _menuOpen = true;
        addMenu.Closed += (_, _) => _menuOpen = false;
        SourceInitialized += (_, _) => InitializeNative();
        ApplyFilters();
    }

    private void InitializeNative()
    {
        NativeMethods.HideFromTaskSwitcher(new WindowInteropHelper(this).Handle); ApplyNativeCorners(new WindowInteropHelper(this).Handle);
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
        ApplyNativeTheme();
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(ExpandPanel); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += TrayPreferenceChanged;
        if (!App.IsSelfTest)
        {
            _foregroundCallback = (_, eventType, window, objectId, childId, _, _) =>
            {
                if (eventType == 3 || (objectId == 0 && childId == 0 &&
                    (eventType == 0x8004 || window == NativeMethods.GetForegroundWindow()))) QueueDockRaise();
            };
            _foregroundHook = NativeMethods.SetWinEventHook(3, 3, IntPtr.Zero, _foregroundCallback, 0, 0, 2);
            _locationHook = NativeMethods.SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, _foregroundCallback, 0, 0, 2);
            _reorderHook = NativeMethods.SetWinEventHook(0x8004, 0x8004, IntPtr.Zero, _foregroundCallback, 0, 0, 2);
            if (_foregroundHook == IntPtr.Zero || _locationHook == IntPtr.Zero || _reorderHook == IntPtr.Zero)
                SetStatus("窗口变化通知不可用，仍可通过 Ctrl + Alt + Space 唤出。", true);
            WatchCaptureWindows();
        }
        InitializeDiagnostics();
    }

    private void QueueDockRaise()
    {
        if (_quitting || !_isCollapsed || _foregroundUpdateQueued) return;
        _foregroundUpdateQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _foregroundUpdateQueued = false;
            if (_quitting || !_isCollapsed || _dock == null) return;
            if (UpdateCaptureVisibility()) return;
            if (!_dock.IsVisible) _dock.Show();
            IntPtr handle = _dock.Handle;
            // Reorder events also occur while unrelated windows change. Avoid
            // redundant raises and feedback when we already sit above the app.
            if (!NativeMethods.IsAboveForeground(handle)) NativeMethods.RaiseWithoutActivation(handle);
        }, DispatcherPriority.Background);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == NativeMethods.HotkeyId) { RecordDiagnosticState("hotkey"); if (IsVisible) CollapsePanel(); else ExpandPanel(); handled = true; }
        if (msg == 0x001A || msg == 0x031A) QueueThemeRefresh();
        if (msg == 0x02E0 && !_movingPanel) Dispatcher.BeginInvoke(() => PositionPanel());
        return IntPtr.Zero;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
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
            _ = LoadKindsAsync(State.Pins.Select(p => p.Path).ToArray());
            UpdatePerformanceActivity();
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
        if (PinsList == null) return;
        if (CurrentFolderPath != null) { ApplyFolderFilters(); return; }
        string query = SearchBox.Text.Trim();
        _displayPins = State.Pins.OrderBy(p => p.Order)
            .Select(p => MakeView(p.Path, p.DisplayName, p.Id, true, ""))
            .Where(e => query.Length == 0 || e.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) || e.Path.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        PinsList.ItemsSource = _displayPins;
        ReconcileFileSelection();
        PinsEmptyText.Visibility = _displayPins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PinsEmptyText.Text = query.Length > 0 ? "没有匹配的常用项目" : "点击“＋ 添加”，选择常用文件或文件夹";
    }

    private EntryView MakeView(string path, string name, string id, bool pinned, string time, bool? directory = null)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        string glyph = "FILE", color = "#74839B", kind = "文档";
        bool isDirectory = directory ?? (_directoryKinds.TryGetValue(path, out var knownKind) ? knownKind : ext == "");
        if (isDirectory) { glyph = "▰"; color = "#E9A322"; kind = "文件夹"; }
        else if (ext is ".doc" or ".docx" or ".rtf") { glyph = "W"; color = "#347BDA"; }
        else if (ext is ".xls" or ".xlsx" or ".csv") { glyph = "X"; color = "#26A176"; kind = "表格"; }
        else if (ext == ".pdf") { glyph = "PDF"; color = "#E76472"; }
        else if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg") { glyph = "IMG"; color = "#9574D6"; kind = "图片"; }
        else if (ext is ".ppt" or ".pptx") { glyph = "P"; color = "#D88244"; }
        else if (ext is ".exe" or ".lnk") { glyph = "APP"; color = "#617AD0"; }
        string parent = System.IO.Path.GetDirectoryName(path.TrimEnd('\\')) ?? path;
        if (!IconBrushes.TryGetValue(color, out Brush? iconBrush))
        {
            iconBrush = (Brush)new BrushConverter().ConvertFromString(color)!;
            iconBrush.Freeze(); IconBrushes[color] = iconBrush;
        }
        return new EntryView { Id = id, Path = path, DisplayName = string.IsNullOrEmpty(name) ? FileName(path) : name, Subtitle = parent, TimeLabel = time, IconGlyph = glyph, IconBrush = iconBrush, Kind = kind, IsPinned = pinned };
    }

    internal async Task LoadKindsAsync(string[] paths)
    {
        PruneDirectoryKinds();
        var pending = paths.Distinct(StringComparer.OrdinalIgnoreCase).Where(p => !_directoryKinds.ContainsKey(p)).ToArray();
        if (pending.Length == 0) return;
        var found = await Task.Run(() => pending.Select(p => (Path: p, IsDirectory: Directory.Exists(p))).ToArray());
        var active = State.Pins.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string stale in _directoryKinds.Keys.Where(p => !active.Contains(p)).ToArray()) _directoryKinds.Remove(stale);
        foreach (var item in found) if (active.Contains(item.Path)) _directoryKinds[item.Path] = item.IsDirectory;
        ApplyFilters();
    }

    private void PruneDirectoryKinds()
    {
        if (_directoryKinds.Count == 0) return;
        var active = State.Pins.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string stale in _directoryKinds.Keys.Where(p => !active.Contains(p)).ToArray()) _directoryKinds.Remove(stale);
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
        if (!_selectedPaths.Contains(entry.Path)) SelectFile(entry, false);
        var menu = new ContextMenu();
        AddMenu(menu, _selectedPaths.Count > 1 ? $"复制 {_selectedPaths.Count} 个文件（Ctrl+C）" : "复制文件（Ctrl+C）", () => _ = CopySelectedFilesAsync());
        if (entry.Kind == "图片" && _selectedPaths.Count == 1) AddMenu(menu, "复制图片", () => _ = CopySelectedFilesAsync(true));
        menu.Items.Add(new Separator());
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
            var dialog = new Window { Title = "修改显示名称", Content = panel, Width = 340, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
            dialog.SetResourceReference(BackgroundProperty, "Surface"); dialog.SetResourceReference(ForegroundProperty, "Ink");
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
            if (path != null) { State.Pins.First(p => p.Id == entry.Id).Path = path; PruneDirectoryKinds(); Persist(); ApplyFilters(); _ = LoadKindsAsync(State.Pins.Select(p => p.Path).ToArray()); }
        }
        finally { _inDialog = false; }
    }

    internal void OpenSettings()
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateAppearanceMenu());
        menu.Items.Add(new Separator());
        AddMenu(menu, "添加文件夹…", AddFolderDialog);
        AddMenu(menu, "贴到左侧", () => { State.PanelLeft = State.PanelTop = null; State.DockSide = "Left"; PositionPanel(); PositionDock(); Persist(); });
        AddMenu(menu, "贴到右侧", () => { State.PanelLeft = State.PanelTop = null; State.DockSide = "Right"; PositionPanel(); PositionDock(); Persist(); });
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
        menu.Items.Add(new Separator());
        AddMenu(menu, "使用说明", () => { _inDialog = true; ShowThemedHelp("点击“＋ 添加”固定文件或文件夹；拖动常用卡片调整顺序。\n按住 Ctrl 点击多选；拖出面板复制，或 Ctrl+C 后在目标处 Ctrl+V。\n图片显示缩略图，悬停查看大图；右键可复制图片。\n使用本机 Snipaste 默认快捷键或系统截图快捷键时隐藏入口；截图结束后恢复。\n单击文件夹在面板内浏览，单击文件用默认应用打开。\n顶部可返回、上一级、回到常用；路径按钮可跳到祖先文件夹。\n浏览时输入关键词，自动搜索当前文件夹及所有子文件夹；清空搜索恢复当前层。\nCtrl + Alt + Space 展开/收起；Esc 收起。\n图钉开启后，打开文件和失去焦点都保持展开。\n\n首页显示 CPU/GPU 占用与温度、内存、显存和实时网速。\n首页展开时每 1 秒更新，CPU 温度每秒读取；收起或浏览文件夹时暂停采集。\n设置 → 外观：跟随系统、浅色、深色，立即生效。\n固定项及设置保存在程序旁的 Data 文件夹。"); _inDialog = false; });
        AddMenu(menu, "退出文件坞", Quit);
        ShowMenu(menu, SettingsButton);
    }
    private static bool StartupEnabled()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("FileDockDemo") is string; } catch { return false; }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("FileDockPin") && CurrentFolderPath == null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true;
    }
    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (CurrentFolderPath == null && e.Data.GetData("FileDockPin") is string id)
        {
            var target = FindEntry(e.OriginalSource as DependencyObject);
            MovePin(id, target?.Id);
        }
        e.Effects = CurrentFolderPath == null && e.Data.GetDataPresent("FileDockPin") ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true;
    }
    internal void MovePin(string id, string? targetId)
    {
        if (targetId == id) return;
        var pin = State.Pins.FirstOrDefault(p => p.Id == id);
        if (pin == null) return;
        State.Pins.Remove(pin);
        int index = targetId == null ? State.Pins.Count : State.Pins.FindIndex(p => p.Id == targetId);
        State.Pins.Insert(index < 0 ? State.Pins.Count : index, pin); ReorderAndSave();
    }
    private void ReorderAndSave() { PruneDirectoryKinds(); for (int i = 0; i < State.Pins.Count; i++) State.Pins[i].Order = i; Persist(); ApplyFilters(); }

    public void ExpandPanel()
    {
        RecordDiagnosticState("expand-request");
        _isCollapsed = false; _dock?.Hide(); PositionPanel(); Show(); Activate(); SearchBox.Focus();
        NativeMethods.RaiseWithoutActivation(new WindowInteropHelper(this).Handle);
        UpdatePerformanceActivity();
    }
    internal void CollapsePanel()
    {
        RecordDiagnosticState("collapse-request");
        if (App.IsSelfTest) { _isCollapsed = true; Hide(); return; }
        _isCollapsed = true; Hide(); PositionDock(); if (_captureActive) return; _dock?.Show();
        if (_dock != null) NativeMethods.RaiseWithoutActivation(_dock.Handle);
    }
    internal void DockMoved(double left, double top)
    {
        double scale = _dock!.ScaleFactor;
        _screen = Forms.Screen.FromHandle(_dock!.Handle);
        State.DockSide = left * scale < _screen.WorkingArea.Left + _screen.WorkingArea.Width / 2 ? "Left" : "Right";
        State.PanelLeft = State.PanelTop = null; State.PanelScreen = _screen.DeviceName; State.DockTop = top; PositionDock(); Persist();
    }
    private Rect WorkArea(Visual? target = null)
    {
        if (!Forms.Screen.AllScreens.Any(s => s.DeviceName == _screen.DeviceName)) _screen = Forms.Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName == State.PanelScreen) ?? Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
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
        if (State.PanelLeft is double left && State.PanelTop is double top) { var saved = ConstrainPanel(new Rect(left, top, Width, Height), area); Left = saved.Left; Top = saved.Top; }
    }
    private void PositionDock()
    {
        if (_dock == null) return;
        var bounds = _screen.WorkingArea; double scale = _dock.ScaleFactor;
        Rect area = new Rect(bounds.Left / scale, bounds.Top / scale, bounds.Width / scale, bounds.Height / scale);
        _dock.Left = State.DockSide == "Left" ? area.Left : area.Right - _dock.Width;
        _dock.Top = Math.Clamp(State.DockTop < 0 ? area.Top + area.Height / 2 - _dock.Height / 2 : State.DockTop, area.Top, Math.Max(area.Top, area.Bottom - _dock.Height));
        _dock.Invalidate();
    }
    private static Button? FindButton(DependencyObject? source)
    {
        while (source != null) { if (source is Button button) return button; source = source is Visual ? VisualTreeHelper.GetParent(source) : null; } return null;
    }
    private void Window_Deactivated(object sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() => { if (!_keepOpen && !_inDialog && !_dragging && !UpdateCaptureVisibility() && !IsActive && !AnyMenuOpen() && !App.IsSelfTest) CollapsePanel(); }, DispatcherPriority.Background);
    }
    private bool AnyMenuOpen() => _menuOpen || AddButton.ContextMenu?.IsOpen == true;
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleTransferKey(e) || HandleBrowserKey(e)) return;
        if (e.Key == Key.Escape) { if (Environment.TickCount64 >= _ignoreDragEscapeUntil) CollapsePanel(); e.Handled = true; }
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        if (e.Key == Key.Enter) { var entry = PinsList.SelectedItem as EntryView ?? _displayPins.FirstOrDefault(); if (entry != null) OpenEntry(entry); e.Handled = true; }
        if (e.Key == Key.Down && SearchBox.IsKeyboardFocused && PinsList.Items.Count > 0) { PinsList.SelectedIndex = 0; PinsList.Focus(); e.Handled = true; }
    }
    private void Window_Closing(object? sender, CancelEventArgs e) { if (!_quitting && !App.IsSelfTest) { e.Cancel = true; CollapsePanel(); } }
    private void DisplaySettingsChanged(object? sender, EventArgs e) {
        DiagnosticLog.Write("display-settings-changed");
        Dispatcher.BeginInvoke(() => { PositionPanel(); PositionDock(); UpdateTrayIcon(); RecordDiagnosticState("display-state"); });
    }
    private void UpdateKeepOpen() { KeepOpenButton.ToolTip = _keepOpen ? "已保持展开，点击取消" : "保持展开"; KeepOpenButton.SetResourceReference(Control.ForegroundProperty, _keepOpen ? "Accent" : "Muted"); }
    internal void SetStatus(string text, bool error = false) { if (_saveFailed && !error) return; StatusText.Text = text; StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "Error" : "Muted"); StatusText.ToolTip = text; }
    internal bool Persist() { try { Storage.Save(State); _saveFailed = false; return true; } catch (Exception ex) { _saveFailed = true; SetStatus("配置保存失败：" + ex.Message, true); return false; } }
    public void Quit() { RecordDiagnosticState("user-exit-request"); _quitting = true; Persist(); Application.Current.Shutdown(); }
    public void DisposeResources()
    {
        DisposeDiagnostics();
        StopFolderWatcher();
        DisposePerformance();
        CancelFolderLoad();
        _copyRequest?.Cancel(); _copyRequest?.Dispose();
        if (_foregroundHook != IntPtr.Zero) { NativeMethods.UnhookWinEvent(_foregroundHook); _foregroundHook = IntPtr.Zero; }
        if (_locationHook != IntPtr.Zero) { NativeMethods.UnhookWinEvent(_locationHook); _locationHook = IntPtr.Zero; }
        if (_reorderHook != IntPtr.Zero) { NativeMethods.UnhookWinEvent(_reorderHook); _reorderHook = IntPtr.Zero; }
        _captureRecovery?.Stop();
        if (_captureKeyHook != IntPtr.Zero) { NativeMethods.UnhookWindowsHookEx(_captureKeyHook); _captureKeyHook = IntPtr.Zero; }
        if (_captureHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_captureHook);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= TrayPreferenceChanged;
        if (_source != null) { NativeMethods.UnregisterHotKey(_source.Handle, NativeMethods.HotkeyId); _source.RemoveHook(WindowProc); }
        _tray?.Dispose(); _trayIcon?.Dispose(); _dock?.Close();
    }
}
