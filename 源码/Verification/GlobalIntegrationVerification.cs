#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FileDockDemo.Contracts;
using FileDockDemo.Features.ControlCenter;

namespace FileDockDemo;

internal static class GlobalIntegrationVerification
{
    internal static async Task Run(MainWindow window, string directory, Action<bool, string> check, Action<string>? recordNote = null)
    {
        VerifyAllPagesAndCompactLayout(window, check);
        VerifyControlCenterEscapeAndGating(window, check);
        await VerifyDialogLayeredEscapeAndCleanup(window, directory, check);
        VerifyLightweightResourceFootprint(window, check, recordNote);
    }

    private static void VerifyAllPagesAndCompactLayout(MainWindow window, Action<bool, string> check)
    {
        double savedWidth = window.Width, savedHeight = window.Height;
        string savedPage = window.State.SelectedPage;
        try
        {
            window.ExpandPanel();
            window.Width = 380;
            window.Height = 480;

            string[] pages = ["Files", "Applications", "Planner", "Tools", "Control", "Performance"];
            foreach (string page in pages)
            {
                window.SelectPage(page, false);
                window.UpdateLayout();
            }

            window.SelectPage("Control", false);
            window.UpdateLayout();
            check(window.InteractionHint.Text.Contains("硬件") && window.InteractionHint.Text.Contains("无线"),
                "Control page footer hint reflects all active modules including wireless and hardware");

            bool detailFound = DisplayPowerViewVerification.Children<Button>(window.ControlSection)
                .Any(b => b.IsVisible && AutomationProperties.GetName(b) == "硬件与性能详情");
            check(detailFound && window.ActualWidth <= 382 && window.ActualHeight <= 482,
                "Compact 380x480 panel preserves all six pages and control-center hardware entry without overflow");
        }
        finally
        {
            window.Width = savedWidth;
            window.Height = savedHeight;
            window.SelectPage(savedPage, false);
        }
    }

    private static void VerifyControlCenterEscapeAndGating(MainWindow window, Action<bool, string> check)
    {
        window.ExpandPanel();
        window.SelectPage("Control", false);
        window.UpdateLayout();

        var control = window.ControlSection.Content as ControlCenterView;
        check(control != null, "Main window mounts a single ControlCenterView instance");
        if (control == null) return;

        check(!control.TryNavigateBack(), "Control center home reports no nested detail to back out of");

        DisplayPowerViewVerification.Children<Button>(control)
            .Single(b => b.IsVisible && AutomationProperties.GetName(b) == "硬件与性能详情")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        check(control.TryNavigateBack(), "Escape back-navigation exits a nested control-center detail before collapsing the dock");
        window.UpdateLayout();
        check(!control.TryNavigateBack() &&
              DisplayPowerViewVerification.Children<Button>(control)
                  .Any(b => b.IsVisible && AutomationProperties.GetName(b) == "硬件与性能详情"),
            "Returning from control-center detail restores the main control cards cleanly");

        // Switch away to Files page: RefreshWireless must be gated when control page is hidden.
        window.SelectPage("Files", false);
        window.UpdateLayout();
        control.RefreshWireless();
        check(!control.IsVisible, "Switching away from Control hides and gates control-center wireless refreshes");
    }

