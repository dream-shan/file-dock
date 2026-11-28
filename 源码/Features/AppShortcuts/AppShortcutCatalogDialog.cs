using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using FileDockDemo.Contracts;
using Microsoft.Win32;

namespace FileDockDemo;

internal sealed partial class AppShortcutCatalogDialog : IModuleDialog
{
    private const int PageSize = 40;
    private readonly IModuleHost _host;
    private readonly Func<IReadOnlyList<AppShortcutGroup>> _getGroups;
    private readonly Func<IReadOnlyList<ApplicationEntry>> _getFixed;
    private readonly Func<AppShortcutGroup,Task<bool>> _save;
    private readonly Action<Action<string,string>,Action> _pickWindow;
    private readonly Action<Action<string,string,IntPtr>,Action>? _pickMenuWindow;
    private readonly Func<IntPtr,string?> _getWindowPackageId;
    private readonly Func<IntPtr,string,CancellationToken,Task<List<ImportedAppShortcut>>> _readMenu;
    private readonly Func<OpenFileDialog,string?>? _chooseFile;
    private readonly Action _back;
    private readonly Func<IReadOnlyDictionary<string,ShortcutBinding>> _getGlobals;
    private readonly StackPanel _body = new();
    private readonly StackPanel _results = new();
    private readonly TextBlock _status = Ui.Text("");
    private readonly ComboBox _software = new() { MinHeight = 34, MaxDropDownHeight = 220 };
    private readonly TextBox _search = Field("搜索软件快捷键", 256);
    private readonly ScrollViewer _scroll;
    private AppShortcutGroup? _group;
    private Func<bool>? _hasDraft;
    private Action? _detailReturn;
    private Control? _initialFocus;
    private CancellationTokenSource? _importCancellation;
    private int _page;
    private bool _changingSoftware, _busy, _closed, _allSoftware = true;

    private readonly record struct CatalogRow(SoftwareChoice Software, ManualAppShortcut? Manual, AppShortcutSource? Source,
        ImportedAppShortcut? Rule, OfficialShortcutReference? Reference);

    public FrameworkElement View => _scroll;
    public bool HasChanges => _busy || _hasDraft?.Invoke() == true;

    internal AppShortcutCatalogDialog(IModuleHost host, Func<IReadOnlyList<AppShortcutGroup>> getGroups,
        Func<IReadOnlyList<ApplicationEntry>> getFixed, Func<AppShortcutGroup,Task<bool>> save,
        Action<Action<string,string>,Action> pickWindow, Action back,
        Func<IReadOnlyDictionary<string,ShortcutBinding>>? getGlobals = null,
        Action<Action<string,string,IntPtr>,Action>? pickMenuWindow = null,
        Func<CancellationToken,Task<IReadOnlyList<DiscoveredApp>>>? discover = null,
        Func<IntPtr,string?>? getWindowPackageId = null,
        Func<IntPtr,string,CancellationToken,Task<List<ImportedAppShortcut>>>? readMenu = null,
        Func<string>? getLocalAppData = null,
        Func<OpenFileDialog,string?>? chooseFile = null,
        Func<CancellationToken,Task<OpenWindowMenuScanResult>>? scanOpenWindows = null)
    {
        _host = host;
        _getGroups = getGroups;
        _getFixed = getFixed;
        _save = save;
        _pickWindow = pickWindow;
        _pickMenuWindow = pickMenuWindow;
        _getWindowPackageId = getWindowPackageId ?? AppShortcutTarget.PackageWindowId;
        _readMenu = readMenu ?? MenuShortcutReader.ReadAsync;
        _getLocalAppData = getLocalAppData ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        _chooseFile = chooseFile;
        _scanOpenWindows = scanOpenWindows;
        _back = back;
        _getGlobals = getGlobals ?? (() => new Dictionary<string,ShortcutBinding>());

        AutomationProperties.SetName(_software, "软件");
        _software.SelectionChanged += (_,_) => {
            if (!_changingSoftware && _software.SelectedItem is ComboBoxItem item)
            {
                if (item.Tag is SoftwareChoice choice) SelectChoice(choice);
                else
                {
                    _allSoftware = true;
                    _selectedSoftware = null;
                    _page = 0;
                    SetStatus("");
                    ShowCatalogAndFocus();
                }
            }
        };
        _search.TextChanged += (_,_) => {
            _page = 0;
            RenderResults();
        };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        SetStatus("");

        var root = new StackPanel();
        var title = Ui.Text("软件内部快捷键", 18);
        title.FontWeight = FontWeights.SemiBold;
        title.Margin = new Thickness(0, 0, 0, 6);
        root.Children.Add(title);
        root.Children.Add(_status);
        root.Children.Add(_body);
        root.Children.Add(Ui.Button("关闭", RequestClose));
        _scroll = Ui.Scroll(root);
        SetDiscoveryHint(discover != null ? "正在读取本机软件列表…" : "");
        SetOpenWindowsHint("");
        ShowCatalog();
        if (discover != null) _ = LoadInstalledAsync(discover);
        if (scanOpenWindows != null) _ = ScanOpenWindowsAsync();
    }

