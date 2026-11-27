#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FileDockDemo.Contracts;

namespace FileDockDemo;

internal static class OpenWindowMenuVerification
{
    internal static async Task Run(MainWindow window, string directory, Action<bool, string> check)
    {
        VerifyMergeAndDeduplication(directory, check);
        await VerifyWorkerScan(check);
        await VerifyDialogIntegration(window, directory, check);
    }

    private static void VerifyMergeAndDeduplication(string directory, Action<bool, string> check)
    {
        string appPath = Path.Combine(directory, "multi-window-editor.exe");
        const string packageId = "Contoso.NotePad_8wekyb3d8bbwe!App";
        var merged = OpenWindowMenuScanner.MergeObservations([
            new(
                "Editor", appPath, null,
                [new ImportedAppShortcut { Command = "File / Open", Key = "Ctrl+O" },
                 new ImportedAppShortcut { Command = "File / Save", Key = "Ctrl + S" }]),
            new(
                "Editor", appPath.ToUpperInvariant(), null,
                [new ImportedAppShortcut { Command = "file / save", Key = "ctrl+s" },
                 new ImportedAppShortcut { Command = "File / Export", Key = "Ctrl+E" }]),
            new(
                "Store Notes", Path.Combine(directory, "StoreNotes.exe"), packageId,
                [new ImportedAppShortcut { Command = "View / Zoom", Key = "Ctrl+Plus" }])
        ], scannedWindows: 4, skippedWindows: 1, timedOutPartial: false);

        var desktop = merged.Apps.Single(app => !app.HasPackageIdentity);
        var store = merged.Apps.Single(app => app.HasPackageIdentity);
        check(merged.MatchedWindows == 3 && desktop.WindowCount == 2 && desktop.Rules.Count == 3 &&
              desktop.Rules.Any(r => r.Key == "Ctrl+O") && desktop.Rules.Any(r => r.Key == "Ctrl+E") &&
              store.PackageId == packageId && store.Path == "" && store.Rules.Count == 1,
            "Open-window batch merge combines multiple windows per app, deduplicates rules and preserves package identity");
    }

    private static async Task VerifyWorkerScan(Action<bool, string> check)
    {
        var visibleWindow = new Window
        {
            Width = 220, Height = 100, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
            Title = "FileDock batch menu visible"
        };
        var minimizedWindow = new Window
        {
            Width = 220, Height = 100, ShowActivated = false, ShowInTaskbar = false,
            WindowState = WindowState.Minimized,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
            Title = "FileDock batch menu minimized"
        };
        IntPtr hwndVisible = IntPtr.Zero, hwndMinimized = IntPtr.Zero;
        IntPtr menuVisible = IntPtr.Zero, menuMinimized = IntPtr.Zero;
        try
        {
            visibleWindow.Show();
            minimizedWindow.Show();
            visibleWindow.UpdateLayout();
            minimizedWindow.UpdateLayout();
            hwndVisible = new WindowInteropHelper(visibleWindow).Handle;
            hwndMinimized = new WindowInteropHelper(minimizedWindow).Handle;
            menuVisible = InstallMenu(hwndVisible, [("BatchOpen", "Ctrl+Shift+F7"), ("BatchShared", "Ctrl+Shift+F8")]);
            menuMinimized = InstallMenu(hwndMinimized, [("BatchShared", "Ctrl+Shift+F8"), ("BatchMinimizedOnly", "Ctrl+Shift+F6")]);

            string selfExe = FileService.NormalizeKey(Environment.ProcessPath!);
            var included = await OpenWindowMenuScanner.ScanAsync(excludedPid: 0, CancellationToken.None);
            var selfApp = included.Apps.FirstOrDefault(app => !app.HasPackageIdentity && FileService.NormalizeKey(app.Path) == selfExe);
            check(selfApp != null && selfApp.WindowCount >= 2 &&
                  selfApp.Rules.Any(r => r.Command == "Batch / BatchOpen" && r.Key == "Ctrl+Shift+F7") &&
                  selfApp.Rules.Any(r => r.Command == "Batch / BatchMinimizedOnly" && r.Key == "Ctrl+Shift+F6") &&
                  selfApp.Rules.Count(r => r.Command == "Batch / BatchShared") == 1 &&
                  minimizedWindow.WindowState == WindowState.Minimized,
                "Batch worker reads native menus across both visible and minimized windows without restoring minimized windows");

            var excluded = await OpenWindowMenuScanner.ScanAsync(Environment.ProcessId, CancellationToken.None);
            check(excluded.Apps.All(app => app.HasPackageIdentity || FileService.NormalizeKey(app.Path) != selfExe),
                "Default batch scan excludes FileDock's own process windows");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            bool threw = false;
            try { await OpenWindowMenuScanner.ScanAsync(cancelled.Token); }
            catch (OperationCanceledException) { threw = true; }
            check(threw, "Cancelled batch open-window scan stops before starting the worker");
        }
        finally
        {
            if (hwndVisible != IntPtr.Zero) SetMenu(hwndVisible, IntPtr.Zero);
            if (hwndMinimized != IntPtr.Zero) SetMenu(hwndMinimized, IntPtr.Zero);
            if (menuVisible != IntPtr.Zero) DestroyMenu(menuVisible);
            if (menuMinimized != IntPtr.Zero) DestroyMenu(menuMinimized);
            visibleWindow.Close();
            minimizedWindow.Close();
        }
    }

