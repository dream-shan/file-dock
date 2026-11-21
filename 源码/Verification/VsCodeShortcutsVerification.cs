using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FileDockDemo;

internal static class VsCodeShortcutsVerification
{
    internal static async Task Run(string directory, Action<bool,string> check)
    {
        string folder = Directory.CreateDirectory(Path.Combine(directory, "vscode-keys")).FullName;
        string path = Path.Combine(folder, "keybindings.json");
        const string example = """
            // Place your key bindings in this file to override the defaults.
            [
              {
                "key": "ctrl+k ctrl+s",
                "command": "workbench.action.openGlobalKeybindings",
                "when": "editorTextFocus && resourceLangId == markdown",
                "args": { "value": "仅供测试的参数" },
                "systemWide": true,
              },
              /* Retain repeated keys and removal rules in source order. */
              { "key": "ctrl+k ctrl+s", "command": "-example.defaultAction", "systemWide": false },
              { "key": "f6", "command": "", "args": null },
              { "key": "ctrl+shift+[KeyG]", "command": "示例命令" },
            ]
            """;
        await File.WriteAllTextAsync(path, example, new UTF8Encoding(true));
        byte[] original = await File.ReadAllBytesAsync(path);
        var rules = await VsCodeShortcuts.ReadAsync(path);
        check(rules.Count == 4 && rules[0].Key == "ctrl+k ctrl+s" &&
            rules[0].Command == "workbench.action.openGlobalKeybindings" &&
            rules[0].Condition == "editorTextFocus && resourceLangId == markdown",
            "VS Code import reads a UTF-8 BOM, comments, trailing commas, chords and conditions");
        check(rules[0].SystemWide && !rules[1].SystemWide && !rules[3].SystemWide,
            "VS Code import retains the explicit system-wide flag without inferring live registration");
        check(rules[0].HasArguments && !rules[1].HasArguments && rules[2].HasArguments &&
            !rules[0].ToString().Contains("仅供测试的参数", StringComparison.Ordinal),
            "VS Code import exposes argument presence without returning argument values");
        check(rules[1].Key == rules[0].Key && rules[1].Command == "-example.defaultAction" &&
            rules[2].Command == "" && rules[3].Key == "ctrl+shift+[KeyG]" && rules[3].Command == "示例命令",
            "VS Code import preserves source order, removal rules, empty commands and physical-key syntax");
        byte[] afterImport = await File.ReadAllBytesAsync(path);
        check(original.AsSpan().SequenceEqual(afterImport),
            "VS Code import leaves its source file byte-for-byte unchanged");

        await File.WriteAllTextAsync(path, "[]");
        check((await VsCodeShortcuts.ReadAsync(path)).Count == 0, "An empty VS Code keybindings array imports no rules");
        await Invalid("[{\"key\":\"f6\",\"command\":\"valid\"},{\"key\":\"f7\",\"command\":false}]",
            "A malformed rule fails the entire VS Code import");
        await Invalid("[{\"key\":\"f6\",\"command\":\"valid\"}", "Truncated VS Code JSON fails clearly");
        await Invalid("{\"key\":\"f6\",\"command\":\"action\"}", "VS Code import requires a top-level array");
        await Invalid("[{\"key\":\"f6\",\"command\":\"valid\",\"systemWide\":1}]",
            "VS Code import rejects an invalid system-wide flag");
        await Invalid("[{\"key\":\"f6\",\"command\":\"" + new string('x', 700) + "\"}]",
            "VS Code import bounds the size of individual fields");
        await File.WriteAllBytesAsync(path, [0x5B, 0x5D, 0x20, 0xC0, 0xAF]);
        await RejectFile("VS Code import rejects invalid UTF-8");
        using (var oversized = new FileStream(path, FileMode.Create))
            oversized.SetLength(4 * 1024 * 1024 + 1);
        await RejectFile("VS Code import rejects oversized files before reading their contents");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool cancelled = false;
        try { await VsCodeShortcuts.ReadAsync(path, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "A cancelled VS Code import stops before opening the file");

        async Task Invalid(string json, string scenario)
        {
            await File.WriteAllTextAsync(path, json);
            await RejectFile(scenario);
        }

        async Task RejectFile(string scenario)
        {
            bool rejected = false;
            try { await VsCodeShortcuts.ReadAsync(path); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, scenario);
        }
    }
}
