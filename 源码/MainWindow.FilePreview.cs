using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace FileDockDemo;

public partial class MainWindow
{
    private FilePreviewWindow? _filePreview;

    private void PreviewFile(EntryView entry)
    {
        if (entry.Kind == "文件夹") return;
        if (_filePreview == null)
        {
            _filePreview = new FilePreviewWindow(Resources);
            _filePreview.Closed += (_, _) => _filePreview = null;
        }
        _ = _filePreview.ShowFileAsync(entry.Path);
        if (!_filePreview.IsVisible) _filePreview.Show();
        if (_filePreview.WindowState == WindowState.Minimized) _filePreview.WindowState = WindowState.Normal;
        _filePreview.Activate();
    }

    private EntryView? FocusedPreviewFile()
    {
        if (!IsFilesPage || _dragging || _movingPanel || _inDialog || _menuOpen ||
            _captureInteractionHeld || ActiveModuleDialog != null ||
            Keyboard.FocusedElement is not DependencyObject focused || focused is TextBoxBase) return null;

        var list = CurrentFolderPath == null ? PinsList : FolderList;
        if (!list.IsKeyboardFocusWithin) return null;
        EntryView? entry = focused == list ? list.SelectedItem as EntryView
            : (ItemsControl.ContainerFromElement(list, focused) as ListBoxItem)?.DataContext as EntryView;
        return entry is { Kind: not "文件夹" } ? entry : null;
    }

    private bool HandleFilePreviewKey(KeyEventArgs args)
    {
        if (!MatchesFilePreviewShortcut(args) || FocusedPreviewFile() is not { } entry) return false;
        args.Handled = true;
        if (!args.IsRepeat) PreviewFile(entry);
        return true;
    }
}
