using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace FileDockDemo;

internal enum PreviewKind { Image, Text, Pdf, Unsupported }

internal sealed class PreviewPdfDocument : IDisposable
{
    private readonly FileStream _file;
    private readonly IRandomAccessStream _stream;
    internal PdfDocument Document { get; }
    internal long Length { get; }

    private PreviewPdfDocument(FileStream file, IRandomAccessStream stream, PdfDocument document)
    {
        _file = file;
        _stream = stream;
        Document = document;
        Length = file.Length;
    }

    internal static async Task<PreviewPdfDocument> OpenAsync(string path, CancellationToken token)
    {
        var file = FilePreviewReader.OpenShared(path);
        IRandomAccessStream? stream = null;
        try
        {
            stream = file.AsRandomAccessStream();
            var document = await PdfDocument.LoadFromStreamAsync(stream).AsTask(token);
            return new PreviewPdfDocument(file, stream, document);
        }
        catch
        {
            try { stream?.Dispose(); }
            finally { file.Dispose(); }
            throw;
        }
    }

    public void Dispose()
    {
        try { _stream.Dispose(); }
        finally { _file.Dispose(); }
    }
}

internal static class FilePreviewReader
{
    internal const int TextLimit = 64 * 1024;
    internal static PreviewKind Kind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".webp" or ".ico" => PreviewKind.Image,
        ".pdf" => PreviewKind.Pdf,
        ".txt" or ".md" or ".markdown" or ".log" or ".csv" or ".tsv" or ".json" or ".xml" or ".yaml" or ".yml"
            or ".ini" or ".toml" or ".cs" or ".c" or ".h" or ".cpp" or ".hpp" or ".py" or ".java" or ".js"
            or ".ts" or ".jsx" or ".tsx" or ".ps1" or ".css" or ".html" or ".sql" or ".go" or ".rs"
            or ".sh" or ".bat" or ".cmd" or ".xaml" => PreviewKind.Text,
        _ => PreviewKind.Unsupported
    };

    internal static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);

    internal static Task<(string Text, bool Truncated)> ReadTextAsync(string path, CancellationToken token) =>
        Task.Run(async () =>
        {
            using var stream = OpenShared(path);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
            var buffer = new char[TextLimit + 1];
            int read = await reader.ReadBlockAsync(buffer.AsMemory(), token);
            int count = Math.Min(TextLimit, read);
            if (count > 0 && char.IsHighSurrogate(buffer[count - 1])) count--;
            if (Array.IndexOf(buffer, '\0', 0, count) >= 0) throw new InvalidDataException("文件包含二进制数据，无法作为文本预览。");
            return (new string(buffer, 0, count), read > TextLimit);
        }, token);

    internal static async Task<BitmapSource> RenderPageAsync(PdfDocument document, uint index, CancellationToken token)
    {
        using var page = document.GetPage(index);
        var size = page.Size;
        double scale = 1800d / Math.Max(size.Width, size.Height);
        if (!double.IsFinite(scale) || scale <= 0 || size.Width <= 0 || size.Height <= 0)
            throw new InvalidDataException("页面尺寸无效。");
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Clamp(Math.Round(size.Width * scale), 1, 1800),
            DestinationHeight = (uint)Math.Clamp(Math.Round(size.Height * scale), 1, 1800)
        };
        using var output = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(output, options).AsTask(token);
        token.ThrowIfCancellationRequested();
        output.Seek(0);
        using var stream = output.AsStreamForRead();
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
