using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using FileDockDemo.Features.WindowPinning;
using Microsoft.Win32.SafeHandles;

namespace FileDockDemo;

internal static class MenuShortcutReader
{
    private const string WorkerArgument = "--read-window-menu";
    private const int MaxNodes = 1500, MaxDepth = 20, MaxRules = 512;
    private const int IdentityChanged = 2, Unavailable = 3, TooLarge = 4;

    internal static async Task<List<ImportedAppShortcut>> ReadAsync(IntPtr hwnd, string expectedPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        uint thread = PinNative.GetWindowThreadProcessId(hwnd, out uint pid);
        if (thread == 0 || !Path.IsPathFullyQualified(expectedPath)) throw Changed();
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("无法定位菜单读取程序。")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(MenuShortcutReader).Assembly.Location);
        start.ArgumentList.Add(WorkerArgument);
        start.ArgumentList.Add(hwnd.ToInt64().ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(thread.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(Path.GetFullPath(expectedPath));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var worker = Process.Start(start) ?? throw new InvalidOperationException("无法启动菜单读取程序。");
        try
        {
            var output = worker.StandardOutput.ReadToEndAsync();
            await worker.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (worker.ExitCode == IdentityChanged || PinNative.GetWindowThreadProcessId(hwnd, out uint current) != thread ||
                current != pid) throw Changed();
            if (worker.ExitCode == TooLarge) throw new InvalidDataException("菜单项过多或层级过深。");
            if (worker.ExitCode != 0) throw new InvalidOperationException("目标软件未允许读取此窗口的菜单。");
            return JsonSerializer.Deserialize<List<ImportedAppShortcut>>(await output.ConfigureAwait(false))
                ?? throw new InvalidDataException("菜单读取结果无效。");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("读取菜单超时，请在目标软件响应后重试。");
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

    private static InvalidOperationException Changed() => new("所选窗口已关闭或不再属于该软件，请重新选择。");

    internal static bool TryRunWorker(string[] args)
    {
        if (args.Length == 0 || args[0] != WorkerArgument) return false;
        Environment.ExitCode = Unavailable;
        if (args.Length != 5 || !long.TryParse(args[1], out long handle) || !uint.TryParse(args[2], out uint pid) ||
            !uint.TryParse(args[3], out uint thread)) return true;
        // The parent normally kills a blocked provider after ten seconds. Cover a parent crash as well.
        using var watchdog = new Timer(_ => Environment.Exit(Unavailable), null, TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
        try
        {
            var hwnd = new IntPtr(handle);
            using var target = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
            var imagePath = new StringBuilder(32768);
            uint size = (uint)imagePath.Capacity;
            bool Matches() => PinNative.GetWindowThreadProcessId(hwnd, out uint current) == thread && current == pid &&
                PinNative.IsWindowVisible(hwnd) && PinNative.GetAncestor(hwnd, 2) == hwnd;
            // Pin the process identity across the read without querying the window title or its text.
            if (target.IsInvalid || !Matches() || !QueryFullProcessImageName(target, 0, imagePath, ref size) ||
                !string.Equals(imagePath.ToString(), args[4], StringComparison.OrdinalIgnoreCase))
            { Environment.ExitCode = IdentityChanged; return true; }

            var snapshot = new Snapshot(pid);
            IntPtr menu = GetMenu(hwnd);
            if (menu != IntPtr.Zero) snapshot.ReadNative(menu, "", 0);
            // One window can expose native menu items and separate framework menus.
            // Use the same snapshot so identical observations are counted once.
            Task.Run(() => snapshot.ReadAutomation(AutomationElement.FromHandle(hwnd), "", false, 0)).GetAwaiter().GetResult();
            if (!Matches()) { Environment.ExitCode = IdentityChanged; return true; }
            using var stdout = Console.OpenStandardOutput();
            JsonSerializer.Serialize(stdout, snapshot.Rules);
            Environment.ExitCode = 0;
        }
        catch (InvalidDataException) { Environment.ExitCode = TooLarge; }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or Win32Exception or
            COMException or UnauthorizedAccessException or IOException or OverflowException or ElementNotAvailableException or ElementNotEnabledException) { }
        return true;
    }

    private sealed class Snapshot(uint pid)
    {
        internal readonly List<ImportedAppShortcut> Rules = [];
        private readonly HashSet<(string, string)> seen = [];
        private int nodes;

        private void Visit(int depth)
        {
            if (++nodes > MaxNodes || depth > MaxDepth) throw new InvalidDataException();
        }

        private void Add(string action, string key)
        {
            key = key.Trim();
            if (action.Length == 0 || key.Length == 0 || key.Length > 256 || !seen.Add((action, key))) return;
            if (Rules.Count == MaxRules) throw new InvalidDataException();
            Rules.Add(new ImportedAppShortcut { Command = action, Key = key });
        }

        private static string Join(string parent, string name) => parent.Length == 0 ? name :
            name.Length == 0 ? parent : parent + " / " + name;

        internal void ReadNative(IntPtr menu, string parent, int depth)
        {
            int count = GetMenuItemCount(menu);
            if (count < 0) throw new InvalidOperationException("无法读取目标菜单。");
            for (int index = 0; index < count; index++)
            {
                Visit(depth);
                var text = new StringBuilder(768);
                int length = GetMenuString(menu, (uint)index, text, text.Capacity, 0x400); // MF_BYPOSITION
                if (length == text.Capacity - 1) continue;
                var fields = text.ToString().Split('\t', 2);
                string name = System.Text.RegularExpressions.Regex.Replace(fields[0], "&&|&", m => m.Length == 2 ? "&" : "").Trim();
                string path = Join(parent, name);
                if (name.Length > 0 && fields.Length == 2) Add(path, fields[1]);
                var child = GetSubMenu(menu, index);
                if (child != IntPtr.Zero) ReadNative(child, path, depth + 1);
            }
        }

        internal void ReadAutomation(AutomationElement element, string parent, bool menuContext, int depth)
        {
            Visit(depth);
            if (element.Current.ProcessId != pid) return;
            var type = element.Current.ControlType;
            if (type == ControlType.MenuItem && menuContext)
            {
                string label = element.Current.Name;
                if (label.Length > 768) return;
                var fields = label.Split('\t', 2);
                string name = fields[0].Trim();
                parent = Join(parent, name);
                string key = element.Current.AcceleratorKey;
                if (string.IsNullOrWhiteSpace(key) && fields.Length == 2) key = fields[1];
                if (name.Length > 0) Add(parent, key);
            }
            else if (type == ControlType.Menu || type == ControlType.MenuBar) menuContext = true;
            else if (type != ControlType.Window && type != ControlType.Pane && type != ControlType.Group &&
                type != ControlType.Custom && type != ControlType.ToolBar) return;
            // Outside menus read structure, type and PID only. Never read edit or document contents.
            var walker = TreeWalker.RawViewWalker;
            for (var child = walker.GetFirstChild(element); child != null; child = walker.GetNextSibling(child))
                ReadAutomation(child, parent, menuContext, depth + 1);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetMenu(IntPtr hwnd);
    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] private static extern IntPtr GetSubMenu(IntPtr menu, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMenuString(
        IntPtr menu, uint item, StringBuilder text, int capacity, uint flags);
}
