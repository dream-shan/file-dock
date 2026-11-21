using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace FileDockDemo;

internal static class AppMappingRulesVerification
{
    internal static void Run(Action<bool,string> check)
    {
        var group = JsonSerializer.Deserialize<AppShortcutGroup>(
            """{"Path":"C:\\Program Files\\Editor\\code.exe","Name":"Editor","Manual":[{"Action":"Save","Keys":"Ctrl+S","Scope":1}]}""")!;
        var manual = group.Manual.Single();
        var globals = new Dictionary<string,ShortcutBinding>();
        check(manual.Mapping == null && !AppMappingRules.Active(group).Any(), "Legacy shortcut records activate no mapping");
        manual.Mapping = new() { Enabled = true, Modifiers = 3, Key = 'M' };
        check(AppMappingRules.Active(group).Single() == (new AppChord(3,'M'),new AppChord(2,'S')) &&
            AppMappingRules.Conflict(group, globals) == null, "A foreground mapping retains both exact keyboard combinations");
        group.Copy().Manual[0].Mapping!.Key = 'X';
        check(manual.Mapping.Key == 'M', "Editing a draft alias leaves the saved mapping intact");
        foreach (string unsupported in new[] { "Ctrl+K Ctrl+S", "Ctrl+K, Ctrl+S", "Ctrl+Ctrl+S", "Ctrl+",
            "Win+L", "Ctrl+Alt+Delete", "Alt+Tab", "Alt+F4", "Ctrl+Shift+Esc" })
            check(!AppMappingRules.TryParse(unsupported, out _), "Reject unsafe or ambiguous original " + unsupported);
        check(AppMappingRules.TryParse(" ctrl + Shift + s ",out var parsed) && parsed == new AppChord(6,'S') &&
            AppMappingRules.TryParse("F12",out parsed) && parsed == new AppChord(0,0x7b), "Parse supported originals");
        foreach (var binding in new[] { manual.Mapping, new ShortcutBinding { Enabled=true, Modifiers=2, Key='S' } })
        {
            globals[ShortcutCatalog.Wake] = binding;
            check(AppMappingRules.Conflict(group,globals) != null, "Global actions take precedence at either end");
        }
        globals.Clear();
        var other = new ManualAppShortcut { Action="Another action", Keys="Ctrl+Alt+M", Scope=ManualShortcutScope.Application };
        group.Manual.Add(other);
        check(AppMappingRules.Conflict(group,globals) != null && !AppMappingRules.Active(group).Any(),
            "An alias cannot shadow a recorded original");
        other.Keys = "Ctrl+P";
        other.Mapping = manual.Mapping.Copy();
        check(AppMappingRules.Conflict(group,globals) != null && !AppMappingRules.Active(group).Any(),
            "Duplicated aliases are rejected regardless of record order");
        group.Manual.Remove(other);
        other.Keys="Ctrl+S Ctrl+P";
        other.Mapping=null;
        group.Manual.Add(other);
        check(!AppMappingRules.Active(group).Any(), "A recorded sequence prefix prevents dispatch to an ambiguous original");
        other.Keys="Ctrl+O";
        var proposed = new ManualAppShortcut { Keys="Ctrl+O", Scope=ManualShortcutScope.Application };
        check(AppMappingRules.Eligibility(group,proposed,null) != null, "A new draft cannot map a key already owned by another record");
        group.Manual.Remove(other);
        manual.Scope = ManualShortcutScope.Global;
        check(!AppMappingRules.Active(group).Any(), "Global records never dispatch through foreground mapping");
        manual.Scope = ManualShortcutScope.Application;
        group.Path = @"C:\Editor.lnk";
        check(!AppMappingRules.Active(group).Any(), "Shell links cannot identify an executable for dispatch");

        var old = new AppShortcutSource { Path=@"C:\keys.json", Rules=[
            new() { Command="editor.save", Key="ctrl+s", Condition="editing", Mapping=manual.Mapping.Copy() },
            new() { Command="editor.other", Key="ctrl+p", HasArguments=true, Mapping=manual.Mapping.Copy() }
        ] };
        var next = old.Copy();
        foreach (var row in next.Rules) row.Mapping=null;
        AppMappingRules.RestoreMappings(old,next);
        check(next.Rules[0].Mapping?.Key == 'M' && next.Rules[1].Mapping == null &&
            !ReferenceEquals(next.Rules[0].Mapping,old.Rules[0].Mapping), "Refresh restores only identical unambiguous argument-free declarations");
        next.Rules[0].Mapping=null;
        next.Rules[0].Command="editor.replace";
        AppMappingRules.RestoreMappings(old,next);
        check(next.Rules[0].Mapping == null, "Reused original key cannot transfer an alias to a different command");
        group.Path = @"C:\Editor.exe";
        group.Sources.Add(new() { Path=old.Path, Rules=[
            new() { Command="new.aliasOwner", Key="ctrl+alt+m" },
            new() { Command="unrelated.action", Key="ctrl+r", Mapping=new() { Enabled=true, Modifiers=3, Key='U' } }
        ] });
        check(AppMappingRules.PauseConflicts(group,globals) == 1 && !manual.Mapping.Enabled &&
            manual.Mapping.Key == 'M' && AppMappingRules.Active(group).Single().Alias.Key == 'U',
            "Refreshing a source pauses only shadowed bindings and keeps the newly imported key");
        var untouched = group.Sources[0].Rules[1].Mapping!;
        globals[ShortcutCatalog.Wake] = new() { Enabled=true, Modifiers=2, Key='R' };
        check(AppMappingRules.PauseConflicts(group,globals) == 1 && !untouched.Enabled,
            "Recovering a saved catalog also pauses an emitted key reserved globally");
        var scan = new ImportedAppShortcut { Command="existing.scan" };
        group.Sources[0].Rules = [scan];
        manual.Mapping.Enabled = true;
        manual.Mapping.Modifiers = 3;
        foreach (var (physicalKey,alias) in new[] { ("[F5]",116), ("[ArrowLeft] Ctrl+Q",37), ("[NumpadEnter]",13),
            ("[Numpad0]",45), ("[KeyQ]",(int)'A'), ("[Digit1]",(int)'7') })
        {
            scan.Key = "ctrl+alt+" + physicalKey;
            manual.Mapping.Key = alias;
            check(!AppMappingRules.Active(group).Any() && !AppMappingRules.TryParse(scan.Key,out _),
                "Reserve the physical key or prefix without enabling scan-code replay: "+physicalKey);
        }
        scan.Key="ctrl+[F5]";
        manual.Mapping.Key=116;
        check(AppMappingRules.Active(group).Any(), "Physical reservations are specific to their modifiers");
        manual.Keys="Ctrl+F5";
        check(!AppMappingRules.Active(group).Any(), "Physical declarations can also make an original key ambiguous");
    }
}
