using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace FileDockDemo;

// Two bounded, local-only files. Never serialize exception messages, user paths,
// command lines, file entries or searches. Failure to log must not break the app.
internal sealed class DiagnosticWriter
{
    internal const long FileLimit = 512 * 1024;
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _run = Guid.NewGuid().ToString("N");
    internal DiagnosticWriter(string directory) => _directory = directory;

    internal bool Write(string eventName, string detail = "")
    {
        try
        {
            if (detail.Length > 2048) detail = detail[..2048];
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
                time = DateTimeOffset.Now, tick = Environment.TickCount64,
                pid = Environment.ProcessId, run = _run, eventName, detail
            }) + "\n");
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                string current = Path.Combine(_directory, "current.jsonl");
                if (File.Exists(current) && new FileInfo(current).Length + bytes.Length > FileLimit)
                    File.Move(current, Path.Combine(_directory, "previous.jsonl"), true);
                using var stream = new FileStream(current, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
            }
            return true;
        }
        catch { return false; }
    }

    internal static string Describe(Exception? exception)
    {
        if (exception == null) return "NonException";
        try
        {
            var result = new StringBuilder();
            for (int depth = 0; exception != null && depth < 4; depth++, exception = exception.InnerException)
            {
                result.Append(exception.GetType().FullName).Append(" hr=").Append(exception.HResult.ToString("X8"));
                foreach (var frame in (new StackTrace(exception, false).GetFrames() ?? Array.Empty<StackFrame>()).Take(12))
                {
                    var method = frame.GetMethod();
                    result.Append(" > ").Append(method?.DeclaringType?.FullName).Append('.').Append(method?.Name);
                }
                result.Append("; ");
            }
            return result.ToString();
        }
        catch { return "ExceptionDetailsUnavailable"; }
    }
}

internal static class DiagnosticLog
{
    private static DiagnosticWriter? _writer;
    internal static bool Initialize(string dataDirectory)
    {
        _writer = new DiagnosticWriter(Path.Combine(dataDirectory, "Diagnostics"));
        return _writer.Write("startup", $"version={App.VersionLabel}; os={Environment.OSVersion.Version}; runtime={Environment.Version}; selfTest={App.IsSelfTest}");
    }
    internal static void Write(string eventName, string detail = "") => _writer?.Write(eventName, detail);
    internal static void Error(string eventName, Exception? exception) => Write(eventName, DiagnosticWriter.Describe(exception));
}
