using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FileDockDemo.Features.WindowPinning;
using Microsoft.Win32.SafeHandles;

namespace FileDockDemo;

public partial class MainWindow
{
    private bool SelectingAppShortcutTarget => _windowPicker != null && ActiveModuleDialog is AppShortcutCatalogDialog;

    private void PickAppShortcutTarget(Action<string, string> selected, Action cancelled)
    {
        var dialog = ActiveModuleDialog;
        if (dialog == null || !IsVisible || _windowPicker != null || _exitSaving || _quitting ||
            _dragging || _movingPanel || MemoryPaused || UpdateCaptureVisibility())
        {
            cancelled();
            return;
        }

        var focus = Keyboard.FocusedElement as UIElement;
        WindowPicker? picker = null;

        void Complete(IntPtr window)
        {
            if (!ReferenceEquals(_windowPicker, picker)) return;
            _windowPicker = null;
            if (_quitting || _exitSaving || Dispatcher.HasShutdownStarted ||
                !ReferenceEquals(ActiveModuleDialog, dialog)) return;

            // WindowPicker first hides its overlay, then resolves the window under the cursor.
            // Read the target before showing the dock, which may occupy that same location.
            var identity = AppShortcutTarget.Read(window);
            ShowPanel(activate: true, reposition: false);
            if (IsVisible)
            {
                if (focus is { IsVisible: true, IsEnabled: true }) focus.Focus();
                else dialog.FocusInitial();
            }
            else QueueDockRaise();

            if (identity is { } app) selected(app.Name, app.Path);
            else
            {
                if (window != IntPtr.Zero) SetStatus("无法识别该窗口的软件路径，请从已固定应用选择。", true);
                cancelled();
            }
        }

        picker = new WindowPicker(Complete);
        if (picker.Content is TextBlock hint) hint.Text = "点击要登记的软件窗口 · Esc 取消";
        _windowPicker = picker;
        picker.Closed += (_, _) =>
        {
            // The selected-window callback runs just after Close. A delayed cancel
            // gives it the first chance to finish without revealing the dock early.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Complete(IntPtr.Zero));
        };

        try
        {
            CollapsePanelCore(manual: false);
            _dock?.Hide();
            picker.Show();
        }
        catch (InvalidOperationException)
        {
            picker.Close();
            Complete(IntPtr.Zero);
        }
    }
}

internal static class AppShortcutTarget
{
    internal static (string Name, string Path)? Read(IntPtr window)
    {
        if (window == IntPtr.Zero || !PinNative.IsWindowVisible(window) ||
            new PinNative().Inspect(window) is not { } target) return null;

        using var process = OpenProcess(0x1000, false, target.Process); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid) return null;
        var path = new StringBuilder(32768);
        uint length = (uint)path.Capacity;
        if (!QueryFullProcessImageName(process, 0, path, ref length)) return null;
        if (PinNative.GetWindowThreadProcessId(window, out uint current) != target.Thread ||
            current != target.Process || !PinNative.IsWindowVisible(window)) return null;

        string executable = path.ToString();
        if (!Path.IsPathFullyQualified(executable)) return null;
        string name = Path.GetFileNameWithoutExtension(executable);
        // Hosted Store windows can share this frame process across unrelated apps.
        if (string.IsNullOrWhiteSpace(name) || target.ClassName == "ApplicationFrameWindow" &&
            string.Equals(name, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) return null;
        return (name, executable);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process, uint flags, StringBuilder imageName, ref uint length);
}
