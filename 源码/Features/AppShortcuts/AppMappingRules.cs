using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FileDockDemo;

internal readonly record struct AppChord(uint Modifiers, int Key);

internal static class AppMappingRules
{
    // Spaces around "+" are formatting. Spaces between two keys start another stroke.
    private static readonly Regex PlusSpacing = new(@"\s*\+\s*", RegexOptions.CultureInvariant);
    private static readonly char[] StrokeSeparators = [' ', '\t', '\r', '\n', ','];
    private static readonly Dictionary<string,int> NamedKeys = new(StringComparer.OrdinalIgnoreCase) {
        ["Backspace"]=8, ["Tab"]=9, ["Enter"]=13, ["Return"]=13, ["Esc"]=27, ["Escape"]=27,
        ["Space"]=32, ["PageUp"]=33, ["PgUp"]=33, ["PageDown"]=34, ["PgDn"]=34,
        ["End"]=35, ["Home"]=36, ["Left"]=37, ["Up"]=38, ["Right"]=39, ["Down"]=40,
        ["Insert"]=45, ["Ins"]=45, ["Delete"]=46, ["Del"]=46
    };

    internal static bool TryParse(string text, out AppChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 256) return false;
        return ParseStroke(PlusSpacing.Replace(text.Trim(), "+"), out chord);
    }

    private static bool ParseStroke(string text, out AppChord chord)
    {
        chord = default;
        string[] tokens = text.Split('+');
        if (!TryModifiers(tokens.AsSpan(0,tokens.Length-1),out uint modifiers)) return false;
        return ParseKey(tokens[^1],modifiers,out chord);
    }

    private static bool TryModifiers(ReadOnlySpan<string> tokens, out uint modifiers)
    {
        modifiers = 0;
        foreach (var token in tokens)
        {
            uint bit = token.ToLowerInvariant() switch { "ctrl" or "control" => 2, "alt" => 1, "shift" => 4, _ => 0 };
            if (bit == 0 || (modifiers & bit) != 0) return false;
            modifiers |= bit;
        }
        return true;
    }

    private static bool ParseKey(string name, uint modifiers, out AppChord chord)
    {
        chord = default;
        string key = name.ToUpperInvariant();
        int vk;
        if (key.Length == 1 && key[0] is (>= 'A' and <= 'Z') or (>= '0' and <= '9')) vk = key[0];
        else if (key.StartsWith('F') && int.TryParse(key.AsSpan(1),NumberStyles.None,CultureInfo.InvariantCulture,out int fn)
            && fn is >= 1 and <= 24 && key == "F"+fn.ToString(CultureInfo.InvariantCulture)) vk = 111+fn;
        else if (!NamedKeys.TryGetValue(key,out vk)) return false;
        var candidate = new AppChord(modifiers,vk);
        if (!Safe(candidate)) return false;
        chord = candidate;
        return true;
    }

    private static bool Safe(AppChord chord)
    {
        var (mod,key) = chord;
        if ((mod & ~7u) != 0) return false;
        if (key is not (>= 'A' and <= 'Z' or >= '0' and <= '9' or >= 112 and <= 135) &&
            !NamedKeys.ContainsValue(key)) return false;
        if ((mod & 1) != 0 && key is (9 or 27 or 32 or 115)) return false;
        if ((mod & 2) != 0 && key == 27) return false;
        return !((mod & 3) == 3 && key == 46);
    }

    private sealed record Entry(string Keys, ManualAppShortcut? Manual, ImportedAppShortcut? Rule, bool ObservedInConfig = false)
    {
        internal ShortcutBinding? Binding => Manual?.Mapping ?? Rule?.Mapping;
    }

    private static IEnumerable<Entry> Entries(AppShortcutGroup group)
    {
        foreach (var item in group.Manual) yield return new(item.Keys,item,null);
        var known = new HashSet<(AppChord,string,string)>();
        foreach (var source in group.Sources)
            foreach (var rule in source.Rules)
            {
                if (group.MenuSource != null && !rule.HasArguments && !rule.SystemWide && TryParse(rule.Key,out var chord))
                    known.Add((chord,rule.Command,rule.Condition));
                yield return new(rule.Key,null,rule);
            }
        if (group.MenuSource == null) yield break;
        foreach (var rule in group.MenuSource.Rules)
        {
            // Count one exact observation once. Other menu rows for this key remain ambiguous.
            bool repeated = !rule.HasArguments && !rule.SystemWide && TryParse(rule.Key,out var chord) &&
                known.Remove((chord,rule.Command,rule.Condition));
            yield return new(rule.Key,null,rule,repeated);
        }
    }

    // A known chord prefix is occupied even if the entire sequence cannot be replayed.
    private static Dictionary<AppChord,int> Occupied(IEnumerable<Entry> entries)
    {
        var counts = new Dictionary<AppChord,int>();
        foreach (var entry in entries)
        {
            if (entry.ObservedInConfig || string.IsNullOrWhiteSpace(entry.Keys) || entry.Keys.Length > 256) continue;
            string text = PlusSpacing.Replace(entry.Keys.Trim(),"+");
            int end = text.IndexOfAny(StrokeSeparators);
            string stroke = end < 0 ? text : text[..end];
            if (ParseStroke(stroke,out var chord)) Count(chord);
            else if (stroke.EndsWith(']')) CountScanCode(stroke,Count);
        }
        return counts;
        void Count(AppChord chord) => counts[chord] = counts.GetValueOrDefault(chord)+1;
    }

    private static void CountScanCode(string stroke, Action<AppChord> count)
    {
        var tokens = stroke.Split('+');
        string key = tokens[^1];
        if (key.Length < 3 || key[0] != '[' || !TryModifiers(tokens.AsSpan(0,tokens.Length-1),out uint modifiers)) return;
        string scan = key[1..^1].ToUpperInvariant();
        // The target window can switch keyboard layouts after the index is built.
        // Printable physical positions may produce different letters/digits in that layout.
        if ((scan.Length == 4 && scan.StartsWith("KEY",StringComparison.Ordinal) && scan[3] is >= 'A' and <= 'Z') ||
            (scan.Length == 6 && scan.StartsWith("DIGIT",StringComparison.Ordinal) && scan[5] is >= '0' and <= '9') ||
            scan is "MINUS" or "EQUAL" or "BRACKETLEFT" or "BRACKETRIGHT" or "BACKSLASH" or "SEMICOLON" or
                "QUOTE" or "BACKQUOTE" or "COMMA" or "PERIOD" or "SLASH" or "INTLBACKSLASH" or "INTLRO" or "INTLYEN")
        {
            for (int vk='A'; vk<='Z'; vk++) count(new(modifiers,vk));
            for (int vk='0'; vk<='9'; vk++) count(new(modifiers,vk));
            return;
        }
        // Num Lock off exposes the keypad's navigation VKs. With Num Lock on its
        // numeric VKs are outside the supported alias/original set.
        string logical = scan switch {
            "ARROWLEFT" or "NUMPAD4" => "Left", "ARROWRIGHT" or "NUMPAD6" => "Right",
            "ARROWUP" or "NUMPAD8" => "Up", "ARROWDOWN" or "NUMPAD2" => "Down",
            "NUMPADENTER" => "Enter", "NUMPAD0" => "Insert", "NUMPAD1" => "End",
            "NUMPAD3" => "PageDown", "NUMPAD7" => "Home", "NUMPAD9" => "PageUp",
            "NUMPADDECIMAL" => "Delete", _ => scan
        };
        if (ParseKey(logical,modifiers,out var chord)) count(chord);
    }

    internal static string? Eligibility(AppShortcutGroup group, ManualAppShortcut? manual, ImportedAppShortcut? rule)
    {
        var entry = new Entry(manual?.Keys ?? rule?.Key ?? "",manual,rule);
        var entries = Entries(group);
        // The manual editor supplies a detached draft, including for a newly inserted row.
        if (manual != null) entries = entries.Where(item => item.Manual?.Id != manual.Id).Append(entry);
        return Eligibility(group,entry,Occupied(entries),out _);
    }

    private static string? Eligibility(AppShortcutGroup group, Entry entry, Dictionary<AppChord,int> occupied, out AppChord original)
    {
        original = default;
        if (!Path.IsPathFullyQualified(group.Path) ||
            !string.Equals(Path.GetExtension(group.Path),".exe",StringComparison.OrdinalIgnoreCase))
            return "需要选择可定位的 .exe 程序。";
        if ((entry.Manual == null) == (entry.Rule == null)) return "请选择一条动作记录。";
        if (entry.Manual is {Scope: not ManualShortcutScope.Application} || entry.Rule is {SystemWide:true})
            return "仅支持明确在软件内生效的动作。";
        if (entry.Rule is {} rule && (string.IsNullOrWhiteSpace(rule.Command) || rule.Command.TrimStart().StartsWith('-')))
            return "此条目没有可执行的软件动作。";
        if (!TryParse(entry.Keys, out original)) return "原快捷键需要是可识别的单组按键，并避开系统保留键。";
        if (occupied.GetValueOrDefault(original)>1) return "原快捷键还关联其他条目或连续按键，无法确定目标动作。";
        return null;
    }

    private static IEnumerable<(ShortcutBinding Binding,AppChord Alias,AppChord Original,string? Error)> Evaluate(
        AppShortcutGroup group, IReadOnlyDictionary<string,ShortcutBinding>? globals = null)
    {
        var entries = Entries(group).ToArray();
        var mapped = entries.Where(item => item.Binding is {Enabled:true}).ToArray();
        if (mapped.Length == 0) yield break;
        var occupied = Occupied(entries);
        var aliases = mapped.Select(item => new AppChord(item.Binding!.Modifiers,item.Binding.Key)).CountBy(chord=>chord).ToDictionary();
        var reserved = globals == null ? null : ReservedGlobals(globals);
        foreach (var item in mapped)
        {
            var alias = new AppChord(item.Binding!.Modifiers,item.Binding.Key);
            var error = Eligibility(group,item,occupied,out var original);
            if (error == null)
            {
                if ((alias.Modifiers & 3) == 0 || !Safe(alias)) error = "映射键需要包含 Ctrl 或 Alt，并避开系统保留键。";
                else if (aliases[alias] > 1) error = "该软件已有其他动作使用这个映射键。";
                else if (occupied.ContainsKey(alias)) error = "映射键与该软件已登记的原快捷键或连续按键的首组相同。";
                else if (reserved != null && (reserved.Contains(alias) || reserved.Contains(original)))
                    error = "映射键或原快捷键与文件坞已启用的全局快捷键冲突。";
            }
            yield return (item.Binding,alias,original,error);
        }
    }

    internal static IEnumerable<(AppChord Alias,AppChord Original)> Active(AppShortcutGroup group) =>
        Evaluate(group).Where(result => result.Error == null).Select(result => (result.Alias,result.Original));

    internal static HashSet<AppChord> ReservedGlobals(IReadOnlyDictionary<string,ShortcutBinding> globals) =>
        globals.Where(pair=>pair.Key != ShortcutCatalog.Preview && pair.Value.Enabled)
            .Select(pair=>new AppChord(pair.Value.Modifiers,pair.Value.Key)).ToHashSet();

    internal static string? Conflict(AppShortcutGroup group, IReadOnlyDictionary<string,ShortcutBinding> globals) =>
        Evaluate(group,globals).Select(mapping=>mapping.Error).FirstOrDefault(reason=>reason != null);

    internal static int PauseConflicts(AppShortcutGroup group, IReadOnlyDictionary<string,ShortcutBinding> globals)
    {
        var conflicts = Evaluate(group,globals).Where(mapping=>mapping.Error != null)
            .Select(mapping=>mapping.Binding).ToArray();
        foreach (var binding in conflicts) binding.Enabled = false;
        return conflicts.Length;
    }

    internal static void RestoreMappings(AppShortcutSource previous, AppShortcutSource refreshed)
    {
        if (!string.Equals(previous.Path,refreshed.Path,StringComparison.OrdinalIgnoreCase) || previous.IsDefault != refreshed.IsDefault) return;
        // Argument values are intentionally absent from the catalog, so their identities cannot be compared.
        static (string,string,string,bool,bool) Identity(ImportedAppShortcut rule) =>
            (rule.Key,rule.Command,rule.Condition,rule.SystemWide,rule.HasArguments);
        var old = previous.Rules.ToLookup(Identity);
        foreach (var rows in refreshed.Rules.GroupBy(Identity))
        {
            var prior = old[rows.Key];
            if (rows.Count() != 1 || prior.Count() != 1) continue;
            var row = rows.First();
            if (!row.HasArguments && !row.SystemWide) row.Mapping = prior.First().Mapping?.Copy();
        }
    }
}
