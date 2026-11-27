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
    public string AppId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<ManualAppShortcut> Manual { get; set; } = [];
    public List<AppShortcutSource> Sources { get; set; } = [];
    public AppShortcutSource? MenuSource { get; set; }
    public DateTimeOffset? LastMenuRead { get; set; }

    internal bool HasPackageIdentity => !string.IsNullOrEmpty(AppId);
    internal string IdentityKey => HasPackageIdentity ? "id:" + AppId : FileService.NormalizeKey(Path);

    internal IEnumerable<AppShortcutSource> AllSources() =>
        MenuSource == null ? Sources : Sources.Append(MenuSource);

    internal AppShortcutGroup Copy() => new() {
        Path = Path, AppId = AppId, Name = Name,
        Manual = Manual.Select(item => item.Copy()).ToList(),
        Sources = Sources.Select(item => item.Copy()).ToList(),
        MenuSource = MenuSource?.Copy(), LastMenuRead = LastMenuRead
    };

    internal bool Valid()
    {
        if (AppId == null || Path == null || !HasText(Name, 256) || Manual == null || Sources == null) return false;
        if (HasPackageIdentity)
        {
            if (Path.Length != 0 || !HasText(AppId, 512) || !AppId.Contains('!') || AppId.Any(char.IsControl) ||
                Manual.Count > 0 || MenuSource != null || LastMenuRead != null) return false;
        }
        else if (!FullyQualified(Path) || !ApplicationEntry.Supported(Path)) return false;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var manual in Manual)
        {
            if (manual == null || !HasText(manual.Id, 80) || !ids.Add(manual.Id) ||
                !HasText(manual.Action, 160) || !HasText(manual.Keys, 256) ||
                manual.Note == null || manual.Note.Length > 1024 || !Enum.IsDefined(manual.Scope)) return false;
        }
        var seenSources = new HashSet<(string Kind, bool IsDefault, string Path)>();
        foreach (var source in Sources)
        {
            if (source == null || !FullyQualified(source.Path) || source.Kind == null ||
                source.Kind is not (AppShortcutSourceKinds.VsCode or AppShortcutSourceKinds.WindowsTerminal) ||
                !seenSources.Add((source.Kind, source.IsDefault, FileService.NormalizeKey(source.Path))) ||
                source.Rules == null || source.Rules.Any(rule => rule == null ||
                    string.IsNullOrWhiteSpace(rule.Key) || rule.Command == null || rule.Condition == null ||
                    !ShortcutRuleStatuses.IsValid(rule.Status)))
                return false;
        }
        return MenuSource == null || (LastMenuRead != null && !MenuSource.IsDefault &&
            string.IsNullOrEmpty(MenuSource.Kind) &&
            string.Equals(Path, MenuSource.Path, StringComparison.OrdinalIgnoreCase) &&
            MenuSource.Rules != null && MenuSource.Rules.All(rule => rule != null &&
                HasText(rule.Key, 256) && !string.IsNullOrWhiteSpace(rule.Command) && rule.Condition != null &&
                ShortcutRuleStatuses.IsValid(rule.Status)));
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
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (state.AppShortcutGroups.Any(group => group == null || !group.Valid() ||
            !identities.Add(group.IdentityKey)))
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
    public string Kind { get; set; } = "";
    public bool IsDefault { get; set; }
    public DateTimeOffset? LastRead { get; set; }
    public List<ImportedAppShortcut> Rules { get; set; } = [];
    internal AppShortcutSource Copy() => new() {
        Path = Path, Kind = Kind, IsDefault = IsDefault, LastRead = LastRead,
        Rules = Rules.Select(rule => rule.Copy()).ToList()
    };
}

public sealed class ImportedAppShortcut
{
    public string Key { get; set; } = "";
    public string Command { get; set; } = "";
    public string Condition { get; set; } = "";
    public string Status { get; set; } = "";
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
