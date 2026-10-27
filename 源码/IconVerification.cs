using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileDockDemo;

internal static class IconVerification
{
    internal static void Run(MainWindow window, Action<bool, string> check)
    {
        foreach (bool light in new[] { false, true })
        {
            using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/FileDock-" + (light ? "light" : "dark") + ".ico"))!.Stream;
            var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            check(decoder.Frames.Select(f => f.PixelWidth).OrderBy(n => n).SequenceEqual(new[] {16,20,24,32,40,48,64,128,256}),
                $"{(light ? "Light" : "Dark")} icon embeds native sizes from 16 to 256 pixels");
            bool transparent = true, contrasted = true;
            foreach (var frame in decoder.Frames)
            {
                var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                int size = bitmap.PixelWidth, stride = size * 4;
                var pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0);
                byte Alpha(double x, double y) => pixels[(int)(y * size) * stride + (int)(x * size) * 4 + 3];
                // Exterior, fold opening, and the gap between the dock and page.
                transparent &= Alpha(0, 0) == 0 && Alpha(.72, .28) < 64 && Alpha(.28, .6) == 0;
                int p = (int)(.5 * size) * stride + (int)(.6 * size) * 4;
                contrasted &= pixels[p + 3] == 255 && (light ? pixels[p] > 200 : pixels[p] < 160);
            }
            check(transparent && contrasted, $"{(light ? "Light" : "Dark")} icon has no background tile and keeps a solid, contrasted glyph at every size");
            using var small = MainWindow.LoadMarkIcon(light, new System.Drawing.Size(16, 16));
            check(small.Width == 16 && small.Height == 16, $"{(light ? "Light" : "Dark")} tray icon loads at native small-icon resolution");
        }
        check(window.Icon != null && window.AppLogo.Source is BitmapSource, "Window and panel header use the new embedded file-dock identity");
    }
}
