using System;
using System.IO;
using System.Linq;

namespace FileDockTemperature;

// Explicit self-test entry only: never opens real hardware or connects to SCM.
internal static class TemperatureRecoveryVerification
{
    private sealed class Hardware : ITemperatureHardware
    {
        internal bool FailOpen, FailRead, FailClose;
        internal int Opens, Reads, Closes;
        internal double Value = 61;
        public void Open() { Opens++; if (FailOpen) throw new InvalidOperationException("fixture-open"); }
        public double Read() { Reads++; if (FailRead) throw new IOException("fixture-update"); return Value; }
        public void Close() { Closes++; if (FailClose) throw new IOException("fixture-close"); }
    }

    internal static int Run(string root)
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks++;
            Console.WriteLine("PASS: " + name);
        }
        try
        {
            long now = 0;
            int created = 0, reported = 0;
            var broken = new Hardware { FailOpen = true, FailClose = true };
            var working = new Hardware();
            using (var sampler = new CpuTemperatureSampler(() => ++created == 1 ? broken : working,
                (_, _) => reported++, () => now))
            {
                Check(created == 0 && sampler.Samples == 0, "Construction performs no hardware work");
                sampler.Sample();
                Check(double.IsNaN(sampler.Temperature) && sampler.Samples == 1,
                    "Failed initialization returns unavailable temperature and counts one attempt");
                Check(broken.Reads == 0 && broken.Closes == 1 && reported == 2,
                    "Partially initialized hardware is closed even when cleanup throws");
                now = 1999; sampler.Sample();
                Check(created == 1 && sampler.Samples == 1, "Requests during backoff do not touch hardware");
                now = 2000; sampler.Sample();
                Check(created == 2 && working.Opens == 1 && working.Reads == 1 && sampler.Temperature == 61,
                    "A later request recovers using a fresh hardware object without service restart");
                sampler.Sample();
                Check(working.Reads == 1 && sampler.Samples == 2, "Successful requests are limited to one sample per second");
                now = 3000; working.FailRead = true; sampler.Sample();
                Check(double.IsNaN(sampler.Temperature) && working.Closes == 1 && sampler.Samples == 3,
                    "Update failure discards stale temperature and closes the failed object");
                now = 4999; sampler.Sample();
                Check(created == 2, "Successful recovery resets the next failure backoff to two seconds");
                working = new Hardware { Value = 72 }; now = 5000; sampler.Sample();
                Check(created == 3 && sampler.Temperature == 72, "Update failure can recover with a new object");
                now = 100000;
                Check(sampler.Samples == 4 && working.Reads == 1, "Elapsed time alone never causes background sampling");
                sampler.Dispose(); sampler.Dispose(); sampler.Sample();
                Check(working.Closes == 1 && sampler.Samples == 4 && double.IsNaN(sampler.Temperature),
                    "Disposal closes once and prevents future hardware work");
            }

            now = 0; created = 0;
            using (var sampler = new CpuTemperatureSampler(() => { created++; throw new IOException("fixture-factory"); },
                (_, _) => throw new InvalidOperationException("fixture-diagnostics"), () => now))
            {
                sampler.Sample();
                Check(created == 1 && sampler.Samples == 1, "Factory and diagnostic failures do not escape sampling");
                foreach (int delay in new[] { 2000, 5000, 15000, 30000, 30000 })
                {
                    int before = created;
                    now += delay - 1; sampler.Sample();
                    Check(created == before, "Backoff holds until the requested retry boundary: " + delay);
                    now++; sampler.Sample();
                    Check(created == before + 1 && sampler.Samples == created,
                        "Backoff retries at the boundary and stays capped: " + delay);
                }
            }

            now = 0; created = 0;
            var empty = new Hardware { Value = double.NaN };
            using (var sampler = new CpuTemperatureSampler(() => { created++; return empty; }, (_, _) => { }, () => now))
            {
                sampler.Sample();
                Check(double.IsNaN(sampler.Temperature) && created == 1 && empty.Closes == 0,
                    "A successful read with no sensor does not rebuild hardware");
                foreach (double invalid in new[] { 0d, -1d, 131d, double.PositiveInfinity })
                {
                    now += 1000; empty.Value = invalid; sampler.Sample();
                    Check(double.IsNaN(sampler.Temperature) && created == 1,
                        "Invalid sensor values remain unavailable without reinitialization");
                }
                now += 1000; empty.Value = 64; sampler.Sample();
                Check(sampler.Temperature == 64 && empty.Opens == 1,
                    "A temporarily missing sensor can recover on the same hardware object");
            }

            string directory = Path.Combine(Path.GetFullPath(root), "temperature-diagnostics");
            Directory.CreateDirectory(directory);
            now = 0;
            var log = new TemperatureDiagnosticLog(directory, () => now);
            var error = new IOException("private-path-and-message");
            log.Write("initialize", error);
            string current = Path.Combine(directory, "current.log");
            string line = File.ReadAllText(current);
            Check(line.Contains("stage=initialize") && line.Contains("System.IO.IOException") && line.Contains("hr="),
                "Service diagnostics record stage, exception type and error code");
            Check(!line.Contains(error.Message), "Service diagnostics omit exception messages");
            now = 29999; log.Write("update", error);
            Check(File.ReadAllText(current) == line, "Repeated diagnostics are rate limited for thirty seconds");
            now = 30000; log.Write("update", error);
            Check(File.ReadAllLines(current).Length == 2, "Diagnostics resume at the rate limit boundary");
            for (int i = 0; i < 1400; i++) { now += 30000; log.Write("update", error); }
            Check(Directory.GetFiles(directory).Length == 2 && Directory.GetFiles(directory)
                .All(path => new FileInfo(path).Length <= TemperatureDiagnosticLog.FileLimit),
                "Diagnostic rotation keeps at most two bounded files");
            now += 30000; log.Write("private-stage", error);
            Check(File.ReadAllText(current).Contains("stage=unknown") && !File.ReadAllText(current).Contains("private-stage"),
                "Only fixed diagnostic stage names are written");
            string blocked = Path.Combine(Path.GetFullPath(root), "temperature-log-blocked");
            File.WriteAllText(blocked, "fixture");
            new TemperatureDiagnosticLog(blocked).Write("close", error);
            Check(File.ReadAllText(blocked) == "fixture", "Unwritable diagnostics do not throw or overwrite existing files");
            Console.WriteLine("Temperature recovery self-test: " + checks + " checks passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Temperature recovery self-test failed: " + error.GetType().Name + ": " + error.Message);
            return 1;
        }
    }
}
