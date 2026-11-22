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

internal static class AppReadUiVerification
{
    internal static async Task Run(MainWindow window, string directory, Action<bool,string> check)
    {
        var first = new AppShortcutGroup { Name = "编辑甲", Path = Path.Combine(directory, "editor-a.exe"),
            Manual = [new() { Action = "另存文稿", Keys = "Ctrl + S", Scope = ManualShortcutScope.Application }] };
        var second = new AppShortcutGroup { Name = "编辑乙", Path = Path.Combine(directory, "editor-b.exe") };
        AppMenuCatalog.Refresh(second, [new() { Key = "Ctrl+O", Command = "文件 / 打开" }],
            new Dictionary<string,ShortcutBinding>());
        second.MenuSource!.Rules[0].Mapping = new() { Enabled = true, Modifiers = 3, Key = 'J' };
        int picks = 0, saves = 0;
        string browserPath = Path.Combine(directory, "firefox.exe");
        var dialog = new AppShortcutCatalogDialog(window, () => [first, second],
            () => [new ApplicationEntry { Path = browserPath, DisplayName = "参考浏览器" }],
            candidate => { second = candidate.Copy(); saves++; return Task.FromResult(true); },
            (_,_) => {}, () => {}, pickMenuWindow: (select, cancel) => {
                if (++picks == 1) cancel();
                else select(second.Name, second.Path, IntPtr.Zero);
            });
        window.ExpandPanel();
        ((IModuleHost)window).ShowDialog(dialog);
        try
        {
            var search = Search();
            check(Result("编辑登记：另存文稿") && Result("查看规则：文件 / 打开") && picks == 0,
                "The initial all-software view lists both applications without reading a window");
            search.Text = "参考浏览器 官方默认参考";
            var example = AppMenuCatalog.OfficialReference(browserPath)!.Rules[0];
            Click("查看参考：" + example.Command);
            check(LabelContains("官方默认参考") && !Controls<Button>().Any(item => Equals(item.Content, "添加映射")),
                "Official defaults for a known application open as labeled read-only references");
            Click("返回软件快捷键");
            search.Text = "编辑甲 Ctrl+S";
            check(Result("编辑登记：另存文稿") && !Result("查看规则：文件 / 打开"),
                "Search combines the software name and formatted original keys across sources");
            search.Text = "Ctrl+Alt+J";
            check(!Result("编辑登记：另存文稿") && Result("查看规则：文件 / 打开"),
                "Searching the alias finds the menu rule in another application");
            Click("查看规则：文件 / 打开");
            Click("暂停映射");
            Click("保存映射");
            check(saves == 1 && second.MenuSource!.Rules.Single().Mapping is {Enabled:false, Key:'J'} &&
                Search().Text == "Ctrl+Alt+J" && Result("查看规则：文件 / 打开"),
                "A cross-application result edits the correct menu mapping and returns to its search");
            Click("读取窗口菜单");
            check(picks == 1 && saves == 1 && LabelContains("已取消读取"), "Canceling a menu pick retains records");
            Click("读取窗口菜单");
            var timer = Stopwatch.StartNew();
            while (dialog.HasChanges && timer.ElapsedMilliseconds < 10000) await Task.Delay(20);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(picks == 2 && saves == 1 && LabelContains("读取窗口菜单失败") &&
                second.MenuSource!.Rules.Single().Mapping is {Enabled:false},
                "A failed window read explicitly repicks and preserves the last snapshot and mapping");
        }
        finally { window.CloseModuleDialog(); }

        IEnumerable<T> Controls<T>() where T : FrameworkElement
        {
            window.UpdateLayout();
            return DisplayPowerViewVerification.Children<T>(dialog.View).Where(item => item.IsVisible);
        }
        bool Result(string name) => Controls<Button>().Any(item => AutomationProperties.GetName(item) == name);
        bool LabelContains(string value) => Controls<TextBlock>().Any(item => item.Text.Contains(value));
        TextBox Search() => Controls<TextBox>().Single(item => AutomationProperties.GetName(item) == "搜索软件快捷键");
        void Click(string name) => Controls<Button>().Single(item =>
            AutomationProperties.GetName(item) == name || Equals(item.Content, name))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
