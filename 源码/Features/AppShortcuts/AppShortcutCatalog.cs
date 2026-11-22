using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FileDockDemo;

// Catalog entries describe keys owned by another application, independently of
// the registered shortcuts that FileDock dispatches.
public sealed class AppShortcutGroup
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public List<ManualAppShortcut> Manual { get; set; } = [];
    public List<AppShortcutSource> Sources { get; set; } = [];
    public AppShortcutSource? MenuSource { get; set; }
    public DateTimeOffset? LastMenuRead { get; set; }

    internal IEnumerable<AppShortcutSource> AllSources() =>
        MenuSource == null ? Sources : Sources.Append(MenuSource);

    internal AppShortcutGroup Copy() => new() {
        Path = Path, Name = Name,
        Manual = Manual.Select(item => item.Copy()).ToList(),
        Sources = Sources.Select(item => item.Copy()).ToList(),
        MenuSource = MenuSource?.Copy(), LastMenuRead = LastMenuRead
    };

    internal bool Valid()
    {
        if (!FullyQualified(Path) || !ApplicationEntry.Supported(Path) || !HasText(Name, 256) ||
            Manual == null || Sources == null) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var manual in Manual)
        {
            if (manual == null || !HasText(manual.Id, 80) || !ids.Add(manual.Id) ||
                !HasText(manual.Action, 160) || !HasText(manual.Keys, 256) ||
                manual.Note == null || manual.Note.Length > 1024 || !Enum.IsDefined(manual.Scope)) return false;
        }
        var userFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var defaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in Sources)
        {
            if (source == null || !FullyQualified(source.Path) ||
                !(source.IsDefault ? defaults : userFiles).Add(FileService.NormalizeKey(source.Path)) ||
                source.Rules == null || source.Rules.Any(rule => rule == null ||
                    string.IsNullOrWhiteSpace(rule.Key) || rule.Command == null || rule.Condition == null))
                return false;
        }
        return MenuSource == null || (LastMenuRead != null && !MenuSource.IsDefault &&
            string.Equals(Path, MenuSource.Path, StringComparison.OrdinalIgnoreCase) &&
            MenuSource.Rules != null && MenuSource.Rules.All(rule => rule != null &&
                HasText(rule.Key, 256) && !string.IsNullOrWhiteSpace(rule.Command) && rule.Condition != null));
    }

    private static bool HasText(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;

    private static bool FullyQualified(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path)) return false;
        try { _ = System.IO.Path.GetFullPath(path); return true; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    internal static void Validate(AppState state)
    {
        state.AppShortcutGroups ??= [];
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (state.AppShortcutGroups.Any(group => group == null || !group.Valid() ||
            !paths.Add(FileService.NormalizeKey(group.Path))))
            throw new InvalidDataException("软件内部快捷键目录无效。");
    }
}

public sealed class ManualAppShortcut
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Action { get; set; } = "";
    public string Keys { get; set; } = "";
    public string Note { get; set; } = "";
    public ManualShortcutScope Scope { get; set; }
    public ShortcutBinding? Mapping { get; set; }
    internal ManualAppShortcut Copy()
    {
        var copy = (ManualAppShortcut)MemberwiseClone();
        copy.Mapping = Mapping?.Copy();
        return copy;
    }
}

public enum ManualShortcutScope { Unknown, Application, Global }

public sealed class AppShortcutSource
{
    public string Path { get; set; } = "";
    public bool IsDefault { get; set; }
    public List<ImportedAppShortcut> Rules { get; set; } = [];
    internal AppShortcutSource Copy() => new() {
        Path = Path, IsDefault = IsDefault, Rules = Rules.Select(rule => rule.Copy()).ToList()
    };
}

public sealed class ImportedAppShortcut
{
    public string Key { get; set; } = "";
    public string Command { get; set; } = "";
    public string Condition { get; set; } = "";
    public bool HasArguments { get; set; }
    public bool SystemWide { get; set; }
    public ShortcutBinding? Mapping { get; set; }
    internal ImportedAppShortcut Copy()
    {
        var copy = (ImportedAppShortcut)MemberwiseClone();
        copy.Mapping = Mapping?.Copy();
        return copy;
    }
}
