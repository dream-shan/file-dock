using System;
using System.Collections.Generic;
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

internal static class AppMappingUiVerification
{
    internal static async Task Run(MainWindow window, string directory, Action<bool,string> check)
    {
        var path = Path.Combine(directory, "mapping-test-editor.exe");
        var sourcePath = Path.Combine(directory, "mapping-ui-keys.json");
        var group = new AppShortcutGroup {
            Name = "键位测试", Path = path,
            Manual = [new() { Action = "保存", Keys = "Ctrl+S", Scope = ManualShortcutScope.Application }],
            Sources = [new() { Path = sourcePath, Rules = [new() { Command = "test.open", Key = "ctrl+o" }] }]
        };
        var globals = new Dictionary<string,ShortcutBinding> {
            [ShortcutCatalog.Wake] = new() { Enabled = true, Modifiers = 3, Key = 'J' }
        };
        var dialog = new AppShortcutCatalogDialog(window, () => [group], () => [], candidate => {
            group = candidate.Copy();
            return Task.FromResult(true);
        }, (choose,_) => choose(group.Name,path), () => {}, () => globals);
        window.ExpandPanel();
        ((IModuleHost)window).ShowDialog(dialog);
        try
        {
            Click("从窗口选择");
            Click("编辑登记：保存");
            Click("添加映射");
            Box("新快捷键").Text = "Ctrl+Alt+J";
            Click("保存登记");
            var attempted = group.Copy();
            attempted.Manual.Single().Mapping = globals[ShortcutCatalog.Wake].Copy();
            string? conflict = AppMappingRules.Conflict(attempted, globals);
            check(conflict != null && Labels().Any(block => block.Text == conflict) &&
                group.Manual.Single().Mapping == null && dialog.HasChanges,
                "A conflicting foreground binding stays in the editor with the specific reason");
            Box("新快捷键").Text = "Ctrl+Alt+M";
            Click("保存登记");
            check(group.Manual.Single().Mapping is { Enabled:true, Key: 'M' }, "Save a manual foreground binding");

            Click("编辑登记：保存");
            Click("暂停映射");
            Click("保存登记");
            check(group.Manual.Single().Mapping is { Enabled:false, Key:'M' }, "Pausing retains the assigned keys");
            Click("编辑登记：保存");
            Click("启用映射");
            Click("保存登记");
            check(group.Manual.Single().Mapping?.Enabled == true, "Resume a paused foreground binding");
            Click("编辑登记：保存");
            Box("原快捷键").Text = "Ctrl+P";
            Click("保存登记");
            check(group.Manual.Single().Mapping == null, "Changing the original clears the previous binding");

            Click("查看规则：test.open");
            Click("添加映射");
            Box("新快捷键").Text = "Ctrl+Alt+P";
            Click("返回软件快捷键");
            check(!dialog.HasChanges && group.Sources.Single().Rules.Single().Mapping == null,
                "Leaving an unsaved imported mapping discards its draft");
            Click("查看规则：test.open");
            Click("添加映射");
            Box("新快捷键").Text = "Ctrl+Alt+P";
            Click("保存映射");
            const string source = """[{"key":"ctrl+o","command":"test.open"}]""";
            File.WriteAllText(sourcePath, source);
            Click("VS Code 导入来源");
            Click("刷新来源");
            await WaitForRefresh();
            check(!dialog.HasChanges && Labels().Any(block => block.Text.Contains("已导入 1 条规则")) &&
                group.Sources.Single().Rules.Single().Mapping is { Enabled:true, Key:'P' } &&
                File.ReadAllText(sourcePath) == source, "Refreshing a matching source preserves the mapping and source file");
            File.WriteAllText(sourcePath, """[{"key":"ctrl+o","command":"test.open"},{"key":"ctrl+alt+p","command":"test.second"}]""");
            Click("刷新来源");
            await WaitForRefresh();
            check(group.Sources.Single().Rules[0].Mapping is { Enabled:false, Key:'P' } &&
                Labels().Any(block => block.Text.Contains("已暂停 1 个映射")),
                "A newly occupied alias pauses on refresh and the user sees the count");
            Click("返回软件快捷键");
            Click("查看规则：test.open");
            Click("删除映射");
            Click("保存映射");
            check(group.Sources.Single().Rules[0].Mapping == null, "Remove the imported mapping independently of its rule");
            Click("查看规则：test.open");
            Click("添加映射");
            Box("新快捷键").Text = "Ctrl+Alt+U";
            Click("保存映射");
            Click("VS Code 导入来源");
            File.WriteAllText(sourcePath, """[{"key":"ctrl+o","command":"test.replaced"}]""");
            Click("刷新来源");
            await WaitForRefresh();
            check(group.Sources.Single().Rules.Single() is { Command:"test.replaced", Mapping:null } &&
                Labels().Any(block => block.Text.Contains("1 个原映射无法继承")),
                "A replaced command loses its old alias and the refresh reports the count");
        }
        finally
        {
            window.CloseModuleDialog();
        }

        void Click(string name)
        {
            window.UpdateLayout();
            DisplayPowerViewVerification.Children<Button>(dialog.View).Single(button => button.IsVisible &&
                (Equals(button.Content, name) || AutomationProperties.GetName(button) == name))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        TextBox Box(string name)
        {
            window.UpdateLayout();
            return DisplayPowerViewVerification.Children<TextBox>(dialog.View)
                .Single(box => box.IsVisible && AutomationProperties.GetName(box) == name);
        }
        IEnumerable<TextBlock> Labels()
        {
            window.UpdateLayout();
            return DisplayPowerViewVerification.Children<TextBlock>(dialog.View).Where(block => block.IsVisible);
        }
        async Task WaitForRefresh()
        {
            var timer = Stopwatch.StartNew();
            while (dialog.HasChanges && timer.ElapsedMilliseconds < 10000) await Task.Delay(20);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    }
}
