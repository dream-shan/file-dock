using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace FileDockDemo;

internal static class TemperatureIsolationVerification
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource<(double? Temperature, long Samples)> Response() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static async Task Run(MainWindow window, Action<bool, string> check)
    {
        await VerifyCache(check);
        await VerifyTransport(check);
        await VerifyWindow(window, check);
    }

    private static async Task VerifyCache(Action<bool, string> check)
    {
        long now = 0;
        int calls = 0;
        var response = Response();
        CancellationToken requestToken = default;
        using var started = new SemaphoreSlim(0);
        using (var sampler = new TemperatureSampler(token => {
            Interlocked.Increment(ref calls); requestToken = token; started.Release(); return response.Task;
        }, () => now))
        {
            sampler.Poll();
            check(calls == 0 && sampler.ReadCached().Value == null, "Inactive temperature cache never starts a request");
            sampler.SetActive(true); sampler.Poll();
            Task pending = sampler.Pending;
            await started.WaitAsync().WaitAsync(Limit);
            for (int i = 0; i < 100; i++) sampler.Poll();
            check(calls == 1 && !pending.IsCompleted, "Repeated temperature polls keep exactly one outstanding request");
            now = 4000; sampler.Poll();
            check(calls == 1, "A slow temperature request does not create a backlog");
            response.SetResult((61, 1)); await pending.WaitAsync(Limit);
            check(sampler.ReadCached().Value == 61, "Successful temperature responses populate the independent cache");
            response = Response(); sampler.Poll(); pending = sampler.Pending;
            await started.WaitAsync().WaitAsync(Limit);
            check(calls == 2, "A completed slow temperature request can resume on the next existing refresh tick");
            now = 6999;
            check(sampler.ReadCached().Value == 61, "A pending request can reuse a temperature younger than three seconds");
            now = 7000;
            check(sampler.ReadCached().Value == null && sampler.ReadCached().Status.Contains("过期"),
                "A three-second-old temperature expires while the next request is still pending");
            response.SetResult((72, 2)); await pending.WaitAsync(Limit);
            check(sampler.ReadCached().Value == 72, "A fresh response replaces expired temperature data");

            response = Response(); now = 8000; sampler.Poll(); pending = sampler.Pending;
            await started.WaitAsync().WaitAsync(Limit);
            sampler.SetActive(false); sampler.SetActive(true); sampler.Poll();
            check(requestToken.IsCancellationRequested && calls == 3,
                "Rapid hide/show cancels the old request without starting an overlapping one");
            response.SetResult((99, 3)); await pending.WaitAsync(Limit);
            check(sampler.ReadCached().Value == null, "A response from a canceled generation cannot restore stale temperature");
            response = Response(); sampler.Poll(); pending = sampler.Pending;
            await started.WaitAsync().WaitAsync(Limit);
            sampler.Dispose(); sampler.Dispose(); sampler.Poll();
            check(requestToken.IsCancellationRequested && calls == 4, "Disposal cancels the request and permanently stops new work");
            response.SetResult((88, 4)); await pending.WaitAsync(Limit);
            check(sampler.ReadCached().Value == null, "A late response cannot publish temperature after disposal");
        }

        now = 0; calls = 0;
        bool success = false;
        double? value = 65;
        using (var sampler = new TemperatureSampler(_ => {
            calls++;
            return success ? Task.FromResult((value, 1L)) :
                Task.FromException<(double?, long)>(new TimeoutException());
        }, () => now))
        {
            sampler.SetActive(true); sampler.Poll(); await sampler.Pending.WaitAsync(Limit);
            check(calls == 1 && sampler.ReadCached().Value == null && sampler.ReadCached().Status.Contains("超时"),
                "Temperature timeout becomes unavailable status without escaping the worker");
            sampler.SetActive(false); sampler.SetActive(true);
            foreach (int delay in new[] { 2000, 5000, 15000, 30000, 30000 })
            {
                int before = calls;
                now += delay - 1; sampler.Poll();
                check(calls == before && sampler.Pending.IsCompleted, "Temperature backoff suppresses early requests: " + delay);
                now++; sampler.Poll(); await sampler.Pending.WaitAsync(Limit);
                check(calls == before + 1, "Temperature retries respect the capped backoff boundary: " + delay);
            }
            success = true; now += 30000; sampler.Poll(); await sampler.Pending.WaitAsync(Limit);
            check(sampler.ReadCached().Value == 65, "Temperature retry recovers after repeated service failures");
            success = false; now += 1000; sampler.Poll(); await sampler.Pending.WaitAsync(Limit);
            int failedCalls = calls;
            now += 1999; sampler.Poll();
            check(calls == failedCalls, "A successful temperature read resets failure backoff to two seconds");
            success = true; value = null; now++; sampler.Poll(); await sampler.Pending.WaitAsync(Limit);
            check(calls == failedCalls + 1 && sampler.ReadCached().Value == null && sampler.ReadCached().Status.Contains("传感器"),
                "A missing sensor is reported separately from a pipe failure");
            value = 67; now += 1000; sampler.Poll(); await sampler.Pending.WaitAsync(Limit);
            check(sampler.ReadCached().Value == 67, "Missing sensors are retried at the normal sampling interval");
            sampler.SetActive(false); now += 100000; sampler.Poll();
            check(sampler.ReadCached().Value == null && sampler.Pending.IsCompleted,
                "Hidden temperature cache clears old readings and owns no background retry timer");
        }
    }

    private static byte[] Bytes(double value, long samples)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(value).CopyTo(bytes, 0);
        BitConverter.GetBytes(samples).CopyTo(bytes, 8);
        return bytes;
    }

    private static NamedPipeServerStream Server(string name) => new(name, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    private static NamedPipeClientStream Client(string name) => new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);

    private static async Task VerifyTransport(Action<bool, string> check)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string name = "FileDock.Test.Temperature." + Guid.NewGuid().ToString("N");
        using (var server = Server(name))
        using (var client = Client(name))
        {
            var connected = server.WaitForConnectionAsync(guard.Token);
            bool authenticated = false;
            var reading = TemperatureClient.ReadFromPipeAsync(client, true, guard.Token,
                _ => authenticated = true, Limit);
            await connected;
            var request = new byte[1]; await server.ReadExactlyAsync(request, guard.Token);
            check(authenticated && request[0] == 1, "Async temperature transport authenticates before sending the sample command");
            var bytes = Bytes(63, 42);
            await server.WriteAsync(bytes.AsMemory(0, 8), guard.Token);
            check(!reading.IsCompleted, "Temperature transport waits for all sixteen response bytes");
            await server.WriteAsync(bytes.AsMemory(8), guard.Token);
            var result = await reading.WaitAsync(Limit);
            check(result == (63d, 42L), "Temperature transport assembles a fragmented response correctly");
        }

        name = "FileDock.Test.Temperature." + Guid.NewGuid().ToString("N");
        using (var server = Server(name))
        using (var client = Client(name))
        {
            var connected = server.WaitForConnectionAsync(guard.Token);
            var reading = TemperatureClient.ReadFromPipeAsync(client, false, guard.Token, _ => { }, Limit);
            await connected;
            var request = new byte[1]; await server.ReadExactlyAsync(request, guard.Token);
            await server.WriteAsync(Bytes(double.NaN, 7), guard.Token);
            var result = await reading.WaitAsync(Limit);
            check(request[0] == 0 && result.Temperature == null && result.Samples == 7,
                "Async read-only requests preserve the sample counter and reject invalid temperatures");
        }

        name = "FileDock.Test.Temperature." + Guid.NewGuid().ToString("N");
        using (var server = Server(name))
        using (var client = Client(name))
        {
            var connected = server.WaitForConnectionAsync(guard.Token);
            var reading = TemperatureClient.ReadFromPipeAsync(client, true, guard.Token,
                _ => throw new IOException("fixture-authentication"), Limit);
            await connected;
            bool rejected = false;
            try { await reading.WaitAsync(Limit); } catch (IOException) { rejected = true; }
            client.Dispose();
            var request = new byte[1]; int received = await server.ReadAsync(request, guard.Token);
            check(rejected && received == 0, "Unverified temperature servers receive no command bytes");
        }

        name = "FileDock.Test.Temperature." + Guid.NewGuid().ToString("N");
        using (var client = Client(name))
        using (var cancellation = new CancellationTokenSource())
        {
            var reading = TemperatureClient.ReadFromPipeAsync(client, true, cancellation.Token, _ => { }, Limit);
            cancellation.Cancel();
            bool canceled = false;
            try { await reading.WaitAsync(Limit); } catch (OperationCanceledException) { canceled = true; }
            check(canceled, "Caller cancellation interrupts an unavailable pipe connection");
        }

        name = "FileDock.Test.Temperature." + Guid.NewGuid().ToString("N");
        using (var client = Client(name))
        {
            var reading = TemperatureClient.ReadFromPipeAsync(client, true, guard.Token, _ => { },
                TimeSpan.FromMilliseconds(300));
            bool timedOut = false;
            try { await reading.WaitAsync(Limit); } catch (TimeoutException) { timedOut = true; }
            check(timedOut && reading.IsCompleted && !guard.IsCancellationRequested,
                "The shared temperature deadline also bounds connection to a missing server");
        }

        foreach (bool cancel in new[] { true, false })
        {
            name = "FileDock.Test.Temperature." + Guid.NewGuid().ToString("N");
            using var server = Server(name);
            using var client = Client(name);
            using var cancellation = new CancellationTokenSource();
            var connected = server.WaitForConnectionAsync(guard.Token);
            var reading = TemperatureClient.ReadFromPipeAsync(client, true, cancellation.Token, _ => { },
                cancel ? Limit : TimeSpan.FromMilliseconds(300));
            await connected;
            var request = new byte[1]; await server.ReadExactlyAsync(request, guard.Token);
            if (cancel) cancellation.Cancel();
            bool stopped = false;
            try { await reading.WaitAsync(Limit); }
            catch (OperationCanceledException) when (cancel) { stopped = true; }
            catch (TimeoutException) when (!cancel) { stopped = true; }
            check(stopped && reading.IsCompleted, cancel ? "Caller cancellation interrupts a pending temperature response" :
                "A connected but unresponsive temperature server reaches the request deadline");
        }

        name = "FileDock.Test.Temperature." + Guid.NewGuid().ToString("N");
        using (var server = Server(name))
        using (var client = Client(name))
        {
            var connected = server.WaitForConnectionAsync(guard.Token);
            var reading = TemperatureClient.ReadFromPipeAsync(client, true, guard.Token, _ => { }, Limit);
            await connected;
            var request = new byte[1]; await server.ReadExactlyAsync(request, guard.Token);
            await server.WriteAsync(Bytes(60, 2).AsMemory(0, 8), guard.Token); server.Dispose();
            bool disconnected = false;
            try { await reading.WaitAsync(Limit); } catch (IOException) { disconnected = true; }
            check(disconnected, "An incomplete temperature response fails rather than publishing fabricated data");
        }
    }

    private static async Task VerifyWindow(MainWindow window, Action<bool, string> check)
    {
        string page = window.State.SelectedPage;
        using var started = new SemaphoreSlim(0);
        var requests = new ConcurrentQueue<(TaskCompletionSource<(double? Temperature, long Samples)> Response, CancellationToken Token)>();
        var all = new ConcurrentBag<TaskCompletionSource<(double? Temperature, long Samples)>>();
        window.SetTemperatureReaderForVerification(token => {
            var response = Response(); all.Add(response); requests.Enqueue((response, token)); started.Release(); return response.Task;
        });
        async Task<(TaskCompletionSource<(double? Temperature, long Samples)> Response, CancellationToken Token)> Next()
        {
            await started.WaitAsync().WaitAsync(Limit);
            if (!requests.TryDequeue(out var request)) throw new InvalidOperationException("Missing temperature fixture request.");
            return request;
        }
        try
        {
            window.SelectPage("Performance", false); window.ExpandPanel(); window.EnablePerformanceForVerification();
            var first = await Next();
            int baseline = window.PerformanceSamples;
            for (int i = 0; i < 100 && window.PerformanceSamples < baseline + 2; i++) await Task.Delay(100);
            check(!first.Response.Task.IsCompleted && window.PerformanceSamples >= baseline + 2 &&
                window.LastPerformanceSnapshot is { CpuLoad: not null, MemoryTotal: > 0, CpuTemperature: null },
                "Real CPU and memory sampling continues while the temperature response remains blocked");
            Task pending = window.PendingTemperatureForVerification;
            window.CollapsePanel();
            check(first.Token.IsCancellationRequested, "Collapsing the panel cancels the outstanding temperature request");
            first.Response.SetResult((99, 1)); await pending.WaitAsync(Limit);
            window.ExpandPanel(); var second = await Next();
            await Task.Delay(1200);
            check(window.LastPerformanceSnapshot?.CpuTemperature == null,
                "Reopening the panel does not display a late temperature from the previous generation");
            pending = window.PendingTemperatureForVerification;
            window.SelectPage("Files", false);
            check(second.Token.IsCancellationRequested && !window.PerformanceActive,
                "Leaving the performance page cancels temperature work");
            second.Response.SetResult((98, 2)); await pending.WaitAsync(Limit);
            window.SelectPage("Performance", false); var third = await Next();
            pending = window.PendingTemperatureForVerification;
            window.SetMemoryPauseForVerification(true);
            check(third.Token.IsCancellationRequested && !window.PerformanceActive,
                "Session or power suspension cancels temperature work through the shared pause path");
            third.Response.SetResult((97, 3)); await pending.WaitAsync(Limit);
            window.SetMemoryPauseForVerification(false); var fourth = await Next();
            pending = window.PendingTemperatureForVerification;
            fourth.Response.SetResult((68, 4)); await pending.WaitAsync(Limit);
            for (int i = 0; i < 30 && window.LastPerformanceSnapshot?.CpuTemperature != 68; i++) await Task.Delay(100);
            check(window.LastPerformanceSnapshot?.CpuTemperature == 68,
                "Resuming performance sampling publishes only the new temperature response");
        }
        finally
        {
            window.SetMemoryPauseForVerification(false);
            window.SelectPage("Files", false);
            Task pending = window.PendingTemperatureForVerification;
            foreach (var response in all) response.TrySetResult((null, 0));
            await pending.WaitAsync(Limit);
            window.SetTemperatureReaderForVerification(null);
            window.SelectPage(page, false);
        }
    }
}
