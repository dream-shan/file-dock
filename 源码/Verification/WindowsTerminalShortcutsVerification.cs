#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using FileDockDemo.Contracts;

namespace FileDockDemo;

internal static class WindowsTerminalShortcutsVerification
{
    internal static async Task Run(MainWindow window, string directory, Action<bool, string> check)
    {
        string root = Directory.CreateDirectory(Path.Combine(directory, "wt-verify")).FullName;
        VerifyIdentityResolution(root, check);
        await VerifyParser(root, check);
        await VerifyCatalogAndUi(window, root, check);
    }

    private static void VerifyIdentityResolution(string root, Action<bool, string> check)
    {
        string localAppData = Path.Combine(root, "LocalAppData");
        string expectedStable = Path.Combine(localAppData, "Packages", WindowsTerminalProfiles.StablePackageFamily, "LocalState", "settings.json");
        string expectedPreview = Path.Combine(localAppData, "Packages", WindowsTerminalProfiles.PreviewPackageFamily, "LocalState", "settings.json");
        string expectedCanary = Path.Combine(localAppData, "Packages", WindowsTerminalProfiles.CanaryPackageFamily, "LocalState", "settings.json");
        string expectedUnpackaged = Path.Combine(localAppData, "Microsoft", "Windows Terminal", "settings.json");

        var stableApp = WindowsTerminalProfiles.Resolve(null, WindowsTerminalProfiles.StableAppId, localAppData);
        var previewApp = WindowsTerminalProfiles.Resolve(null, WindowsTerminalProfiles.PreviewAppId, localAppData);
        var canaryApp = WindowsTerminalProfiles.Resolve(null, WindowsTerminalProfiles.CanaryAppId, localAppData);
        check(stableApp.Kind == WindowsTerminalInstallKind.StoreStable && stableApp.SettingsPath == expectedStable &&
              previewApp.Kind == WindowsTerminalInstallKind.StorePreview && previewApp.SettingsPath == expectedPreview &&
              canaryApp.Kind == WindowsTerminalInstallKind.StoreCanary && canaryApp.SettingsPath == expectedCanary,
            "Windows Terminal Store editions resolve to their own isolated LocalState settings.json paths");

        string storeStableExe = Path.Combine(root, "WindowsApps", "Microsoft.WindowsTerminal_1.22.10731.0_x64__8wekyb3d8bbwe", "WindowsTerminal.exe");
        string storePreviewExe = Path.Combine(root, "WindowsApps", "Microsoft.WindowsTerminalPreview_1.23.1000.0_x64__8wekyb3d8bbwe", "WindowsTerminal.exe");
        string genericAlias = Path.Combine(localAppData, "Microsoft", "WindowsApps", "wt.exe");
        check(WindowsTerminalProfiles.Resolve(storeStableExe, null, localAppData).SettingsPath == expectedStable &&
              WindowsTerminalProfiles.Resolve(storePreviewExe, null, localAppData).SettingsPath == expectedPreview,
            "Running Store WindowsTerminal.exe paths distinguish Stable and Preview without mixing settings");

        var aliasIdentity = WindowsTerminalProfiles.Resolve(genericAlias, null, localAppData);
        check(aliasIdentity.Kind == WindowsTerminalInstallKind.Ambiguous && !aliasIdentity.CanAutoLocate,
            "Generic WindowsApps wt.exe execution alias refuses to guess between Terminal editions");

        string portableDir = Directory.CreateDirectory(Path.Combine(root, "portable-wt")).FullName;
        File.WriteAllText(Path.Combine(portableDir, ".portable"), "");
        string portableExe = Path.Combine(portableDir, "WindowsTerminal.exe");
        var portable = WindowsTerminalProfiles.Resolve(portableExe, null, localAppData);
        check(portable.Kind == WindowsTerminalInstallKind.Portable &&
              portable.SettingsPath == Path.Combine(portableDir, "settings", "settings.json"),
            "Portable Windows Terminal with .portable marker stays isolated to its local settings directory");

        string unpackagedDir = Directory.CreateDirectory(Path.Combine(root, "unpackaged-wt")).FullName;
        string unpackagedExe = Path.Combine(unpackagedDir, "WindowsTerminal.exe");
        var unpackaged = WindowsTerminalProfiles.Resolve(unpackagedExe, null, localAppData);
        check(unpackaged.Kind == WindowsTerminalInstallKind.Unpackaged && unpackaged.SettingsPath == expectedUnpackaged,
            "Unpackaged Windows Terminal resolves to LocalAppData Microsoft\\Windows Terminal\\settings.json");

        string shimDir = Directory.CreateDirectory(Path.Combine(root, "shim-wt")).FullName;
        var shim = WindowsTerminalProfiles.Resolve(Path.Combine(shimDir, "wt.exe"), null, localAppData);
        var mismatch = WindowsTerminalProfiles.Resolve(storeStableExe, WindowsTerminalProfiles.PreviewAppId, localAppData);
        var shortcut = WindowsTerminalProfiles.Resolve(Path.Combine(root, "Windows Terminal.lnk"), null, localAppData, "Windows 终端");
        check(shim.Kind == WindowsTerminalInstallKind.Ambiguous && mismatch.Kind == WindowsTerminalInstallKind.Ambiguous &&
              shortcut.Kind == WindowsTerminalInstallKind.Ambiguous,
            "Standalone wt.exe shims, mismatched ID/path pairs, and .lnk shortcuts avoid ambiguous auto-location");

        check(WindowsTerminalProfiles.Resolve(null, "Microsoft.WindowsTerminal_8wekyb3d8bbwe!Other", localAppData).Kind == WindowsTerminalInstallKind.None &&
              WindowsTerminalProfiles.Resolve(Path.Combine(root, "notepad.exe"), null, localAppData).Kind == WindowsTerminalInstallKind.None &&
              AppMenuCatalog.OfficialReference("", WindowsTerminalProfiles.PreviewAppId) != null &&
              AppMenuCatalog.OfficialReference("", WindowsTerminalProfiles.CanaryAppId) != null,
            "Unrelated packages and executables are ignored while Preview and Canary share the Terminal official reference");
    }

