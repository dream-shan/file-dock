#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FileDockDemo.Features.WindowPinning;
using Microsoft.Win32.SafeHandles;

namespace FileDockDemo;

public sealed class OpenWindowMenuApp
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string PackageId { get; set; } = "";
    public int WindowCount { get; set; }
    public List<ImportedAppShortcut> Rules { get; set; } = [];

    internal bool HasPackageIdentity => !string.IsNullOrEmpty(PackageId);
    internal string IdentityKey => HasPackageIdentity ? "id:" + PackageId : FileService.NormalizeKey(Path);
}

public sealed class OpenWindowMenuScanResult
{
    public int ScannedWindows { get; set; }
    public int MatchedWindows { get; set; }
    public int SkippedWindows { get; set; }
    public bool TimedOutPartial { get; set; }
    public List<OpenWindowMenuApp> Apps { get; set; } = [];
}

internal static class OpenWindowMenuScanner
{
    private const string WorkerArgument = "--read-open-window-menus";
    private const int MaxWindows = 40;
    private const int TotalBudgetMs = 7500;
    private const int PerWindowUiaTimeoutMs = 1100;
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "DummyDWMListenerWindow", "EdgeUiInputTopWndClass",
        "ForegroundStaging", "MultitaskingViewFrame", "Shell_InputSwitchTopLevelWindow"
    };

    internal sealed record WindowObservation(string Name, string Path, string? PackageId,
        IReadOnlyList<ImportedAppShortcut> Rules);

    private sealed record CandidateWindow(IntPtr Handle, uint Pid, uint Thread, string Name,
        string Path, string? PackageId, bool IsMinimized);

    internal static async Task<OpenWindowMenuScanResult> ScanAsync(CancellationToken token) =>
        await ScanAsync(Environment.ProcessId, token).ConfigureAwait(false);

    internal static async Task<OpenWindowMenuScanResult> ScanAsync(int excludedPid, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("无法定位菜单读取程序。"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(OpenWindowMenuScanner).Assembly.Location);
        start.ArgumentList.Add(WorkerArgument);
        start.ArgumentList.Add(excludedPid.ToString(CultureInfo.InvariantCulture));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        using var worker = Process.Start(start) ?? throw new InvalidOperationException("无法启动批量窗口菜单读取程序。");
        try
        {
            var output = worker.StandardOutput.ReadToEndAsync();
            await worker.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (worker.ExitCode != 0)
                throw new InvalidOperationException("批量读取打开窗口菜单未能完成。");
            return JsonSerializer.Deserialize<OpenWindowMenuScanResult>(await output.ConfigureAwait(false))
                ?? new OpenWindowMenuScanResult();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("批量读取打开窗口菜单超时。");
        }
        finally
        {
            if (!worker.HasExited)
            {
                try { worker.Kill(); }
                catch (InvalidOperationException) when (worker.HasExited) { }
            }
        }
    }

    internal static bool TryRunWorker(string[] args)
    {
        if (args.Length == 0 || args[0] != WorkerArgument) return false;
        Environment.ExitCode = 1;
        if (args.Length != 2 || !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int excludedPid))
            return true;

        using var watchdog = new Timer(_ => Environment.Exit(2), null, TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
        try
        {
            var result = ScanInProcess(excludedPid);
            using var stdout = Console.OpenStandardOutput();
            JsonSerializer.Serialize(stdout, result);
            Environment.ExitCode = 0;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or Win32Exception or
            COMException or UnauthorizedAccessException or IOException or OverflowException)
        {
            Environment.ExitCode = 3;
        }
        return true;
    }

    private static OpenWindowMenuScanResult ScanInProcess(int excludedPid)
    {
        var watch = Stopwatch.StartNew();
        var (candidates, skipped) = EnumerateCandidates(excludedPid);
        var observations = new List<WindowObservation>();
        var nativeByHandle = new Dictionary<IntPtr, List<ImportedAppShortcut>>();
        int scanned = 0;
        bool partial = false;

        // Pass 1: Read Win32 native menus for all candidates (including minimized windows) with near-zero latency.
        foreach (var candidate in candidates)
        {
            if (!StillMatches(candidate)) { skipped++; continue; }
            var nativeRules = MenuShortcutReader.ReadBatchWindow(candidate.Handle, candidate.Pid,
                includeAutomation: false, automationTimeout: TimeSpan.Zero);
            if (!StillMatches(candidate)) { skipped++; continue; }
            nativeByHandle[candidate.Handle] = nativeRules;
            scanned++;
        }

        // Pass 2: Supplement non-minimized visible windows with UI Automation within bounded budgets.
        foreach (var candidate in candidates)
        {
            if (!nativeByHandle.TryGetValue(candidate.Handle, out var rules)) continue;
            if (!candidate.IsMinimized && StillMatches(candidate))
            {
                int remainingMs = TotalBudgetMs - (int)watch.ElapsedMilliseconds;
                if (remainingMs <= 150)
                {
                    partial = true;
                }
                else if (!IsHungAppWindow(candidate.Handle))
                {
                    var uiaBudget = TimeSpan.FromMilliseconds(Math.Min(PerWindowUiaTimeoutMs, remainingMs));
                    var combined = MenuShortcutReader.ReadBatchWindow(candidate.Handle, candidate.Pid,
                        includeAutomation: true, automationTimeout: uiaBudget);
                    if (StillMatches(candidate) && combined.Count > 0)
                        rules = MergeRuleLists(rules, combined);
                }
            }
            if (rules.Count > 0)
                observations.Add(new WindowObservation(candidate.Name, candidate.Path, candidate.PackageId, rules));
        }

        return MergeObservations(observations, scanned, skipped, partial);
    }

    internal static OpenWindowMenuScanResult MergeObservations(IEnumerable<WindowObservation> observations,
        int scannedWindows, int skippedWindows, bool timedOutPartial)
    {
        var byIdentity = new Dictionary<string, (OpenWindowMenuApp App, HashSet<(string Command, string Key)> Seen)>(StringComparer.OrdinalIgnoreCase);
        int matchedWindows = 0;

        foreach (var obs in observations)
        {
            if (obs.Rules.Count == 0) continue;
            bool isPackage = !string.IsNullOrEmpty(obs.PackageId) && obs.PackageId.Contains('!');
            if (!isPackage && (string.IsNullOrWhiteSpace(obs.Path) || !Path.IsPathFullyQualified(obs.Path))) continue;
            string identity = isPackage ? "id:" + obs.PackageId : FileService.NormalizeKey(obs.Path);
            matchedWindows++;

            if (!byIdentity.TryGetValue(identity, out var bucket))
            {
                var app = new OpenWindowMenuApp
                {
                    Name = string.IsNullOrWhiteSpace(obs.Name)
                        ? (isPackage ? obs.PackageId! : Path.GetFileNameWithoutExtension(obs.Path))
                        : obs.Name,
                    Path = isPackage ? "" : Path.GetFullPath(obs.Path),
                    PackageId = isPackage ? obs.PackageId! : ""
                };
                bucket = (app, new HashSet<(string, string)>(RuleComparer.Instance));
                byIdentity[identity] = bucket;
            }
            bucket.App.WindowCount++;
            foreach (var rule in obs.Rules)
            {
                if (rule == null || string.IsNullOrWhiteSpace(rule.Command) || string.IsNullOrWhiteSpace(rule.Key)) continue;
                string command = rule.Command.Trim();
                string key = rule.Key.Trim();
                if (bucket.Seen.Add((command, key)))
                    bucket.App.Rules.Add(new ImportedAppShortcut { Command = command, Key = key, Condition = rule.Condition ?? "" });
            }
        }

        return new OpenWindowMenuScanResult
        {
            ScannedWindows = scannedWindows,
            MatchedWindows = matchedWindows,
            SkippedWindows = skippedWindows,
            TimedOutPartial = timedOutPartial,
            Apps = byIdentity.Values
                .Select(item => item.App)
                .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(app => app.IdentityKey, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    internal static bool ContainsRule(IEnumerable<ImportedAppShortcut>? existing, ImportedAppShortcut candidate)
    {
        if (existing == null) return false;
        var key = (candidate.Command.Trim(), candidate.Key.Trim());
        foreach (var item in existing)
        {
            if (RuleComparer.Instance.Equals((item.Command.Trim(), item.Key.Trim()), key))
                return true;
        }
        return false;
    }

    private static List<ImportedAppShortcut> MergeRuleLists(List<ImportedAppShortcut> first, List<ImportedAppShortcut> second)
    {
        var seen = new HashSet<(string, string)>(RuleComparer.Instance);
        var merged = new List<ImportedAppShortcut>(first.Count + second.Count);
        foreach (var rule in first.Concat(second))
        {
            if (seen.Add((rule.Command.Trim(), rule.Key.Trim())))
                merged.Add(rule.Copy());
        }
        return merged;
    }

    private static (List<CandidateWindow> Candidates, int Skipped) EnumerateCandidates(int excludedPid)
    {
        var candidates = new List<CandidateWindow>();
        int skipped = 0;
        int selfPid = Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            if (candidates.Count >= MaxWindows) return false;
            if (!PinNative.IsWindowVisible(hwnd)) return true;
            if (PinNative.GetAncestor(hwnd, 2) != hwnd) return true;
            IntPtr owner = GetWindow(hwnd, 4); // GW_OWNER: skip dialogs owned by another visible window
            if (owner != IntPtr.Zero && PinNative.IsWindowVisible(owner)) return true;

            long exStyle = NativeMethods.GetExtendedWindowStyle(hwnd);
            if ((exStyle & 0x80L) != 0) return true; // WS_EX_TOOLWINDOW
            if (IsCloaked(hwnd)) return true;

            uint thread = PinNative.GetWindowThreadProcessId(hwnd, out uint pid);
            if (thread == 0 || pid == 0 || pid == selfPid || pid == excludedPid) return true;

            string className = ReadClassName(hwnd);
            if (className.Length == 0 || ShellClasses.Contains(className)) return true;

            if (IsHungAppWindow(hwnd))
            {
                skipped++;
                return true;
            }

            string? executable = QueryProcessPath(pid);
            if (string.IsNullOrWhiteSpace(executable))
            {
                skipped++;
                return true;
            }

            string name = Path.GetFileNameWithoutExtension(executable);
            if (string.IsNullOrWhiteSpace(name) ||
                (className.Equals("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase) &&
                 name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                return true;
            }

            string? packageId = AppShortcutTarget.PackageWindowId(hwnd);
            bool minimized = IsIconic(hwnd);
            candidates.Add(new CandidateWindow(hwnd, pid, thread, name, executable, packageId, minimized));
            return true;
        }, IntPtr.Zero);

        return (candidates, skipped);
    }

    private static bool StillMatches(CandidateWindow candidate) =>
        PinNative.GetWindowThreadProcessId(candidate.Handle, out uint pid) == candidate.Thread &&
        pid == candidate.Pid && PinNative.IsWindowVisible(candidate.Handle);

    private static string? QueryProcessPath(uint pid)
    {
        using var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid) return null;
        var buffer = new StringBuilder(32768);
        uint size = (uint)buffer.Capacity;
        if (!QueryFullProcessImageName(process, 0, buffer, ref size)) return null;
        string path = buffer.ToString();
        return Path.IsPathFullyQualified(path) ? path : null;
    }

    private static string ReadClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        int length = GetClassName(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : "";
    }

    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private sealed class RuleComparer : IEqualityComparer<(string Command, string Key)>
    {
        internal static readonly RuleComparer Instance = new();
        public bool Equals((string Command, string Key) x, (string Command, string Key) y) =>
            string.Equals(x.Command, y.Command, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(NormalizeKey(x.Key), NormalizeKey(y.Key), StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Command, string Key) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Command),
                StringComparer.OrdinalIgnoreCase.GetHashCode(NormalizeKey(obj.Key)));
        private static string NormalizeKey(string value) => value.Replace(" +", "+").Replace("+ ", "+");
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
}
