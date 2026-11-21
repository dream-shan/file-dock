using System;
using System.IO;
using System.Threading.Tasks;

namespace FileDockDemo;

internal static class AppMappingIntegrationVerification
{
    internal static async Task Run(MainWindow window, string directory, Action<bool,string> check)
    {
        var alias = new ShortcutBinding { Enabled = true, Modifiers = 6, Key = 0x71 };
        var original = new ShortcutBinding { Enabled = true, Modifiers = 2, Key = 0x53 };
        var group = new AppShortcutGroup {
            Name = "映射验证", Path = Path.Combine(directory, "mapping-test.exe"),
            Manual = [new() { Action = "保存", Keys = "Ctrl+S", Scope = ManualShortcutScope.Application, Mapping = alias.Copy() }]
        };
        var savedGlobal = window.State.Shortcuts![ShortcutCatalog.Screenshot].Copy();
        try
        {
            check(await window.SaveAppShortcutGroupAsync(group), "An app mapping is saved through the catalog");
            check(!(await window.ChangeShortcutAsync(ShortcutCatalog.Screenshot, alias)).Success,
                "A later global registration cannot steal a configured application alias");
            check(!(await window.ChangeShortcutAsync(ShortcutCatalog.Screenshot, original)).Success,
                "A later global registration cannot intercept the original key emitted by a mapping");
            group.Manual[0].Mapping!.Enabled = false;
            check(await window.SaveAppShortcutGroupAsync(group) &&
                (await window.ChangeShortcutAsync(ShortcutCatalog.Screenshot, alias)).Success,
                "Pausing an application mapping releases its keys for global registration");
            group.Manual[0].Mapping!.Enabled = true;
            check(!await window.SaveAppShortcutGroupAsync(group) &&
                window.Storage.Load().AppShortcutGroups.Find(g => g.Path == group.Path)!.Manual[0].Mapping?.Enabled == false,
                "Re-enabling a conflicting mapping leaves the saved and running rule paused");
        }
        finally
        {
            group.Manual.Clear();
            await window.SaveAppShortcutGroupAsync(group);
            await window.ChangeShortcutAsync(ShortcutCatalog.Screenshot, savedGlobal);
        }
    }
}
