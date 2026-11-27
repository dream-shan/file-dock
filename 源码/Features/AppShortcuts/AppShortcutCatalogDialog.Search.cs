using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileDockDemo;

internal sealed partial class AppShortcutCatalogDialog
{
    private static string SourceLabel(AppShortcutGroup group, AppShortcutSource source) =>
        group.MenuSource == source ? "窗口菜单实读" : SourceName(source);

    private IEnumerable<CatalogRow> CatalogRows(string query)
    {
        static string NormalizeKeys(string value) => value.Replace(" +", "+").Replace("+ ", "+");
        string[] terms = NormalizeKeys(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<SoftwareChoice> choices = _allSoftware ? _choices :
            _selectedSoftware != null ? [_selectedSoftware] : [];
        foreach (var choice in choices)
        {
            var remaining = terms.Where(term => !choice.Name.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                choice.InstalledName?.Contains(term, StringComparison.OrdinalIgnoreCase) != true &&
                choice.PinnedName?.Contains(term, StringComparison.OrdinalIgnoreCase) != true &&
                choice.Path?.Contains(term, StringComparison.OrdinalIgnoreCase) != true &&
                choice.Id?.Contains(term, StringComparison.OrdinalIgnoreCase) != true).ToArray();
            if (!_onlyLiveWindowShortcuts && _allSoftware && terms.Length != 0 && remaining.Length == 0)
                yield return new(choice, null, null, null, null);
            bool Matches(string action, string keys, string source, string condition, ShortcutBinding? mapping)
            {
                if (remaining.Length == 0) return true;
                var chords = NormalizeKeys(keys + MappingSuffix(mapping));
                foreach (var term in remaining)
                    if (!action.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                        !chords.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                        !source.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                        !condition.Contains(term, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }
            if (!_onlyLiveWindowShortcuts && choice.Group is { } group)
            {
                foreach (var entry in group.Manual)
                    if (Matches(entry.Action, entry.Keys, "手动登记 " + ScopeLabel(entry.Scope), entry.Note, entry.Mapping))
                        yield return new(choice, entry, null, null, null);
                foreach (var source in group.AllSources())
                {
                    string label = SourceLabel(group, source) + " " + source.Path;
                    foreach (var rule in source.Rules)
                    {
                        string sourceMeta = string.IsNullOrEmpty(rule.Status) ? label :
                            label + " " + ShortcutRuleStatuses.Label(rule.Status);
                        if (Matches(RuleLabel(rule), rule.Key, sourceMeta, rule.Condition, rule.Mapping))
                            yield return new(choice, null, source, rule, null);
                    }
                }
            }
            foreach (var rule in LiveRulesFor(choice, deduplicateWithSaved: !_onlyLiveWindowShortcuts))
                if (Matches(rule.Command, rule.Key, "本次窗口实读", rule.Condition, null))
                    yield return new(choice, null, null, rule, null);
            if (!_onlyLiveWindowShortcuts && AppMenuCatalog.OfficialReference(choice.Path ?? "", choice.Id) is { } reference)
                foreach (var rule in reference.Rules)
                    if (Matches(rule.Command, rule.Key, "官方默认参考", reference.Title, null))
                        yield return new(choice, null, null, rule, reference);
        }
    }

    private void ShowReference(SoftwareChoice choice, OfficialShortcutReference reference, ImportedAppShortcut example)
    {
        ShowDetail(example.Command, ShowCatalogAndFocus);
        _body.Children.Add(Ui.Text($"{choice.Name} · {example.Key}", 14));
        _body.Children.Add(Ui.Text("官方默认参考 · " + reference.Title));
        _body.Children.Add(Ui.Text(reference.Scope, 12));
        if (choice.Path != null)
            _body.Children.Add(Ui.Text("如已在本机确认此键位，可在该软件下手动登记并设置前台映射。", 12));
        _body.Children.Add(Ui.Button("打开官方说明", () => {
            try { using var browser = Process.Start(new ProcessStartInfo(reference.Url) { UseShellExecute = true }); }
            catch (Exception error) { SetStatus("打开官方说明失败：" + error.Message); }
        }));
        FocusInitial();
    }

    private void ShowPackageMenuRule(SoftwareChoice choice, ImportedAppShortcut rule)
    {
        ShowDetail(rule.Command, ShowCatalogAndFocus);
        _body.Children.Add(Ui.Text($"{choice.Name} · {rule.Key}", 14));
        _body.Children.Add(Ui.Text("本次窗口实读 · 仅本次查看，关闭本页后清除。", 12));
        if (choice.Path != null)
            _body.Children.Add(Ui.Text("如需长期保存或配置前台映射，可使用「读取窗口菜单」手动单窗读取并保存。", 12));
        if (rule.Condition.Length > 0) _body.Children.Add(Ui.Text(rule.Condition));
        FocusInitial();
    }

    private void BeginMenuPick()
    {
        if (_closed || _busy) return;
        var target = _allSoftware ? null : _selectedSoftware;
        _pickMenuWindow?.Invoke(async (name, path, hwnd) => {
            string? packageId = _getWindowPackageId(hwnd);
            if (target != null && (target.PackageOnly ? !string.Equals(packageId, target.Id, StringComparison.OrdinalIgnoreCase) :
                target.Path == null || !SamePath(target.Path, path)))
            {
                SetStatus($"所选窗口与「{target.Name}」不匹配。请重新选择该软件的窗口。");
                FocusInitial();
                return;
            }
            await ReadMenu(target?.Name ?? name, path, hwnd, packageId);
        },
            () => { if (!_closed) { SetStatus("已取消读取窗口菜单。"); FocusInitial(); } });
    }

    internal void ReportUnreadableWindow()
    {
        if (_closed) return;
        SetStatus("无法识别这个窗口的软件路径。原记录已保留，请重新选择窗口。");
        FocusInitial();
    }

    private async Task ReadMenu(string name, string path, IntPtr hwnd, string? packageId)
    {
        if (_closed || _busy) return;
        BeginWork("正在读取所选窗口公开的菜单键位…");
        using var cancellation = new CancellationTokenSource();
        _importCancellation = cancellation;
        try
        {
            var rules = await _readMenu(hwnd, path, cancellation.Token);
            if (_closed) return;
            if (!string.Equals(_getWindowPackageId(hwnd), packageId, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("窗口已更换或关闭，原有菜单记录已保留。");
                return;
            }
            if (packageId != null)
            {
                var installed = _installed.FirstOrDefault(app => string.Equals(app.Id, packageId, StringComparison.OrdinalIgnoreCase));
                string label = installed?.Name ?? name;
                _packageMenus[packageId] = (label, rules);
                SelectChoice(new SoftwareChoice(label, null, packageId));
                SetStatus($"本次读到 {rules.Count} 条菜单快捷键。关闭本页后清除。");
                return;
            }
            var group = _getGroups().FirstOrDefault(item => SamePath(item.Path, path))?.Copy() ??
                new AppShortcutGroup { Name = name, Path = path };
            string status = AppMenuCatalog.Refresh(group, rules, _getGlobals());
            bool saved = await _save(group);
            if (_closed) return;
            if (saved)
            {
                _allSoftware = false;
                SetSavedGroup(group);
                _page = 0;
                _search.Clear();
                ShowCatalog();
                SetStatus(status);
            }
            else SetStatus("菜单读取结果保存失败。原记录已保留。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { if (!_closed) SetStatus("读取窗口菜单失败，原记录已保留。" + error.Message); }
        finally
        {
            _importCancellation = null;
            EndWork();
            if (!_closed) FocusInitial();
        }
    }
}
