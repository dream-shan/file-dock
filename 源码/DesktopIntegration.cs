using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace FileDockDemo;

internal static class NativeMethods
{
    internal const int HotkeyId = 0xD0C;
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out RectNative rect);
    [DllImport("user32.dll")] internal static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] internal static extern IntPtr GetDesktopWindow();
    [StructLayout(LayoutKind.Sequential)] internal struct RectNative { public int Left, Top, Right, Bottom; }

    internal static bool ForegroundIsFullscreen(IntPtr ownWindow, IntPtr dockWindow)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == ownWindow || foreground == dockWindow || foreground == GetDesktopWindow() || foreground == GetShellWindow()) return false;
        if (!GetWindowRect(foreground, out var rect)) return false;
        var screen = System.Windows.Forms.Screen.FromHandle(foreground);
        return rect.Left <= screen.Bounds.Left && rect.Top <= screen.Bounds.Top && rect.Right >= screen.Bounds.Right && rect.Bottom >= screen.Bounds.Bottom;
    }
}

internal sealed class DockTabWindow : Window
{
    private readonly MainWindow _owner;
    private Point _start;
    private bool _moved;

    internal DockTabWindow(MainWindow owner)
    {
        _owner = owner;
        Title = "文件坞入口"; Width = 34; Height = 76;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        var body = new Border { CornerRadius = new CornerRadius(13), Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(212, 223, 239)), BorderThickness = new Thickness(1), Margin = new Thickness(1), Cursor = Cursors.Hand, ToolTip = "文件坞 · 点击展开\n拖动调整位置\nCtrl + Alt + Space" };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = "▦", FontSize = 26, Foreground = new SolidColorBrush(Color.FromRgb(41, 111, 235)), HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = "‹", FontSize = 17, Foreground = Brushes.SlateGray, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -5, 0, 0) });
        body.Child = stack; Content = body;
        body.MouseLeftButtonDown += (_, e) => { _start = e.GetPosition(this); _moved = false; CaptureMouse(); e.Handled = true; };
        MouseMove += (_, e) =>
        {
            if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _start.X) + Math.Abs(p.Y - _start.Y) > 4) _moved = true;
            if (_moved) { Left += p.X - _start.X; Top += p.Y - _start.Y; }
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!IsMouseCaptured) return;
            ReleaseMouseCapture();
            if (_moved) owner.DockMoved(Left, Top); else owner.ExpandPanel();
            e.Handled = true;
        };
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "展开文件坞" }; open.Click += (_, _) => owner.ExpandPanel();
        var quit = new MenuItem { Header = "退出" }; quit.Click += (_, _) => owner.Quit();
        menu.Items.Add(open); menu.Items.Add(quit); ContextMenu = menu;
    }
}
