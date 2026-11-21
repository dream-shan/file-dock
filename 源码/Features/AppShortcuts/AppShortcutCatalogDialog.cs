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

internal sealed class AppShortcutCatalogDialog : IModuleDialog
{
    private const int PageSize = 40;
    private readonly IModuleHost _host;
    private readonly Func<IReadOnlyList<AppShortcutGroup>> _getGroups;
    private readonly Func<IReadOnlyList<ApplicationEntry>> _getFixed;
    private readonly Func<AppShortcutGroup,Task<bool>> _save;
    private readonly Action<Action<string,string>,Action> _pickWindow;
    private readonly Action _back;
    private readonly StackPanel _body = new();
    private readonly StackPanel _results = new();
    private readonly TextBlock _status = Ui.Text("");
    private readonly ComboBox _software = new() { MinHeight = 34, MaxDropDownHeight = 220 };
    private readonly TextBox _search = Field("搜索软件快捷键", 256);
    private readonly ScrollViewer _scroll;
    private AppShortcutGroup? _group;
    private Func<bool>? _hasDraft;
    private Control? _initialFocus;
    private CancellationTokenSource? _importCancellation;
    private int _page;
    private bool _changingSoftware, _busy, _closed;

    private sealed record SoftwareChoice(string Name, string Path);
    private readonly record struct CatalogRow(ManualAppShortcut? Manual, AppShortcutSource? Source, ImportedAppShortcut? Rule);

    public FrameworkElement View => _scroll;
    public bool HasChanges => _busy || _hasDraft?.Invoke() == true;

    internal AppShortcutCatalogDialog(IModuleHost host, Func<IReadOnlyList<AppShortcutGroup>> getGroups,
        Func<IReadOnlyList<ApplicationEntry>> getFixed, Func<AppShortcutGroup,Task<bool>> save,
        Action<Action<string,string>,Action> pickWindow, Action back)
    {
        _host = host;
        _getGroups = getGroups;
        _getFixed = getFixed;
        _save = save;
        _pickWindow = pickWindow;
        _back = back;

        AutomationProperties.SetName(_software, "软件");
        _software.SelectionChanged += (_,_) => {
            if (!_changingSoftware && _software.SelectedItem is ComboBoxItem { Tag: SoftwareChoice choice })
                SelectSoftware(choice.Name, choice.Path);
        };
        _search.TextChanged += (_,_) => {
            _page = 0;
            RenderResults();
        };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        SetStatus("");

        var root = new StackPanel();
        root.Children.Add(Ui.Text("软件内部快捷键", 18));
        root.Children.Add(_status);
        root.Children.Add(_body);
        root.Children.Add(Ui.Button("关闭", RequestClose));
        _scroll = Ui.Scroll(root);
        ShowCatalog();
    }

    private void ShowCatalog()
    {
        _hasDraft = null;
        _body.Children.Clear();
        _body.Children.Add(Ui.Button("返回快捷键管理", () => { if (!_busy) _back(); }));
        RefreshSoftwareChoices();
        _body.Children.Add(_software);
        _initialFocus = _software.Items.Count > 0 ? _software : null;

        var softwareActions = new WrapPanel();
        var browse = CompactButton("选择程序", BrowseSoftware);
        _initialFocus ??= browse;
        softwareActions.Children.Add(browse);
        softwareActions.Children.Add(CompactButton("从窗口选择", BeginWindowPick));
        _body.Children.Add(softwareActions);
        if (_group == null)
        {
            _body.Children.Add(Ui.Text("选择一个软件，查看或登记它的原快捷键。"));
            _scroll.ScrollToTop();
            return;
        }

        var actions = new WrapPanel();
        actions.Children.Add(CompactButton("添加手动登记", () => EditManual(null)));
        if (IsVsCode(_group))
            actions.Children.Add(CompactButton("VS Code 导入来源", ShowSources));
        _body.Children.Add(actions);
        _body.Children.Add(Ui.Text("按动作、键位或备注查找", 12));
        _body.Children.Add(_search);
        _body.Children.Add(_results);
        RenderResults();
        _scroll.ScrollToTop();
    }

    private void RefreshSoftwareChoices()
    {
        var choices = new Dictionary<string,SoftwareChoice>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in _getGroups())
            choices[group.Path] = new SoftwareChoice(group.Name, group.Path);
        foreach (var entry in _getFixed())
            choices.TryAdd(entry.Path, new SoftwareChoice(entry.DisplayName, entry.Path));
        if (_group != null)
            choices[_group.Path] = new SoftwareChoice(_group.Name, _group.Path);

