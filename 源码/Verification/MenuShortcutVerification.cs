using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FileDockDemo;

internal static class MenuShortcutVerification
{
    internal static int RunStandalone()
    {
        int exitCode = 0;
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await Run("", (ok, label) =>
                {
                    Console.WriteLine((ok ? "PASS " : "FAIL ") + label);
                    if (!ok) exitCode = 1;
                });
            }
            catch (Exception error) { Console.Error.WriteLine(error); exitCode = 1; }
            finally { dispatcher.InvokeShutdown(); }
        }));
        Dispatcher.Run();
        return exitCode;
    }

    internal static async Task Run(string directory, Action<bool,string> check)
    {
        var command = new MenuItem { Header = "校验菜单", InputGestureText = "Ctrl+Shift+F9" };
        AutomationProperties.SetAcceleratorKey(command, "Ctrl+Shift+F9");
        var accessOnly = new MenuItem { Header = "访问键" };
        AutomationProperties.SetAccessKey(accessOnly, "Alt+A");
        var menu = new Menu();
        menu.Items.Add(command);
        menu.Items.Add(accessOnly);
        var unrelated = new TextBox { Text = "Private fixture text" };
        AutomationProperties.SetAcceleratorKey(unrelated, "Ctrl+F11");
        var layout = new StackPanel();
        layout.Children.Add(menu);
        layout.Children.Add(unrelated);
        var window = new Window {
            Content = layout, Width = 260, Height = 120, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
            Title = "FileDock menu verification"
        };
        IntPtr hwnd = IntPtr.Zero, native = IntPtr.Zero;
        string executable = Environment.ProcessPath!;
        try
        {
            window.Show();
            window.UpdateLayout();
            hwnd = new WindowInteropHelper(window).Handle;
            native = CreateMenu();
            IntPtr submenu = CreatePopupMenu();
            if (native == IntPtr.Zero || submenu == IntPtr.Zero) throw new InvalidOperationException("Cannot create fixture menu.");
            if (!AppendMenu(native, 0x10, submenu, "&File") || !AppendMenu(submenu, 0, new IntPtr(1), "&Open\tCtrl+O") ||
                !AppendMenu(submenu, 0, new IntPtr(2), "Save && Close\tCtrl+Shift+S") ||
                !AppendMenu(submenu, 0, new IntPtr(3), "&Other") || !SetMenu(hwnd, native))
                throw new InvalidOperationException("Cannot install fixture menu.");

            var rows = await MenuShortcutReader.ReadAsync(hwnd, executable, default);
            check(rows.Count == 3 && rows.Any(r => r.Command == "校验菜单" && r.Key == "Ctrl+Shift+F9") &&
                rows.Any(r => r.Command == "File / Open" && r.Key == "Ctrl+O") &&
                rows.Any(r => r.Command == "File / Save & Close" && r.Key == "Ctrl+Shift+S"),
                "Hybrid window retains both framework and native menus, paths and accelerator text");
            bool rejected = false;
            try { await MenuShortcutReader.ReadAsync(hwnd, executable + ".other.exe", default); }
            catch (InvalidOperationException) { rejected = true; }
            check(rejected, "Selected window must belong to the expected executable");

            if (!SetMenu(hwnd, IntPtr.Zero)) throw new InvalidOperationException("Cannot detach fixture menu.");
            rows = await MenuShortcutReader.ReadAsync(hwnd, executable, default);
            check(rows.Count == 1 && rows[0].Command == "校验菜单" && rows[0].Key == "Ctrl+Shift+F9",
                "UI Automation reads only menu accelerator keys, excluding access keys and ordinary controls");

            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            bool cancelled = false;
            try { await MenuShortcutReader.ReadAsync(hwnd, executable, cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "Cancelled menu scan stops before starting the worker");
        }
        finally
        {
            if (hwnd != IntPtr.Zero) SetMenu(hwnd, IntPtr.Zero);
            if (native != IntPtr.Zero) DestroyMenu(native);
            window.Close();
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr CreateMenu();
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr item, string text);
    [DllImport("user32.dll")] private static extern bool SetMenu(IntPtr hwnd, IntPtr menu);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
}
