using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileDockDemo;

internal sealed partial class AppShortcutCatalogDialog
{
    private IEnumerable<AppShortcutGroup> VisibleGroups()
    {
        if (_group != null) return [_group];
        var groups = _getGroups().ToDictionary(group => group.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var app in _getFixed())
            groups.TryAdd(app.Path, new AppShortcutGroup { Name = app.DisplayName, Path = app.Path });
        return groups.Values.OrderBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    private static string SourceLabel(AppShortcutGroup group, AppShortcutSource source) =>
        group.MenuSource == source ? "窗口菜单实读" : SourceName(source);

    private IEnumerable<CatalogRow> CatalogRows(string query)
    {
        static string NormalizeKeys(string value) => value.Replace(" +", "+").Replace("+ ", "+");
        string[] terms = NormalizeKeys(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var group in VisibleGroups())
        {
            var remaining = terms.Where(term => !group.Name.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                !group.Path.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
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
            foreach (var entry in group.Manual)
                if (Matches(entry.Action, entry.Keys, "手动登记 " + ScopeLabel(entry.Scope), entry.Note, entry.Mapping))
                    yield return new(group, entry, null, null, null);
            foreach (var source in group.AllSources())
            {
                string label = SourceLabel(group, source) + " " + source.Path;
                foreach (var rule in source.Rules)
                    if (Matches(RuleLabel(rule), rule.Key, label, rule.Condition, rule.Mapping))
                        yield return new(group, null, source, rule, null);
            }
            if (AppMenuCatalog.OfficialReference(group.Path) is { } reference)
                foreach (var rule in reference.Rules)
                    if (Matches(rule.Command, rule.Key, "官方默认参考", reference.Title, null))
                        yield return new(group, null, null, rule, reference);
        }
    }

    private void ShowReference(AppShortcutGroup group, OfficialShortcutReference reference, ImportedAppShortcut example)
    {
        ShowDetail(example.Command, ShowCatalogAndFocus);
        _body.Children.Add(Ui.Text($"{group.Name} · {example.Key}", 14));
        _body.Children.Add(Ui.Text("官方默认参考 · " + reference.Title));
        _body.Children.Add(Ui.Text(reference.Scope, 12));
        _body.Children.Add(Ui.Text("如已在本机确认此键位，可在该软件下手动登记并设置前台映射。", 12));
        _body.Children.Add(Ui.Button("打开官方说明", () => {
            try { using var browser = Process.Start(new ProcessStartInfo(reference.Url) { UseShellExecute = true }); }
            catch (Exception error) { SetStatus("打开官方说明失败：" + error.Message); }
        }));
        FocusInitial();
    }

    private void BeginMenuPick()
    {
        if (_closed || _busy) return;
        _pickMenuWindow?.Invoke(async (name, path, hwnd) => await ReadMenu(name, path, hwnd),
            () => { if (!_closed) { SetStatus("已取消读取窗口菜单。"); FocusInitial(); } });
    }

    internal void ReportUnreadableWindow()
    {
        if (_closed) return;
        SetStatus("无法识别这个窗口的软件路径。原记录已保留，请重新选择窗口。");
        FocusInitial();
    }

    private async Task ReadMenu(string name, string path, IntPtr hwnd)
    {
        if (_closed || _busy) return;
        BeginWork("正在读取所选窗口公开的菜单键位…");
        using var cancellation = new CancellationTokenSource();
        _importCancellation = cancellation;
        try
        {
            var rules = await MenuShortcutReader.ReadAsync(hwnd, path, cancellation.Token);
            if (_closed) return;
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