    private void ShowCatalog()
    {
        if (_allSoftware) _group = null;
        _catalogVisible = true;
        _hasDraft = null;
        _detailReturn = null;
        _body.Children.Clear();
        _body.Children.Add(NavBackButton("返回快捷键管理", () => { if (!_busy) _back(); }));
        RefreshSoftwareChoices();
        _body.Children.Add(_software);
        _body.Children.Add(_discoveryHint);
        _body.Children.Add(_openWindowsHint);
        _initialFocus = _software.Items.Count > 0 ? _software : null;

        var softwareActions = new WrapPanel();
        var browse = CompactButton("选择程序", BrowseSoftware);
        _initialFocus ??= browse;
        softwareActions.Children.Add(browse);
        softwareActions.Children.Add(CompactButton("从窗口选择", BeginWindowPick));
        bool packageMenu = _selectedSoftware?.PackageOnly == true;
        if (_pickMenuWindow != null && (_allSoftware || SelectedExecutable || packageMenu))
            softwareActions.Children.Add(CompactButton("读取窗口菜单", BeginMenuPick));
        AddOpenWindowCatalogControls(softwareActions);
        _body.Children.Add(softwareActions);
        var terminalIdentity = _allSoftware ? default : SelectedTerminalIdentity();
        bool isTerminal = !_allSoftware && terminalIdentity is { IsWindowsTerminal: true };
        bool canManual = _group != null && !_group.HasPackageIdentity;
        if (canManual || isTerminal || (_group != null && IsVsCode(_group)))
        {
            var actions = new WrapPanel();
            if (canManual)
                actions.Children.Add(CompactButton("添加手动登记", () => EditManual(null)));
            if (_group != null && IsVsCode(_group))
                actions.Children.Add(CompactButton("VS Code 导入来源", ShowSources));
            if (isTerminal)
                AddWindowsTerminalCatalogActions(actions, terminalIdentity!);
            if (actions.Children.Count > 0) _body.Children.Add(actions);
        }
        if (_group != null)
        {
            if (!IsVsCode(_group) && !isTerminal && AppMenuCatalog.OfficialReference(_group.Path, _selectedSoftware?.Id) == null &&
                _group.LastMenuRead == null && _group.Manual.Count == 0 && !_liveWindowMenus.ContainsKey(_group.IdentityKey) && SelectedExecutable)
                _body.Children.Add(Ui.Text("暂无可导入的配置或官方默认参考。打开软件后可读取窗口菜单，也可手动登记。", 12));
            if (isTerminal) AddWindowsTerminalCatalogSummary();
            if (_group.LastMenuRead is { } read)
            {
                int count = _group.MenuSource?.Rules.Count ?? 0;
                string coverage = count > 0 ? $"读到菜单公开的 {count} 条键位" : "所选窗口未公开可读的菜单键位";
                _body.Children.Add(Ui.Text($"{read.ToLocalTime():yyyy-MM-dd HH:mm} · {coverage}。", 12));
            }
        }
        if (!_allSoftware && !SelectedExecutable)
            _body.Children.Add(Ui.Text(packageMenu
                ? (isTerminal
                    ? "可读取此实例的个人 settings.json 快捷键配置；也可打开应用后读取窗口菜单（关闭本页后清除）。"
                    : "打开此应用后可读取窗口菜单，结果保留到关闭本页。若要手动登记，可从窗口选择实际程序。")
                : _group == null
                ? "Windows 仅提供此应用的入口。要登记本机键位或读取菜单，可打开软件后点「从窗口选择」识别实际程序。"
                : "要读取窗口菜单，可打开此快捷方式后点「从窗口选择」识别实际程序。", 12));
        _body.Children.Add(Ui.Text("按软件、动作、原键位或映射键位查找", 12));
        _body.Children.Add(_search);
        _body.Children.Add(_results);
        RenderResults();
        _scroll.ScrollToTop();
    }

