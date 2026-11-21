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

internal readonly record struct VsCodeShortcutRule(string Key, string Command, string Condition, bool HasArguments, bool SystemWide);

internal static class VsCodeShortcuts
{
    private const int MaxFileBytes = 4 * 1024 * 1024;
    private const int MaxRules = 10_000;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    // The editor's resolved keymap depends on defaults, extensions and active context.
    // Read just the ordered declarations in the selected file.
    internal static Task<IReadOnlyList<VsCodeShortcutRule>> ReadAsync(string path, CancellationToken token = default) =>
        Task.Run(() => ReadFileAsync(path, token), token);

    private static async Task<IReadOnlyList<VsCodeShortcutRule>> ReadFileAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long expected = source.Length;
        if (expected > MaxFileBytes) throw new InvalidDataException("VS Code 键位文件超过 4 MiB。");

        var bytes = new byte[(int)expected + 1];
        int read = await source.ReadAtLeastAsync(bytes, bytes.Length, false, token)
            .ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (read != expected) throw new InvalidDataException("读取时键位文件发生变化，请重试。");
        return Parse(bytes.AsMemory(0, read), token);
    }

    private static IReadOnlyList<VsCodeShortcutRule> Parse(ReadOnlyMemory<byte> input, CancellationToken token)
    {
        if (input.Span.StartsWith(Utf8Bom)) input = input[Utf8Bom.Length..];
        try { StrictUtf8.GetCharCount(input.Span); }
        catch (DecoderFallbackException) { throw new InvalidDataException("VS Code 键位文件必须是有效的 UTF-8 文本。"); }

        try
        {
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions {
                CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("VS Code 键位文件的顶层应为快捷键数组。");
            if (root.GetArrayLength() > MaxRules)
                throw new InvalidDataException("VS Code 键位文件超过 10,000 条。");

            var rules = new List<VsCodeShortcutRule>(root.GetArrayLength());
            foreach (var item in root.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                int row = rules.Count + 1;
                if (item.ValueKind != JsonValueKind.Object) throw BadField(row, "键位条目");
                string key = ReadString(item, row, "key", 256, required: true);
                if (string.IsNullOrWhiteSpace(key)) throw BadField(row, "key");
                string command = ReadString(item, row, "command", 512, required: true);
                string condition = ReadString(item, row, "when", 4096, required: false);
                bool systemWide = false;
                if (item.TryGetProperty("systemWide", out var systemWideValue))
                {
                    if (systemWideValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw BadField(row, "systemWide");
                    systemWide = systemWideValue.GetBoolean();
                }
                rules.Add(new VsCodeShortcutRule(key, command, condition, item.TryGetProperty("args", out _), systemWide));
            }
            return rules.AsReadOnly();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"无法读取 VS Code 键位文件，请检查第 {(ex.LineNumber ?? 0) + 1} 行的 JSON 格式。");
        }
    }

    private static string ReadString(JsonElement item, int row, string property, int maxLength, bool required)
    {
        if (!item.TryGetProperty(property, out var value)) return !required ? "" : throw BadField(row, property);
        if (value.ValueKind != JsonValueKind.String) throw BadField(row, property);
        string text;
        try { text = value.GetString()!; }
        catch (InvalidOperationException) { throw BadField(row, property); }
        if (text.Length > maxLength || text.Any(char.IsControl)) throw BadField(row, property);
        return text;
    }

    private static InvalidDataException BadField(int row, string property) =>
        new($"VS Code 键位文件第 {row} 条的 {property} 无效。");
}
