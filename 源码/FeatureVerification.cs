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
        await window.WaitForSavesAsync();
        var first = window.PinsList.Items.Cast<EntryView>().First(e => e.Path == imagePath);
        var second = window.PinsList.Items.Cast<EntryView>().First(e => e.Path == tallPath);
        window.SelectFile(first, true); window.SelectFile(second, true);
        check(window.SelectedFileCount == 2 && first.IsChosen && second.IsChosen, "Ctrl selection highlights multiple entries without opening them");
        check(window.CopySelectionButton.IsVisible,"Selected files expose the copy action in the footer");
        window.SelectPage("Performance",false);
        check(!window.CopySelectionButton.IsVisible,"File copy action is hidden on the performance page");
        window.SelectPage("Files",false);
        check(window.CopySelectionButton.IsVisible && window.SelectedFileCount==2,"Returning to files restores the selected group and copy action");
        window.ApplyFilters();
        check(window.SelectedFileCount == 2 && window.PinsList.Items.Cast<EntryView>().Count(e => e.IsChosen) == 2, "Selection survives metadata/UI refresh");
        window.SelectFile(second, true); check(window.SelectedFileCount == 1, "Ctrl clicking a selected file removes it from the group");
        var data = MainWindow.BuildFileData(new[] { imagePath, tallPath });
        check(data.GetFileDropList()!.Count == 2 && data.GetFileDropList()![0] == imagePath, "Native transfer exposes complete Unicode file paths as a file list");
        check(data.GetData("Preferred DropEffect") is MemoryStream effect && BitConverter.ToInt32(effect.ToArray()) == 1, "Native transfer requests copy and keeps original files");
        first = window.PinsList.Items.Cast<EntryView>().First(e => e.Path == imagePath);
        second = window.PinsList.Items.Cast<EntryView>().First(e => e.Path == tallPath);
        window.SelectFile(second, true);
        window.BeginFileSelection(first, false);
        check(window.SelectedFileCount == 2, "Pressing a selected group member preserves the whole group for dragging");
        check(!window.CompleteFileClick(first) && window.SelectedFileCount == 1 && first.IsChosen && !second.IsChosen,
            "Releasing a group member without dragging selects only it without opening");
        window.BeginFileSelection(second, false);
        check(window.SelectedFileCount == 1 && second.IsChosen && !first.IsChosen, "Pressing an unselected file replaces the old selection before dragging");
        check(window.CompleteFileClick(second), "Ordinary single file clicks retain click-to-open behavior");
        window.BeginFileSelection(first, true);
        check(!window.CompleteFileClick(first) && window.SelectedFileCount == 2, "Ctrl clicks extend selection without opening files");
        DataObject? copied = null;
        await window.CopySelectedFilesAsync(writeClipboard: value => copied = value);
        check(window.SelectedFileCount == 0 && !first.IsChosen && !second.IsChosen && !window.CopySelectionButton.IsVisible,
            "Successful copying clears highlights and hides the copy action");
        check(copied?.GetFileDropList()?.Count == 2, "Clearing selection leaves the copied file payload intact for pasting");
        window.BeginFileSelection(second, true);
        await window.CopySelectedFilesAsync(writeClipboard: value => copied = value);
        check(copied?.GetFileDropList()?.Cast<string>().SequenceEqual(new[]{tallPath}) == true && window.SelectedFileCount == 0,
            "The next Ctrl-selected copy batch contains no files from the previous batch");
        window.SelectFile(first, false);
        await window.CopySelectedFilesAsync(writeClipboard: _ => throw new InvalidOperationException("test clipboard failure"));
        check(window.SelectedFileCount == 1 && first.IsChosen, "Failed copying preserves selection for retry");
        await window.CopySelectedFilesAsync(writeClipboard: _ => window.SelectFile(second, false));
        check(window.SelectedFileCount == 1 && second.IsChosen && !first.IsChosen, "A completed image copy never clears a newer selection");
        window.SelectFile(first, true);
        await window.CopySelectedFilesAsync(writeClipboard: _ => { window.ClearFileSelection(); window.SelectFile(first, false); window.SelectFile(second, true); });
        check(window.SelectedFileCount == 2, "Reselecting the same paths during copying is treated as a new selection");
        int attempts = 0;
        DataObject? delayedPayload = null;
        var delayedCopy = window.CopySelectedFilesAsync(writeClipboard: value => {
            if (attempts++ == 0) throw new System.Runtime.InteropServices.COMException("Clipboard temporarily busy");
            delayedPayload = value;
        });
        check(!delayedCopy.IsCompleted, "Clipboard contention leaves the original copy pending for a real asynchronous retry");
        window.BeginFileSelection(second, true);
        check(window.SelectedFileCount == 1 && second.IsChosen && !first.IsChosen,
            "Starting a Ctrl selection during a pending copy starts a clean batch");
        window.BeginFileSelection(first, true);
        check(window.SelectedFileCount == 2, "Further Ctrl clicks extend the new batch instead of resetting it");
        window.SelectFile(first, true);
        await delayedCopy;
        check(delayedPayload?.GetFileDropList()?.Count == 2 && window.SelectedFileCount == 1 && second.IsChosen && !first.IsChosen,
            "The pending copy keeps its original payload and never clears or merges the newer selection");
        await window.CopySelectedFilesAsync(writeClipboard: value => copied = value);
        check(copied?.GetFileDropList()?.Cast<string>().SequenceEqual(new[]{tallPath}) == true,
            "Copying the newer batch replaces the old clipboard payload without appending files");

        window.SelectFile(first, false); window.SelectFile(second, true);
        long dragRevision = window.FileSelectionRevision;
        var dragPayload = MainWindow.BuildFileData(new[]{imagePath,tallPath});
        window.CompleteFileDrag(DragDropEffects.Copy, false, dragRevision);
        check(window.SelectedFileCount == 0 && !first.IsChosen && !second.IsChosen && dragPayload.GetFileDropList()!.Count == 2,
            "A successful external drag clears selection without changing the transferred file list");
        window.BeginFileSelection(second, true);
        check(window.SelectedFileCount == 1 && !first.IsChosen, "The next Ctrl drag batch excludes the previous files");
        dragRevision = window.FileSelectionRevision;
        window.CompleteFileDrag(DragDropEffects.None, false, dragRevision);
        check(window.SelectedFileCount == 1 && second.IsChosen, "Cancelled or rejected external drops preserve selection for retry");
        window.CompleteFileDrag(DragDropEffects.Copy, true, dragRevision);
        check(window.SelectedFileCount == 1, "Internal favorites reordering does not end a file transfer batch");
        window.SelectFile(first, false);
        window.CompleteFileDrag(DragDropEffects.Copy, false, dragRevision);
        check(window.SelectedFileCount == 1 && first.IsChosen, "An older drag completion never clears a newer selection");
        window.SelectFile(second, true);
        window.PinsList.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = new System.Windows.Controls.Primitives.ScrollBar() });
        check(window.SelectedFileCount == 2, "Scrollbar clicks do not clear the selected group");
        window.PinsList.SelectedItem = first;
        window.PinsList.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = window.PinsList });
        check(window.SelectedFileCount == 0 && window.PinsList.SelectedItem == null && !first.IsChosen && !second.IsChosen,
            "Clearing selection also removes the native list selection used by keyboard copy");
        await window.NavigateToFolderAsync(folder);
        check(window.SelectedFileCount == 0, "Folder navigation clears prior selection");
        var dock = window.DockEntry;
        string originalSide = window.State.DockSide;
        double originalTop = window.State.DockTop;
        var area = System.Windows.Forms.Screen.FromHandle(dock.Handle).WorkingArea;
        double scale = dock.ScaleFactor;
        double left = area.Left / scale, right = area.Right / scale - dock.Width;
        double top = area.Top / scale + 100;
        double ArrowOffset(string name)
        {
            using var bitmap = new System.Drawing.Bitmap(dock.ClientSize.Width,dock.ClientSize.Height);
            dock.DrawToBitmap(bitmap,new System.Drawing.Rectangle(System.Drawing.Point.Empty,bitmap.Size));
            bitmap.Save(Path.Combine(root,name+".png"));
            double weightedX=0, weight=0, center=bitmap.Width/2d;
            for(int y=bitmap.Height/2-1;y<=bitmap.Height/2+1;y++)
                for(int x=Math.Max(0,(int)(center-6*scale));x<Math.Min(bitmap.Width,center+6*scale);x++)
                {
                    var pixel=bitmap.GetPixel(x,y); double blue=Math.Max(0,pixel.B-pixel.R-30);
                    weightedX+=x*blue; weight+=blue;
                }
            return weight > 0 ? weightedX/weight-center : double.NaN;
        }
        try
        {
            window.DockMoved(right,top); dock.Show();
            check(window.State.DockSide == "Right" && ArrowOffset("entry-right") < -scale,
                "Right-docked entry paints an inward left-pointing arrow");
            window.DockMoved(left,top);
            check(window.State.DockSide == "Left" && ArrowOffset("entry-left") > scale,
                "Dragging the entry left paints an inward right-pointing arrow");
            // No coordinate change: only an explicit repaint can invalidate this update.
            int invalidations=0;
            System.Windows.Forms.InvalidateEventHandler onInvalidated=(_,_)=>invalidations++;
            dock.Invalidated+=onInvalidated;
            try { window.DockMoved(left,top); }
            finally { dock.Invalidated-=onInvalidated; }
            check(invalidations > 0, "Dock positioning invalidates the cached entry surface even without a size or position change");
            await window.WaitForSavesAsync();check(window.Storage.Load().DockSide == "Left", "Dragging to the left persists its side for restart");
            window.DockMoved(right,top);
            await window.WaitForSavesAsync();check(window.Storage.Load().DockSide == "Right" && ArrowOffset("entry-right-restored") < -scale,
                "Dragging back right restores the arrow and saves the new side");
        }
        finally
        {
            window.State.DockSide=originalSide; window.State.DockTop=originalTop;
            window.DockMoved(originalSide == "Left" ? left : right, originalTop < 0 ? top : originalTop);
            window.State.DockTop=originalTop; window.Persist(); dock.Hide();
        }
        check(NativeMethods.GetWindowDisplayAffinity(dock.Handle, out uint affinity) && affinity == 0, "Edge remains capturable for remote desktop visibility");
        check(NativeMethods.GetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, out uint panelAffinity) && panelAffinity == 0, "Deliberately expanded panel remains capturable");
        window.ExpandPanel(); window.BeginCaptureShortcut();
        check(window.IsVisible, "Screenshot shortcut preserves the deliberately expanded panel");
        window.CancelCaptureShortcutPending();
        window.CollapsePanel(); window.BeginCaptureShortcut();
        check(!dock.IsVisible && window.UpdateCaptureVisibility(), "Screenshot shortcut hides entry before capture starts");
        await Task.Delay(1650);
        check(!window.UpdateCaptureVisibility(), "Unused screenshot shortcut expires instead of stranding a hidden entry");
        await Task.Delay(100);
        check(dock.IsVisible, "Entry returns after screenshot-start timeout");
    }
}
