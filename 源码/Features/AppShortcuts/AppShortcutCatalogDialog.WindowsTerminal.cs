#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;

namespace FileDockDemo;

internal sealed partial class AppShortcutCatalogDialog
{
    private readonly Func<string> _getLocalAppData;

    private WindowsTerminalIdentity SelectedTerminalIdentity()
    {
        bool hasTerminalSource = _group?.Sources.Any(source => source.Kind == AppShortcutSourceKinds.WindowsTerminal) == true;
        string? path = _selectedSoftware?.Path ?? (_group is { HasPackageIdentity: false } ? _group.Path : null);
        string? appId = _selectedSoftware?.Id ?? (_group is { HasPackageIdentity: true } ? _group.AppId : null);
        string? name = _selectedSoftware?.Name ?? _group?.Name;
        return WindowsTerminalProfiles.Resolve(path, appId, _getLocalAppData(), name, hasTerminalSource);
    }

    private IReadOnlyList<AppShortcutSource> SelectedTerminalSources() =>
        _group?.Sources.Where(source => source.Kind == AppShortcutSourceKinds.WindowsTerminal).ToArray() ?? [];

    private void AddWindowsTerminalCatalogActions(WrapPanel actions, WindowsTerminalIdentity identity)
    {
        var sources = SelectedTerminalSources();
        if (sources.Count == 0)
        {
            actions.Children.Add(CompactButton("读取终端配置", async () => await ReadDetectedTerminalSource(identity, returnToCatalog: true)));
        }
        else
        {
            actions.Children.Add(CompactButton("刷新终端配置", async () => await RefreshTerminalSources(returnToCatalog: true)));
        }
        actions.Children.Add(CompactButton("Windows 终端配置来源", ShowWindowsTerminalSources));
    }

    private void AddWindowsTerminalCatalogSummary()
    {
        var sources = SelectedTerminalSources();
        if (sources.Count == 0) return;
        int totalRules = sources.Sum(source => source.Rules.Count);
        DateTimeOffset? latest = sources.Where(source => source.LastRead != null)
            .Select(source => source.LastRead)
            .DefaultIfEmpty()
            .Max();
        string timePrefix = latest is { } read ? $"{read.ToLocalTime():yyyy-MM-dd HH:mm} · " : "";
        string detail = totalRules > 0
            ? $"已读取 Windows 终端配置（{totalRules} 条规则）"
            : "已读取 Windows 终端配置（未声明自定义快捷键，当前沿用终端默认键位）";
        _body.Children.Add(Ui.Text(timePrefix + detail + "。", 12));
    }

    private void ShowWindowsTerminalSources()
    {
        var identity = SelectedTerminalIdentity();
        if (!identity.IsWindowsTerminal) return;

        ShowDetail("Windows 终端配置来源", ShowCatalogAndFocus);
        _body.Children.Add(Ui.Text($"当前实例：{identity.EditionLabel}", 13));
        _body.Children.Add(Ui.Text("只读读取 settings.json，不会修改 Windows 终端原配置；命令行与路径等参数值不保存在文件坞。", 12));

        if (identity.CanAutoLocate)
        {
            var autoPath = Ui.Text($"自动查找位置：{identity.SettingsPath}", 12);
            autoPath.ToolTip = identity.SettingsPath;
            autoPath.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            _body.Children.Add(autoPath);
            var autoActions = new WrapPanel();
            autoActions.Children.Add(CompactButton("自动读取此版本配置",
                async () => await ReadDetectedTerminalSource(identity, returnToCatalog: false)));
            autoActions.Children.Add(CompactButton("手动选择 settings.json", PickWindowsTerminalSource));
            _body.Children.Add(autoActions);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(identity.AmbiguityReason))
                _body.Children.Add(Ui.Text(identity.AmbiguityReason, 12));
            _body.Children.Add(Ui.Button("手动选择 settings.json", PickWindowsTerminalSource));
        }

