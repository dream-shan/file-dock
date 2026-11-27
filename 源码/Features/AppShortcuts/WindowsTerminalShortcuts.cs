#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FileDockDemo;

internal static class ShortcutRuleStatuses
{
    internal const string None = "";
    internal const string Custom = "custom";
    internal const string Override = "override";
    internal const string DefaultDeclared = "default";
    internal const string Disabled = "disabled";
    internal const string Uncertain = "uncertain";

    internal static bool IsValid(string? status) =>
        status is None or Custom or Override or DefaultDeclared or Disabled or Uncertain;

    internal static string Label(string? status) => status switch
    {
        Custom => "自定义绑定",
        Override => "覆盖默认键位",
        DefaultDeclared => "显式声明默认",
        Disabled => "禁用默认键位",
        Uncertain => "生效键位待确认",
        _ => ""
    };
}

internal readonly record struct WindowsTerminalShortcutRule(
    string Key,
    string Command,
    string Condition,
    string Status,
    bool HasArguments,
    bool SystemWide);

internal static class WindowsTerminalShortcuts
{
    private const int MaxFileBytes = 4 * 1024 * 1024;
    private const int MaxRules = 10_000;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private sealed record DefaultBinding(string ActionName, string DisplayName, string DefaultKey, bool SystemWide = false);
    private sealed record ActionDefinition(string ActionName, string DisplayName, bool HasArguments, bool SystemWide,
        bool IsUnbound, bool IsDynamic, bool IsUncertain, string? UncertainReason);
    private sealed record ParsedEntry(string Key, string NormalizedKey, string Section, string Command,
        string Condition, string Status, bool HasArguments, bool SystemWide);

    // Built-in defaults documented in Microsoft's Windows Terminal actions/defaults reference.
    // Used to classify default overrides, default unbindings and Terminal.* action IDs.
    private static readonly Dictionary<string, DefaultBinding> DefaultByKey = BuildDefaultByKey();
    private static readonly Dictionary<string, DefaultBinding> DefaultById = BuildDefaultById();
    private static readonly Dictionary<string, (string DisplayName, string? DefaultKey, bool SystemWide)> ActionMeta = BuildActionMeta();

    internal static Task<IReadOnlyList<WindowsTerminalShortcutRule>> ReadAsync(string path, CancellationToken token = default) =>
        Task.Run(() => ReadFileAsync(path, token), token);

    private static async Task<IReadOnlyList<WindowsTerminalShortcutRule>> ReadFileAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long expected = source.Length;
        if (expected > MaxFileBytes) throw new InvalidDataException("Windows 终端配置文件超过 4 MiB。");

        var bytes = new byte[(int)expected + 1];
        int read = await source.ReadAtLeastAsync(bytes, bytes.Length, false, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (read != expected) throw new InvalidDataException("读取时配置文件发生变化，请重试。");
        return Parse(bytes.AsMemory(0, read), token);
    }