    private static async Task VerifyDialogIntegration(MainWindow window, string directory, Action<bool, string> check)
    {
        string savedAppPath = Path.Combine(directory, "saved-menu-app.exe");
        string liveOnlyPath = Path.Combine(directory, "live-only-app.exe");
        const string storeId = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

        var savedGroup = new AppShortcutGroup
        {
            Name = "已存菜单软件",
            Path = savedAppPath,
            Manual = [new ManualAppShortcut { Action = "手工记录动作", Keys = "F9", Scope = ManualShortcutScope.Application }]
        };
        AppMenuCatalog.Refresh(savedGroup, [new ImportedAppShortcut { Command = "文件 / 打开", Key = "Ctrl+O" }],
            new Dictionary<string, ShortcutBinding>());
        savedGroup.MenuSource!.Rules[0].Mapping = new ShortcutBinding { Enabled = true, Modifiers = 3, Key = 'J' };

        int saves = 0, scans = 0;
        var firstScan = new TaskCompletionSource<OpenWindowMenuScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool failNextScan = false;
        var dialog = new AppShortcutCatalogDialog(
            window,
            () => [savedGroup],
            () => [],
            _ => { saves++; return Task.FromResult(true); },
            (_, _) => { },
            () => { },
            () => window.State.Shortcuts!,
            scanOpenWindows: token =>
            {
                scans++;
                if (scans == 1) return firstScan.Task;
                if (failNextScan) return Task.FromException<OpenWindowMenuScanResult>(new IOException("scan failed"));
                return Task.FromResult(new OpenWindowMenuScanResult());
            });

        window.ExpandPanel();
        ((IModuleHost)window).ShowDialog(dialog);
        try
        {
            check(scans == 1 && Labels("正在后台读取当前已打开窗口"),
                "Opening the catalog automatically starts an asynchronous open-window menu scan");

            // Ensure background scan completion does not interrupt an open edit form.
            Click("编辑登记：手工记录动作");
            Box("备注").Text = "正在编辑的草稿";
            firstScan.SetResult(new OpenWindowMenuScanResult
            {
                ScannedWindows = 3,
                MatchedWindows = 3,
                Apps =
                [
                    new OpenWindowMenuApp
                    {
                        Name = "已存菜单软件", Path = savedAppPath, WindowCount = 1,
                        Rules =
                        [
                            new ImportedAppShortcut { Command = "文件 / 打开", Key = "Ctrl+O" },
                            new ImportedAppShortcut { Command = "文件 / 实时新增", Key = "Ctrl+Shift+E" }
                        ]
                    },
                    new OpenWindowMenuApp
                    {
                        Name = "临时打开工具", Path = liveOnlyPath, WindowCount = 1,
                        Rules = [new ImportedAppShortcut { Command = "工具 / 快速检查", Key = "Ctrl+Shift+Q" }]
                    },
                    new OpenWindowMenuApp
                    {
                        Name = "计算器", PackageId = storeId, WindowCount = 1,
                        Rules = [new ImportedAppShortcut { Command = "查看 / 历史", Key = "Ctrl+H" }]
                    }
                ]
            });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(Box("备注").Text == "正在编辑的草稿" && saves == 0,
                "Background open-window scan completion preserves in-progress edits and writes nothing to disk");

            Box("备注").Clear();
            Click("返回软件快捷键");
            check(Labels("已从 3 个打开窗口读到 4 条菜单快捷键") &&
                  Buttons("查看规则：文件 / 打开").Count() == 1 &&
                  Buttons("查看菜单：文件 / 实时新增").Count() == 1 &&
                  Buttons("查看菜单：工具 / 快速检查").Count() == 1 &&
                  savedGroup.MenuSource!.Rules.Single().Mapping is { Enabled: true, Key: 'J' } &&
                  saves == 0,
                "Live open-window results merge without duplicating saved menu items, preserve mappings and never persist automatically");

            Search().Text = "Ctrl+Shift+Q";
            check(Buttons("查看菜单：工具 / 快速检查").Count() == 1 && !Buttons("编辑登记：手工记录动作").Any(),
                "Live open-window shortcuts from unregistered apps are immediately searchable");
            Search().Clear();

            Click("仅看当前窗口实读");
            check(!Buttons("编辑登记：手工记录动作").Any() &&
                  Buttons("查看菜单：文件 / 打开").Count() == 1 &&
                  Buttons("查看菜单：文件 / 实时新增").Count() == 1 &&
                  Buttons("查看菜单：工具 / 快速检查").Count() == 1 &&
                  Buttons("查看菜单：查看 / 历史").Count() == 1,
                "Live-only filter isolates current open-window shortcuts cleanly across desktop and packaged apps");

            Click("仅看当前窗口实读");
            check(Buttons("编辑登记：手工记录动作").Any(),
                "Toggling live-only filter off restores the full catalog view");

            failNextScan = true;
            Click("刷新打开窗口");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(scans == 2 && Labels("已保留本次上次读取结果") && Buttons("查看菜单：工具 / 快速检查").Count() == 1 && saves == 0,
                "A failed manual open-window refresh retains the previous session scan results");
        }
        finally { window.CloseModuleDialog(); }

        CancellationToken captured = default;
        var pendingCancel = new TaskCompletionSource<OpenWindowMenuScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog = new AppShortcutCatalogDialog(window, () => [], () => [], _ => { saves++; return Task.FromResult(true); },
            (_, _) => { }, () => { }, scanOpenWindows: token => { captured = token; return pendingCancel.Task; });
        ((IModuleHost)window).ShowDialog(dialog);
        window.CloseModuleDialog();
        pendingCancel.TrySetCanceled();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        check(captured.IsCancellationRequested && saves == 0,
            "Closing the catalog cancels an in-flight open-window batch scan without writing state");

        IEnumerable<T> Controls<T>() where T : FrameworkElement
        {
            window.UpdateLayout();
            return DisplayPowerViewVerification.Children<T>(dialog.View).Where(item => item.IsVisible);
        }
        IEnumerable<Button> Buttons(string name) => Controls<Button>().Where(item =>
            AutomationProperties.GetName(item) == name || Equals(item.Content, name));
        bool Labels(string value) => Controls<TextBlock>().Any(item => item.Text.Contains(value));
        TextBox Box(string name) => Controls<TextBox>().Single(item => AutomationProperties.GetName(item) == name);
        TextBox Search() => Box("搜索软件快捷键");
        void Click(string name) => Buttons(name).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static IntPtr InstallMenu(IntPtr hwnd, (string Name, string Key)[] items)
    {
        IntPtr root = CreateMenu();
        IntPtr submenu = CreatePopupMenu();
        if (root == IntPtr.Zero || submenu == IntPtr.Zero) throw new InvalidOperationException("Cannot create test menu.");
        if (!AppendMenu(root, 0x10, submenu, "&Batch")) throw new InvalidOperationException("Cannot append root menu.");
        for (int i = 0; i < items.Length; i++)
        {
            if (!AppendMenu(submenu, 0, new IntPtr(i + 1), $"{items[i].Name}\t{items[i].Key}"))
                throw new InvalidOperationException("Cannot append menu item.");
        }
        if (!SetMenu(hwnd, root)) throw new InvalidOperationException("Cannot attach menu.");
        return root;
    }

    [DllImport("user32.dll")] private static extern IntPtr CreateMenu();
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr item, string text);
    [DllImport("user32.dll")] private static extern bool SetMenu(IntPtr hwnd, IntPtr menu);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
}
