using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace FileDockDemo;

internal static class AppMenuCatalogVerification
{
    internal static void Run(Action<bool,string> check)
    {
        var group = new AppShortcutGroup { Path = @"C:\Apps\Code.exe", Name = "Editor" };
        var globals = new Dictionary<string,ShortcutBinding>();
        var manual = new ManualAppShortcut { Keys="Ctrl+F", Action="查找" };
        group.Manual.Add(manual);
        var config = new ImportedAppShortcut { Key="ctrl+s", Command="Save",
            Mapping=new() { Enabled=true, Modifiers=3, Key='M' } };
        group.Sources.Add(new() { Path=@"C:\Apps\settings.json", Rules=[config] });
        var save = new ImportedAppShortcut { Key="Ctrl+S", Command="Save" };
        var print = new ImportedAppShortcut { Key="Ctrl+P", Command="Print" };
        AppMenuCatalog.Refresh(group,[save,print],globals);
        check(group.Valid() && group.LastMenuRead != null && group.AllSources().Count()==2 &&
            config.Mapping.Enabled && AppMappingRules.Active(group).Single().Original.Key=='S',
            "An identical menu observation preserves an existing configuration mapping");
        group.MenuSource!.Rules[1].Mapping = new() { Enabled=true, Modifiers=3, Key='N' };
        AppMenuCatalog.Refresh(group,[print,save],globals);
        var loaded = JsonSerializer.Deserialize<AppShortcutGroup>(JsonSerializer.Serialize(group))!;
        check(loaded.Valid() && loaded.Copy().MenuSource!.Rules[0].Mapping?.Key=='N' &&
            AppMappingRules.Active(loaded).Count()==2 && print.Mapping==null,
            "A unique unchanged menu binding survives refresh, draft copying and persistence");
        AppMenuCatalog.Refresh(group,[print,new() { Key="Ctrl+P", Command="Preview" },save],globals);
        check(group.MenuSource.Rules[0].Mapping is { Enabled:false } && config.Mapping.Enabled,
            "A newly ambiguous menu key pauses its mapping without disturbing unrelated keys");
        AppMenuCatalog.Refresh(group,[],globals);
        check(group.LastMenuRead!=null && group.MenuSource?.Rules.Count==0 && group.Manual.Single()==manual &&
            group.Sources.Single().Rules.Single()==config, "An empty successful read replaces only the menu snapshot");
        foreach (string app in new[] { "Code.exe","firefox.exe","chrome.exe" })
            check(AppMenuCatalog.OfficialReference(app) is { Rules.Count: > 0 } reference &&
                reference.Url.StartsWith("https://",StringComparison.Ordinal) && reference.Scope.Length>0,
                "On-demand reference identifies its official source and scope for "+app);
        check(AppMenuCatalog.OfficialReference("custom.exe")==null && AppMappingRules.Active(group).Count()==1,
            "Looking up reference defaults does not create dispatchable bindings");
    }
}