    private static async Task VerifyParser(string root, Action<bool, string> check)
    {
        string file = Path.Combine(root, "sample-settings.json");
        const string sample = """
            // Windows Terminal mixed v1.x and v1.22+ settings sample
            {
              "$help": "https://aka.ms/terminal-documentation",
              "actions": [
                {
                  "id": "User.LaunchPrivateTool",
                  "command": {
                    "action": "newTab",
                    "commandline": "C:\\SecretTools\\private.exe --token=SUPER_SECRET_TOKEN",
                    "startingDirectory": "D:\\PrivateWorkspace\\Confidential",
                    "profile": "SecretProfile"
                  }
                },
                {
                  "command": {
                    "action": "sendInput",
                    "input": "echo PRIVATE_PAYLOAD_123\r"
                  },
                  "keys": "ctrl+alt+k"
                },
                {
                  "command": "closeTab",
                  "keys": "ctrl+shift+t"
                },
                {
                  "command": "unbound",
                  "keys": "ctrl+shift+f"
                },
                {
                  "command": {
                    "action": "globalSummon",
                    "name": "_quake"
                  },
                  "keys": "win+alt+q"
                },
                {
                  "command": "splitPane",
                  "keys": ["alt+shift++", "ctrl+alt+p"]
                },
                {
                  "iterateOn": "profiles",
                  "command": "newTab",
                  "keys": "ctrl+alt+9"
                }
              ],
              "keybindings": [
                { "id": "User.LaunchPrivateTool", "keys": "ctrl+alt+n" },
                { "id": "Terminal.CopyToClipboard", "keys": "ctrl+c" },
                { "id": "Terminal.DuplicateTab", "keys": "ctrl+alt+d" },
                { "id": null, "keys": "ctrl+shift+d" },
                { "id": "User.MissingExternalAction", "keys": "ctrl+alt+u" },
                { "id": "Terminal.FindText", "keys": "ctrl+shift+t" }
              ]
            }
            """;
        await File.WriteAllTextAsync(file, sample, new UTF8Encoding(true));
        byte[] before = await File.ReadAllBytesAsync(file);

        var rules = await WindowsTerminalShortcuts.ReadAsync(file);
        byte[] after = await File.ReadAllBytesAsync(file);
        check(before.AsSpan().SequenceEqual(after), "Reading Windows Terminal settings.json leaves the file byte-for-byte unchanged");

        string dump = string.Join("\n", rules.Select(r => $"{r.Key}|{r.Command}|{r.Condition}|{r.Status}"));
        check(!dump.Contains("SUPER_SECRET_TOKEN", StringComparison.Ordinal) &&
              !dump.Contains("PrivateWorkspace", StringComparison.Ordinal) &&
              !dump.Contains("PRIVATE_PAYLOAD_123", StringComparison.Ordinal) &&
              !dump.Contains("SecretProfile", StringComparison.Ordinal),
            "Windows Terminal parser excludes user command lines, paths, profiles and sendInput payloads");

        var customFromId = rules.Single(r => r.Key == "ctrl+alt+n");
        var customSendInput = rules.Single(r => r.Key == "ctrl+alt+k");
        var customBuiltInId = rules.Single(r => r.Key == "ctrl+alt+d");
        check(customFromId.Status == ShortcutRuleStatuses.Custom && customFromId.Command.Contains("newTab") && customFromId.HasArguments &&
              customSendInput.Status == ShortcutRuleStatuses.Custom && customSendInput.Command.Contains("sendInput") && customSendInput.HasArguments &&
              customBuiltInId.Status == ShortcutRuleStatuses.Custom && customBuiltInId.Command.Contains("duplicateTab"),
            "Custom bindings resolve both inline actions, user action IDs and built-in Terminal.* IDs");

        var defaultDeclared = rules.Single(r => r.Key == "ctrl+c");
        var overriddenPlus = rules.Single(r => r.Key == "alt+shift++");
        var duplicateCtrlShiftT = rules.Where(r => r.Key == "ctrl+shift+t").ToArray();
        check(defaultDeclared.Status == ShortcutRuleStatuses.DefaultDeclared && defaultDeclared.Command.Contains("copy") &&
              overriddenPlus.Status == ShortcutRuleStatuses.DefaultDeclared &&
              duplicateCtrlShiftT.Length == 2 &&
              duplicateCtrlShiftT[0].Status == ShortcutRuleStatuses.Uncertain &&
              duplicateCtrlShiftT[1].Status == ShortcutRuleStatuses.Override && duplicateCtrlShiftT[1].Command.Contains("find"),
            "Explicit default declarations stay distinguished from real overrides, and earlier duplicate declarations are flagged uncertain");

        var disabledLegacy = rules.Single(r => r.Key == "ctrl+shift+f");
        var disabledModern = rules.Single(r => r.Key == "ctrl+shift+d");
        check(disabledLegacy.Status == ShortcutRuleStatuses.Disabled && disabledLegacy.Command.Contains("查找") &&
              disabledModern.Status == ShortcutRuleStatuses.Disabled && disabledModern.Command.Contains("复制当前标签页"),
            "Disabled default shortcuts are recognized for both legacy command:'unbound' and v1.22+ id:null");

        var globalRule = rules.Single(r => r.Key == "win+alt+q");
        var missingIdRule = rules.Single(r => r.Key == "ctrl+alt+u");
        var dynamicRule = rules.Single(r => r.Key == "ctrl+alt+9");
        check(globalRule.SystemWide &&
              missingIdRule.Status == ShortcutRuleStatuses.Uncertain && missingIdRule.Condition.Contains("未在本文件定义") &&
              dynamicRule.Status == ShortcutRuleStatuses.Uncertain && dynamicRule.Condition.Contains("iterateOn"),
            "Global summon hotkeys, unresolved action IDs and dynamic iterateOn rules preserve scope and uncertain status");

        await File.WriteAllTextAsync(file, """{ "defaultProfile": "{00000000-0000-0000-0000-000000000000}" }""");
        check((await WindowsTerminalShortcuts.ReadAsync(file)).Count == 0,
            "Valid settings.json without actions or keybindings succeeds with zero custom rules");

        await Reject("[]", "Windows Terminal parser requires a top-level JSON object");
        await Reject("""{ "actions": "not-an-array" }""", "Windows Terminal parser rejects non-array actions");
        await Reject("""{ "keybindings": [ { "id": null, "keys": 123 } ] }""", "Windows Terminal parser rejects invalid keys field");
        await Reject("""{ "actions": [""", "Truncated Windows Terminal JSON fails clearly");
        await File.WriteAllBytesAsync(file, [0x7B, 0x7D, 0xC0, 0xAF]);
        await RejectCurrent("Windows Terminal parser rejects invalid UTF-8");
        using (var oversized = new FileStream(file, FileMode.Create))
            oversized.SetLength(4 * 1024 * 1024 + 1);
        await RejectCurrent("Windows Terminal parser rejects oversized settings files");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool wasCancelled = false;
        try { await WindowsTerminalShortcuts.ReadAsync(file, cancelled.Token); }
        catch (OperationCanceledException) { wasCancelled = true; }
        check(wasCancelled, "Cancelled Windows Terminal read stops before opening the file");

        async Task Reject(string json, string scenario)
        {
            await File.WriteAllTextAsync(file, json);
            await RejectCurrent(scenario);
        }
        async Task RejectCurrent(string scenario)
        {
            bool threw = false;
            try { await WindowsTerminalShortcuts.ReadAsync(file); }
            catch (InvalidDataException) { threw = true; }
            check(threw, scenario);
        }
    }

