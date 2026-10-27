using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileDockDemo;

internal static class ImageLoader
{
    private record Cached(string Key, BitmapSource? Image);
    private static readonly LinkedList<Cached> Cache = new();
    private static readonly SemaphoreSlim Workers = new(2);
    internal static int CacheCount { get { lock (Cache) return Cache.Count; } }

    internal static async Task<BitmapSource?> LoadAsync(string path, bool preview, CancellationToken token)
    {
        await Workers.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists || info.Length > 100 * 1024 * 1024) return null;
                    string key = path.ToUpperInvariant() + "|" + info.LastWriteTimeUtc.Ticks + "|" + info.Length;
                    if (!preview)
                    {
                        lock (Cache)
                            for (var node = Cache.First; node != null; node = node.Next)
                                if (node.Value.Key == key) { var item = node.Value; Cache.Remove(node); Cache.AddFirst(item); return item.Image; }
                    }
                    BitmapSource? result = null;
                    try
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                        int width = decoder.Frames[0].PixelWidth, height = decoder.Frames[0].PixelHeight;
                        if ((long)width * height > 200_000_000) return null;
                        token.ThrowIfCancellationRequested();
                        stream.Position = 0;
                        int maxWidth = preview ? 720 : 96, maxHeight = preview ? 560 : 96;
                        var image = new BitmapImage();
                        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream;
                        if (width / (double)maxWidth >= height / (double)maxHeight) image.DecodePixelWidth = Math.Min(width, maxWidth);
                        else image.DecodePixelHeight = Math.Min(height, maxHeight);
                        image.EndInit(); image.Freeze(); result = image;
                    }
                    catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException or InvalidOperationException) { }
                    token.ThrowIfCancellationRequested();
                    if (!preview) lock (Cache) { Cache.AddFirst(new Cached(key, result)); while (Cache.Count > 64) Cache.RemoveLast(); }
                    return result;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
            }, token);
        }
        finally { Workers.Release(); }
    }
}

// Each realized row owns only its small thumbnail. Unloaded/recycled rows cancel
// pending work and release references; large previews live only while hovered.
public sealed class EntryThumbnail : Grid
{
    private readonly TextBlock _glyph = new() { FontFamily = new FontFamily("Segoe UI"), FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private CancellationTokenSource? _thumbnailRequest, _previewRequest;
    private ToolTip? _tip;
    private ListBoxItem? _row;

    public EntryThumbnail()
    {
        Children.Add(_glyph); Children.Add(_image);
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => Release();
        DataContextChanged += (_, _) => { if (IsLoaded) Refresh(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); else Release(); };
    }

    private void Release()
    {
        _thumbnailRequest?.Cancel(); _thumbnailRequest?.Dispose(); _thumbnailRequest = null;
        ClosePreview();
        if (_tip != null) _tip.IsOpen = false;
        _image.Source = null;
    }
    private void ClosePreview()
    {
        _previewRequest?.Cancel(); _previewRequest?.Dispose(); _previewRequest = null;
        if (_tip != null) _tip.Content = null;
    }
    private async void Refresh()
    {
        Release();
        if (DataContext is not EntryView entry) return;
        _glyph.Text = entry.IconGlyph; _glyph.SetResourceReference(TextBlock.ForegroundProperty, entry.Kind switch { "文件夹" => "FolderStroke", "图片" => "GpuAccent", "表格" => "MemoryAccent", _ => "Muted" }); _glyph.Visibility = Visibility.Visible;
        if (!IsVisible) return;
        _row = null;
        for (DependencyObject? p = this; p != null; p = VisualTreeHelper.GetParent(p))
            if (p is ListBoxItem row) { _row = row; break; }
        if (_row != null)
        {
            _row.ToolTip = entry.Path;
            if (entry.Kind == "图片")
            {
                _tip = new ToolTip { Padding = new Thickness(10), MaxWidth = 390,
                    PlacementTarget = _row, Placement = Window.GetWindow(this) is MainWindow owner && owner.State.DockSide == "Left" ? PlacementMode.Right : PlacementMode.Left };
                _tip.Opened += async (_, _) =>
                {
                    ClosePreview();
                    var request = _previewRequest = new CancellationTokenSource();
                    _tip.Content = new TextBlock { Text = "正在加载预览…" };
                    try
                    {
                        var bitmap = await ImageLoader.LoadAsync(entry.Path, true, request.Token);
                        if (request.IsCancellationRequested || !_tip.IsOpen) return;
                        var panel = new StackPanel();
                        if (bitmap != null) panel.Children.Add(new Image { Source = bitmap, MaxWidth = 360, MaxHeight = 280, Stretch = Stretch.Uniform });
                        panel.Children.Add(new TextBlock { Text = bitmap == null ? "无法预览此图片，可单击用默认应用打开" : entry.DisplayName, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), MaxWidth = 360 });
                        _tip.Content = panel;
                    }
                    catch (OperationCanceledException) { }
                };
                _tip.Closed += (_, _) => ClosePreview();
                _row.ToolTip = _tip;
                ToolTipService.SetInitialShowDelay(_row, 350);
                ToolTipService.SetShowDuration(_row, 60000);
            }
        }
        if (entry.Kind != "图片") return;
        var thumbnail = _thumbnailRequest = new CancellationTokenSource();
        try
        {
            var bitmap = await ImageLoader.LoadAsync(entry.Path, false, thumbnail.Token);
            if (thumbnail.IsCancellationRequested || !IsVisible || !ReferenceEquals(DataContext, entry)) return;
            _image.Source = bitmap;
            _glyph.Visibility = bitmap == null ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
    }
}
