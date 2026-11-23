using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using FileDockDemo.Contracts;

namespace FileDockDemo;

internal static class InstalledAppUiVerification
{
    internal static async Task Run(MainWindow window, string directory, Action<bool,string> check)
    {
        const string packageId = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
        var editor = new AppShortcutGroup { Name = "已登记的编辑器", Path = Path.Combine(directory, "installed-editor.exe"),
            Manual = [new ManualAppShortcut { Action = "核对动作", Keys = "F8" }] };
        string secondPath = Path.Combine(directory, "other-editor.exe");
        var pending = new TaskCompletionSource<IReadOnlyList<DiscoveredApp>>(TaskCreationOptions.RunContinuationsAsynchronously);
        int saves = 0, discoveries = 0;
        var dialog = Create(_ => { discoveries++; return pending.Task; });
        window.ExpandPanel();
        ((IModuleHost)window).ShowDialog(dialog);
        try
        {
            Search().Text = "核对动作";
            Click("编辑登记：核对动作");
            Box("备注").Text = "未保存的编辑";
            pending.SetResult([
                new("系统编辑器别名", "editor", Path.Combine(directory, ".", "installed-editor.exe")),
                new("便携画板", "painter", secondPath), new("系统计算器", packageId, null),
                new("同名软件", "unrelated-one", null), new("同名软件", "unrelated-two", null)
            ]);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(Box("备注").Text == "未保存的编辑" && !Buttons("查看软件：便携画板").Any() && saves == 0,
                "Asynchronous discovery leaves an in-progress shortcut edit intact");
            Box("备注").Clear();
            Click("返回软件快捷键");
            var combo = Controls<ComboBox>().Single();
            check(combo.Items.Count == 6 && combo.SelectedIndex == 0 && Search().Text == "核对动作",
                "Registered, pinned and normalized installed paths merge while distinct pathless app IDs remain separate");
            Search().Text = "系统编辑器别名";
            check(Buttons("查看软件：已登记的编辑器").Count() == 1 && Buttons("编辑登记：核对动作").Any(),
                "Installed aliases find the original registered app and its existing shortcuts");
            Search().Text = "已固定编辑器";
            check(Buttons("查看软件：已登记的编辑器").Count() == 1, "Pinned display aliases search the same unified application");
            Search().Text = "便携画板";
            Click("查看软件：便携画板");
            check(Buttons("添加手动登记").Any() && Buttons("读取窗口菜单").Any() && saves == 0,
                "A never-pinned app with no shortcuts opens its available sources without creating a group");
            Click("读取窗口菜单");
            check(Labels("不匹配") && saves == 0, "Choosing another program cannot read or overwrite the selected application's records");
            combo.SelectedIndex = 0;
            Search().Text = "系统计算器";
            Click("查看软件：系统计算器");
            check(Buttons("读取窗口菜单").Any() && !Buttons("添加手动登记").Any() && Labels("官方默认参考") && saves == 0,
                "A packaged application exposes its reference and identity-checked menu without inventing a file path");
            Click("读取窗口菜单");
            check(Labels("不匹配") && saves == 0, "A pathless application rejects a window without its exact package identity");
            combo.SelectedIndex = 0;
            Search().Text = "同名软件";
            check(Buttons("查看软件：同名软件").Count() == 2 && discoveries == 1,
                "Searching and switching software retain separate shell identities and never rescan the PC");
        }
        finally { window.CloseModuleDialog(); }

        // A real package identity can be read from the picked window before AppsFolder has returned.
        // Simulate a second read while the window changes package to check stale data is preserved.
        int identityQueries = 0, reads = 0;
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog = new(window, () => [], () => [],
            _ => { saves++; return Task.FromResult(true); }, (_,_) => {}, () => {},
            pickMenuWindow: (select,_) => select("计算器窗口", secondPath, new IntPtr(42)), discover: _ => pending.Task,
            getWindowPackageId: _ => ++identityQueries < 4 ? packageId : "Other.Package_x!App",
            readMenu: (_,_,_) => Task.FromResult(new List<ImportedAppShortcut> {
                new() { Key = "F8", Command = ++reads == 1 ? "窗口公开动作" : "换窗后的动作" }
            }));
        ((IModuleHost)window).ShowDialog(dialog);
        try
        {
            Click("读取窗口菜单");
            check(saves == 0 && Buttons("查看菜单：窗口公开动作").Any() && !Buttons("添加手动登记").Any(),
                "A package picked from all software before discovery opens a session-only menu without saving its executable");
            Click("读取窗口菜单");
            check(Labels("窗口已更换") && saves == 0 && Buttons("查看菜单：窗口公开动作").Any() &&
                !Buttons("查看菜单：换窗后的动作").Any(),
                "A package identity change during a read preserves the previous session menu");
            pending.SetResult([new("系统计算器", packageId, null)]);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(Controls<ComboBox>().Single().Items.Count == 2 && Buttons("查看菜单：窗口公开动作").Any(),
                "Finishing discovery merges a package picked from its window into the installed identity");
            Controls<ComboBox>().Single().SelectedIndex = 0;
            Search().Text = "系统计算器 F8";
            check(Buttons("查看菜单：窗口公开动作").Any(), "A session-only menu remains searchable under the discovered application name");
        }
        finally { window.CloseModuleDialog(); }

        CancellationToken captured = default;
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog = Create(token => { captured = token; return pending.Task; });
        ((IModuleHost)window).ShowDialog(dialog);
        window.CloseModuleDialog();
        pending.SetResult([]);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        check(captured.IsCancellationRequested && saves == 0, "Closing during discovery cancels it without writing any catalog records");

        dialog = Create(_ => Task.FromException<IReadOnlyList<DiscoveredApp>>(new IOException("discovery unavailable")));
        ((IModuleHost)window).ShowDialog(dialog);
        try
        {
            Search().Text = "核对动作";
            check(Labels("暂时无法读取") && Buttons("编辑登记：核对动作").Any(),
                "A failed installed software scan retains search access to registered shortcuts");
        }
        finally { window.CloseModuleDialog(); }

        AppShortcutCatalogDialog Create(Func<CancellationToken,Task<IReadOnlyList<DiscoveredApp>>> discover) =>
            new(window, () => [editor], () => [new ApplicationEntry { Path = editor.Path, DisplayName = "已固定编辑器" }],
                _ => { saves++; return Task.FromResult(true); }, (_,_) => {}, () => {},
                pickMenuWindow: (select,_) => select("无关应用", editor.Path, IntPtr.Zero), discover: discover);
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
}
