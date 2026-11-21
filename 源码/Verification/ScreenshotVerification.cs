using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using FileDockDemo.Features.Screenshot;

namespace FileDockDemo;

internal static class ScreenshotVerification
{
    internal static void Run(Action<bool, string> check)
    {
        var desktop = new Rectangle(-1920, -100, 4480, 1540);
        var area = ScreenshotSession.Selection(desktop, new Point(20, 50), new Point(-30, -50));
        check(area == new Rectangle(1890, 50, 51, 101), "Reverse cross-monitor drag uses physical pixels relative to the virtual desktop");
        check(ScreenshotSession.Selection(desktop, desktop.Location, desktop.Location).IsEmpty, "A click alone never captures a region");
        var edge = ScreenshotSession.Selection(desktop, new Point(3000, 2000), new Point(2558, 1438));
        check(edge == new Rectangle(4478, 1538, 2, 2), "Selection includes the last display pixel and clips to desktop bounds");
        using var image = new Bitmap(8, 7, PixelFormat.Format24bppRgb);
        image.SetPixel(3, 2, Color.Crimson);
        using var crop = image.Clone(new Rectangle(3, 2, 2, 3), PixelFormat.Format24bppRgb);
        using var data = ScreenshotSession.EncodePng(crop);
        using var decoded = new Bitmap(data);
        check(decoded.Size == crop.Size && decoded.GetPixel(0, 0).ToArgb() == Color.Crimson.ToArgb(), "Screenshot PNG preserves exact crop dimensions and pixels");

        // Send messages to this fixture's window only. Do not move the user's
        // pointer, capture their actual desktop, or read/change their clipboard.
        using var sample = new Bitmap(320, 180, PixelFormat.Format24bppRgb);
        sample.SetPixel(20, 18, Color.OrangeRed);
        using var overlay = new ScreenshotOverlay(sample, new Rectangle(Point.Empty, sample.Size));
        IntPtr window = overlay.Handle;
        SendMessage(window, 0x0201, 1, Position(45, 32));
        SendMessage(window, 0x0200, 1, Position(20, 18));
        SendMessage(window, 0x0202, 0, Position(20, 18));
        string saved = Path.Combine(Path.GetTempPath(), $"filedock-screenshot-test-{Guid.NewGuid():N}.png");
        try
        {
            overlay.SaveSelection(saved);
            using var pixels = new Bitmap(saved);
            check(pixels.Size == new Size(26, 15) && pixels.GetPixel(0, 0).ToArgb() == Color.OrangeRed.ToArgb()
                && overlay.Result == "截图已保存", "Screenshot overlay saves selected synthetic pixels through its production PNG path");
        }
        finally { if (File.Exists(saved)) File.Delete(saved); }

        using var cancel = new ScreenshotOverlay(sample, new Rectangle(Point.Empty, sample.Size));
        SendMessage(cancel.Handle, 0x0204, 2, Position(12, 10));
        check(cancel.Result == "已取消截图" && cancel.IsDisposed, "Right-click cancels a native screenshot overlay without touching the clipboard");

        var previous = new System.Windows.Window();
        bool restored = false;
        try
        {
            ScreenshotSession.Run(previous, () => throw new OperationCanceledException(), () => restored = true);
        }
        catch (OperationCanceledException) { }
        finally { previous.Close(); }
        check(restored, "Screenshot failure during desktop preparation restores its caller's state");
    }

    private static IntPtr Position(int x, int y) => (IntPtr)((y << 16) | x);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr w, IntPtr l);
}