        _changingSoftware = true;
        try
        {
            _software.Items.Clear();
            foreach (var choice in choices.Values.OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new ComboBoxItem {
                    Content = choice.Name + (Path.GetExtension(choice.Path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                        ? " · 快捷方式" : " · 程序"),
                    Tag = choice,
                    ToolTip = choice.Path
                };
                _software.Items.Add(item);
                if (_group != null && SamePath(_group.Path, choice.Path))
                    _software.SelectedItem = item;
            }
        }
        finally { _changingSoftware = false; }
    }

    private void SelectSoftware(string name, string path)
    {
        if (_closed) return;
        _group = _getGroups().FirstOrDefault(group => SamePath(group.Path, path))?.Copy() ??
            new AppShortcutGroup { Name = name, Path = path };
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
        var owner = Window.GetWindow(View);
        return (owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true ? dialog.FileName : null;
    }

    private void RenderResults()
    {
        _results.Children.Clear();
        if (_group == null) return;
        var group = _group;
        var query = _search.Text.Trim();
        bool Matches(string value) => query.Length == 0 || value.Contains(query, StringComparison.OrdinalIgnoreCase);
        var rows = new List<CatalogRow>();
        foreach (var entry in group.Manual)
        {
            if (!Matches(entry.Action) && !Matches(entry.Keys) && !Matches(entry.Note) &&
                !Matches("手动登记") && !Matches(ScopeLabel(entry.Scope))) continue;
            rows.Add(new(entry, null, null));
        }
        foreach (var source in group.Sources)
        {
            string sourceName = SourceName(source);
            foreach (var rule in source.Rules)
            {
                if (!Matches(rule.Command) && !Matches(rule.Key) && !Matches(rule.Condition) &&
                    !Matches(sourceName) && !Matches(source.Path)) continue;
                rows.Add(new(null, source, rule));
            }
        }
        if (rows.Count == 0)
        {
            _results.Children.Add(Ui.Text(query.Length == 0 ? "暂无快捷键记录。" : "没有匹配的记录。"));
            return;
        }

        int pages = (rows.Count + PageSize - 1) / PageSize;
        _page = Math.Clamp(_page, 0, pages - 1);
        if (pages > 1)
        {
            var paging = new WrapPanel();
            var previous = CompactButton("上一页", () => { _page--; RenderResults(); });
            var next = CompactButton("下一页", () => { _page++; RenderResults(); });
            previous.IsEnabled = _page > 0;
            next.IsEnabled = _page + 1 < pages;
            paging.Children.Add(previous);
            paging.Children.Add(next);
            paging.Children.Add(Ui.Text($"第 {_page + 1} / {pages} 页 · {rows.Count} 条", 12));
            _results.Children.Add(paging);
        }
        foreach (var row in rows.Skip(_page * PageSize).Take(PageSize))
        {
            if (row.Manual is { } manual)
                AddResult(manual.Action, manual.Keys, $"手动登记 · {ScopeLabel(manual.Scope)}", manual.Note,
                    "编辑登记", () => EditManual(manual));
            else if (row.Source is { } source && row.Rule is { } rule)
            {
                string scope = rule.SystemWide ? "声明全局" : "应用内规则";
                AddResult(RuleLabel(rule), rule.Key, $"{SourceName(source)} · {scope}", rule.Condition,
                    "查看规则", () => ShowImportedRule(source, rule));
            }
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
        _hasDraft = () => action.Text != (current?.Action ?? "") || keys.Text != (current?.Keys ?? "") ||
            note.Text != (current?.Note ?? "") || SelectedScope() != (current?.Scope ?? ManualShortcutScope.Unknown);
        ShowDetail(current == null ? "添加手动登记" : "编辑手动登记", () => {
            if (_hasDraft?.Invoke() != true || _host.ConfirmDiscardChanges())
                ShowCatalogAndFocus();
        });
        _body.Children.Add(Ui.Text("动作名称"));
        _body.Children.Add(action);
        _body.Children.Add(Ui.Text("原快捷键，可按文字填写连按键位"));
        _body.Children.Add(keys);
        _body.Children.Add(Ui.Text("生效范围（手动标记）"));
        _body.Children.Add(scope);
        _body.Children.Add(Ui.Text("备注，可填写在哪个页面或工具中生效"));
        _body.Children.Add(note);
        _body.Children.Add(Ui.Button("保存登记", async () => {
            if (string.IsNullOrWhiteSpace(action.Text) || string.IsNullOrWhiteSpace(keys.Text))
            {
                SetStatus("请填写动作名称和原快捷键。");
                return;
            }
            var candidate = group.Copy();
            var entry = new ManualAppShortcut {
                Action = action.Text.Trim(), Keys = keys.Text.Trim(), Note = note.Text.Trim(), Scope = SelectedScope()
            };
            if (current == null) candidate.Manual.Add(entry);
            else
            {
                entry.Id = current.Id;
                candidate.Manual[candidate.Manual.FindIndex(item => item.Id == current.Id)] = entry;
            }
            await SaveGroup(candidate, "登记已保存。", ShowCatalogAndFocus);
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
        _body.Children.Add(Ui.Text("手动选择配置文件，导入这份文件中声明的键位。实际触发仍取决于 VS Code 的活动配置、扩展和当前编辑位置。"));
        _body.Children.Add(Ui.Button("导入 VS Code 用户配置", async () => await PickSource(false)));
        _body.Children.Add(Ui.Text("从 VS Code 命令面板打开默认快捷键 JSON，复制内容到本地 JSON 文件后导入。", 12));
        _body.Children.Add(Ui.Button("导入 VS Code 默认快捷键", async () => await PickSource(true)));
        foreach (var source in group.Sources)
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
                candidate.Sources.RemoveAll(item => item.IsDefault == source.IsDefault && SamePath(item.Path, source.Path));
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
            int existing = candidate.Sources.FindIndex(item => item.IsDefault == isDefault && SamePath(item.Path, path));
            if (existing < 0) candidate.Sources.Add(replacement);
            else candidate.Sources[existing] = replacement;
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
            SetStatus($"已导入 {rulesCount} 条规则。");
        }
    }

    private void ShowImportedRule(AppShortcutSource source, ImportedAppShortcut rule)
    {
        ShowDetail(RuleLabel(rule), ShowCatalogAndFocus);
        _body.Children.Add(Ui.Text("键位"));
        _body.Children.Add(Ui.Text(rule.Key, 14));
        _body.Children.Add(Ui.Text("来源与范围"));
        _body.Children.Add(Ui.Text(SourceName(source) + (rule.SystemWide ? " · 声明全局" : " · 应用内规则")));
        _body.Children.Add(Ui.Text(source.Path, 12));
        _body.Children.Add(Ui.Text("命令"));
        _body.Children.Add(Ui.Text(rule.Command.Length == 0 ? "空命令" : rule.Command, 13));
        if (!string.IsNullOrEmpty(rule.Condition))
        {
            _body.Children.Add(Ui.Text("生效条件"));
            _body.Children.Add(Ui.Text(rule.Condition, 13));
        }
        if (rule.HasArguments)
            _body.Children.Add(Ui.Text("该规则声明了命令参数。", 12));
        FocusInitial();
    }

    private void ShowDetail(string heading, Action returnToCatalog)
    {
        _body.Children.Clear();
        SetStatus("");
        var back = Ui.Button("返回软件快捷键", returnToCatalog);
        _body.Children.Add(back);
        _body.Children.Add(Ui.Text(heading, 16));
        _initialFocus = back;
        _scroll.ScrollToTop();
    }

    private void ShowCatalogAndFocus()
    {
        ShowCatalog();
        FocusInitial();
    }

    private async Task SaveGroup(AppShortcutGroup candidate, string success, Action done)
    {
        if (_busy || _closed) return;
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

    private void SetSavedGroup(AppShortcutGroup candidate) =>
        _group = _getGroups().FirstOrDefault(group => SamePath(group.Path, candidate.Path))?.Copy() ?? candidate.Copy();

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
        string label = group.Name;
        string fileName = Path.GetFileNameWithoutExtension(group.Path);
        return group.Sources.Count > 0 || fileName.Equals("Code", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("Code - Insiders", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("Visual Studio Code", StringComparison.OrdinalIgnoreCase) ||
            label.StartsWith("Visual Studio Code", StringComparison.OrdinalIgnoreCase) ||
            label.Equals("VS Code", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static string ScopeLabel(ManualShortcutScope scope) => scope switch {
        ManualShortcutScope.Application => "应用内",
        ManualShortcutScope.Global => "全局",
        _ => "范围未标注"
    };
    private static string SourceName(AppShortcutSource source) => source.IsDefault ? "VS Code 默认导出" : "VS Code 用户配置";
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
    }

    public void FocusInitial() => _initialFocus?.Focus();
    public void RestoreFocus() { }
    public void RefreshTheme() { }
    public void HandleWindowKey(KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _software.IsDropDownOpen) return;
        e.Handled = true;
        RequestClose();
    }
}
