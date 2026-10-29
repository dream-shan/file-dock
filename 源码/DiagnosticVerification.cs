using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace FileDockDemo;

internal static class DiagnosticVerification
{
    internal static void Run(MainWindow window, string directory, Action<bool, string> check)
    {
        string root = Path.Combine(directory, "diagnostic-checks");
        var writer = new DiagnosticWriter(root);
        check(writer.Write("startup", "fixture"), "Diagnostics create a local readable record");
        string file = Path.Combine(root, "current.jsonl");
        using (var record = JsonDocument.Parse(File.ReadAllText(file)))
            check(record.RootElement.GetProperty("pid").GetInt32() == Environment.ProcessId &&
                record.RootElement.GetProperty("run").GetString()?.Length == 32 &&
                record.RootElement.GetProperty("time").GetDateTimeOffset() != default,
                "Diagnostic records identify process, run and timestamp");
        string secret = "private-file-and-search-fixture";
        Exception problem;
        try { throw new IOException(secret, new InvalidOperationException(secret)); }
        catch (Exception ex) { problem = ex; }
        string detail = DiagnosticWriter.Describe(problem);
        check(!detail.Contains(secret) && detail.Contains("System.IO.IOException") && detail.Contains("System.InvalidOperationException") && detail.Contains(nameof(Run)),
            "Exception diagnostics retain types and methods without messages or paths");
        check(writer.Write("exception", detail), "Exception diagnostics can be persisted");
        Parallel.For(0, 700, i => writer.Write("concurrent", new string('x', 1800)));
        var files = Directory.GetFiles(root);
        check(files.Length == 2 && files.All(f => new FileInfo(f).Length <= DiagnosticWriter.FileLimit),
            "Diagnostic rotation retains at most two 512 KiB files under concurrent writes");
        bool valid = true;
        foreach (string line in files.SelectMany(File.ReadAllLines))
        {
            using var record = JsonDocument.Parse(line);
            valid &= record.RootElement.TryGetProperty("eventName", out _);
        }
        check(valid, "Concurrent diagnostic writes preserve complete JSON lines");
        string blocked = Path.Combine(directory, "diagnostic-blocked"); File.WriteAllText(blocked,"fixture");
        check(!new DiagnosticWriter(blocked).Write("test"), "Unavailable diagnostic directory does not throw or stop the application");
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            check(!writer.Write("locked"), "Exclusive file lock does not break application behavior");
        check(writer.Write("recovered"), "Diagnostics recover after a temporary file lock");
        string snapshot = window.DiagnosticSnapshot();
        check(snapshot.Contains("dockVisible=") && snapshot.Contains("captureActive=") && snapshot.Contains("uiTick=") &&
            !snapshot.Contains(directory) && !snapshot.Contains("\\"),
            "Window diagnostics contain visibility and responsiveness without browsed paths");
    }
}
