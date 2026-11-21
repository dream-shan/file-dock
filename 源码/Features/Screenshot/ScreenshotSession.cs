using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace FileDockDemo.Features.Screenshot;

internal static class ScreenshotSession
{
    private sealed record DialogOwner(IntPtr Handle) : Forms.IWin32Window;

    internal static string Run(System.Windows.Window owner, Action beforeCapture, Action afterCapture)
    {
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        try
        {
            beforeCapture();
            NativeMethods.DwmFlush();
            Rectangle desktop = Forms.SystemInformation.VirtualScreen;
            if (desktop.Width <= 0 || desktop.Height <= 0 || (long)desktop.Width * desktop.Height > 64_000_000)
                throw new InvalidOperationException("当前桌面尺寸无法创建截图。");

            using var frame = new Bitmap(desktop.Width, desktop.Height, PixelFormat.Format24bppRgb);
            Capture(frame, desktop);
            using var selection = new ScreenshotOverlay(frame, desktop);
            selection.ShowDialog(new DialogOwner(ownerHandle));
            if (selection.Failure is { } failure) throw new InvalidOperationException(failure);
            return selection.Result;
        }
        finally { afterCapture(); }
    }

    internal static Rectangle Selection(Rectangle desktop, Point anchor, Point pointer)
    {
        int startX = Math.Clamp(anchor.X, desktop.Left, desktop.Right - 1);
        int startY = Math.Clamp(anchor.Y, desktop.Top, desktop.Bottom - 1);
        int endX = Math.Clamp(pointer.X, desktop.Left, desktop.Right - 1);
        int endY = Math.Clamp(pointer.Y, desktop.Top, desktop.Bottom - 1);
        if (startX == endX && startY == endY) return Rectangle.Empty;
        return new Rectangle(Math.Min(startX, endX) - desktop.X, Math.Min(startY, endY) - desktop.Y,
            Math.Abs(startX - endX) + 1, Math.Abs(startY - endY) + 1);
    }

    internal static MemoryStream EncodePng(Bitmap image)
    {
        var stream = new MemoryStream();
        try
        {
            image.Save(stream, ImageFormat.Png);
            stream.Position = 0;
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static void Capture(Bitmap destination, Rectangle desktop)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new InvalidOperationException("无法读取当前桌面。");
        try
        {
            using var graphics = Graphics.FromImage(destination);
            IntPtr target = graphics.GetHdc();
            try
            {
                const uint copyVisiblePixels = 0x00CC0020 | 0x40000000;
                if (!BitBlt(target, 0, 0, desktop.Width, desktop.Height, screen, desktop.X, desktop.Y, copyVisiblePixels))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "截图失败。");
            }
            finally { graphics.ReleaseHdc(target); }
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(IntPtr target, int x, int y,
        int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
}
