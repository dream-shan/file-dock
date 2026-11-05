using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace FileDockTemperature;

// The installed service directory is writable only by SYSTEM/administrators.
// Two bounded files, no exception messages, paths, sensor names or stack traces.
internal sealed class TemperatureDiagnosticLog
{
    internal const int FileLimit = 64 * 1024;
    private readonly string _directory;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Func<long> _milliseconds;
    private long _nextWrite;

    internal TemperatureDiagnosticLog(string? directory = null, Func<long>? milliseconds = null)
    {
        _directory = directory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Diagnostics");
        _milliseconds = milliseconds ?? (() => _clock.ElapsedMilliseconds);
    }

    internal void Write(string stage, Exception error)
    {
        try
        {
            long now = _milliseconds();
            if (now < _nextWrite) return;
            _nextWrite = now + 30000;
            string phase = stage == "initialize" || stage == "update" || stage == "close" ? stage : "unknown";
            string line = DateTimeOffset.UtcNow.ToString("O") + " stage=" + phase +
                " type=" + error.GetType().FullName + " hr=" + error.HResult.ToString("X8") + "\n";
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            Directory.CreateDirectory(_directory);
            string current = Path.Combine(_directory, "current.log");
            if (File.Exists(current) && new FileInfo(current).Length + bytes.Length > FileLimit)
            {
                string previous = Path.Combine(_directory, "previous.log");
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(current, previous);
            }
            using var stream = new FileStream(current, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes, 0, bytes.Length);
        }
        catch { /* Logging failures must not affect sampling or shutdown. */ }
    }
}
