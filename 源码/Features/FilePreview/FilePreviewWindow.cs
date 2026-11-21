using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileDockDemo;

internal sealed partial class FilePreviewWindow : Window
{
    private readonly ContentControl _content = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch
    };
    private readonly TextBlock _status = Ui.Text("");
    private readonly WrapPanel _controls = new();
    private readonly DockPanel _fileNavigation = new() { Visibility = Visibility.Collapsed };
    private readonly ScrollViewer _scroller = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly SemaphoreSlim _loadGate = new(1);
    private PreviewPdfDocument? _pdf;
    private CancellationTokenSource? _request;
    private string _path = "";
    private uint _page, _pages;
    private double _zoom = 1;
    private bool _actualPixels;
    private bool _closed;

    internal FilePreviewWindow(ResourceDictionary theme)
    {
        Resources = theme;
        Title = "文件预览";
        Width = Math.Min(780, SystemParameters.WorkArea.Width);
        Height = Math.Min(600, SystemParameters.WorkArea.Height);
        MinWidth = 360;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Surface");
        var layout = new DockPanel { Margin = new Thickness(14) };
        InitializeFileNavigation();
        DockPanel.SetDock(_fileNavigation, Dock.Top);
        layout.Children.Add(_fileNavigation);
        var footer = new DockPanel { LastChildFill = false };
        var open = Ui.Button("打开文件", () =>
        {
            try { FileService.Open(_path); }
            catch (Exception ex) { _status.Text = "打开失败 · " + ex.Message; }
        });
        DockPanel.SetDock(open, Dock.Right);
        footer.Children.Add(open);
        footer.Children.Add(_controls);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        DockPanel.SetDock(_status, Dock.Bottom);
        layout.Children.Add(_status);
        layout.Children.Add(_content);
        Content = layout;
        _scroller.Content = _image;
        _scroller.SizeChanged += (_, _) => FitImage();
        DpiChanged += (_, args) => FitImage(args.NewDpi);
        Closed += (_, _) =>
        {
            _closed = true;
            _request?.Cancel();
            if (_loadGate.Wait(0))
            {
                try { ReleasePdf(); }
                finally { _loadGate.Release(); }
            }
            _image.Source = null;
            _content.Content = null;
            _controls.Children.Clear();
            _fileNavigation.Children.Clear();
            _moveFile = null;
        };
    }

    internal Task ShowFileAsync(string path)
    {
        if (_closed) return Task.CompletedTask;
        _path = path;
        _page = _pages = 0;
        _zoom = 1;
        _actualPixels = false;
        Title = Path.GetFileName(path) + " · 文件预览";
        return LoadAsync(openFile: true);
    }

    private void ReleasePdf()
    {
        var pdf = _pdf;
        _pdf = null;
        pdf?.Dispose();
    }

    private async Task LoadAsync(bool openFile)
    {
        _request?.Cancel();
        var request = _request = new CancellationTokenSource();
        var token = request.Token;
        string path = _path;
        uint page = _page;
        _status.Text = "正在读取…";
        _controls.Children.Clear();
        _image.Source = null;
        _content.Content = null;
        bool entered = false;
        try
        {
            await _loadGate.WaitAsync(token);
            entered = true;
            if (openFile) ReleasePdf();
            var kind = FilePreviewReader.Kind(path);
            long bytes = _pdf?.Length ?? await Task.Run(() => new FileInfo(path).Length, token);
            token.ThrowIfCancellationRequested();
            string metadata = $"{Path.GetExtension(path).TrimStart('.').ToUpperInvariant()} · {bytes:N0} 字节";
            _status.Text = metadata;
            switch (kind)
            {
                case PreviewKind.Text:
                    var text = await FilePreviewReader.ReadTextAsync(path, token);
                    token.ThrowIfCancellationRequested();
                    string extension = Path.GetExtension(path).ToLowerInvariant();
                    if (extension is ".md" or ".markdown")
                    {
                        var markdown = await Task.Run(() => MarkdownPreview.Parse(text.Text), token);
                        token.ThrowIfCancellationRequested();
                        ShowMarkdown(text.Text, markdown);
                    }
                    else ShowText(text.Text, extension == ".txt");
                    if (text.Truncated) _status.Text = metadata + " · 已显示开头 64 K 字符";
                    break;
                case PreviewKind.Image:
                    var bitmap = await ImageLoader.LoadAsync(path, true, token, 2560, 2560, originalPixels: _actualPixels);
                    token.ThrowIfCancellationRequested();
                    bool originalUnavailable = _actualPixels && bitmap == null;
                    if (originalUnavailable)
                    {
                        bitmap = await ImageLoader.LoadAsync(path, true, token, 2560, 2560);
                        token.ThrowIfCancellationRequested();
                        _actualPixels = false;
                        _status.Text = metadata + " · 无法载入原始像素，已适应窗口";
                    }
                    token.ThrowIfCancellationRequested();
                    ShowBitmap(bitmap ?? throw new InvalidDataException("图片无法解码或超过预览大小限制。"), imageFile: true);
                    break;
                case PreviewKind.Pdf:
                    _pdf ??= await Task.Run(() => PreviewPdfDocument.OpenAsync(path, token), token);
                    var document = _pdf.Document;
                    if (page >= document.PageCount) throw new InvalidDataException("页面已不存在，请重新打开文件。");
                    var pageImage = await Task.Run(() => FilePreviewReader.RenderPageAsync(document, page, token), token);
                    token.ThrowIfCancellationRequested();
                    _pages = document.PageCount;
                    AddPageControls(page);
                    ShowBitmap(pageImage);
                    _status.Text = $"{metadata} · 第 {page + 1} / {_pages} 页";
                    break;
                default:
                    _content.Content = Ui.Text("此类型暂不支持预览，可用默认应用打开。");
                    break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (entered) ReleasePdf();
            if (!token.IsCancellationRequested)
                _content.Content = Ui.Text(ex is DecoderFallbackException ? "文件编码无法识别，可用默认应用打开。" : "无法预览此文件 · " + ex.Message);
            if (!token.IsCancellationRequested && _status.Text == "正在读取…") _status.Text = Path.GetFileName(path);
        }
        finally
        {
            if (entered)
            {
                try { if (_closed || token.IsCancellationRequested) ReleasePdf(); }
                finally { _loadGate.Release(); }
            }
            if (ReferenceEquals(_request, request)) _request = null;
            request.Dispose();
        }
    }

    private void ChangePage(uint page)
    {
        if (_closed || page == _page || page >= _pages) return;
        _page = page;
        _ = LoadAsync(openFile: false);
    }

    private Button AddControl(string name, Action action, bool enabled = true)
    {
        var button = Ui.Button(name, action);
        button.IsEnabled = enabled;
        button.Margin = new Thickness(0, 4, 4, 0);
        _controls.Children.Add(button);
        return button;
    }

    private void ShowBitmap(BitmapSource bitmap, bool imageFile = false)
    {
        _image.Source = bitmap;
        _content.Content = _scroller;
        AddControl("缩小", () => Zoom(0.8));
        AddControl("放大", () => Zoom(1.25));
        if (imageFile)
        {
            AddControl("适应窗口", () => SetImageSize(false));
            AddControl("实际大小", () => SetImageSize(true));
        }
        _scroller.ScrollToTop();
        _scroller.ScrollToLeftEnd();
        FitImage();
    }

    private void Zoom(double factor)
    {
        _zoom = Math.Clamp(_zoom * factor, 0.25, 8);
        FitImage();
    }

    private void SetImageSize(bool actualPixels)
    {
        _zoom = 1;
        if (_actualPixels == actualPixels)
        {
            FitImage();
            return;
        }
        _actualPixels = actualPixels;
        _ = LoadAsync(openFile: true);
    }

    private void FitImage(DpiScale? newDpi = null)
    {
        if (_image.Source is not BitmapSource bitmap || _scroller.ActualWidth < 2 || _scroller.ActualHeight < 2) return;
        double fit = Math.Min((_scroller.ActualWidth - 1) / bitmap.PixelWidth, (_scroller.ActualHeight - 1) / bitmap.PixelHeight);
        var dpi = newDpi ?? VisualTreeHelper.GetDpi(_image);
        _image.Width = bitmap.PixelWidth * (_actualPixels ? 1 / dpi.DpiScaleX : fit) * _zoom;
        _image.Height = bitmap.PixelHeight * (_actualPixels ? 1 / dpi.DpiScaleY : fit) * _zoom;
    }
}
