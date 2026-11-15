using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileDockDemo;

internal sealed class AppLauncherView
{
    private readonly Window _owner;
    private readonly Func<IReadOnlyList<ApplicationEntry>> _entries;
    private readonly Func<Action<List<ApplicationEntry>>, Task<bool>> _save;
    private readonly Action<string, bool> _status;
    private readonly Action<bool> _dialogState;
    private readonly Action<string> _launch;
    private readonly UniformGrid _tiles = new() { Columns = 3 };
    private readonly TextBlock _empty;
    private readonly Button _add;
    private readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private bool _saving;
    private int _dialogs;
    public FrameworkElement View { get; }

    internal AppLauncherView(Window owner, Func<IReadOnlyList<ApplicationEntry>> entries,
        Func<Action<List<ApplicationEntry>>, Task<bool>> save, Action<string, bool> status,
        Action<bool> dialogState, Action<string>? launch = null)
    {
        _owner = owner; _entries = entries; _save = save; _status = status; _dialogState = dialogState;
        _launch = launch ?? (path => { using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); });
        var root = new DockPanel();
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        _add = Button("＋ 添加", () => Pick(null)); _add.Padding = new Thickness(9, 5, 9, 5);
        _add.SetResourceReference(Control.ForegroundProperty, "Accent"); DockPanel.SetDock(_add, Dock.Right); heading.Children.Add(_add);
        heading.Children.Add(Label("应用", "Ink", 18, FontWeights.SemiBold));
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var content = new StackPanel();
        _empty = Label("添加常用应用，点击图标即可启动。\n支持 .exe 程序和 .lnk 快捷方式。", "Muted", 12);
        _empty.Margin = new Thickness(4, 14, 4, 14); content.Children.Add(_empty); content.Children.Add(_tiles);
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        View = root; KeyboardNavigation.SetTabNavigation(root, KeyboardNavigationMode.Continue); Refresh();
    }

    public void Refresh()
    {
        var entries = _entries().OrderBy(e => e.Order).ToList();
        // Icon cache is confined to paths explicitly added by the user.
        foreach (var stale in _icons.Keys.Where(k => !entries.Any(e => string.Equals(e.Path, k, StringComparison.OrdinalIgnoreCase))).ToList()) _icons.Remove(stale);
        _tiles.Children.Clear(); _empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var entry in entries)
        {
            bool valid = IsAvailable(entry.Path);
            var tile = new Grid { Margin = new Thickness(3), MinWidth = 0 };
            var content = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            ImageSource? icon = null;
            if (valid && !_icons.TryGetValue(entry.Path, out icon)) _icons[entry.Path] = icon = ReadIcon(entry.Path);
            if (icon != null) content.Children.Add(new Image { Source = icon, Width = 32, Height = 32, Margin = new Thickness(0, 2, 0, 7) });
            else content.Children.Add(new TextBlock { Text = valid ? "\uE8A5" : "\uE7BA", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 28,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 7) });
            var name = Label(entry.DisplayName, "Ink", 12); name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.TextWrapping = TextWrapping.NoWrap; name.TextAlignment = TextAlignment.Center; content.Children.Add(name);
            if (!valid) { var missing = Label("路径失效", "Muted", 10); missing.TextAlignment = TextAlignment.Center; content.Children.Add(missing); }
            var run = Button("", () => Launch(entry)); run.Content = content; run.MinHeight = 94;
            run.Padding = new Thickness(5, 5, 5, 19); run.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            run.SetResourceReference(Control.BackgroundProperty, "PaneCard"); run.ToolTip = valid ? entry.Path : "路径失效，请在菜单中重新选择：" + entry.Path;
            AutomationProperties.SetName(run, "启动 " + entry.DisplayName + (valid ? "" : "，路径失效"));
            var menu = BuildMenu(entry, entries.IndexOf(entry), entries.Count); run.ContextMenu = menu; tile.Children.Add(run);
            var more = Button("···", () => { menu.PlacementTarget = tile; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; });
            more.HorizontalAlignment = HorizontalAlignment.Right; more.VerticalAlignment = VerticalAlignment.Bottom;
            more.Padding = new Thickness(6, 0, 6, 0); more.Height = 20; more.ToolTip = "管理 " + entry.DisplayName;
            AutomationProperties.SetName(more, "管理 " + entry.DisplayName); tile.Children.Add(more); _tiles.Children.Add(tile);
        }
    }

    private ContextMenu BuildMenu(ApplicationEntry entry, int index, int count)
    {
        var menu = new ContextMenu(); menu.Resources.MergedDictionaries.Add(_owner.Resources);
        // Window resources (including theme brushes) are inherited through PlacementTarget.
        menu.Opened += (_, _) => EnterDialog(); menu.Closed += (_, _) => ExitDialog();
        void Item(string label, Action action, bool enabled = true) { var item = new MenuItem { Header = label, IsEnabled = enabled }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Item("启动", () => Launch(entry)); Item("重命名", () => RenameDialog(entry));
        Item("上移", async () => await MoveAsync(entry.Id, -1), index > 0); Item("下移", async () => await MoveAsync(entry.Id, 1), index < count - 1);
        Item("重新选择路径…", () => Pick(entry)); menu.Items.Add(new Separator()); Item("移除", async () => await RemoveAsync(entry.Id));
        return menu;
    }

    internal static bool IsSupportedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { var extension = System.IO.Path.GetExtension(path); return System.IO.Path.IsPathFullyQualified(path) &&
            (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)); }
        catch (ArgumentException) { return false; }
    }
    internal static bool IsAvailable(string? path) => IsSupportedPath(path) && File.Exists(path);
    private static string CheckedPath(string path)
    {
        if (!IsAvailable(path)) throw new InvalidOperationException("请选择存在的 .exe 程序或 .lnk 快捷方式。");
        return System.IO.Path.GetFullPath(path);
    }
    private static ApplicationEntry Find(List<ApplicationEntry> list, string id) => list.FirstOrDefault(e => e.Id == id) ?? throw new InvalidOperationException("此应用已移除，请刷新后重试。");
    private static void EnsureUnique(List<ApplicationEntry> list, string path, string? except = null)
    {
        if (list.Any(e => e.Id != except && string.Equals(System.IO.Path.GetFullPath(e.Path), path, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("此应用已经添加。");
    }
    internal Task<bool> AddAsync(string path) => Mutate(list => {
        var full = CheckedPath(path); EnsureUnique(list, full);
        list.Add(new ApplicationEntry { Path = full, DisplayName = System.IO.Path.GetFileNameWithoutExtension(full), Order = list.Count });
    }, "已添加应用。");
    internal Task<bool> RenameAsync(string id, string name) => Mutate(list => {
        var trimmed = name.Trim(); if (trimmed.Length == 0 || trimmed.Length > 80) throw new InvalidOperationException("应用名称需为 1 至 80 个字符。");
        Find(list, id).DisplayName = trimmed;
    }, "已重命名。");
    internal Task<bool> RelocateAsync(string id, string path) => Mutate(list => {
        var full = CheckedPath(path); EnsureUnique(list, full, id); Find(list, id).Path = full; _icons.Remove(full);
    }, "已更新应用路径。");
    internal Task<bool> RemoveAsync(string id) => Mutate(list => list.Remove(Find(list, id)), "已移除应用。");
    internal Task<bool> MoveAsync(string id, int delta) => Mutate(list => {
        var ordered = list.OrderBy(e => e.Order).ToList(); int index = ordered.FindIndex(e => e.Id == id);
        if (index < 0) throw new InvalidOperationException("此应用已移除。");
        int next = Math.Clamp(index + Math.Sign(delta), 0, ordered.Count - 1);
        var item = ordered[index]; ordered.RemoveAt(index); ordered.Insert(next, item); list.Clear(); list.AddRange(ordered);
    }, "已调整应用顺序。");
    private async Task<bool> Mutate(Action<List<ApplicationEntry>> mutation, string message)
    {
        if (_saving) { _status("正在保存，请稍后重试。", true); return false; }
        _saving = true; _add.IsEnabled = false;
        try
        {
            bool saved = await _save(list => {
                var ordered = list.OrderBy(e => e.Order).ToList(); list.Clear(); list.AddRange(ordered);
                mutation(list); for (int i = 0; i < list.Count; i++) list[i].Order = i;
            });
            if (!saved) { _status("保存失败，应用列表未更改。", true); return false; }
            Refresh(); _status(message, false); return true;
        }
        catch (Exception error) { _status("操作未完成：" + error.Message, true); return false; }
        finally { _saving = false; _add.IsEnabled = true; }
    }
    internal void Launch(ApplicationEntry entry)
    {
        if (!IsAvailable(entry.Path)) { Refresh(); _status("应用路径失效，请在管理菜单中重新选择路径。", true); return; }
        try { _launch(entry.Path); _status("已启动 " + entry.DisplayName, false); }
        catch (Exception error) { _status("无法启动应用：" + error.Message, true); }
    }

    private async void Pick(ApplicationEntry? entry)
    {
        EnterDialog();
        try
        {
            var picker = new OpenFileDialog { Title = entry == null ? "添加应用" : "重新选择应用路径", Filter = "程序和快捷方式 (*.exe;*.lnk)|*.exe;*.lnk",
                CheckFileExists = true, Multiselect = false, DereferenceLinks = false };
            if (picker.ShowDialog(_owner) == true)
            {
                if (entry == null) await AddAsync(picker.FileName); else await RelocateAsync(entry.Id, picker.FileName);
            }
        }
        catch (Exception error) { _status("无法选择应用：" + error.Message, true); }
        finally { ExitDialog(); }
    }
    private void RenameDialog(ApplicationEntry entry)
    {
        EnterDialog();
        try
        {
            var dialog = new Window { Owner = _owner, Title = "重命名应用", Width = 320, SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
                FontFamily = _owner.FontFamily };
            dialog.Resources.MergedDictionaries.Add(_owner.Resources);
            dialog.SetResourceReference(Control.BackgroundProperty, "Surface"); dialog.SetResourceReference(Control.ForegroundProperty, "Ink");
            var panel = new StackPanel { Margin = new Thickness(18) }; var input = new TextBox { Text = entry.DisplayName, MaxLength = 80, Margin = new Thickness(0, 8, 0, 14), MinHeight = 30 };
            input.SetResourceReference(Control.BackgroundProperty, "PaneInput"); input.SetResourceReference(Control.ForegroundProperty, "Ink");
            panel.Children.Add(Label("应用名称", "Ink", 13)); panel.Children.Add(input);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = Button("取消", () => dialog.Close()); cancel.IsCancel = true;
            var save = Button("保存", async () => { if (await RenameAsync(entry.Id, input.Text)) dialog.Close(); }); save.IsDefault = true;
            actions.Children.Add(cancel); actions.Children.Add(save); panel.Children.Add(actions); dialog.Content = panel;
            dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); }; dialog.ShowDialog();
        }
        finally { ExitDialog(); }
    }
    private void EnterDialog() { if (_dialogs++ == 0) _dialogState(true); }
    private void ExitDialog() { if (_dialogs > 0 && --_dialogs == 0) _dialogState(false); }
    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(9, 5, 9, 5) };
        button.SetResourceReference(FrameworkElement.StyleProperty, "QuietButton"); button.SetResourceReference(Control.ForegroundProperty, "Ink");
        button.Click += (_, _) => action(); return button;
    }
    private static TextBlock Label(string text, string brush, double size, FontWeight? weight = null)
    {
        var label = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontWeight = weight ?? FontWeights.Normal };
        label.SetResourceReference(TextBlock.ForegroundProperty, brush); return label;
    }

    // Ask the Windows shell only for explicit local entries; do not load application images or enumerate installed software.
    private static ImageSource? ReadIcon(string path)
    {
        // A remote shortcut can trigger shell/network resolution; leave it on the generic glyph.
        IntPtr icon = IntPtr.Zero;
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(System.IO.Path.GetPathRoot(path)!).DriveType == DriveType.Network) return null;
            if (SHGetFileInfo(path, 0, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100) == IntPtr.Zero) return null;
            icon = info.Icon;
            if (icon == IntPtr.Zero) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32)); source.Freeze(); return source;
        }
        catch { return null; }
        finally { if (icon != IntPtr.Zero) DestroyIcon(icon); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo { public IntPtr Icon; public int IconIndex; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
}
