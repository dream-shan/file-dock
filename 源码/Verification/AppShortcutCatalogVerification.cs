using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using FileDockDemo.Contracts;

namespace FileDockDemo;

internal static class AppShortcutCatalogVerification
{
    internal static async Task Run(MainWindow window, string directory, Action<bool,string> check)
    {
        string legacyDir = Path.Combine(directory,"legacy-software-shortcuts");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir,"state.json"),"""{"Version":1,"State":{"Pins":[]}}""");
        check(new StorageService(legacyDir).Load().AppShortcutGroups.Count == 0,
            "Existing configurations start with an empty software shortcut catalog");

        string appPath = Path.Combine(directory,"code.exe");
        string sourcePath = Path.Combine(directory,"keys.json");
        var entry = new ManualAppShortcut { Action = "查找文档", Keys = "Ctrl + K, Ctrl + S", Note = "编辑窗口" };
        var group = new AppShortcutGroup {
            Path = appPath,
            Name = "适配测试",
            Manual = [entry],
            Sources = [new() {
                Path = sourcePath,
                Rules = [new() { Key = "ctrl+e", Command = "test.quickOpen", Condition = "editorFocus", SystemWide = true }]
            }]
        };
        var draft = group.Copy();
        draft.Manual[0].Note = "编辑后的备忘";
        draft.Sources[0].Rules.Clear();
        check(group.Manual[0].Note == "编辑窗口" && group.Sources[0].Rules.Count == 1,
            "Software shortcut editor uses a detached copy of manual entries and imports");
        var managedBefore = window.State.Shortcuts!.Keys.OrderBy(key => key).ToArray();
        check(await window.SaveAppShortcutGroupAsync(group),"Save a local software shortcut catalog");
        try
        {
            var after = window.Storage.Load().AppShortcutGroups.Single(item => item.Path == appPath);
            check(after.Manual.Single().Keys == "Ctrl + K, Ctrl + S" &&
                after.Sources.Single().Rules.Single() is { Condition: "editorFocus", SystemWide: true },
                "Reload preserves manual chords, custom source and VS Code's declared scope");
            after.Manual[0].Action = "";
            check(!await window.SaveAppShortcutGroupAsync(after) &&
                window.State.AppShortcutGroups.Single(item => item.Path == appPath).Manual[0].Action == "查找文档",
                "Rejecting an invalid update preserves the committed software shortcut catalog");
            check(managedBefore.SequenceEqual(window.State.Shortcuts!.Keys.OrderBy(key => key)),
                "Software shortcut records do not create managed hotkey actions");
        }
        finally
        {
            group.Manual.Clear();
            group.Sources.Clear();
            await window.SaveAppShortcutGroupAsync(group);
        }
        check(window.Storage.Load().AppShortcutGroups.All(item => item.Path != appPath),
            "Removing the final catalog entries clears the empty group on disk");
        await VerifyDialog(window, appPath, sourcePath, check);
    }

    private static async Task VerifyDialog(MainWindow window, string appPath, string sourcePath, Action<bool,string> check)
    {
        window.ExpandPanel();
        window.OpenShortcutSettings();
        try
        {
            var settings = window.ActiveModuleDialog!;
            window.UpdateLayout();
            check(DisplayPowerViewVerification.Children<Button>(settings.View)
                .Any(button => Equals(button.Content, "软件内部快捷键  ›")),
                "The software shortcut catalog is reachable from unified shortcut settings");
            DisplayPowerViewVerification.Children<Button>(settings.View)
                .Single(button => Equals(button.Content, "软件内部快捷键  ›"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(window.ActiveModuleDialog is AppShortcutCatalogDialog,
                "Unified settings opens the real application shortcut catalog");

            const string refreshedSource = """[{"key":"ctrl+k ctrl+s","command":"workbench.action.openGlobalKeybindings","when":"editorTextFocus","args":"test-only-value"}]""";
            File.WriteAllText(sourcePath, refreshedSource);
            check(await window.SaveAppShortcutGroupAsync(new AppShortcutGroup {
                Path = appPath, Name = "测试应用",
                Sources = [new() { Path = sourcePath, Rules = [new() { Key = "ctrl+s", Command = "old.command" }] }]
            }), "Set up a previously imported source for refresh");
            var dialog = new AppShortcutCatalogDialog(window, () => window.State.AppShortcutGroups,
                () => [new ApplicationEntry { Path = appPath, DisplayName = "测试应用" }],
                window.SaveAppShortcutGroupAsync, (selected,_) => selected("测试应用",appPath),
                () => window.OpenShortcutSettings());
            ((IModuleHost)window).ShowDialog(dialog);
            window.UpdateLayout();
            var software = DisplayPowerViewVerification.Children<ComboBox>(dialog.View)
                .Single(item => AutomationProperties.GetName(item) == "软件");
            if (software.SelectedIndex < 0) software.SelectedIndex = 0;
            window.UpdateLayout();

            Click("添加手动登记");
            FindBox("动作名称").Text = "跳到定义";
            FindBox("原快捷键").Text = "F12";
            FindBox("备注").Text = "只有编辑器焦点时生效";
            DisplayPowerViewVerification.Children<ComboBox>(dialog.View)
                .Single(item => AutomationProperties.GetName(item) == "生效范围").SelectedIndex = 1;
            Click("保存登记");
            await window.WaitForSavesAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            check(window.Storage.Load().AppShortcutGroups.Any(group => group.Path == appPath &&
                group.Manual.Any(item => item.Action == "跳到定义" && item.Keys == "F12" &&
                    item.Scope == ManualShortcutScope.Application)),
                "The real registration form persists a user-entered original shortcut");

            Click("VS Code 导入来源");
            Click("刷新来源");
            await WaitForRead();
            var catalog = window.Storage.Load().AppShortcutGroups.Single(group => group.Path == appPath);
            var rule = catalog.Sources.Single().Rules.Single();
            check(rule.Key == "ctrl+k ctrl+s" && rule.Condition == "editorTextFocus" && rule.HasArguments &&
                catalog.Manual.Single().Keys == "F12" && File.ReadAllText(sourcePath) == refreshedSource,
                "Refreshing a VS Code source preserves manual entries and the original file");

            File.WriteAllText(sourcePath,"[{");
            Click("刷新来源");
            await WaitForRead();
            check(window.Storage.Load().AppShortcutGroups.Single(group => group.Path == appPath)
                .Sources.Single().Rules.Single().Command == rule.Command,
                "A malformed VS Code refresh retains the last imported rules on disk");
        }
        finally
        {
            window.CloseModuleDialog();
            await window.SaveAppShortcutGroupAsync(new AppShortcutGroup { Path = appPath, Name = "测试应用" });
        }

        void Click(string name)
        {
            window.UpdateLayout();
            DisplayPowerViewVerification.Children<Button>(window.ActiveModuleDialog!.View)
                .Single(button => button.IsVisible && (AutomationProperties.GetName(button) == name ||
                    Equals(button.Content,name))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        TextBox FindBox(string name)
        {
            window.UpdateLayout();
            return DisplayPowerViewVerification.Children<TextBox>(window.ActiveModuleDialog!.View)
                .Single(box => box.IsVisible && AutomationProperties.GetName(box) == name);
        }
        async Task WaitForRead()
        {
            var wait = Stopwatch.StartNew();
            while (window.ActiveModuleDialog?.HasChanges == true && wait.ElapsedMilliseconds < 10000)
                await Task.Delay(20);
            check(window.ActiveModuleDialog?.HasChanges == false, "The software shortcut operation returns to an editable state");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    }
}