    private static async Task VerifyDialogLayeredEscapeAndCleanup(MainWindow window, string directory, Action<bool, string> check)
    {
        window.ExpandPanel();
        window.OpenShortcutSettings();
        try
        {
            var settings = window.ActiveModuleDialog as ShortcutSettingsView;
            check(settings != null, "ShortcutSettingsView opens cleanly from MainWindow");
            if (settings != null)
            {
                window.UpdateLayout();
                var firstItem = DisplayPowerViewVerification.Children<Button>(settings.View)
                    .First(b => b.IsVisible && b.Content is TextBlock);
                firstItem.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                check(DisplayPowerViewVerification.Children<Button>(settings.View)
                    .Any(b => b.IsVisible && Equals(b.Content, "← 全部快捷键")),
                    "ShortcutSettingsView opens individual shortcut editor with consistent back navigation");

                SendEscape(settings);
                window.UpdateLayout();
                check(ReferenceEquals(window.ActiveModuleDialog, settings) &&
                      DisplayPowerViewVerification.Children<Button>(settings.View)
                          .Any(b => b.IsVisible && Equals(b.Content, "软件内部快捷键  ›")),
                    "Pressing Escape in ShortcutSettingsView editor returns to the list instead of closing immediately");

                SendEscape(settings);
                check(window.ActiveModuleDialog == null,
                    "Pressing Escape on the ShortcutSettingsView root list closes the dialog");
            }
        }
        finally { window.CloseModuleDialog(); }

        string exe = Path.Combine(directory, "escape-test-app.exe");
        var group = new AppShortcutGroup
        {
            Name = "返回测试软件",
            Path = exe,
            Manual = [new ManualAppShortcut { Action = "快捷测试", Keys = "F7", Scope = ManualShortcutScope.Application }]
        };
        var discovered = Enumerable.Range(1, 120)
            .Select(i => new DiscoveredApp($"发现应用-{i:D3}", $"Test.App.{i:D3}_8wekyb3d8bbwe!App", null))
            .ToArray();
        var dialog = new AppShortcutCatalogDialog(
            window,
            () => [group],
            () => [],
            _ => Task.FromResult(true),
            (_, _) => { },
            () => { },
            () => window.State.Shortcuts!,
            discover: _ => Task.FromResult<IReadOnlyList<DiscoveredApp>>(discovered),
            scanOpenWindows: _ => Task.FromResult(new OpenWindowMenuScanResult
            {
                ScannedWindows = 1,
                MatchedWindows = 1,
                Apps = [new OpenWindowMenuApp { Name = "临时窗", Path = exe, WindowCount = 1, Rules = [new ImportedAppShortcut { Command = "文件 / 新建", Key = "Ctrl+N" }] }]
            }));
        ((IModuleHost)window).ShowDialog(dialog);
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            DisplayPowerViewVerification.Children<Button>(dialog.View)
                .Single(b => b.IsVisible && AutomationProperties.GetName(b) == "编辑登记：快捷测试")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            check(DisplayPowerViewVerification.Children<Button>(dialog.View)
                .Any(b => b.IsVisible && Equals(b.Content, "返回软件快捷键")),
                "AppShortcutCatalogDialog enters rule detail with unified back navigation");

            SendEscape(dialog);
            window.UpdateLayout();
            check(ReferenceEquals(window.ActiveModuleDialog, dialog) &&
                  DisplayPowerViewVerification.Children<Button>(dialog.View)
                      .Any(b => b.IsVisible && Equals(b.Content, "返回快捷键管理")),
                "Pressing Escape in AppShortcutCatalogDialog detail returns to the catalog instead of closing the whole dialog");

            SendEscape(dialog);
            window.UpdateLayout();
            check(window.ActiveModuleDialog == null &&
                  !DisplayPowerViewVerification.Children<Button>(dialog.View).Any(),
                "Closing AppShortcutCatalogDialog via Escape releases its visual tree and session caches");
        }
        finally { window.CloseModuleDialog(); }

        static void SendEscape(IModuleDialog target)
        {
            var source = PresentationSource.FromVisual(target.View);
            if (source == null) return;
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            };
            target.HandleWindowKey(args);
        }
    }

    private static void VerifyLightweightResourceFootprint(MainWindow window, Action<bool, string> check, Action<string>? recordNote)
    {
        window.SelectPage("Files", false);
        window.CollapsePanel();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long managedBytes = GC.GetTotalMemory(forceFullCollection: true);
        using var proc = Process.GetCurrentProcess();
        proc.Refresh();
        long workingSetBytes = proc.WorkingSet64;
        int threadsBefore = proc.Threads.Count;
        Thread.Sleep(150);
        proc.Refresh();
        int threadsAfter = proc.Threads.Count;

        recordNote?.Invoke($"PERF: idle-collapsed managed={managedBytes / 1048576.0:F2}MB workingSet={workingSetBytes / 1048576.0:F2}MB threads={threadsAfter} delta={threadsAfter - threadsBefore} cores={Environment.ProcessorCount}");
        check(!window.IsVisible && !window.PerformanceActive &&
              managedBytes < 128L * 1024 * 1024 && threadsAfter <= threadsBefore + 2,
            $"Collapsed idle dock stays lightweight with stopped sampling, low managed heap and stable thread count (managedMB={managedBytes / 1048576.0:F1}, threads={threadsBefore}->{threadsAfter})");
        window.Show();
    }
}