        var sources = SelectedTerminalSources();
        foreach (var source in sources)
        {
            string stamp = source.LastRead is { } read ? $" · {read.ToLocalTime():yyyy-MM-dd HH:mm}" : "";
            _body.Children.Add(Ui.Text($"Windows 终端配置 · {source.Rules.Count} 条{stamp}", 14));
            var path = Ui.Text(source.Path, 12);
            path.ToolTip = source.Path;
            path.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            _body.Children.Add(path);

            var rowActions = new WrapPanel();
            var refresh = CompactButton("刷新来源", async () => await ReadWindowsTerminalSource(source.Path, returnToCatalog: false));
            AutomationProperties.SetName(refresh, $"刷新 Windows 终端配置 {source.Path}");
            rowActions.Children.Add(refresh);

            var remove = CompactButton("移除来源", async () => {
                if (_group == null || !ConfirmRemove("移除该 Windows 终端配置在文件坞里的读取记录？（不会改动原 settings.json）")) return;
                var candidate = _group.Copy();
                candidate.Sources.RemoveAll(item => item.Kind == AppShortcutSourceKinds.WindowsTerminal && SamePath(item.Path, source.Path));
                await SaveGroup(candidate, "Windows 终端配置来源已移除。", ShowWindowsTerminalSources);
            });
            AutomationProperties.SetName(remove, $"移除 Windows 终端配置 {source.Path}");
            rowActions.Children.Add(remove);
            _body.Children.Add(rowActions);
        }
        FocusInitial();
    }

    private async Task ReadDetectedTerminalSource(WindowsTerminalIdentity identity, bool returnToCatalog)
    {
        if (_busy || _closed) return;
        if (!identity.CanAutoLocate || identity.SettingsPath == null)
        {
            ShowWindowsTerminalSources();
            SetStatus(identity.AmbiguityReason ?? "无法自动确认配置文件位置，请手动选择 settings.json。");
            return;
        }
        if (!File.Exists(identity.SettingsPath))
        {
            ShowWindowsTerminalSources();
            SetStatus($"未在 {identity.EditionLabel} 的默认位置找到 settings.json，请确认该版本已初始化或手动选择配置文件。");
            return;
        }
        await ReadWindowsTerminalSource(identity.SettingsPath, returnToCatalog);
    }

    private async void PickWindowsTerminalSource()
    {
        if (_busy || _closed) return;
        var identity = SelectedTerminalIdentity();
        var dialog = new OpenFileDialog
        {
            Title = "选择 Windows 终端的 settings.json",
            Filter = "Windows 终端配置|settings.json;*.json;*.jsonc|所有文件|*.*",
            CheckFileExists = true,
            FileName = "settings.json"
        };
        if (identity.SettingsPath != null)
        {
            string? folder = Path.GetDirectoryName(identity.SettingsPath);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                dialog.InitialDirectory = folder;
        }
        if (ChooseFile(dialog) is { } path)
            await ReadWindowsTerminalSource(path, returnToCatalog: false);
    }

    private async Task RefreshTerminalSources(bool returnToCatalog)
    {
        var sources = SelectedTerminalSources();
        if (sources.Count == 0) return;
        if (sources.Count == 1)
        {
            await ReadWindowsTerminalSource(sources[0].Path, returnToCatalog);
            return;
        }
        ShowWindowsTerminalSources();
        SetStatus("已存在多个 Windows 终端配置文件来源，请在下方逐个刷新或管理。");
    }

    private AppShortcutGroup? BuildTerminalGroupCandidate()
    {
        if (_group != null) return _group.Copy();
        if (_selectedSoftware == null) return null;
        if (_selectedSoftware.Path is { } path)
            return new AppShortcutGroup { Name = _selectedSoftware.Name, Path = path };
        if (_selectedSoftware.PackageOnly && !string.IsNullOrWhiteSpace(_selectedSoftware.Id))
            return new AppShortcutGroup { Name = _selectedSoftware.Name, AppId = _selectedSoftware.Id };
        return null;
    }

    private async Task ReadWindowsTerminalSource(string path, bool returnToCatalog)
    {
        var candidate = BuildTerminalGroupCandidate();
        if (candidate == null || _closed || _busy) return;

        bool saved = false;
        int rulesCount = 0;
        int lostMappings = 0, pausedMappings = 0;
        bool hadPrevious = candidate.Sources.Any(item =>
            item.Kind == AppShortcutSourceKinds.WindowsTerminal && SamePath(item.Path, path));

        BeginWork("正在只读读取 Windows 终端配置…");
        using var cancellation = new CancellationTokenSource();
        _importCancellation = cancellation;
        try
        {
            var rules = await WindowsTerminalShortcuts.ReadAsync(path, cancellation.Token);
            if (_closed) return;
            rulesCount = rules.Count;
            var replacement = new AppShortcutSource
            {
                Path = Path.GetFullPath(path),
                Kind = AppShortcutSourceKinds.WindowsTerminal,
                IsDefault = false,
                LastRead = DateTimeOffset.UtcNow,
                Rules = rules.Select(rule => new ImportedAppShortcut
                {
                    Key = rule.Key,
                    Command = rule.Command,
                    Condition = rule.Condition,
                    Status = rule.Status,
                    HasArguments = rule.HasArguments,
                    SystemWide = rule.SystemWide
                }).ToList()
            };

            int existing = candidate.Sources.FindIndex(item =>
                item.Kind == AppShortcutSourceKinds.WindowsTerminal && SamePath(item.Path, replacement.Path));
            if (existing < 0) candidate.Sources.Add(replacement);
            else
            {
                var previous = candidate.Sources[existing];
                AppMappingRules.RestoreMappings(previous, replacement);
                lostMappings = previous.Rules.Count(rule => rule.Mapping != null) -
                    replacement.Rules.Count(rule => rule.Mapping != null);
                candidate.Sources[existing] = replacement;
            }
            pausedMappings = AppMappingRules.PauseConflicts(candidate, _getGlobals());
            saved = await _save(candidate);
            if (_closed) return;
            if (saved) SetSavedGroup(candidate);
            else SetStatus(hadPrevious ? "保存 Windows 终端配置失败，已保留上次成功结果。" : "保存 Windows 终端配置失败，请重试。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_closed)
            {
                string prefix = hadPrevious ? "读取 Windows 终端配置失败，已保留上次成功结果：" : "读取 Windows 终端配置失败：";
                SetStatus(prefix + error.Message);
            }
        }
        finally
        {
            _importCancellation = null;
            EndWork();
        }

        if (saved && !_closed)
        {
            if (returnToCatalog) ShowCatalogAndFocus();
            else ShowWindowsTerminalSources();
            string summary = rulesCount == 0
                ? "已读取 Windows 终端配置，未发现自定义快捷键声明（当前沿用终端默认键位）。"
                : $"已读取 Windows 终端配置中的 {rulesCount} 条快捷键声明。";
            SetStatus(summary +
                (pausedMappings == 0 ? "" : $"因键位冲突，已暂停 {pausedMappings} 个映射。") +
                (lostMappings == 0 ? "" : $"因配置规则变更，{lostMappings} 个原映射无法继承。"));
        }
    }
}