    private void SelectSoftware(string name, string path)
    {
        SelectChoice(_choices.FirstOrDefault(item => item.Path != null && SamePath(item.Path, path))
            ?? new SoftwareChoice(name, path));
    }

    private void SelectChoice(SoftwareChoice choice)
    {
        if (_closed) return;
        _allSoftware = false;
        _selectedSoftware = choice;
        _group = _getGroups().FirstOrDefault(group =>
            string.Equals(group.IdentityKey, choice.Identity, StringComparison.OrdinalIgnoreCase))?.Copy() ??
            (choice.Path is { } path ? new AppShortcutGroup { Name = choice.Name, Path = path } : null);
        SetStatus("");
        _search.Clear();
        _page = 0;
        ShowCatalog();
        FocusInitial();
    }

    private void BrowseSoftware()
    {
        var dialog = new OpenFileDialog {
            Title = "选择要登记的软件",
            Filter = "程序及快捷方式|*.exe;*.lnk",
            CheckFileExists = true
        };
        if (ChooseFile(dialog) is { } path)
            SelectSoftware(Path.GetFileNameWithoutExtension(path), path);
    }

    private void BeginWindowPick()
    {
        if (_busy || _closed) return;
        _pickWindow(SelectSoftware, () => {
            if (_closed) return;
            SetStatus("已返回软件快捷键。可再次选择窗口或直接选择程序。");
            FocusInitial();
        });
    }

