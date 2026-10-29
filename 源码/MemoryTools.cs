using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Media;

namespace FileDockDemo;

internal readonly record struct MemoryReading(ulong Used, ulong Total)
{
    internal double? Percent => Total > 0 ? Math.Clamp(Used * 100d / Total, 0, 100) : null;
}

internal static class MemoryPressure
{
    internal static Color ColorFor(double percent, bool dark)
    {
        Color green = dark ? Color.FromRgb(94, 207, 186) : Color.FromRgb(24, 143, 121);
        Color yellow = dark ? Color.FromRgb(236, 200, 104) : Color.FromRgb(163, 120, 16);
        Color red = dark ? Color.FromRgb(255, 119, 124) : Color.FromRgb(207, 55, 66);
        return percent <= 50 ? green : percent < 65 ? Mix(green, yellow, (percent - 50) / 15) : Mix(yellow, red, (percent - 65) / 15);
    }
    internal static Color Mix(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte Lerp(byte a, byte b) => (byte)Math.Round(a + (b - a) * t);
        return Color.FromRgb(Lerp(from.R,to.R), Lerp(from.G,to.G), Lerp(from.B,to.B));
    }
    internal static bool Warning(bool previous, double? value) => value.HasValue && (previous ? value >= 78 : value > 80);
}

internal sealed class MemoryActionGate
{
    internal bool Busy { get; private set; }
    internal bool TryStart() { if (Busy) return false; Busy = true; return true; }
    internal void Complete() { Busy = false; }
}

internal readonly record struct MemoryCleanupResult(MemoryReading Before, MemoryReading After, int Trimmed, int Skipped);

internal static class MemoryTools
{
    internal static MemoryReading Read()
    {
        var data = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref data) || data.Total == 0 || data.Available > data.Total) return default;
        return new(data.Total - data.Available, data.Total);
    }
    internal static uint ForegroundPid()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid); return pid;
    }
    internal static bool Eligible(int pid, int session, int currentSession, bool sameUser, bool elevated, bool critical, bool systemImage, uint protectedPid, uint foregroundPid) =>
        pid > 4 && pid != Environment.ProcessId && (uint)pid != protectedPid && (uint)pid != foregroundPid &&
        session == currentSession && sameUser && !elevated && !critical && !systemImage;

    internal static bool TryTrim(int pid, uint protectedPid)
    {
        if (pid <= 4 || pid == Environment.ProcessId || (uint)pid == protectedPid || (uint)pid == ForegroundPid()) return false;
        IntPtr process = OpenProcess(0x1100, false, pid); // limited query + set quota; never request termination/debug rights.
        if (process == IntPtr.Zero) return false;
        try
        {
            if (!ProcessIdToSessionId((uint)pid, out uint session) || !IsProcessCritical(process, out bool critical) ||
                !OpenProcessToken(process, 8, out IntPtr token)) return false;
            bool sameUser, elevated;
            try
            {
                using var identity = new WindowsIdentity(token);
                using var current = WindowsIdentity.GetCurrent();
                sameUser = identity.User != null && identity.User == current.User;
                if (!GetTokenInformation(token, 20, out int elevation, sizeof(int), out _)) return false;
                elevated = elevation != 0;
            }
            finally { CloseHandle(token); }
            var path = new StringBuilder(32768); uint size = (uint)path.Capacity;
            if (!QueryFullProcessImageName(process, 0, path, ref size)) return false;
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
            bool system = path.ToString().StartsWith(windows, StringComparison.OrdinalIgnoreCase);
            using var own = Process.GetCurrentProcess();
            if (!Eligible(pid, (int)session, own.SessionId, sameUser, elevated, critical, system, protectedPid, ForegroundPid())) return false;
            return K32EmptyWorkingSet(process);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException) { return false; }
        finally { CloseHandle(process); }
    }
    internal static MemoryCleanupResult Cleanup(uint protectedPid, CancellationToken token)
    {
        if (App.IsSelfTest) throw new InvalidOperationException("Self-tests must not trim unrelated applications.");
        MemoryReading before = Read(); int trimmed = 0, skipped = 0;
        var clock = Stopwatch.StartNew();
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                token.ThrowIfCancellationRequested();
                if (clock.Elapsed > TimeSpan.FromSeconds(8)) break;
                if (TryTrim(process.Id, protectedPid)) trimmed++; else skipped++;
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        return new(before, Read(), trimmed, skipped);
    }

    // Isolated real-process fixture; no user data or unrelated processes are touched.
    internal static int RunProbe(string directory)
    {
        byte[] pages = new byte[64 * 1024 * 1024];
        for (int i = 0; i < pages.Length; i += 4096) pages[i] = 1;
        File.WriteAllText(Path.Combine(directory,"ready"), "ready");
        for (int i = 0; i < 200 && !File.Exists(Path.Combine(directory,"stop")); i++) Thread.Sleep(100);
        GC.KeepAlive(pages); return 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong Total, Available, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool IsProcessCritical(IntPtr process, out bool critical);
    [DllImport("kernel32.dll")] private static extern bool K32EmptyWorkingSet(IntPtr process);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
    [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(IntPtr token,int info,out int data,int length,out int returned);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
}
