using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace FileDockDemo;

public partial class MainWindow
{
    private bool _movingPanel;

    private void InitializeMovement()
    {
        WindowChrome.SetWindowChrome(this, new WindowChrome {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(20),
            UseAeroCaptionButtons = false
        });
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    private void ApplyNativeCorners(IntPtr handle)
    {
        int round = 2;
        _ = DwmSetWindowAttribute(handle, 33, ref round, sizeof(int)); // Older Windows may ignore this preference.
    }

    internal static Rect ConstrainPanel(Rect desired, Rect area) => new Rect(
        Math.Clamp(desired.Left, area.Left, Math.Max(area.Left, area.Right - desired.Width)),
        Math.Clamp(desired.Top, area.Top, Math.Max(area.Top, area.Bottom - desired.Height)),
        desired.Width, desired.Height);

    internal static bool IsMovementSurface(DependencyObject? source)
    {
        for (var p = source; p != null; p = p is Visual ? VisualTreeHelper.GetParent(p) : null)
        {
            if (p is ButtonBase or TextBoxBase or Selector or ScrollViewer or PerformanceCard) return false;
            if (p is FrameworkElement element && element.Tag as string == "PanelDrag") return true;
        }
        return false;
    }

    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in VisualChildren(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private void MovePanelFromSurface(object sender, MouseButtonEventArgs e)
    {
        if (_captureInteractionHeld || _movingPanel || e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed ||
            !IsMovementSurface(e.OriginalSource as DependencyObject)) return;
        var enabled = VisualChildren(this).OfType<FrameworkElement>()
            .Where(element => ToolTipService.GetIsEnabled(element)).ToArray();
        _movingPanel = true;
        e.Handled = true;
        try
        {
            foreach (var element in enabled)
            {
                ToolTipService.SetIsEnabled(element, false);
                if (element.ToolTip is ToolTip tip) tip.IsOpen = false;
            }
            DragMove();
        }
        catch (InvalidOperationException) { }
        finally
        {
            foreach (var element in enabled) ToolTipService.SetIsEnabled(element, true);
            _movingPanel = false;
        }
        _screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        var position = ConstrainPanel(new Rect(Left, Top, ActualWidth, ActualHeight), WorkArea());
        Left = position.Left; Top = position.Top;
        State.PanelLeft = Left; State.PanelTop = Top; State.PanelScreen = _screen.DeviceName;
        State.DockTop = Top + 30;
        Rect area = WorkArea();
        State.DockSide = Left + Width / 2 < area.Left + area.Width / 2 ? "Left" : "Right";
        PositionDock(); Persist(); QueueDesktopPresence();
    }
}