    private static async Task VerifyCatalogAndUi(MainWindow window, string root, Action<bool, string> check)
    {
        string localAppData = Path.Combine(root, "ui-localappdata");
        string stableSettings = Path.Combine(localAppData, "Packages", WindowsTerminalProfiles.StablePackageFamily, "LocalState", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(stableSettings)!);
        const string stableJson = """
            {
              "actions": [
                { "command": "newTab", "keys": "ctrl+alt+t" },
                { "command": "closePane", "keys": "ctrl+shift+t" }
              ],
              "keybindings": [
                { "id": null, "keys": "ctrl+shift+f" }
              ]
            }
            """;
        await File.WriteAllTextAsync(stableSettings, stableJson, new UTF8Encoding(false));
        byte[] originalStable = await File.ReadAllBytesAsync(stableSettings);

        string manualSample = Path.Combine(root, "manual-terminal-settings.json");
        await File.WriteAllTextAsync(manualSample, """{ "keybindings": [ { "id": "Terminal.OpenSettingsUI", "keys": "ctrl+alt+o" } ] }""");

        string unpackagedExe = Path.Combine(root, "desktop-wt", "WindowsTerminal.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(unpackagedExe)!);

        var desktopGroup = new AppShortcutGroup
        {
            Name = "Windows 终端（桌面）",
            Path = unpackagedExe,
            Sources =
            [
                new AppShortcutSource
                {
                    Path = stableSettings,
                    Kind = AppShortcutSourceKinds.WindowsTerminal,
                    LastRead = DateTimeOffset.UtcNow,
                    Rules =
                    [
                        new ImportedAppShortcut { Key = "ctrl+alt+t", Command = "使用默认配置新建标签页 (newTab)", Status = ShortcutRuleStatuses.Custom },
                        new ImportedAppShortcut { Key = "ctrl+shift+f", Command = "禁用默认键位（原默认：在终端中查找文本）", Status = ShortcutRuleStatuses.Disabled },
                        new ImportedAppShortcut { Key = "ctrl+alt+u", Command = "User.Unknown", Status = ShortcutRuleStatuses.Uncertain }
                    ]
                }
            ]
        };
        check(AppMappingRules.Eligibility(desktopGroup, null, desktopGroup.Sources[0].Rules[0]) == null &&
              AppMappingRules.Eligibility(desktopGroup, null, desktopGroup.Sources[0].Rules[1]) != null &&
              AppMappingRules.Eligibility(desktopGroup, null, desktopGroup.Sources[0].Rules[2]) != null,
            "Foreground mapping allows actionable unpackaged Terminal bindings while blocking disabled and uncertain rules");

        var dialog = new AppShortcutCatalogDialog(
            window,
            () => window.State.AppShortcutGroups,
            () => [],
            window.SaveAppShortcutGroupAsync,
            (_, _) => { },
            () => { },
            () => window.State.Shortcuts!,
            discover: _ => Task.FromResult<IReadOnlyList<DiscoveredApp>>([
                new("Windows 终端", WindowsTerminalProfiles.StableAppId, null),
                new("Windows 终端 Preview", WindowsTerminalProfiles.PreviewAppId, null)
            ]),
            getLocalAppData: () => localAppData,
            chooseFile: _ => manualSample);

        window.ExpandPanel();
        ((IModuleHost)window).ShowDialog(dialog);
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Search().Text = "Windows 终端";
            Click("查看软件：Windows 终端");
            check(Buttons("读取终端配置").Any() && Buttons("Windows 终端配置来源").Any() && !Buttons("VS Code 导入来源").Any(),
                "Windows Terminal detail exposes read and source management entries without VS Code controls");

            Click("读取终端配置");
            await WaitForIdle();
            var savedStable = window.Storage.Load().AppShortcutGroups.Single(g =>
                string.Equals(g.AppId, WindowsTerminalProfiles.StableAppId, StringComparison.OrdinalIgnoreCase));
            check(savedStable.Sources.Single().Rules.Count == 3 &&
                  savedStable.Sources.Single().Kind == AppShortcutSourceKinds.WindowsTerminal &&
                  Buttons("刷新终端配置").Any() &&
                  (await File.ReadAllBytesAsync(stableSettings)).AsSpan().SequenceEqual(originalStable),
                "Reading Store Stable Windows Terminal persists its rules under its package AppId without altering settings.json");

            Search().Text = "ctrl+alt+t";
            check(Buttons("查看规则：使用默认配置新建标签页 (newTab)").Any(),
                "Newly added Windows Terminal shortcut is displayed and searchable");
            Search().Text = "覆盖默认键位";
            check(Buttons("查看规则：关闭当前窗格 (closePane)").Any(),
                "Overridden Windows Terminal default shortcut is searchable by override status");
            Search().Text = "禁用";
            check(Buttons("查看规则：禁用默认键位（原默认：在终端中查找文本）").Any(),
                "Disabled Windows Terminal default shortcut is displayed and searchable");

            // Corrupt the file and refresh: previous successful rules must remain intact.
            await File.WriteAllTextAsync(stableSettings, "{ broken");
            Click("刷新终端配置");
            await WaitForIdle();
            var preserved = window.Storage.Load().AppShortcutGroups.Single(g =>
                string.Equals(g.AppId, WindowsTerminalProfiles.StableAppId, StringComparison.OrdinalIgnoreCase));
            check(Labels("已保留上次成功结果") && preserved.Sources.Single().Rules.Count == 3,
                "Failed Windows Terminal refresh preserves the last successful snapshot on disk and in UI");

            // Switch to Preview edition: must not mix Stable settings and must support manual selection.
            Controls<ComboBox>().Single().SelectedIndex = 0;
            Search().Text = "Windows 终端 Preview";
            Click("查看软件：Windows 终端 Preview");
            check(Buttons("读取终端配置").Any() && !Buttons("刷新终端配置").Any(),
                "Preview edition starts clean and never mixes Stable Terminal configuration");

            Click("读取终端配置");
            await WaitForIdle();
            check(Labels("未在 Windows 终端 Preview") &&
                  window.Storage.Load().AppShortcutGroups.All(g =>
                      !string.Equals(g.AppId, WindowsTerminalProfiles.PreviewAppId, StringComparison.OrdinalIgnoreCase)),
                "Missing Preview settings.json reports clearly without falling back to Stable settings");

            Click("手动选择 settings.json");
            await WaitForIdle();
            var savedPreview = window.Storage.Load().AppShortcutGroups.Single(g =>
                string.Equals(g.AppId, WindowsTerminalProfiles.PreviewAppId, StringComparison.OrdinalIgnoreCase));
            check(savedPreview.Sources.Single().Rules.Single().Key == "ctrl+alt+o",
                "Manual selection imports an explicit settings.json for Preview without mixing with Stable");
        }
        finally
        {
            window.CloseModuleDialog();
            await window.SaveAppShortcutGroupAsync(new AppShortcutGroup { Name = "Windows 终端", AppId = WindowsTerminalProfiles.StableAppId });
            await window.SaveAppShortcutGroupAsync(new AppShortcutGroup { Name = "Windows 终端 Preview", AppId = WindowsTerminalProfiles.PreviewAppId });
        }

        IEnumerable<T> Controls<T>() where T : FrameworkElement
        {
            window.UpdateLayout();
            return DisplayPowerViewVerification.Children<T>(dialog.View).Where(item => item.IsVisible);
        }
        IEnumerable<Button> Buttons(string name) => Controls<Button>().Where(item =>
            AutomationProperties.GetName(item) == name || Equals(item.Content, name));
        bool Labels(string value) => Controls<TextBlock>().Any(item => item.Text.Contains(value));
        TextBox Search() => Controls<TextBox>().Single(item => AutomationProperties.GetName(item) == "搜索软件快捷键");
        void Click(string name) => Buttons(name).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task WaitForIdle()
        {
            var timer = Stopwatch.StartNew();
            while (dialog.HasChanges && timer.ElapsedMilliseconds < 10000) await Task.Delay(20);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    }
}
