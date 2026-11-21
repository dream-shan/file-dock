using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace FileDockDemo;

public partial class MainWindow
{
    private FilePreviewWindow? _filePreview;
    private PreviewSession? _previewSession;

    private sealed class PreviewSession(ListBox list, string? folder, string query, int filter, string[] paths, int index)
    {
        internal ListBox List { get; } = list;
        internal string? Folder { get; } = folder;
        internal string Query { get; } = query;
        internal int Filter { get; } = filter;
        internal string[] Paths { get; } = paths;
        internal int Index { get; set; } = index;
    }

    private ListBox PreviewList => CurrentFolderPath == null ? PinsList : FolderList;
    private int PreviewFilter => CurrentFolderPath == null ? -1 : FolderFilterComboBox.SelectedIndex;

    private void PreviewFile(EntryView entry)
    {
        if (!IsFilesPage || entry.Kind == "文件夹" || _quitting || _exitSaving) return;

        var list = PreviewList;
        string[] paths = list.Items.OfType<EntryView>().Where(item => item.Kind != "文件夹")
            .Select(item => item.Path).ToArray();
        int index = Array.FindIndex(paths, path => string.Equals(path, entry.Path, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            paths = [entry.Path];
            index = 0;
        }

        var viewer = _filePreview ??= CreateFilePreviewWindow();
        var session = _previewSession = new PreviewSession(list, CurrentFolderPath, SearchBox.Text.Trim(), PreviewFilter, paths, index);
        void Move(int offset)
        {
            int target = session.Index + offset;
            if (!ReferenceEquals(_previewSession, session) || target < 0 || target >= paths.Length) return;
            session.Index = target;
            ShowCurrent();
        }
        void ShowCurrent()
        {
            viewer.SetFileNavigation(session.Index, paths.Length, Move);
            _ = viewer.ShowFileAsync(paths[session.Index]);
        }

        ShowCurrent();
        if (!viewer.IsVisible) viewer.Show();
        if (viewer.WindowState == WindowState.Minimized) viewer.WindowState = WindowState.Normal;
        viewer.Activate();
    }

    private FilePreviewWindow CreateFilePreviewWindow()
    {
        var viewer = new FilePreviewWindow(Resources);
        bool wasActive = false;
        viewer.Closing += (_, _) => wasActive = viewer.IsActive;
        viewer.Closed += (_, _) =>
        {
            var session = _previewSession;
            _previewSession = null;
            _filePreview = null;
            if (session != null && (wasActive || IsActive))
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => RestorePreviewFocus(session));
        };
        return viewer;
    }

    private void RestorePreviewFocus(PreviewSession session)
    {
        if (_filePreview != null || _quitting || _exitSaving || !IsFilesPage || _inDialog || AnyMenuOpen() ||
            !ReferenceEquals(session.List, PreviewList) ||
            !string.Equals(session.Folder, CurrentFolderPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(session.Query, SearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase) ||
            session.Filter != PreviewFilter || _desktopPresence.Dismissed || MemoryPaused || UpdateCaptureVisibility()) return;

        string lastPath = session.Paths[session.Index];
        var item = session.List.Items.OfType<EntryView>().FirstOrDefault(entry =>
            entry.Kind != "文件夹" && string.Equals(entry.Path, lastPath, StringComparison.OrdinalIgnoreCase));
        if (item == null) return;

        if (!IsVisible) ExpandPanel();
        else if (!IsActive) Activate();
        if (!session.List.IsVisible) return;

        session.List.SelectedItem = item;
        session.List.ScrollIntoView(item);
        session.List.UpdateLayout();
        if (session.List.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem row) row.Focus();
        else session.List.Focus();
    }

    private EntryView? FocusedPreviewFile()
    {
        if (!IsFilesPage || _dragging || _movingPanel || _inDialog || _menuOpen ||
            _captureInteractionHeld || ActiveModuleDialog != null ||
            Keyboard.FocusedElement is not DependencyObject focused || focused is TextBoxBase) return null;

        var list = PreviewList;
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
