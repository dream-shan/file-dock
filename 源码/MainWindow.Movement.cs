using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace FileDockDemo;

public partial class MainWindow
{
    private bool _movingPanel;

    private void InitializeMovement()
    {
        // DWM draws desktop acrylic only behind a non-layered top-level HWND.
        AllowsTransparency = !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
        if (!AllowsTransparency)
        {
            ResizeMode = ResizeMode.NoResize; // The panel has its own resize grip.
            SourceInitialized += (_, _) => ApplyGlassMaterial();
        }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [StructLayout(LayoutKind.Sequential)]
    private struct FrameMargins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref FrameMargins margins);
    internal bool HasDesktopAcrylic { get; private set; }

    private void ApplyGlassMaterial()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (AllowsTransparency || handle == IntPtr.Zero || HwndSource.FromHwnd(handle)?.CompositionTarget is not { } target) return;
        int dark = IsDarkTheme ? 1 : 0;
        int desktopAcrylic = 3; // DWMSBT_TRANSIENTWINDOW
        var margins = new FrameMargins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        HasDesktopAcrylic = DwmExtendFrameIntoClientArea(handle, ref margins) >= 0 &&
            DwmSetWindowAttribute(handle, 38, ref desktopAcrylic, sizeof(int)) >= 0;
        target.BackgroundColor = HasDesktopAcrylic ? Colors.Transparent : ThemePalette.Brush("Surface").Color;
        ApplyBlankTransparency();
    }

    private void ApplyNativeCorners(IntPtr handle)
    {
        // The custom region owns the complete silhouette, including tab cutouts.
        int round = 1, noBorder = unchecked((int)0xFFFFFFFE), noFrame = 1;
        if (AllowsTransparency) _ = DwmSetWindowAttribute(handle, 2, ref noFrame, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 34, ref noBorder, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 33, ref round, sizeof(int)); // Older Windows may ignore this preference.
    }

    internal static double DockedLeft(string side,Rect area,double width)=>side=="Left"?area.Left:Math.Max(area.Left,area.Right-width);
    internal static bool InferDocked(AppState state,Rect area,double width)=>state.PanelDocked ?? (!state.PanelLeft.HasValue || Math.Abs(state.PanelLeft.Value-DockedLeft(state.DockSide,area,width))<=80);
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
        State.PanelScreen = _screen.DeviceName;
        State.DockTop = Top + 30;
        Rect area = WorkArea();
        State.DockSide = Left + Width / 2 < area.Left + area.Width / 2 ? "Left" : "Right";
        State.PanelDocked=Math.Abs(Left-DockedLeft(State.DockSide,area,Width))<=16;
        if(State.PanelDocked==true)Left=DockedLeft(State.DockSide,area,Width);
        State.PanelLeft=Left;State.PanelTop=Top;
        PositionDock(); Persist(); QueueDesktopPresence();
    }
}
