using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FileDockDemo;

public partial class MainWindow
{
    private readonly HashSet<string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _controlClick, _preserveGroup, _dragHidden;
    private Rect _dragBounds;
    private long _ignoreDragEscapeUntil;
    private readonly SemaphoreSlim _clipboardWorker = new(1);
    private CancellationTokenSource? _copyRequest;
    internal int SelectedFileCount => _selectedPaths.Count;
    private IEnumerable<EntryView> VisibleEntries() => CurrentFolderPath == null
        ? PinsList.Items.Cast<EntryView>()
        : FolderList.Items.Cast<EntryView>();

    private void InitializeTransfers()
    {
        foreach (var list in new[] { PinsList, FolderList })
        {
            list.PreviewMouseLeftButtonDown += Entries_MouseDown;
            list.PreviewMouseMove += Entries_MouseMove;
            list.PreviewMouseLeftButtonUp += Entries_MouseUp;
        }
        QueryContinueDrag += Transfer_QueryContinueDrag;
    }
    internal void ClearFileSelection()
    {
        _selectedPaths.Clear(); UpdateFileSelection();
    }
    private void UpdateFileSelection()
    {
        foreach (var entry in VisibleEntries()) entry.IsChosen = _selectedPaths.Contains(entry.Path);
        InteractionHint.Text = _selectedPaths.Count > 0 ? $"已选 {_selectedPaths.Count} 项 · Ctrl+C 复制" : "Ctrl 多选 · 拖出复制";
    }
    private void ReconcileFileSelection()
    {
        _selectedPaths.IntersectWith(VisibleEntries().Select(e => e.Path)); UpdateFileSelection();
    }
    internal void SelectFile(EntryView entry, bool toggle)
    {
        if (!toggle) _selectedPaths.Clear();
        if (!toggle || !_selectedPaths.Remove(entry.Path)) _selectedPaths.Add(entry.Path);
        UpdateFileSelection();
    }
    private void Entries_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var entry = FindEntry(e.OriginalSource as DependencyObject);
        if (entry == null) return;
        _dragEntry = entry; _dragStart = e.GetPosition(this); _dragging = false;
        _controlClick = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        _preserveGroup = _selectedPaths.Contains(entry.Path) && _selectedPaths.Count > 1;
        if (_controlClick) SelectFile(entry, true);
        else if (!_selectedPaths.Contains(entry.Path)) SelectFile(entry, false);
        ((ListBox)sender).Focus(); ((ListBox)sender).CaptureMouse();
        e.Handled = true;
    }
    private void Entries_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragEntry == null) return;
        var entry = _dragEntry; _dragEntry = null;
        ((ListBox)sender).ReleaseMouseCapture(); e.Handled = true;
        if (!_dragging && !_controlClick && !_preserveGroup) OpenEntry(entry);
    }
    private void Entries_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragEntry == null || e.LeftButton != MouseButtonState.Pressed || _dragging) return;
        Point position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var entry = _dragEntry;
        if (!_selectedPaths.Contains(entry.Path)) SelectFile(entry, false);
        // Releasing capture can synchronously raise MouseMove again.
        _dragging = true; _dragHidden = false;
        ((ListBox)sender).ReleaseMouseCapture();
        string[] paths = SelectedPaths();
        var data = BuildFileData(paths);
        if (entry.IsPinned && paths.Length == 1) data.SetData("FileDockPin", entry.Id);
        data.SetData("FileDockSource", true);
        _dragBounds = new Rect(PointToScreen(new Point()), PointToScreen(new Point(ActualWidth, ActualHeight)));
        DragDropEffects result = DragDropEffects.None;
        try { result = DragDrop.DoDragDrop(this, data, DragDropEffects.Copy); }
        catch (Exception ex) { SetStatus("拖拽未完成：" + ex.Message, true); }
        finally
        {
            _dragEntry = null;
            // OLE can return while the canceling Escape key is still down. Do
            // not let the same key close the panel we have just restored.
            if (result == DragDropEffects.None) _ignoreDragEscapeUntil = Environment.TickCount64 + 400;
            if (_dragHidden) { if (result == DragDropEffects.None) ExpandPanel(); else CollapsePanel(); }
            _dragHidden = false;
            Dispatcher.BeginInvoke(() => _dragging = false, DispatcherPriority.Background);
        }
        e.Handled = true;
    }
    private void Transfer_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (!_dragging || _dragHidden || e.EscapePressed) return;
        var position = System.Windows.Forms.Cursor.Position;
        if (!_dragBounds.Contains(new Point(position.X, position.Y))) { _dragHidden = true; Hide(); }
    }
    private string[] SelectedPaths() => VisibleEntries().Where(e => _selectedPaths.Contains(e.Path)).Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static DataObject BuildFileData(string[] paths)
    {
        var data = new DataObject();
        var files = new StringCollection(); files.AddRange(paths); data.SetFileDropList(files);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1))); // COPY, never delete source.
        return data;
    }

    private async Task CopySelectedFilesAsync(bool imageOnly = false)
    {
        string[] paths = SelectedPaths();
        if (paths.Length == 0) return;
        _copyRequest?.Cancel(); _copyRequest?.Dispose();
        var request = _copyRequest = new CancellationTokenSource();
        var token = request.Token;
        bool singleImage = paths.Length == 1 && VisibleEntries().Any(e => string.Equals(e.Path, paths[0], StringComparison.OrdinalIgnoreCase) && e.Kind == "图片");
        try
        {
            // File lists are always available. For one image add a native bitmap
            // and PNG representation so editors can paste image content as well.
            var data = imageOnly ? new DataObject() : BuildFileData(paths);
            if (singleImage)
            {
                (BitmapSource? Image, MemoryStream? Png) content;
                await _clipboardWorker.WaitAsync(token);
                try
                {
                    content = await Task.Run(() =>
                    {
                        var image = LoadClipboardImage(paths[0]);
                        if (image == null) return ((BitmapSource?)null, (MemoryStream?)null);
                        token.ThrowIfCancellationRequested();
                        var png = new MemoryStream(); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(png); png.Position = 0;
                        return ((BitmapSource?)image, (MemoryStream?)png);
                    }, token);
                }
                finally { _clipboardWorker.Release(); }
                token.ThrowIfCancellationRequested();
                if (content.Image != null)
                {
                    data.SetImage(content.Image);
                    data.SetData("PNG", content.Png!);
                }
                else if (imageOnly) { SetStatus("此图片无法复制内容，可使用“复制文件”上传原文件。", true); return; }
            }
            token.ThrowIfCancellationRequested();
            for (int retry = 0; ; retry++)
                try { token.ThrowIfCancellationRequested(); Clipboard.SetDataObject(data, true); break; }
                catch (System.Runtime.InteropServices.COMException) when (retry < 3) { await Task.Delay(60, token); }
            SetStatus(imageOnly ? "已复制图片内容，可以粘贴到支持图片的页面。" : $"已复制 {paths.Length} 个文件，可在目标位置 Ctrl+V 粘贴。");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus("复制失败：" + ex.Message, true); }
    }
    private static BitmapSource? LoadClipboardImage(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 100 * 1024 * 1024) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if ((long)decoder.Frames[0].PixelWidth * decoder.Frames[0].PixelHeight > 32_000_000) return null;
            stream.Position = 0;
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException or InvalidOperationException) { return null; }
    }
    private bool HandleTransferKey(KeyEventArgs e)
    {
        if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control || Keyboard.FocusedElement is TextBox) return false;
        if (_selectedPaths.Count == 0)
        {
            var list = CurrentFolderPath != null ? FolderList : PinsList;
            if (list.SelectedItem is EntryView entry) SelectFile(entry, false);
        }
        _ = CopySelectedFilesAsync(); e.Handled = true; return true;
    }
}