    private static IReadOnlyList<WindowsTerminalShortcutRule> Parse(ReadOnlyMemory<byte> input, CancellationToken token)
    {
        if (input.Span.StartsWith(Utf8Bom)) input = input[Utf8Bom.Length..];
        try { StrictUtf8.GetCharCount(input.Span); }
        catch (DecoderFallbackException) { throw new InvalidDataException("Windows 终端配置文件必须是有效的 UTF-8 文本。"); }

        try
        {
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Windows 终端配置文件的顶层应为 JSON 对象。");

            var sections = new List<(string Name, JsonElement Array)>();
            CollectSection(root, "actions", sections);
            CollectSection(root, "keybindings", sections);

            // First pass: index user-defined action IDs so v1.22+ keybindings referencing them resolve cleanly.
            // Never retain argument values (commandline, startingDirectory, input, etc.).
            var definedIds = new Dictionary<string, ActionDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var (sectionName, array) in sections)
            {
                int row = 0;
                foreach (var item in array.EnumerateArray())
                {
                    token.ThrowIfCancellationRequested();
                    row++;
                    if (item.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException($"Windows 终端配置 {sectionName} 第 {row} 项应为 JSON 对象。");
                    if (TryReadOptionalId(item, sectionName, row, out string? id, out _) &&
                        !string.IsNullOrWhiteSpace(id) &&
                        (item.TryGetProperty("command", out _) || item.TryGetProperty("commands", out _)))
                    {
                        definedIds[id] = ParseActionDefinition(item, sectionName, row);
                    }
                }
            }

            // Second pass: read key declarations in file order.
            var parsed = new List<ParsedEntry>();
            foreach (var (sectionName, array) in sections)
            {
                int row = 0;
                foreach (var item in array.EnumerateArray())
                {
                    token.ThrowIfCancellationRequested();
                    row++;
                    var keys = ReadKeys(item, sectionName, row);
                    if (keys.Count == 0) continue;

                    var entrySpec = ResolveEntry(item, sectionName, row, definedIds);
                    foreach (string rawKey in keys)
                    {
                        if (parsed.Count >= MaxRules)
                            throw new InvalidDataException("Windows 终端快捷键声明超过 10,000 条。");
                        string normalized = NormalizeKey(rawKey);
                        parsed.Add(ClassifyBinding(rawKey, normalized, sectionName, entrySpec));
                    }
                }
            }

            return FinalizeDuplicateKeys(parsed);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"无法读取 Windows 终端配置文件，请检查第 {(ex.LineNumber ?? 0) + 1} 行的 JSON 格式。");
        }
    }

    private static void CollectSection(JsonElement root, string name, List<(string Name, JsonElement Array)> sections)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Windows 终端配置的 {name} 字段应为数组。");
        sections.Add((name, value));
    }

    private static List<string> ReadKeys(JsonElement item, string section, int row)
    {
        var keys = new List<string>();
        if (!item.TryGetProperty("keys", out var value) || value.ValueKind == JsonValueKind.Null)
            return keys;
        if (value.ValueKind == JsonValueKind.String)
        {
            string text = ReadBoundedString(value, section, row, "keys", 256).Trim();
            if (text.Length > 0) keys.Add(text);
            return keys;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in value.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException($"Windows 终端配置 {section} 第 {row} 项的 keys 数组元素应为字符串。");
                string text = ReadBoundedString(element, section, row, "keys", 256).Trim();
                if (text.Length > 0) keys.Add(text);
            }
            return keys;
        }
        throw new InvalidDataException($"Windows 终端配置 {section} 第 {row} 项的 keys 字段无效。");
    }

    private static bool TryReadOptionalId(JsonElement item, string section, int row, out string? id, out bool explicitNullOrEmpty)
    {
        id = null;
        explicitNullOrEmpty = false;
        if (!item.TryGetProperty("id", out var value)) return false;
        if (value.ValueKind == JsonValueKind.Null)
        {
            explicitNullOrEmpty = true;
            return true;
        }
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Windows 终端配置 {section} 第 {row} 项的 id 字段无效。");
        string text = ReadBoundedString(value, section, row, "id", 256).Trim();
        if (text.Length == 0)
        {
            explicitNullOrEmpty = true;
            return true;
        }
        id = text;
        return true;
    }

    private static ActionDefinition ResolveEntry(JsonElement item, string section, int row,
        IReadOnlyDictionary<string, ActionDefinition> definedIds)
    {
        bool hasCommand = item.TryGetProperty("command", out _);
        bool hasCommands = item.TryGetProperty("commands", out _);
        bool hasId = TryReadOptionalId(item, section, row, out string? id, out bool idExplicitNullOrEmpty);
        bool hasIterateOn = item.TryGetProperty("iterateOn", out var iterateOn) && iterateOn.ValueKind != JsonValueKind.Null;

        if (hasIterateOn)
            return new("", "动态展开动作", true, false, false, true, true, "包含 iterateOn 动态展开规则，无法仅凭配置文件确定实际生效键位");

        if (hasCommand || hasCommands)
            return ParseActionDefinition(item, section, row);

        if (hasId)
        {
            if (idExplicitNullOrEmpty || string.Equals(id, "Terminal.Unbound", StringComparison.OrdinalIgnoreCase))
                return new("unbound", "禁用键位", false, false, true, false, false, null);

            if (definedIds.TryGetValue(id!, out var userDefined))
                return userDefined;

            if (DefaultById.TryGetValue(id!, out var builtIn))
                return new(builtIn.ActionName, FormatActionDisplay(builtIn.ActionName, builtIn.DisplayName),
                    false, builtIn.SystemWide, false, false, false, null);

            return new(id!, id!, false, false, false, false, true,
                $"引用了未在本文件定义的动作标识「{id}」，实际生效动作以 Windows 终端为准");
        }

        return new("", "未指明动作", false, false, false, false, true, "配置条目缺少 command 或 id，无法确定实际生效动作");
    }

    private static ActionDefinition ParseActionDefinition(JsonElement item, string section, int row)
    {
        if (item.TryGetProperty("iterateOn", out var iterateOn) && iterateOn.ValueKind != JsonValueKind.Null)
            return new("", "动态展开动作", true, false, false, true, true, "包含 iterateOn 动态展开规则，无法仅凭配置文件确定实际生效键位");

        if (item.TryGetProperty("commands", out var nested) && nested.ValueKind != JsonValueKind.Null)
            return new("", "嵌套命令组", true, false, false, false, true, "包含嵌套 commands 定义，无法仅凭配置文件确定快捷键触发行为");

        if (!item.TryGetProperty("command", out var command))
            return new("", "未指明动作", false, false, false, false, true, "配置条目缺少 command，无法确定实际生效动作");

        if (command.ValueKind == JsonValueKind.Null)
            return new("unbound", "禁用键位", false, false, true, false, false, null);

        if (command.ValueKind == JsonValueKind.String)
        {
            string action = ReadBoundedString(command, section, row, "command", 256).Trim();
            if (action.Length == 0)
                return new("", "空动作", false, false, false, false, true, "command 为空字符串，无法确定实际生效动作");
            if (action.Equals("unbound", StringComparison.OrdinalIgnoreCase))
                return new("unbound", "禁用键位", false, false, true, false, false, null);
            return BuildKnownAction(action, hasArguments: false);
        }

        if (command.ValueKind == JsonValueKind.Object)
        {
            if (!command.TryGetProperty("action", out var actionElement) || actionElement.ValueKind != JsonValueKind.String)
                return new("", "未识别动作对象", true, false, false, false, true, "command 对象缺少有效的 action 名称，无法确定实际生效动作");
            string action = ReadBoundedString(actionElement, section, row, "command.action", 256).Trim();
            if (action.Length == 0)
                return new("", "空动作对象", true, false, false, false, true, "command.action 为空，无法确定实际生效动作");
            if (action.Equals("unbound", StringComparison.OrdinalIgnoreCase))
                return new("unbound", "禁用键位", false, false, true, false, false, null);
            bool hasExtraArgs = command.EnumerateObject().Any(prop => !prop.Name.Equals("action", StringComparison.Ordinal));
            return BuildKnownAction(action, hasExtraArgs);
        }

        throw new InvalidDataException($"Windows 终端配置 {section} 第 {row} 项的 command 字段无效。");
    }

    private static ActionDefinition BuildKnownAction(string action, bool hasArguments)
    {
        if (ActionMeta.TryGetValue(action, out var meta))
            return new(action, FormatActionDisplay(action, meta.DisplayName), hasArguments, meta.SystemWide, false, false, false, null);
        bool systemWide = action.Equals("globalSummon", StringComparison.OrdinalIgnoreCase) ||
                          action.Equals("quakeMode", StringComparison.OrdinalIgnoreCase);
        return new(action, action, hasArguments, systemWide, false, false, false, null);
    }

    private static ParsedEntry ClassifyBinding(string rawKey, string normalizedKey, string section, ActionDefinition action)
    {
        DefaultBinding? defaultAtKey = null;
        bool hasKnownDefault = normalizedKey.Length > 0 && DefaultByKey.TryGetValue(normalizedKey, out defaultAtKey);
        string sourcePrefix = $"来源：{section}";

        if (action.IsUnbound)
        {
            if (hasKnownDefault)
            {
                string label = $"禁用默认键位（原默认：{defaultAtKey!.DisplayName}）";
                string condition = $"{sourcePrefix}；已禁用原默认键位（原默认：{defaultAtKey.DisplayName}）";
                return new(rawKey, normalizedKey, section, label, condition, ShortcutRuleStatuses.Disabled, false, false);
            }
            return new(rawKey, normalizedKey, section, "已禁用键位（unbound）",
                $"{sourcePrefix}；配置声明禁用/解绑此键位", ShortcutRuleStatuses.Disabled, false, false);
        }

        if (action.IsUncertain || normalizedKey.Length == 0)
        {
            string reason = normalizedKey.Length == 0
                ? "键位语法无法静态识别，无法确定实际生效键位"
                : action.UncertainReason ?? "无法仅凭配置文件确定实际生效键位";
            string condition = $"{sourcePrefix}；{reason}";
            if (hasKnownDefault)
                condition += $"（该键位默认对应：{defaultAtKey!.DisplayName}）";
            return new(rawKey, normalizedKey, section, action.DisplayName, condition,
                ShortcutRuleStatuses.Uncertain, action.HasArguments, action.SystemWide);
        }

        if (hasKnownDefault)
        {
            bool sameAction = action.ActionName.Equals(defaultAtKey!.ActionName, StringComparison.OrdinalIgnoreCase);
            if (sameAction && !action.HasArguments)
            {
                return new(rawKey, normalizedKey, section, action.DisplayName,
                    $"{sourcePrefix}；配置显式保留此默认键位（{defaultAtKey.DisplayName}）",
                    ShortcutRuleStatuses.DefaultDeclared, action.HasArguments, action.SystemWide);
            }
            return new(rawKey, normalizedKey, section, action.DisplayName,
                $"{sourcePrefix}；覆盖默认键位（原默认：{defaultAtKey.DisplayName}）",
                ShortcutRuleStatuses.Override, action.HasArguments, action.SystemWide);
        }

        string customDetail = $"{sourcePrefix}；自定义绑定";
        if (ActionMeta.TryGetValue(action.ActionName, out var meta) && !string.IsNullOrEmpty(meta.DefaultKey))
            customDetail += $"（该动作官方默认参考键位：{meta.DefaultKey}）";
        return new(rawKey, normalizedKey, section, action.DisplayName, customDetail,
            ShortcutRuleStatuses.Custom, action.HasArguments, action.SystemWide);
    }

    private static IReadOnlyList<WindowsTerminalShortcutRule> FinalizeDuplicateKeys(List<ParsedEntry> entries)
    {
        var lastIndexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var countByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < entries.Count; i++)
        {
            string key = entries[i].NormalizedKey;
            if (key.Length == 0) continue;
            lastIndexByKey[key] = i;
            countByKey[key] = countByKey.GetValueOrDefault(key) + 1;
        }

        var result = new List<WindowsTerminalShortcutRule>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            string status = entry.Status;
            string condition = entry.Condition;
            if (entry.NormalizedKey.Length > 0 && countByKey.GetValueOrDefault(entry.NormalizedKey) > 1)
            {
                if (lastIndexByKey[entry.NormalizedKey] != i)
                {
                    status = ShortcutRuleStatuses.Uncertain;
                    condition += "；同一键位在配置文件后文再次声明，此条可能被后续规则覆盖，实际生效键位以 Windows 终端为准";
                }
                else
                {
                    condition += "；同一键位在配置中多次声明，按配置顺序此条靠后";
                }
            }
            result.Add(new WindowsTerminalShortcutRule(entry.Key, entry.Command, condition, status,
                entry.HasArguments, entry.SystemWide));
        }
        return result.AsReadOnly();
    }

    internal static string NormalizeKey(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string text = raw.Trim();
        // Split chords cleanly even when the final key itself is "+" (e.g. "alt+shift++" or "ctrl++").
        var tokens = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '+') continue;
            if (i == text.Length - 1)
            {
                // Trailing "+" as the key token itself.
                string before = text[start..i].Trim();
                if (before.Length > 0) tokens.Add(before);
                tokens.Add("+");
                start = text.Length;
                break;
            }
            string part = text[start..i].Trim();
            if (part.Length > 0) tokens.Add(part);
            start = i + 1;
        }
        if (start < text.Length)
        {
            string tail = text[start..].Trim();
            if (tail.Length > 0) tokens.Add(tail);
        }
        if (tokens.Count == 0) return "";

        bool ctrl = false, alt = false, shift = false, win = false;
        string? mainKey = null;
        for (int i = 0; i < tokens.Count; i++)
        {
            string lower = tokens[i].ToLowerInvariant();
            bool isLast = i == tokens.Count - 1;
            if (!isLast)
            {
                switch (lower)
                {
                    case "ctrl" or "control": if (ctrl) return ""; ctrl = true; continue;
                    case "alt": if (alt) return ""; alt = true; continue;
                    case "shift": if (shift) return ""; shift = true; continue;
                    case "win" or "meta": if (win) return ""; win = true; continue;
                    default: return "";
                }
            }
            mainKey = CanonicalKeyToken(lower);
        }
        if (string.IsNullOrEmpty(mainKey)) return "";

        var parts = new List<string>(5);
        if (ctrl) parts.Add("ctrl");
        if (alt) parts.Add("alt");
        if (shift) parts.Add("shift");
        if (win) parts.Add("win");
        parts.Add(mainKey);
        return string.Join('+', parts);
    }

    private static string CanonicalKeyToken(string token) => token switch
    {
        "pgup" or "pageup" => "pageup",
        "pgdn" or "pagedown" => "pagedown",
        "esc" or "escape" => "escape",
        "del" or "delete" => "delete",
        "ins" or "insert" => "insert",
        "return" or "enter" => "enter",
        "plus" or "+" or "oem_plus" => "+",
        "minus" or "-" or "oem_minus" => "-",
        "comma" or "," or "oem_comma" => ",",
        "period" or "." or "oem_period" => ".",
        "oem_3" or "`" => "`",
        "numpad_plus" or "numpadplus" => "numpad_plus",
        "numpad_minus" or "numpadminus" => "numpad_minus",
        "numpad_0" or "numpad0" => "numpad_0",
        _ => token
    };

    private static string ReadBoundedString(JsonElement element, string section, int row, string field, int maxLength)
    {
        string text;
        try { text = element.GetString()!; }
        catch (InvalidOperationException) { throw new InvalidDataException($"Windows 终端配置 {section} 第 {row} 项的 {field} 无效。"); }
        if (text.Length > maxLength || text.Any(char.IsControl))
            throw new InvalidDataException($"Windows 终端配置 {section} 第 {row} 项的 {field} 超出长度限制或包含控制字符。");
        return text;
    }

    private static string FormatActionDisplay(string action, string displayName) =>
        $"{displayName} ({action})";

    private static Dictionary<string, DefaultBinding> BuildDefaultByKey()
    {
        var map = new Dictionary<string, DefaultBinding>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, string action, string display, bool systemWide = false) =>
            map[NormalizeKey(key)] = new(action, display, key, systemWide);

        Add("Ctrl+Shift+T", "newTab", "使用默认配置新建标签页");
        Add("Ctrl+Shift+D", "duplicateTab", "复制当前标签页");
        Add("Ctrl+Shift+F", "find", "在终端中查找文本");
        Add("Ctrl+,", "openSettings", "打开终端设置");
        Add("Ctrl+Shift+,", "openSettings", "打开 settings.json");
        Add("Ctrl+Alt+,", "openSettings", "打开默认设置文件");
        Add("Alt+Enter", "toggleFullscreen", "切换全屏显示");
        Add("F11", "toggleFullscreen", "切换全屏显示");
        Add("Ctrl+Shift+Space", "openNewTabDropdown", "展开新建标签页菜单");
        Add("Ctrl+Shift+P", "commandPalette", "打开命令面板");
        Add("Ctrl+Shift+W", "closePane", "关闭当前窗格");
        Add("Alt+Shift+D", "splitPane", "自动拆分当前窗格");
        Add("Alt+Shift+-", "splitPane", "水平拆分窗格");
        Add("Alt+Shift++", "splitPane", "垂直拆分窗格");
        Add("Ctrl+C", "copy", "复制选中内容");
        Add("Ctrl+Shift+C", "copy", "复制选中内容");
        Add("Ctrl+Insert", "copy", "复制选中内容");
        Add("Ctrl+V", "paste", "粘贴剪贴板内容");
        Add("Ctrl+Shift+V", "paste", "粘贴剪贴板内容");
        Add("Shift+Insert", "paste", "粘贴剪贴板内容");
        Add("Ctrl+Tab", "nextTab", "切换到下一个标签页");
        Add("Ctrl+Shift+Tab", "prevTab", "切换到上一个标签页");
        Add("Ctrl+Shift+N", "newWindow", "打开新窗口");
        Add("Alt+F4", "closeWindow", "关闭终端窗口");
        Add("Ctrl+=", "adjustFontSize", "增大字号");
        Add("Ctrl++", "adjustFontSize", "增大字号");
        Add("Ctrl+NumPadPlus", "adjustFontSize", "增大字号");
        Add("Ctrl+-", "adjustFontSize", "减小字号");
        Add("Ctrl+NumPadMinus", "adjustFontSize", "减小字号");
        Add("Ctrl+0", "resetFontSize", "重置字号");
        Add("Ctrl+NumPad0", "resetFontSize", "重置字号");
        Add("Ctrl+Shift+Up", "scrollUp", "向上滚动一行");
        Add("Ctrl+Shift+Down", "scrollDown", "向下滚动一行");
        Add("Ctrl+Shift+PageUp", "scrollUpPage", "向上滚动一页");
        Add("Ctrl+Shift+PageDown", "scrollDownPage", "向下滚动一页");
        Add("Ctrl+Shift+Home", "scrollToTop", "滚动到顶部");
        Add("Ctrl+Shift+End", "scrollToBottom", "滚动到底部");
        Add("Alt+Left", "moveFocus", "焦点移至左侧窗格");
        Add("Alt+Right", "moveFocus", "焦点移至右侧窗格");
        Add("Alt+Up", "moveFocus", "焦点移至上方窗格");
        Add("Alt+Down", "moveFocus", "焦点移至下方窗格");
        Add("Alt+Shift+Left", "resizePane", "向左调整窗格大小");
        Add("Alt+Shift+Right", "resizePane", "向右调整窗格大小");
        Add("Alt+Shift+Up", "resizePane", "向上调整窗格大小");
        Add("Alt+Shift+Down", "resizePane", "向下调整窗格大小");
        Add("Win+`", "quakeMode", "切换全局 Quake 模式", systemWide: true);
        return map;
    }

    private static Dictionary<string, DefaultBinding> BuildDefaultById()
    {
        var map = new Dictionary<string, DefaultBinding>(StringComparer.OrdinalIgnoreCase);
        void Add(string id, string action, string display, string defaultKey, bool systemWide = false) =>
            map[id] = new(action, display, defaultKey, systemWide);

        Add("Terminal.OpenNewTab", "newTab", "使用默认配置新建标签页", "Ctrl+Shift+T");
        Add("Terminal.DuplicateTab", "duplicateTab", "复制当前标签页", "Ctrl+Shift+D");
        Add("Terminal.FindText", "find", "在终端中查找文本", "Ctrl+Shift+F");
        Add("Terminal.OpenSettingsUI", "openSettings", "打开终端设置", "Ctrl+,");
        Add("Terminal.OpenSettingsFile", "openSettings", "打开 settings.json", "Ctrl+Shift+,");
        Add("Terminal.OpenDefaultSettingsFile", "openSettings", "打开默认设置文件", "Ctrl+Alt+,");
        Add("Terminal.ToggleFullscreen", "toggleFullscreen", "切换全屏显示", "Alt+Enter");
        Add("Terminal.OpenNewTabDropdown", "openNewTabDropdown", "展开新建标签页菜单", "Ctrl+Shift+Space");
        Add("Terminal.ToggleCommandPalette", "commandPalette", "打开命令面板", "Ctrl+Shift+P");
        Add("Terminal.ClosePane", "closePane", "关闭当前窗格", "Ctrl+Shift+W");
        Add("Terminal.CloseTab", "closeTab", "关闭当前标签页", "");
        Add("Terminal.CloseWindow", "closeWindow", "关闭终端窗口", "Alt+F4");
        Add("Terminal.DuplicatePaneAuto", "splitPane", "自动拆分当前窗格", "Alt+Shift+D");
        Add("Terminal.SplitPaneDown", "splitPane", "水平拆分窗格", "Alt+Shift+-");
        Add("Terminal.SplitPaneRight", "splitPane", "垂直拆分窗格", "Alt+Shift++");
        Add("Terminal.CopyToClipboard", "copy", "复制选中内容", "Ctrl+Shift+C");
        Add("Terminal.PasteFromClipboard", "paste", "粘贴剪贴板内容", "Ctrl+Shift+V");
        Add("Terminal.NextTab", "nextTab", "切换到下一个标签页", "Ctrl+Tab");
        Add("Terminal.PrevTab", "prevTab", "切换到上一个标签页", "Ctrl+Shift+Tab");
        Add("Terminal.OpenNewWindow", "newWindow", "打开新窗口", "Ctrl+Shift+N");
        Add("Terminal.IncreaseFontSize", "adjustFontSize", "增大字号", "Ctrl+=");
        Add("Terminal.DecreaseFontSize", "adjustFontSize", "减小字号", "Ctrl+-");
        Add("Terminal.ResetFontSize", "resetFontSize", "重置字号", "Ctrl+0");
        Add("Terminal.QuakeMode", "quakeMode", "切换全局 Quake 模式", "Win+`", systemWide: true);
        return map;
    }

    private static Dictionary<string, (string DisplayName, string? DefaultKey, bool SystemWide)> BuildActionMeta()
    {
        var map = new Dictionary<string, (string, string?, bool)>(StringComparer.OrdinalIgnoreCase)
        {
            ["newTab"] = ("使用默认配置新建标签页", "Ctrl+Shift+T", false),
            ["duplicateTab"] = ("复制当前标签页", "Ctrl+Shift+D", false),
            ["find"] = ("在终端中查找文本", "Ctrl+Shift+F", false),
            ["openSettings"] = ("打开终端设置", "Ctrl+,", false),
            ["toggleFullscreen"] = ("切换全屏显示", "Alt+Enter", false),
            ["openNewTabDropdown"] = ("展开新建标签页菜单", "Ctrl+Shift+Space", false),
            ["commandPalette"] = ("打开命令面板", "Ctrl+Shift+P", false),
            ["closePane"] = ("关闭当前窗格", "Ctrl+Shift+W", false),
            ["closeTab"] = ("关闭当前标签页", null, false),
            ["closeWindow"] = ("关闭终端窗口", "Alt+F4", false),
            ["splitPane"] = ("拆分窗格", "Alt+Shift+D", false),
            ["copy"] = ("复制选中内容", "Ctrl+Shift+C", false),
            ["paste"] = ("粘贴剪贴板内容", "Ctrl+Shift+V", false),
            ["nextTab"] = ("切换到下一个标签页", "Ctrl+Tab", false),
            ["prevTab"] = ("切换到上一个标签页", "Ctrl+Shift+Tab", false),
            ["newWindow"] = ("打开新窗口", "Ctrl+Shift+N", false),
            ["adjustFontSize"] = ("调整字号", "Ctrl+= / Ctrl+-", false),
            ["resetFontSize"] = ("重置字号", "Ctrl+0", false),
            ["sendInput"] = ("发送输入", null, false),
            ["switchToTab"] = ("切换标签页", null, false),
            ["moveFocus"] = ("切换窗格焦点", "Alt+方向键", false),
            ["resizePane"] = ("调整窗格大小", "Alt+Shift+方向键", false),
            ["scrollUp"] = ("向上滚动一行", "Ctrl+Shift+Up", false),
            ["scrollDown"] = ("向下滚动一行", "Ctrl+Shift+Down", false),
            ["scrollUpPage"] = ("向上滚动一页", "Ctrl+Shift+PageUp", false),
            ["scrollDownPage"] = ("向下滚动一页", "Ctrl+Shift+PageDown", false),
            ["scrollToTop"] = ("滚动到顶部", "Ctrl+Shift+Home", false),
            ["scrollToBottom"] = ("滚动到底部", "Ctrl+Shift+End", false),
            ["toggleFocusMode"] = ("切换专注模式", null, false),
            ["toggleAlwaysOnTop"] = ("切换窗口置顶", null, false),
            ["quakeMode"] = ("切换全局 Quake 模式", "Win+`", true),
            ["globalSummon"] = ("全局唤出终端", null, true)
        };
        return map;
    }
}
