using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileDockDemo;

internal static class FeatureVerification
{
    internal static async Task Run(MainWindow window, string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "图片与复制测试"); Directory.CreateDirectory(folder);
        string imagePath = Path.Combine(folder, "宽图.png"), tallPath = Path.Combine(folder, "长图.png"), badPath = Path.Combine(folder, "损坏.png");
        void Save(string path, int width, int height)
        {
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 48; pixels[i + 1] = 160; pixels[i + 2] = 230; pixels[i + 3] = 255; }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path); encoder.Save(stream);
        }
        Save(imagePath, 1600, 900); Save(tallPath, 100, 4000); File.WriteAllText(badPath, "not an image");
        var thumbnail = await ImageLoader.LoadAsync(imagePath, false, CancellationToken.None);
        check(thumbnail != null && thumbnail.IsFrozen && thumbnail.PixelWidth <= 96 && thumbnail.PixelHeight <= 96, "Thumbnails decode small and can cross worker/UI threads");
        var tall = await ImageLoader.LoadAsync(tallPath, false, CancellationToken.None);
        check(tall != null && tall.PixelWidth <= 96 && tall.PixelHeight <= 96, "Very tall images also obey both thumbnail bounds");
        var preview = await ImageLoader.LoadAsync(imagePath, true, CancellationToken.None);
        check(preview != null && preview.PixelWidth <= 720 && preview.PixelHeight <= 560 && preview.PixelWidth > 96, "Hover preview has a separate bounded resolution");
        check(await ImageLoader.LoadAsync(badPath, false, CancellationToken.None) == null, "Corrupt image falls back safely");
        using (File.Open(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        check(true, "Thumbnail/preview decoding releases source file handles");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); bool stopped = false;
            try { await ImageLoader.LoadAsync(imagePath, false, canceled.Token); } catch (OperationCanceledException) { stopped = true; }
            check(stopped, "Recycled/offscreen thumbnail requests can be canceled");
        }
        for (int i = 0; i < 70; i++)
        {
            string path = Path.Combine(folder, "缓存-" + i + ".png"); File.Copy(imagePath, path);
            await ImageLoader.LoadAsync(path, false, CancellationToken.None);
        }
        check(ImageLoader.CacheCount <= 64, "Thumbnail cache stays bounded after browsing many images");
        window.GoHome(); window.SearchBox.Clear();
        window.AddPins(new[] { imagePath, tallPath });
        var first = window.PinsList.Items.Cast<EntryView>().First(e => e.Path == imagePath);
        var second = window.PinsList.Items.Cast<EntryView>().First(e => e.Path == tallPath);
        window.SelectFile(first, true); window.SelectFile(second, true);
        check(window.SelectedFileCount == 2 && first.IsChosen && second.IsChosen, "Ctrl selection highlights multiple entries without opening them");
        window.ApplyFilters();
        check(window.SelectedFileCount == 2 && window.PinsList.Items.Cast<EntryView>().Count(e => e.IsChosen) == 2, "Selection survives metadata/UI refresh");
        window.SelectFile(second, true); check(window.SelectedFileCount == 1, "Ctrl clicking a selected file removes it from the group");
        var data = MainWindow.BuildFileData(new[] { imagePath, tallPath });
        check(data.GetFileDropList()!.Count == 2 && data.GetFileDropList()![0] == imagePath, "Native transfer exposes complete Unicode file paths as a file list");
        check(data.GetData("Preferred DropEffect") is MemoryStream effect && BitConverter.ToInt32(effect.ToArray()) == 1, "Native transfer requests copy and keeps original files");
        await window.NavigateToFolderAsync(folder);
        check(window.SelectedFileCount == 0, "Folder navigation clears prior selection");
        var dock = window.DockEntry;
        check(NativeMethods.GetWindowDisplayAffinity(dock.Handle, out uint affinity) && affinity == 0, "Edge remains capturable for remote desktop visibility");
        check(NativeMethods.GetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, out uint panelAffinity) && panelAffinity == 0, "Deliberately expanded panel remains capturable");
        window.ExpandPanel(); window.BeginCaptureShortcut();
        check(window.IsVisible, "Screenshot shortcut preserves the deliberately expanded panel");
        window.CollapsePanel(); window.BeginCaptureShortcut();
        check(!dock.IsVisible && window.UpdateCaptureVisibility(), "Screenshot shortcut hides entry before capture starts");
        await Task.Delay(1650);
        check(!window.UpdateCaptureVisibility(), "Unused screenshot shortcut expires instead of stranding a hidden entry");
        await Task.Delay(100);
        check(dock.IsVisible, "Entry returns after screenshot-start timeout");
    }
}
