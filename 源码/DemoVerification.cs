using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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
        bool IsUtilityWindow(Window target)
        {
            long style = NativeMethods.GetExtendedWindowStyle(new WindowInteropHelper(target).Handle);
            return !target.ShowInTaskbar && (style & 0x80) != 0 && (style & 0x40000) == 0 && (style & 0x08000000) == 0;
        }
        Check(IsUtilityWindow(window), "Main panel is excluded from Alt+Tab and taskbar while remaining activatable");
        var dock = window.DockEntry;
        dock.Left = -10000; dock.Top = -10000;
        dock.Show();
        Check((!dock.ShowInTaskbar && (NativeMethods.GetExtendedWindowStyle(dock.Handle) & 0x80) != 0 && (NativeMethods.GetExtendedWindowStyle(dock.Handle) & 0x40000) == 0), "Edge entry is excluded from Alt+Tab and taskbar");
        dock.Hide(); dock.Show();
        Check((!dock.ShowInTaskbar && (NativeMethods.GetExtendedWindowStyle(dock.Handle) & 0x80) != 0 && (NativeMethods.GetExtendedWindowStyle(dock.Handle) & 0x40000) == 0), "Edge entry retains utility style after hiding and showing");
        dock.Hide();
        window.AddPins(new[] { project, note, budget, plan, note.ToUpperInvariant() });
        Check(window.State.Pins.Count == 4, "Pin files/folders and deduplicate paths");
        Check(window.Storage.Load().Pins.Count == 4, "Persist and reload pin order");
        Check(File.Exists(note) && File.Exists(budget), "Pinning preserves source files");
        window.SearchBox.Text = "预算";
        Check(window.PinsList.Items.Count == 1, "Search by Chinese filename");
        window.SearchBox.Clear();
        var saved = window.Storage.Load();
        saved.DockSide = "Left"; window.Storage.Save(saved);
        File.WriteAllText(Path.Combine(directory, "data", "state.json"), "{invalid");
        var recovered = window.Storage.Load();
        Check(recovered.Pins.Count == 4, "Recover corrupt configuration from backup");
        window.Storage.Save(recovered);
        Check(window.Storage.Load().Pins.Count == 4, "Save after backup recovery");
        string shortcut = Path.Combine(fixture, "启动参数验证.lnk"), marker = Path.Combine(fixture, "launch-result.txt");
        CreateProbeShortcut(shortcut, marker, fixture);
        FileService.Open(shortcut);
        for (int i = 0; i < 60 && !File.Exists(marker); i++) await Task.Delay(100);
        Check(File.Exists(marker) && FileService.NormalizeKey(File.ReadAllText(marker)) == FileService.NormalizeKey(fixture), "Shell opens shortcut preserving arguments and working directory");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.SetStatus("演示界面 · 单击打开，右键管理");
        SaveRender(window, Path.Combine(directory, "ui-standard.png"));
        window.Width = 380; window.Height = 480;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        SaveRender(window, Path.Combine(directory, "ui-small.png"));
        Check(window.PerformanceSection.ActualHeight > 150 && window.AddButton.IsVisible, "Minimum window size preserves performance panel and add button");
        window.Width = 430; window.Height = 650;
        window.CollapsePanel(); Check(!window.IsVisible, "Collapse hides main window");
        window.Show(); Check(window.IsVisible, "Expanded window can be restored");
        Check(IsUtilityWindow(window), "Main panel retains utility style after hiding and showing");
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
        CreateProbeShortcut(browserShortcut, browserMarker, project);
        bool AtFolder(string path) => window.CurrentFolderPath != null && FileService.NormalizeKey(window.CurrentFolderPath) == FileService.NormalizeKey(path);
        EntryView[] FolderEntries() => window.FolderList.Items.Cast<EntryView>().ToArray();
        EntryView FolderEntry(string path) => FolderEntries().Single(e => FileService.NormalizeKey(e.Path) == FileService.NormalizeKey(path));

        Check(window.FindName("IncludeSubfoldersCheckBox") == null, "Folder search has no subfolder scope switch");
        await window.OpenEntryAsync(new EntryView { Path = project, DisplayName = "项目资料", Kind = "文档" });
        Check(AtFolder(project) && window.IsVisible && window.BrowserPanel.Visibility == Visibility.Visible && FolderEntries().Length == 6,
            "Opening a folder displays its immediate contents inside the visible panel");
        Check(FolderEntry(child).Kind == "文件夹" && FolderEntry(extensionless).Kind != "文件夹",
            "Directory names with dots and files without extensions use actual filesystem types");

        window.SearchBox.Text = "预算";
        await window.WaitForFolderSearchAsync();
        Check(FolderEntries().Length == 1 && FolderEntry(browserBudget).Path == browserBudget, "Search matches Chinese filenames inside current folder");
        window.SearchBox.Text = "仅在子目录";
        await window.WaitForFolderSearchAsync();
        Check(FolderEntries().Length == 1 && FolderEntry(childNote).Path == childNote, "Folder search automatically includes child folders without a scope switch");
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
            window.FolderHomeButton.IsVisible && window.FolderRefreshButton.IsVisible,
            "Minimum window size preserves folder contents and navigation");
        window.Width = 430; window.Height = 650;
        window.GoHome();

        // Keep the recursive-search fixture separate so the original browser
        // item counts and existing file-opening checks remain meaningful.
        string searchRoot = Path.Combine(fixture, "递归搜索样本");
        string branchA = Path.Combine(searchRoot, "分组甲");
        string branchB = Path.Combine(searchRoot, "分组乙");
        string deep = Path.Combine(branchA, "更深一层");
        string foundFolder = Path.Combine(branchA, "查找目标文件夹");
        Directory.CreateDirectory(deep);
        Directory.CreateDirectory(branchB);
        Directory.CreateDirectory(foundFolder);
        string currentNote = Path.Combine(searchRoot, "当前记录.txt");
        string deepNote = Path.Combine(deep, "深层报告.TXT");
        string duplicateA = Path.Combine(branchA, "共享预算.csv");
        string duplicateB = Path.Combine(branchB, "共享预算.csv");
        string budgetExplanation = Path.Combine(deep, "预算说明.txt");
        string foundFolderNote = Path.Combine(foundFolder, "目录内记录.txt");
        foreach (string path in new[] { currentNote, deepNote, duplicateA, duplicateB, budgetExplanation, foundFolderNote })
            File.WriteAllText(path, "递归搜索自动验证。\n");
        string recursiveShortcut = Path.Combine(deep, "递归启动验证.lnk");
        File.Copy(browserShortcut, recursiveShortcut);

        async Task SearchTree(string query)
        {
            window.SearchBox.Text = query;
            await window.WaitForFolderSearchAsync();
        }

        await window.NavigateToFolderAsync(searchRoot);
        await SearchTree("深层报告.txt");
        Check(FolderEntries().Length == 1 && FolderEntry(deepNote).Path == deepNote,
            "Recursive search finds Chinese filenames several levels deep with case-insensitive extensions");
        Check(FolderEntry(deepNote).Subtitle.Contains(Path.Combine("分组甲", "更深一层"), StringComparison.Ordinal),
            "Recursive results show the relative parent directory");
        await SearchTree("共享预算");
        Check(FolderEntries().Length == 2 && FolderEntry(duplicateA).Subtitle.Contains("分组甲", StringComparison.Ordinal) &&
            FolderEntry(duplicateB).Subtitle.Contains("分组乙", StringComparison.Ordinal) &&
            FolderEntry(duplicateA).Subtitle != FolderEntry(duplicateB).Subtitle,
            "Same-named files in different subfolders remain distinct and show their locations");

        await SearchTree("预算");
        Check(FolderEntries().Length == 3, "Recursive search combines matches from different levels");
        window.FolderFilterComboBox.SelectedIndex = 2;
        Check(FolderEntries().Length == 2 && FolderEntries().All(e => e.Kind == "表格"),
            "Type filtering applies to recursive results");
        window.FolderFilterComboBox.SelectedIndex = 0;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        SaveRender(window, Path.Combine(directory, "search-standard.png"));
        window.Width = 380; window.Height = 480;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        SaveRender(window, Path.Combine(directory, "search-small.png"));
        Check(window.FolderList.ActualHeight > 80 && window.FolderFilterComboBox.IsVisible &&
            window.SearchPlaceholder.Text == "搜索当前文件夹及子文件夹",
            "Minimum window size preserves recursive results, type filter, and search hint");
        window.Width = 430; window.Height = 650;

        window.SearchBox.Text = "共享预算";
        Task replacedSearch = window.WaitForFolderSearchAsync();
        window.SearchBox.Text = "深层报告";
        await window.WaitForFolderSearchAsync();
        await replacedSearch;
        Check(FolderEntries().Length == 1 && FolderEntry(deepNote).Path == deepNote,
            "Rapid query replacement cancels the old search without restoring stale results");

        window.SearchBox.Text = "共享预算";
        await window.NavigateToFolderAsync(branchA);
        await window.WaitForFolderSearchAsync();
        Check(AtFolder(branchA) && FolderEntries().All(e => FileService.NormalizeKey(Path.GetDirectoryName(e.Path)!) == FileService.NormalizeKey(branchA)),
            "Switching folders cancels the old query and shows only the new current level");
        await SearchTree("共享预算");
        Check(FolderEntries().Length == 1 && FolderEntry(duplicateA).Path == duplicateA,
            "A new folder search stays within the newly selected folder tree");
        await window.NavigateToFolderAsync(searchRoot);

        window.SearchBox.Text = "深层报告";
        Task clearedSearch = window.WaitForFolderSearchAsync();
        window.SearchBox.Clear();
        await clearedSearch;
        Check(FolderEntries().Length == 3 && FolderEntry(currentNote).Path == currentNote &&
            FolderEntries().All(e => FileService.NormalizeKey(Path.GetDirectoryName(e.Path)!) == FileService.NormalizeKey(searchRoot)),
            "Clearing a pending recursive query restores only the current folder contents");
        Check(window.StatusText.Text.Contains("当前层显示", StringComparison.Ordinal), "Clearing a recursive query clears the previous search status");

        await SearchTree("查找目标文件夹");
        await window.OpenEntryAsync(FolderEntry(foundFolder));
        Check(AtFolder(foundFolder) && FolderEntries().Length == 1 && FolderEntry(foundFolderNote).Path == foundFolderNote,
            "Opening a recursive folder result navigates to that exact folder inside the panel");
        await window.NavigateToFolderAsync(searchRoot);
        await SearchTree("递归启动验证");
        File.Delete(browserMarker);
        await window.OpenEntryAsync(FolderEntry(recursiveShortcut));
        for (int i = 0; i < 60 && !File.Exists(browserMarker); i++) await Task.Delay(100);
        Check(File.Exists(browserMarker) && FileService.NormalizeKey(File.ReadAllText(browserMarker)) == FileService.NormalizeKey(project),
            "Opening a recursive file result preserves shortcut arguments and working directory");

        window.SearchBox.Text = "共享预算";
        Task homeSearch = window.WaitForFolderSearchAsync();
        window.GoHome();
        await homeSearch;
        Check(window.CurrentFolderPath == null && window.BrowserPanel.Visibility == Visibility.Collapsed &&
            window.FolderList.Items.Count == 0 && window.PinsList.Items.Count == 4,
            "Returning home cancels recursive search without repopulating the browser");

        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            bool cancelled = false;
            try { await FolderSearchService.SearchAsync(searchRoot, "预算", cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Recursive search honors an already-cancelled token");
        }
        string missingRoot = Path.Combine(fixture, "不存在的搜索根目录");
        var emptySearch = await FolderSearchService.SearchAsync(missingRoot, "   ", CancellationToken.None);
        Check(emptySearch.Items.Count == 0 && !emptySearch.Truncated,
            "An empty query does not enumerate even a nonexistent root");
        bool missingRootFailed = false;
        try { await FolderSearchService.SearchAsync(missingRoot, "报告", CancellationToken.None); }
        catch (IOException) { missingRootFailed = true; }
        Check(missingRootFailed, "A missing search root reports failure rather than empty results");

        string hiddenNote = Path.Combine(deep, "隐藏唯一.txt");
        File.WriteAllText(hiddenNote, "隐藏文件。\n");
        File.SetAttributes(hiddenNote, File.GetAttributes(hiddenNote) | FileAttributes.Hidden);
        var hiddenSearch = await FolderSearchService.SearchAsync(searchRoot, "隐藏唯一", CancellationToken.None);
        Check(hiddenSearch.Items.Count == 0, "Recursive search excludes hidden entries");

        string linkTarget = Path.Combine(fixture, "链接目标原件");
        Directory.CreateDirectory(linkTarget);
        string targetNote = Path.Combine(linkTarget, "链接内部唯一.txt");
        File.WriteAllText(targetNote, "链接目标不得被修改。\n");
        string linkPath = Path.Combine(searchRoot, "目录链接");
        bool createdLink = false;
        string linkCreationFailure = "";
        try { Directory.CreateSymbolicLink(linkPath, linkTarget); createdLink = true; }
        catch (UnauthorizedAccessException ex) { linkCreationFailure = ex.Message; }
        catch (IOException ex) { linkCreationFailure = ex.Message; }
        catch (PlatformNotSupportedException ex) { linkCreationFailure = ex.Message; }
        if (!createdLink)
        {
            try
            {
                string fixturePrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fixture)) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(linkPath).StartsWith(fixturePrefix, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFullPath(linkTarget).StartsWith(fixturePrefix, StringComparison.OrdinalIgnoreCase) ||
                    Directory.Exists(linkPath) || File.Exists(linkPath))
                    throw new IOException("Junction test paths must be new paths inside this verification fixture.");
                var start = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-NonInteractive");
                start.ArgumentList.Add("-Command");
                // Keep paths out of shell source: each is supplied as process-local
                // data to this fixed command, with no interpolation or deletion.
                start.ArgumentList.Add("$ErrorActionPreference = 'Stop'\nNew-Item -ItemType Junction -Path $env:FILEDOCK_VERIFY_LINK -Target $env:FILEDOCK_VERIFY_TARGET | Out-Null");
                start.Environment["FILEDOCK_VERIFY_LINK"] = linkPath;
                start.Environment["FILEDOCK_VERIFY_TARGET"] = linkTarget;
                using var process = Process.Start(start) ?? throw new IOException("Could not start junction creation helper.");
                Task<string> errorOutput = process.StandardError.ReadToEndAsync();
                Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    process.Kill();
                    await process.WaitForExitAsync();
                    throw new IOException("Junction creation helper timed out.");
                }
                string error = await errorOutput;
                await standardOutput;
                if (process.ExitCode != 0) throw new IOException("Junction creation failed: " + error.Trim());
                createdLink = (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0;
                if (!createdLink) throw new IOException("Junction creation did not produce a reparse point.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { linkCreationFailure += " Junction fallback: " + ex.Message; }
        }
        if (createdLink)
        {
            try
            {
                var linkedContents = await FolderSearchService.SearchAsync(searchRoot, "链接内部唯一", CancellationToken.None);
                var linkEntry = await FolderSearchService.SearchAsync(searchRoot, "目录链接", CancellationToken.None);
                Check(linkedContents.Items.Count == 0 && linkEntry.Items.Count == 1 && linkEntry.Items[0].IsDirectory,
                    "Recursive search can match a directory link without following its contents");
                var explicitLinkRoot = await FolderSearchService.SearchAsync(linkPath, "链接内部唯一", CancellationToken.None);
                Check(explicitLinkRoot.Items.Count == 1, "An explicitly selected linked root remains searchable");
            }
            finally { Directory.Delete(linkPath); }
            Check(File.Exists(targetNote) && File.ReadAllText(targetNote) == "链接目标不得被修改。\n",
                "Directory-link checks preserve the original target and remove only the test link");
        }
        else checks.Add("SKIP: Could not create a symbolic link or junction: " + linkCreationFailure.Replace('\r', ' ').Replace('\n', ' '));

        string limitRoot = Path.Combine(fixture, "搜索数量上限");
        Directory.CreateDirectory(limitRoot);
        await Task.Run(() =>
        {
            for (int i = 0; i < 1001; i++)
                File.WriteAllText(Path.Combine(limitRoot, $"上限匹配-{i:D4}.txt"), "");
        });
        var limitedSearch = await FolderSearchService.SearchAsync(limitRoot, "上限匹配", CancellationToken.None);
        Check(limitedSearch.Items.Count == 1000 && limitedSearch.Truncated,
            "Recursive search bounds results at 1000 and reports an incomplete traversal");

        window.GoHome();
        window.SearchBox.Text = "";
        int cachedKinds = window.CachedPathKindCount;
        await window.NavigateToFolderAsync(limitRoot);
        Check(window.FolderList.Items.Count == 1001 && window.CachedPathKindCount == cachedKinds,
            "Browsing a large folder does not retain its paths in the home metadata cache");
        window.GoHome();
        var originalPins = window.State.Pins;
        window.State.Pins = Enumerable.Range(0, 500).Select(i => new PinnedEntry
        { Id = "perf-" + i, Path = i == 499 ? project : Path.Combine(limitRoot, $"上限匹配-{i:D4}.txt"), DisplayName = $"常用-{i:D4}", Order = i }).ToList();
        window.ApplyFilters(); window.UpdateLayout();
        T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent is T match) return match;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            { var found = FindVisual<T>(VisualTreeHelper.GetChild(parent, i)); if (found != null) return found; }
            return null;
        }
        var pinPanel = FindVisual<VirtualizingPinPanel>(window.PinsList)!;
        Check(pinPanel != null && VisualTreeHelper.GetChildrenCount(pinPanel) <= 12 && window.PinsList.Items.Count == 500,
            "500 pins realize only visible card containers");
        pinPanel!.SetVerticalOffset(double.PositiveInfinity); window.UpdateLayout();
        var finalCard = window.PinsList.ItemContainerGenerator.ContainerFromIndex(499) as FrameworkElement;
        Check(finalCard?.DataContext is EntryView last && last.Id == "perf-499", "Scrolling reaches the last virtualized pin with correct binding");
        Check(VisualTreeHelper.GetChildrenCount(pinPanel) <= 12, "Scrolling releases offscreen pin containers");
        FindVisual<Button>(finalCard!)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int i = 0; i < 100 && !AtFolder(project); i++) await Task.Delay(20);
        Check(AtFolder(project), "Clicking a virtualized final card opens its own folder inside the panel");
        window.GoHome(); window.UpdateLayout();
        pinPanel.SetVerticalOffset(double.PositiveInfinity); window.UpdateLayout();
        window.SearchBox.Text = "常用-0499"; window.UpdateLayout();
        Check(window.PinsList.Items.Count == 1 && pinPanel.VerticalOffset == 0 &&
            ((EntryView)window.PinsList.Items[0]).Id == "perf-499", "Filtering from the final row resets scrolling and preserves item identity");
        window.SearchBox.Text = "no-such-pin"; window.UpdateLayout();
        Check(window.PinsList.Items.Count == 0 && VisualTreeHelper.GetChildrenCount(pinPanel) == 0, "Empty pin results release realized containers");
        window.SearchBox.Text = ""; window.Width = 380; window.Height = 480; window.UpdateLayout();
        Check(VisualTreeHelper.GetChildrenCount(pinPanel) <= 8 && window.PerformanceSection.ActualHeight > 150,
            "Virtualized pins preserve the compact window layout");
        window.Width = 430; window.Height = 650; window.UpdateLayout();
        var timings = new List<double>();
        for (int i = 0; i < 20; i++)
        {
            var watch = Stopwatch.StartNew();
            window.SearchBox.Text = i % 2 == 0 ? "常用-04" : "";
            window.UpdateLayout(); timings.Add(watch.Elapsed.TotalMilliseconds);
        }
        checks.Add($"PERF: 500-pin filter/layout median={timings.OrderBy(t => t).ElementAt(10):F2}ms max={timings.Max():F2}ms, realized={VisualTreeHelper.GetChildrenCount(pinPanel)}");
        SaveRender(window, Path.Combine(directory, "ui-500-pins.png"));
        window.MovePin("perf-499", "perf-0"); window.UpdateLayout();
        Check(window.State.Pins[0].Id == "perf-499" && ((EntryView)window.PinsList.Items[0]).Id == "perf-499",
            "Drag reorder path preserves pin identity and refreshes the virtualized first row");
        window.State.Pins = originalPins; window.ApplyFilters();
        string restartData = Path.Combine(directory, "restart-data");
        new StorageService(restartData).Save(new AppState { Pins = originalPins });
        var restarted = new MainWindow(restartData) { Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            restarted.Show(); restarted.UpdateLayout();
            Check(restarted.PinsList.Items.Count == originalPins.Count &&
                restarted.PinsList.ItemContainerGenerator.ContainerFromIndex(0) != null,
                "Starting with saved pins initializes and renders virtualized cards");
        }
        finally { restarted.DisposeResources(); restarted.Close(); }
        Check(dock.Width == 20 && dock.Height == 48 && dock.Opacity < 0.5, "Edge entry is compact and translucent while idle");
        dock.Show();
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        Check(NativeMethods.RaiseWithoutActivation(dock.Handle) &&
            NativeMethods.GetForegroundWindow() == foreground, "Raising the edge entry does not steal foreground focus");
        dock.Hide();

        IconVerification.Run(window, Check);
        await FeatureVerification.Run(window, directory, Check);
        await PerformanceVerification.Run(window, directory, Check);
        await ThemeVerification.Run(window, directory, Check);
        await StabilityVerification.Run(window, directory, Check);
        ReleaseVerification.Run(window, Check);
        MovementVerification.Run(window, directory, Check);
        await LiveFolderVerification.Run(window, directory, Check);
        DiagnosticVerification.Run(window, directory, Check);
        await MemoryVerification.Run(window,directory,Check);
        File.WriteAllLines(Path.Combine(directory, "verification.txt"), checks);
        window.Close();
    }

    // WScript.Shell saves through the system ANSI code page on some Windows images.
    // Use the Unicode shell interface so Chinese fixtures are also valid on English CI.
    private static void CreateProbeShortcut(string path, string marker, string workingDirectory)
    {
        object instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!)!;
        try
        {
            var link = (IShellLinkW)instance;
            link.SetPath(Environment.ProcessPath!);
            link.SetArguments("--launch-probe \"" + marker + "\"");
            link.SetWorkingDirectory(workingDirectory);
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)instance).Save(path, true);
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(IntPtr buffer, int size, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription(IntPtr buffer, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory(IntPtr buffer, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments(IntPtr buffer, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation(IntPtr buffer, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    internal static void SaveRender(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}

