using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FileDockDemo;

internal sealed record OfficialShortcutReference(string Title, string Url, string Scope,
    IReadOnlyList<ImportedAppShortcut> Rules);

internal static class AppMenuCatalog
{
    internal static string Refresh(AppShortcutGroup group, List<ImportedAppShortcut> rules,
        IReadOnlyDictionary<string,ShortcutBinding> globals)
    {
        var snapshot = new AppShortcutSource { Path=group.Path, Rules=rules.Select(rule=>rule.Copy()).ToList() };
        foreach (var row in snapshot.Rules) row.Mapping = null;
        var previous = group.MenuSource;
        if (previous != null) AppMappingRules.RestoreMappings(previous,snapshot);
        group.MenuSource = snapshot;
        group.LastMenuRead = DateTimeOffset.UtcNow;
        int paused = AppMappingRules.PauseConflicts(group,globals);
        int lost = (previous?.Rules.Count(row=>row.Mapping != null) ?? 0) - snapshot.Rules.Count(row=>row.Mapping != null);
        string status = rules.Count == 0 ? "此次未读到窗口公开的菜单快捷键，当前菜单快照为 0 条。" :
            $"已读取窗口菜单公开的 {rules.Count} 条快捷键。";
        if (lost > 0) status += $"因菜单条目变更，{lost} 个原映射无法继承。";
        if (paused > 0) status += $"因键位冲突，已暂停 {paused} 个映射。";
        return status;
    }

    internal static OfficialShortcutReference? OfficialReference(string applicationPath)
    {
        // Reference-only excerpts. They are never inserted into a user's observed configuration.
        const string scope = "适用于 Windows 桌面版。以下为官方文档列举的部分默认键位；已安装版本、扩展及个人设置可能有所不同。";
        static ImportedAppShortcut Row(string key, string action) => new() { Key=key, Command=action };
        return Path.GetFileName(applicationPath).ToLowerInvariant() switch {
            "code.exe" => new("Visual Studio Code · 微软官方快捷键表",
                "https://code.visualstudio.com/shortcuts/keyboard-shortcuts-windows.pdf", scope,
                [Row("Ctrl+Shift+P","显示命令面板"), Row("Ctrl+P","按名称打开文件"), Row("Ctrl+K Ctrl+S","打开键盘快捷方式")]),
            "chrome.exe" => new("Google Chrome · Google 官方帮助",
                "https://support.google.com/chrome/answer/157179?hl=zh-Hans", scope,
                [Row("Ctrl+T","打开新标签页"), Row("Ctrl+Shift+T","恢复关闭的标签页"), Row("Ctrl+Shift+N","打开无痕窗口")]),
            "firefox.exe" => new("Firefox · Mozilla 官方帮助",
                "https://support.mozilla.org/en-US/kb/keyboard-shortcuts-perform-firefox-tasks-quickly", scope,
                [Row("Ctrl+T","打开新标签页"), Row("Ctrl+N","打开新窗口"), Row("Ctrl+Shift+P","打开隐私窗口")]),
            _ => null
        };
    }
}
