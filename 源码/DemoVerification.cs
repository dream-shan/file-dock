using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FileDockDemo;

internal static class DemoVerification
{
    internal static async Task Run(MainWindow window, string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAIL: " + name); checks.Add("PASS: " + name); }
        string fixture = Path.Combine(directory, "中文与空格 files");
        Directory.CreateDirectory(fixture);
        string project = Path.Combine(fixture, "项目资料"); Directory.CreateDirectory(project);
        string note = Path.Combine(fixture, "每周周报.txt"), budget = Path.Combine(fixture, "项目预算.csv"), plan = Path.Combine(fixture, "产品方案.txt");
        File.WriteAllText(note, "仅供演示版自动验证使用。\n"); File.WriteAllText(budget, "项目,金额\n设计,300\n"); File.WriteAllText(plan, "文件坞验证文件。\n");
        window.Show();
        window.AddPins(new[] { project, note, budget, plan, note.ToUpperInvariant() });
        Check(window.State.Pins.Count == 4, "Pin files/folders and deduplicate paths");
        Check(window.Storage.Load().Pins.Count == 4, "Persist and reload pin order");
        Check(File.Exists(note) && File.Exists(budget), "Pinning preserves source files");
        window.SearchBox.Text = "预算";
        Check(window.PinsList.Items.Count == 1, "Search by Chinese filename");
        window.SearchBox.Text = ""; window.FilterComboBox.SelectedIndex = 2;
        Check(window.PinsList.Items.Count == 1, "Filter spreadsheets");
        window.FilterComboBox.SelectedIndex = 0;
        window.State.History = new List<RecentEntry>
        {
            new() { Path = plan, DisplayName = "产品方案.txt", LastUsed = DateTime.Now, Source = "从这里打开" },
            new() { Path = note, DisplayName = "每周周报.txt", LastUsed = DateTime.Now.AddMinutes(-12), Source = "从这里打开" },
            new() { Path = budget, DisplayName = "项目预算.csv", LastUsed = DateTime.Now.AddHours(-1), Source = "从这里打开" },
            new() { Path = note.ToUpperInvariant(), DisplayName = "duplicate", LastUsed = DateTime.Now.AddDays(-1), Source = "从这里打开" }
        };
        window.ApplyFilters();
        Check(window.RecentList.Items.Count == 3, "Merge duplicate recent records");
        window.State.HiddenRecent.Add(note); window.ApplyFilters();
        Check(window.RecentList.Items.Count == 2, "Hide recent record locally");
        window.State.HiddenRecent.Clear(); window.ApplyFilters();
        var saved = window.Storage.Load();
        saved.DockSide = "Left"; window.Storage.Save(saved);
        File.WriteAllText(Path.Combine(directory, "data", "state.json"), "{invalid");
        var recovered = window.Storage.Load();
        Check(recovered.Pins.Count == 4, "Recover corrupt configuration from backup");
        window.Storage.Save(recovered);
        Check(window.Storage.Load().Pins.Count == 4, "Save after backup recovery");
        string shortcut = Path.Combine(fixture, "启动参数验证.lnk"), marker = Path.Combine(fixture, "launch-result.txt");
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        object link = ((dynamic)shell).CreateShortcut(shortcut);
        try
        {
            ((dynamic)link).TargetPath = Environment.ProcessPath!;
            ((dynamic)link).Arguments = "--launch-probe \"" + marker + "\"";
            ((dynamic)link).WorkingDirectory = fixture;
            ((dynamic)link).Save();
        }
        finally { Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(shell); }
        FileService.Open(shortcut);
        for (int i = 0; i < 60 && !File.Exists(marker); i++) await Task.Delay(100);
        Check(File.Exists(marker) && FileService.NormalizeKey(File.ReadAllText(marker)) == FileService.NormalizeKey(fixture), "Shell opens shortcut preserving arguments and working directory");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.SetStatus("演示界面 · 单击打开，右键管理");
        SaveRender(window, Path.Combine(directory, "ui-standard.png"));
        window.Width = 380; window.Height = 480;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        SaveRender(window, Path.Combine(directory, "ui-small.png"));
        Check(window.RecentList.ActualHeight > 40 && window.DropZone.ActualHeight > 20, "Minimum window size preserves recent list and drop zone");
        window.Width = 430; window.Height = 650;
        window.CollapsePanel(); Check(!window.IsVisible, "Collapse hides main window");
        window.Show(); Check(window.IsVisible, "Expanded window can be restored");
        int recentCount = await Task.Run(() => RecentService.LoadSystemRecent(100).Count);
        Check(recentCount >= 0 && recentCount <= 100, "Read real Windows Recent entries via COM");
        checks.Add("System recent count: " + recentCount);

        // Exercise the same entry-opening path used by pinned cards and browser rows.
        string child = Path.Combine(project, "设计稿.v2"), empty = Path.Combine(project, "空文件夹");
        Directory.CreateDirectory(child); Directory.CreateDirectory(empty);
        string childNote = Path.Combine(child, "仅在子目录.txt");
        string browserNote = Path.Combine(project, "会议纪要.txt");
        string browserBudget = Path.Combine(project, "本月预算.csv");
        string extensionless = Path.Combine(project, "README");
        File.WriteAllText(childNote, "子目录中的文件。");
        File.WriteAllText(browserNote, "从文件坞内浏览。");
        File.WriteAllText(browserBudget, "项目,金额\n资料,20\n");
        File.WriteAllText(extensionless, "这是无扩展名的文件。");
        string browserShortcut = Path.Combine(project, "启动验证.lnk");
        string browserMarker = Path.Combine(fixture, "browser-launch-result.txt");
        object browserShell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        object browserLink = ((dynamic)browserShell).CreateShortcut(browserShortcut);
        try
        {
            ((dynamic)browserLink).TargetPath = Environment.ProcessPath!;
            ((dynamic)browserLink).Arguments = "--launch-probe \"" + browserMarker + "\"";
            ((dynamic)browserLink).WorkingDirectory = project;
            ((dynamic)browserLink).Save();
        }
        finally { Marshal.FinalReleaseComObject(browserLink); Marshal.FinalReleaseComObject(browserShell); }
        bool AtFolder(string path) => window.CurrentFolderPath != null && FileService.NormalizeKey(window.CurrentFolderPath) == FileService.NormalizeKey(path);
        EntryView[] FolderEntries() => window.FolderList.Items.Cast<EntryView>().ToArray();
        EntryView FolderEntry(string path) => FolderEntries().Single(e => FileService.NormalizeKey(e.Path) == FileService.NormalizeKey(path));

        await window.OpenEntryAsync(new EntryView { Path = project, DisplayName = "项目资料", Kind = "文档" });
        Check(AtFolder(project) && window.IsVisible && window.BrowserPanel.Visibility == Visibility.Visible && FolderEntries().Length == 6,
            "Opening a folder displays its immediate contents inside the visible panel");
        Check(FolderEntry(child).Kind == "文件夹" && FolderEntry(extensionless).Kind != "文件夹",
            "Directory names with dots and files without extensions use actual filesystem types");

        window.SearchBox.Text = "预算";
        Check(FolderEntries().Length == 1 && FolderEntry(browserBudget).Path == browserBudget, "Search matches Chinese filenames inside current folder");
        window.SearchBox.Text = "仅在子目录";
        Check(FolderEntries().Length == 0 && window.FolderEmptyText.Visibility == Visibility.Visible, "Folder search stays within the current level");
        window.SearchBox.Text = "";
        window.FolderFilterComboBox.SelectedIndex = 2;
        Check(FolderEntries().Length == 1 && FolderEntry(browserBudget).Path == browserBudget, "Folder type filter selects spreadsheets");
        window.FolderFilterComboBox.SelectedIndex = 4;
        Check(FolderEntries().Length == 2 && FolderEntries().All(e => e.Kind == "文件夹"), "Folder type filter selects actual directories");
        window.FolderFilterComboBox.SelectedIndex = 0;

        await window.OpenEntryAsync(FolderEntry(child));
        Check(AtFolder(child) && FolderEntries().Length == 1 && FolderEntry(childNote).Path == childNote, "Opening a child folder replaces the browser contents");
        await window.GoBackAsync();
        Check(AtFolder(project) && FolderEntries().Length == 6, "Back restores the previous folder");
        await window.OpenEntryAsync(FolderEntry(child));
        await window.GoUpAsync();
        Check(AtFolder(project) && FolderEntries().Length == 6, "Up navigates to the parent directory");
        window.GoHome();
        Check(window.CurrentFolderPath == null && window.BrowserPanel.Visibility == Visibility.Collapsed && window.PinsList.Items.Count == 4,
            "Home restores pinned entries without changing them");
        var pendingFolder = window.NavigateToFolderAsync(project);
        window.GoHome();
        await pendingFolder;
        Check(window.CurrentFolderPath == null && window.BrowserPanel.Visibility == Visibility.Collapsed && window.PinsList.Items.Count == 4,
            "Returning home cancels pending folder results without reopening the browser");

        await window.NavigateToFolderAsync(project);
        await window.OpenEntryAsync(FolderEntry(empty));
        Check(AtFolder(empty) && FolderEntries().Length == 0 && window.FolderEmptyText.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(window.FolderEmptyText.Text),
            "Empty folders show an explicit empty state");
        string emptyMessage = window.FolderEmptyText.Text;
        await window.NavigateToFolderAsync(Path.Combine(project, "不存在的文件夹"));
        Check(window.IsVisible && window.BrowserPanel.Visibility == Visibility.Visible && FolderEntries().Length == 0 &&
            window.FolderEmptyText.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(window.FolderEmptyText.Text) && window.FolderEmptyText.Text != emptyMessage,
            "Unavailable folder displays an error without leaving stale contents");

        await window.NavigateToFolderAsync(project);
        if (!window.State.KeepOpen) window.KeepOpenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await window.OpenEntryAsync(FolderEntry(browserShortcut));
        for (int i = 0; i < 60 && !File.Exists(browserMarker); i++) await Task.Delay(100);
        Check(File.Exists(browserMarker) && FileService.NormalizeKey(File.ReadAllText(browserMarker)) == FileService.NormalizeKey(project),
            "Opening a shortcut from folder contents preserves launch arguments and working directory");
        Check(AtFolder(project) && window.IsVisible && window.BrowserPanel.Visibility == Visibility.Visible,
            "Opening a file preserves the browsed folder when keep-open is enabled");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        SaveRender(window, Path.Combine(directory, "folder-standard.png"));
        window.Width = 380; window.Height = 480;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        SaveRender(window, Path.Combine(directory, "folder-small.png"));
        Check(window.FolderList.ActualHeight > 80 && window.FolderBackButton.IsVisible && window.FolderUpButton.IsVisible &&
            window.FolderHomeButton.IsVisible && window.FolderRefreshButton.IsVisible && window.DropZone.ActualHeight > 20,
            "Minimum window size preserves folder contents, navigation and drop zone");
        window.Width = 430; window.Height = 650;
        window.GoHome();
        File.WriteAllLines(Path.Combine(directory, "verification.txt"), checks);
        window.Close();
    }

    private static void SaveRender(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