    private string? ChooseFile(OpenFileDialog dialog)
    {
        if (_chooseFile != null) return _chooseFile(dialog);
        var owner = Window.GetWindow(View);
        return (owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true ? dialog.FileName : null;
    }

    private void RenderResults()
    {
        _results.Children.Clear();
        var query = _search.Text.Trim();
        int total = 0;
        var rows = new List<CatalogRow>(PageSize);
        foreach (var row in CatalogRows(query))
        {
            if (total / PageSize == _page) rows.Add(row);
            total++;
        }
        if (total == 0)
        {
            _results.Children.Add(Ui.Text(query.Length != 0 ? "没有匹配的记录。" :
                _onlyLiveWindowShortcuts ? "当前未筛选到打开窗口的菜单快捷键。可关闭筛选查看全部，或点击「刷新打开窗口」。" :
                _allSoftware ? "暂无快捷键记录。可搜索软件名称查看可用来源。" :
                "暂无快捷键记录。", 12));
            return;
        }

        int pages = (total + PageSize - 1) / PageSize;
        if (_page >= pages)
        {
            _page = pages - 1;
            RenderResults();
            return;
        }
        if (pages > 1)
        {
            var paging = new WrapPanel();
            var previous = CompactButton("上一页", () => { _page--; RenderResults(); });
            var next = CompactButton("下一页", () => { _page++; RenderResults(); });
            previous.IsEnabled = _page > 0;
            next.IsEnabled = _page + 1 < pages;
            paging.Children.Add(previous);
            paging.Children.Add(next);
            paging.Children.Add(Ui.Text($"第 {_page + 1} / {pages} 页 · {total} 条", 12));
            _results.Children.Add(paging);
        }
        foreach (var row in rows)
        {
            var choice = row.Software;
            string app = _allSoftware ? choice.Name + " · " : "";
            if (choice.Group is { } group && row.Manual is { } manual)
                AddResult(manual.Action, manual.Keys, app + $"手动登记 · {ScopeLabel(manual.Scope)}" + MappingSuffix(manual.Mapping), manual.Note,
                    "编辑登记", () => { _group = group; EditManual(manual); });
            else if (row.Reference is { } reference && row.Rule is { } example)
                AddResult(example.Command, example.Key, app + "官方默认参考", reference.Scope,
                    "查看参考", () => ShowReference(choice, reference, example));
            else if (choice.Group is { } owner && row.Source is { } source && row.Rule is { } rule)
            {
                string statusText = ShortcutRuleStatuses.Label(rule.Status);
                string scope = rule.Status == ShortcutRuleStatuses.Disabled ? "" :
                    (rule.SystemWide ? "声明全局" : "应用内规则");
                string summary = string.Join(" · ", new[] { SourceLabel(owner, source), statusText, scope }
                    .Where(part => part.Length > 0));
                AddResult(RuleLabel(rule), rule.Key, app + summary + MappingSuffix(rule.Mapping), rule.Condition,
                    "查看规则", () => { _group = owner; ShowImportedRule(source, rule); });
            }
            else if (row.Rule is { } observed)
                AddResult(observed.Command, observed.Key, app + "本次窗口实读", observed.Condition,
                    "查看菜单", () => ShowPackageMenuRule(choice, observed));
            else
                AddResult(choice.Name, choice.Id != null ? "本机应用" : "软件", "查看快捷键来源",
                    choice.Path ?? choice.Id ?? "", "查看软件", () => SelectChoice(choice));
        }
    }

    private void AddResult(string name, string keys, string summary, string details, string actionName, Action open)
    {
        var text = new StackPanel();
        text.Children.Add(RowText(name, 13));
        var description = RowText($"{keys} · {summary}", 12);
        description.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        text.Children.Add(description);
        var button = Ui.Button(name, open);
        button.Content = text;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.ToolTip = string.Join("\n", new[] { name, keys, summary, details }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        AutomationProperties.SetName(button, $"{actionName}：{name}");
        _results.Children.Add(button);
    }

    private void EditManual(ManualAppShortcut? current)
    {
        var group = _group;
        if (group == null) return;
        var action = Field("动作名称", 160, current?.Action);
        var keys = Field("原快捷键", 256, current?.Keys);
        var scope = new ComboBox { MinHeight = 34 };
        AutomationProperties.SetName(scope,"生效范围");
        foreach (var value in Enum.GetValues<ManualShortcutScope>())
        {
            var option = new ComboBoxItem { Content = ScopeLabel(value), Tag = value };
            scope.Items.Add(option);
            if (value == (current?.Scope ?? ManualShortcutScope.Unknown)) scope.SelectedItem = option;
        }
        var note = Field("备注", 1024, current?.Note);
        note.AcceptsReturn = true;
        note.TextWrapping = TextWrapping.Wrap;
        note.MinHeight = 58;
        note.MaxHeight = 110;
        note.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        ManualShortcutScope SelectedScope() => (scope.SelectedItem as ComboBoxItem)?.Tag is ManualShortcutScope value
            ? value : ManualShortcutScope.Unknown;
        ShowDetail(current == null ? "添加手动登记" : "编辑手动登记", ReturnFromDetail);
        _body.Children.Add(Ui.Text(group.Name + " · 手动登记", 12));
        _body.Children.Add(Ui.Text("动作名称"));
        _body.Children.Add(action);
        _body.Children.Add(Ui.Text("原快捷键，可按文字填写连按键位"));
        _body.Children.Add(keys);
        _body.Children.Add(Ui.Text("生效范围（手动标记）"));
        _body.Children.Add(scope);
        _body.Children.Add(Ui.Text("备注，可填写在哪个页面或工具中生效"));
        _body.Children.Add(note);
        var entry = current?.Copy() ?? new ManualAppShortcut();
        var mapping = new MappingEditor(current?.Mapping);
        void UpdateEntry()
        {
            entry.Action = action.Text.Trim();
            entry.Keys = keys.Text.Trim();
            entry.Note = note.Text.Trim();
            entry.Scope = SelectedScope();
            bool changedOriginal = current?.Mapping != null && current.Keys != entry.Keys;
            mapping.SetAvailability(changedOriginal ? "原快捷键已更改。" :
                AppMappingRules.Eligibility(group, entry, null),
                changedOriginal || (current?.Mapping != null && current.Scope != entry.Scope));
        }
        UpdateEntry();
        keys.TextChanged += (_,_) => UpdateEntry();
        scope.SelectionChanged += (_,_) => UpdateEntry();
        _body.Children.Add(mapping.View);
        _hasDraft = () => mapping.HasChanges || action.Text != (current?.Action ?? "") || keys.Text != (current?.Keys ?? "") ||
            note.Text != (current?.Note ?? "") || SelectedScope() != (current?.Scope ?? ManualShortcutScope.Unknown);
        _body.Children.Add(Ui.Button("保存登记", async () => {
            if (string.IsNullOrWhiteSpace(action.Text) || string.IsNullOrWhiteSpace(keys.Text))
            {
                SetStatus("请填写动作名称和原快捷键。");
                return;
            }
            UpdateEntry();
            if (!mapping.TryRead(out var binding, out var invalid)) { ShowValidation(invalid); return; }
            entry.Mapping = binding;
            var candidate = group.Copy();
            if (current == null) candidate.Manual.Add(entry);
            else
            {
                entry.Id = current.Id;
                candidate.Manual[candidate.Manual.FindIndex(item => item.Id == current.Id)] = entry;
            }
            await SaveGroup(candidate, current?.Mapping != null && binding == null ? "登记已保存，映射已清除。" : "登记已保存。", ShowCatalogAndFocus);
        }));
        if (current != null)
        {
            _body.Children.Add(Ui.Button("删除登记", async () => {
                if (!ConfirmRemove("删除这条手动登记？")) return;
                var candidate = group.Copy();
                candidate.Manual.RemoveAll(item => item.Id == current.Id);
                await SaveGroup(candidate, "登记已删除。", ShowCatalogAndFocus);
            }));
        }
        _initialFocus = action;
        FocusInitial();
    }

    private void ShowSources()
    {
        var group = _group;
        if (group == null) return;
        ShowDetail("VS Code 导入来源", ShowCatalogAndFocus);
        AddDetectedProfiles(group);
        _body.Children.Add(Ui.Text("手动选择配置文件，导入这份文件中声明的键位。实际触发仍取决于 VS Code 的活动配置、扩展和当前编辑位置。"));
        _body.Children.Add(Ui.Button("导入 VS Code 用户配置", async () => await PickSource(false)));
        _body.Children.Add(Ui.Text("从 VS Code 命令面板打开默认快捷键 JSON，复制内容到本地 JSON 文件后导入。", 12));
        _body.Children.Add(Ui.Button("导入 VS Code 默认快捷键", async () => await PickSource(true)));
        foreach (var source in group.Sources.Where(item => item.Kind == AppShortcutSourceKinds.VsCode))
        {
            _body.Children.Add(Ui.Text($"{SourceName(source)} · {source.Rules.Count} 条", 14));
            var path = Ui.Text(source.Path, 12);
            path.ToolTip = source.Path;
            path.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            _body.Children.Add(path);
            var actions = new WrapPanel();
            var refresh = CompactButton("刷新来源", async () => await ReadSource(source.Path, source.IsDefault));
            AutomationProperties.SetName(refresh, $"刷新{SourceName(source)} {source.Path}");
            actions.Children.Add(refresh);
            var remove = CompactButton("移除来源", async () => {
                if (!ConfirmRemove("移除该来源在文件坞里的导入记录？")) return;
                var candidate = group.Copy();
                candidate.Sources.RemoveAll(item => item.Kind == AppShortcutSourceKinds.VsCode &&
                    item.IsDefault == source.IsDefault && SamePath(item.Path, source.Path));
                await SaveGroup(candidate, "导入来源已移除。", ShowSources);
            });
            AutomationProperties.SetName(remove, $"移除{SourceName(source)} {source.Path}");
            actions.Children.Add(remove);
            _body.Children.Add(actions);
        }
        FocusInitial();
    }

    private async Task PickSource(bool isDefault)
    {
        if (_busy || _closed || _group == null) return;
        var dialog = new OpenFileDialog {
            Title = isDefault ? "选择从 VS Code 导出的默认快捷键 JSON" : "选择 VS Code 的 keybindings.json",
            Filter = "JSON 配置文件|*.json;*.jsonc",
            CheckFileExists = true
        };
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!isDefault && !string.IsNullOrEmpty(appData))
        {
            string name = _group.Path.Contains("insiders", StringComparison.OrdinalIgnoreCase) ? "Code - Insiders" : "Code";
            string folder = Path.Combine(appData, name, "User");
            if (Directory.Exists(folder)) dialog.InitialDirectory = folder;
            dialog.FileName = "keybindings.json";
        }
        if (ChooseFile(dialog) is { } path)
            await ReadSource(path, isDefault);
    }

    private async Task ReadSource(string path, bool isDefault)
    {
        var group = _group;
        if (group == null || _closed || _busy) return;
        bool saved = false;
        int rulesCount = 0;
        int lostMappings = 0, pausedMappings = 0;
        BeginWork("正在读取快捷键…");
        using var cancellation = new CancellationTokenSource();
        _importCancellation = cancellation;
        try
        {
            var rules = await VsCodeShortcuts.ReadAsync(path, cancellation.Token);
            if (_closed) return;
            rulesCount = rules.Count;
            var candidate = group.Copy();
            var replacement = new AppShortcutSource {
                Path = path,
                IsDefault = isDefault,
                Rules = rules.Select(rule => new ImportedAppShortcut {
                    Key = rule.Key, Command = rule.Command, Condition = rule.Condition,
                    HasArguments = rule.HasArguments, SystemWide = rule.SystemWide
                }).ToList()
            };
            int existing = candidate.Sources.FindIndex(item => item.Kind == AppShortcutSourceKinds.VsCode &&
                item.IsDefault == isDefault && SamePath(item.Path, path));
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
            if (!saved) SetStatus("保存失败。可重试导入。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { if (!_closed) SetStatus("读取快捷键失败：" + error.Message); }
        finally
        {
            _importCancellation = null;
            EndWork();
        }
        if (saved && !_closed)
        {
            ShowSources();
            SetStatus($"已导入 {rulesCount} 条规则。" +
                (pausedMappings == 0 ? "" : $"因键位冲突，已暂停 {pausedMappings} 个映射。") +
                (lostMappings == 0 ? "" : $"因来源规则变更，{lostMappings} 个原映射无法继承。"));
        }
    }

    private void ShowImportedRule(AppShortcutSource source, ImportedAppShortcut rule)
    {
        var group = _group;
        if (group == null) return;
        bool isTerminalSource = source.Kind == AppShortcutSourceKinds.WindowsTerminal;
        ShowDetail(RuleLabel(rule), ReturnFromDetail);
        _body.Children.Add(Ui.Text("键位"));
        _body.Children.Add(Ui.Text(rule.Key, 14));
        _body.Children.Add(Ui.Text("来源与范围"));
        string statusText = ShortcutRuleStatuses.Label(rule.Status);
        string scopeText = rule.Status == ShortcutRuleStatuses.Disabled ? "" :
            (rule.SystemWide ? "声明全局" : "应用内规则");
        _body.Children.Add(Ui.Text(string.Join(" · ", new[] { group.Name, SourceLabel(group, source), statusText, scopeText }
            .Where(part => part.Length > 0))));
        _body.Children.Add(Ui.Text(source.Path, 12));
        _body.Children.Add(Ui.Text(source == group.MenuSource ? "菜单项" : isTerminalSource ? "动作" : "命令"));
        _body.Children.Add(Ui.Text(rule.Command.Length == 0 ? "空命令" : rule.Command, 13));
        if (!string.IsNullOrEmpty(rule.Condition))
        {
            _body.Children.Add(Ui.Text(isTerminalSource ? "状态与来源说明" : "生效条件"));
            _body.Children.Add(Ui.Text(rule.Condition, 13));
            if (!isTerminalSource)
                _body.Children.Add(Ui.Text("映射只发送原键，具体动作由 VS Code 按当前条件决定。", 12));
        }
        if (rule.HasArguments)
            _body.Children.Add(Ui.Text(isTerminalSource
                ? "该规则声明了额外命令参数；具体命令与路径仅保留在原 settings.json 中。"
                : "该规则声明了命令参数。", 12));
        var mapping = new MappingEditor(rule.Mapping);
        string? unavailable = AppMappingRules.Eligibility(group, null, rule);
        mapping.SetAvailability(unavailable);
        _body.Children.Add(mapping.View);
        _hasDraft = () => mapping.HasChanges;
        if (unavailable == null || rule.Mapping != null)
        {
            _body.Children.Add(Ui.Button("保存映射", async () => {
                if (!mapping.TryRead(out var binding, out var invalid)) { ShowValidation(invalid); return; }
                var candidate = group.Copy();
                var target = source == group.MenuSource ? candidate.MenuSource! : candidate.Sources[group.Sources.IndexOf(source)];
                target.Rules[source.Rules.IndexOf(rule)].Mapping = binding;
                await SaveGroup(candidate, "映射已保存。", ShowCatalogAndFocus);
            }));
        }
        FocusInitial();
    }

    private void ShowDetail(string heading, Action returnToCatalog)
    {
        _catalogVisible = false;
        _hasDraft = null;
        _detailReturn = returnToCatalog;
        _body.Children.Clear();
        SetStatus("");
        var back = NavBackButton("返回软件快捷键", returnToCatalog);
        _body.Children.Add(back);
        var headingBlock = Ui.Text(heading, 16);
        headingBlock.FontWeight = FontWeights.SemiBold;
        _body.Children.Add(headingBlock);
        _initialFocus = back;
        _scroll.ScrollToTop();
    }

    private static Button NavBackButton(string title, Action handler)
    {
        var button = Ui.Button(title, handler);
        button.SetResourceReference(FrameworkElement.StyleProperty, "QuietButton");
        button.SetResourceReference(Control.ForegroundProperty, "Accent");
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Padding = new Thickness(6, 4, 8, 4);
        button.Margin = new Thickness(0, 0, 0, 4);
        return button;
    }

    private void ReturnFromDetail()
    {
        if (_hasDraft?.Invoke() != true || _host.ConfirmDiscardChanges()) ShowCatalogAndFocus();
    }

    private void ShowCatalogAndFocus()
    {
        ShowCatalog();
        FocusInitial();
    }

    private async Task SaveGroup(AppShortcutGroup candidate, string success, Action done)
    {
        if (_busy || _closed) return;
        if (AppMappingRules.Conflict(candidate, _getGlobals()) is { } conflict)
        {
            ShowValidation(conflict);
            return;
        }
        bool saved = false;
        BeginWork("正在保存…");
        try
        {
            saved = await _save(candidate);
            if (!_closed)
            {
                if (saved) SetSavedGroup(candidate);
                if (!saved) SetStatus("保存失败。请检查配置目录后重试。");
            }
        }
        catch (Exception error) { if (!_closed) SetStatus("保存失败：" + error.Message); }
        finally { EndWork(); }
        if (saved && !_closed)
        {
            done();
            SetStatus(success);
        }
    }

    private void SetSavedGroup(AppShortcutGroup candidate)
    {
        bool empty = candidate.Manual.Count == 0 && candidate.Sources.Count == 0 &&
            candidate.MenuSource == null && candidate.LastMenuRead == null;
        var persisted = _getGroups().FirstOrDefault(group =>
            string.Equals(group.IdentityKey, candidate.IdentityKey, StringComparison.OrdinalIgnoreCase))?.Copy();
        _group = persisted ?? (candidate.HasPackageIdentity && empty ? null : candidate.Copy());
        if (!_allSoftware)
        {
            string? path = candidate.HasPackageIdentity ? null : candidate.Path;
            string? id = candidate.HasPackageIdentity ? candidate.AppId : _selectedSoftware?.Id;
            _selectedSoftware = new SoftwareChoice(candidate.Name, path, id,
                InstalledName: _selectedSoftware?.InstalledName, Group: _group, PinnedName: _selectedSoftware?.PinnedName);
        }
    }

    private void BeginWork(string status)
    {
        _busy = true;
        _body.IsEnabled = false;
        SetStatus(status);
    }

    private void EndWork()
    {
        _busy = false;
        _body.IsEnabled = true;
    }

    private void SetStatus(string status)
    {
        _status.Text = status;
        _status.Visibility = status.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowValidation(string message)
    {
        SetStatus(message);
        _scroll.ScrollToTop();
    }

    private bool ConfirmRemove(string message)
    {
        if (_busy || _closed) return false;
        if (App.IsSelfTest) return true;
        var owner = Window.GetWindow(View);
        var choice = owner == null ?
            MessageBox.Show(message, "文件坞", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) :
            MessageBox.Show(owner, message, "文件坞", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        return choice == MessageBoxResult.Yes;
    }

    private static bool IsVsCode(AppShortcutGroup group)
    {
        if (group.HasPackageIdentity) return false;
        string label = group.Name;
        string fileName = Path.GetFileNameWithoutExtension(group.Path);
        return group.Sources.Any(source => source.Kind == AppShortcutSourceKinds.VsCode) ||
            fileName.Equals("Code", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("Code - Insiders", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("Visual Studio Code", StringComparison.OrdinalIgnoreCase) ||
            label.StartsWith("Visual Studio Code", StringComparison.OrdinalIgnoreCase) ||
            label.Equals("VS Code", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string left, string right) => FileService.NormalizeKey(left) == FileService.NormalizeKey(right);
    private static string ScopeLabel(ManualShortcutScope scope) => scope switch {
        ManualShortcutScope.Application => "应用内",
        ManualShortcutScope.Global => "全局",
        _ => "范围未标注"
    };
    private static string SourceName(AppShortcutSource source) => source.Kind == AppShortcutSourceKinds.WindowsTerminal
        ? "Windows 终端配置"
        : source.IsDefault ? "VS Code 默认导出" : "VS Code 用户配置";
    private static string RuleLabel(ImportedAppShortcut rule) =>
        rule.Command.Length == 0 ? "空动作规则" : rule.Command[0] == '-' ? $"移除 {rule.Command[1..]}" : rule.Command;

    private static TextBlock RowText(string text, double size)
    {
        var block = Ui.Text(text, size);
        block.TextWrapping = TextWrapping.NoWrap;
        block.TextTrimming = TextTrimming.CharacterEllipsis;
        block.Margin = new Thickness(0);
        return block;
    }

    private static TextBox Field(string name, int limit, string? value = null)
    {
        var box = new TextBox { Text = value ?? "", MaxLength = limit, MinHeight = 34, Padding = new Thickness(6, 3, 6, 3) };
        box.SetResourceReference(Control.BackgroundProperty, "ContentSurface");
        box.SetResourceReference(Control.ForegroundProperty, "Ink");
        box.SetResourceReference(Control.BorderBrushProperty, "Border");
        AutomationProperties.SetName(box, name);
        return box;
    }

    private static Button CompactButton(string title, Action handler)
    {
        var button = Ui.Button(title, handler);
        button.Margin = new Thickness(0,4,6,0);
        return button;
    }

    public void RequestClose()
    {
        if (_busy)
        {
            SetStatus("正在处理，请稍候。");
            return;
        }
        if (_hasDraft?.Invoke() != true || _host.ConfirmDiscardChanges())
            _host.CloseDialog(this);
    }

    public void OnClosed()
    {
        _closed = true;
        _importCancellation?.Cancel();
        _discoveryCancellation?.Cancel();
        _openWindowsCancellation?.Cancel();
        _installed = [];
        _choices = [];
        _liveWindowMenus.Clear();
        _packageMenus.Clear();
        _results.Children.Clear();
        _body.Children.Clear();
        _scroll.Content = null;
    }

    public void FocusInitial() => _initialFocus?.Focus();
    public void RestoreFocus() { }
    public void RefreshTheme() { }
    public void HandleWindowKey(KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _software.IsDropDownOpen) return;
        e.Handled = true;
        if (!_catalogVisible && _detailReturn != null)
        {
            if (_busy) { SetStatus("正在处理，请稍候。"); return; }
            _detailReturn();
            return;
        }
        RequestClose();
    }
}
