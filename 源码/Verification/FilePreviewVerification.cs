using System;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileDockDemo;

internal static class FilePreviewVerification
{
    internal static async Task Run(MainWindow window, string directory, Action<bool, string> check)
    {
        string sample = Path.Combine(directory, "preview-sample.md");
        await File.WriteAllTextAsync(sample, "# 中文教程\n" + new string('x', FilePreviewReader.TextLimit));
        var preview = await FilePreviewReader.ReadTextAsync(sample, CancellationToken.None);
        check(preview.Text.StartsWith("# 中文教程") && preview.Truncated && preview.Text.Length == FilePreviewReader.TextLimit,
            "Markdown preview preserves Unicode and bounds the displayed text");
        check(FilePreviewReader.Kind("SAMPLE.PDF") == PreviewKind.Pdf && FilePreviewReader.Kind("run.exe") == PreviewKind.Unsupported,
            "Preview routes PDF and unsupported file types without launching them");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool stopped = false;
        try { await FilePreviewReader.ReadTextAsync(sample, cancelled.Token); }
        catch (OperationCanceledException) { stopped = true; }
        check(stopped, "Closing a preview cancels a pending text read");

        string pdfPath = Path.Combine(directory, "preview-pages.pdf");
        WriteTwoPagePdf(pdfPath);
        var viewer = new FilePreviewWindow(window.Resources)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -12000, Top = -12000, ShowActivated = false
        };
        viewer.Show();
        try
        {
            string imagePath = Path.Combine(directory, "preview-picture.png");
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr32, null, new byte[16], 8)));
            using (var target = File.Create(imagePath)) png.Save(target);
            File.SetAttributes(imagePath, File.GetAttributes(imagePath) | FileAttributes.ReadOnly);
            try { await viewer.ShowFileAsync(imagePath); }
            finally { File.SetAttributes(imagePath, File.GetAttributes(imagePath) & ~FileAttributes.ReadOnly); }
            viewer.UpdateLayout();
            var picture = DisplayPowerViewVerification.Children<Image>(viewer).Single(item => item.Source is BitmapSource);
            double oldWidth = picture.Width;
            DisplayPowerViewVerification.Children<Button>(viewer).Single(item => Equals(item.Content, "放大"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(picture.Width > oldWidth, "The preview opens and zooms a read-only PNG");

            await viewer.ShowFileAsync(pdfPath);
            viewer.UpdateLayout();
            bool HasPage(uint number) => DisplayPowerViewVerification.Children<TextBlock>(viewer)
                .Any(item => item.Text.Contains($"第 {number} / 2 页"));
            check(HasPage(1) && DisplayPowerViewVerification.Children<Image>(viewer).Any(item => item.Source is BitmapSource),
                "A real two-page PDF renders its first page using Windows PDF");
            string renamed = Path.Combine(directory, "preview-renamed.pdf");
            File.Move(pdfPath, renamed);
            var next = DisplayPowerViewVerification.Children<Button>(viewer).Single(item => Equals(item.Content, "下一页"));
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var timer = Stopwatch.StartNew();
            while (!HasPage(2) && timer.ElapsedMilliseconds < 8000) { await Task.Delay(25); viewer.UpdateLayout(); }
            check(HasPage(2), "PDF navigation reuses the open document and renders page two after the file is renamed");
            File.Move(renamed, pdfPath);
            Task pending = viewer.ShowFileAsync(pdfPath);
            await Task.WhenAll(pending, viewer.ShowFileAsync(sample));
            viewer.UpdateLayout();
            check(DisplayPowerViewVerification.Children<TextBox>(viewer).Any(box => box.Text.StartsWith("# 中文教程")),
                "Switching preview drops an older PDF completion");
            using (var exclusive = new FileStream(pdfPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                check(exclusive.Length > 0, "Leaving the PDF releases its file handle");
            string unknown = Path.ChangeExtension(sample, ".unknown");
            File.WriteAllText(unknown, "example");
            await viewer.ShowFileAsync(unknown);
            viewer.UpdateLayout();
            check(DisplayPowerViewVerification.Children<TextBlock>(viewer).Any(item => item.Text.Contains("暂不支持预览"))
                && DisplayPowerViewVerification.Children<Button>(viewer).Any(item => Equals(item.Content, "打开文件")),
                "An unsupported preview offers the default application after showing file information");
            string broken = Path.Combine(directory, "preview-broken.pdf");
            File.WriteAllText(broken, "%PDF-1.4\ntruncated");
            await viewer.ShowFileAsync(broken);
            viewer.UpdateLayout();
            check(DisplayPowerViewVerification.Children<TextBlock>(viewer).Any(item => item.Text.Contains("无法预览此文件")),
                "A damaged PDF reports a preview error");
            using (var exclusive = new FileStream(broken, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                check(exclusive.Length > 0, "A failed PDF preview releases the input file");
            await viewer.ShowFileAsync(pdfPath);
        }
        finally { viewer.Close(); }
        using (var exclusive = new FileStream(pdfPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            check(exclusive.Length > 0, "Closing the PDF window releases its cached document stream");
    }

    internal static async Task VerifyFocus(MainWindow window, string directory, Action<bool, string> check)
    {
        string folder = Path.Combine(directory, "preview-focus-files");
        Directory.CreateDirectory(folder);
        string sample = Path.Combine(folder, "focus-note.txt");
        File.WriteAllText(sample, "Focus sample");
        string previousPage = window.State.SelectedPage;
        var previousBinding = window.State.Shortcuts![ShortcutCatalog.Preview].Copy();
        bool Space(UIElement target)
        {
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!,
                Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            target.RaiseEvent(key);
            return key.Handled;
        }
        try
        {
            var result = await window.ChangeShortcutAsync(ShortcutCatalog.Preview, new() { Enabled = true, Key = 0x20 });
            check(result.Success, "The file-list preview accepts an explicitly configured Space binding");
            window.ExpandPanel();
            await window.NavigateToFolderAsync(folder);
            window.SearchBox.Text = "focus-note";
            await window.WaitForFolderSearchAsync();
            window.Activate();
            window.UpdateLayout();
            window.SearchBox.Focus();
            check(window.SearchBox.IsKeyboardFocused && !Space(window.SearchBox)
                && !Application.Current.Windows.OfType<FilePreviewWindow>().Any(),
                "Space in the search field reaches the editor without opening a preview");
            var entry = window.FolderList.Items.Cast<EntryView>().Single(e => e.Path == sample);
            window.FolderList.ScrollIntoView(entry);
            window.UpdateLayout();
            var row = (ListBoxItem)window.FolderList.ItemContainerGenerator.ContainerFromItem(entry);
            row.Focus();
            check(row.IsKeyboardFocusWithin && Space(row)
                && Application.Current.Windows.OfType<FilePreviewWindow>().Any(w => w.IsVisible && w.Title.Contains("focus-note.txt")),
                "The configured Space shortcut opens the focused file from real recursive search results");
        }
        finally
        {
            foreach (var preview in Application.Current.Windows.OfType<FilePreviewWindow>().ToArray()) preview.Close();
            window.GoHome();
            await window.ChangeShortcutAsync(ShortcutCatalog.Preview, previousBinding);
            window.SelectPage(previousPage, false);
        }
    }

    private static void WriteTwoPagePdf(string path)
    {
        string[] objects =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 120 80] /Resources << >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 120] /Resources << >> >>"
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = pdf.Length;
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int startXref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{startXref}\n%%EOF\n");
        File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
    }
}
