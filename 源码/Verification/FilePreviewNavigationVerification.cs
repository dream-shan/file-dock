using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace FileDockDemo;

internal static class FilePreviewNavigationVerification
{
    internal static async Task Run(MainWindow window, string directory, Action<bool, string> check)
    {
        string root = Path.Combine(directory, "preview-navigation");
        string child = Directory.CreateDirectory(Path.Combine(root, "chapter-folder")).FullName;
        string first = Path.Combine(root, "chapter-a.txt");
        string second = Path.Combine(child, "chapter-b.txt");
        string third = Path.Combine(root, "chapter-c.txt");
        File.WriteAllText(first, "First chapter");
        File.WriteAllText(second, "Second chapter");
        File.WriteAllText(third, "Third chapter");
        string previousPage = window.State.SelectedPage;
        var oldBinding = window.State.Shortcuts![ShortcutCatalog.Preview].Copy();
        try
        {
            var binding = await window.ChangeShortcutAsync(ShortcutCatalog.Preview, new() { Enabled = true, Key = 0x20 });
            check(binding.Success, "The navigation verification configures its preview shortcut");
            window.ExpandPanel();
            await window.NavigateToFolderAsync(root);
            window.SearchBox.Text = "chapter";
            await window.WaitForFolderSearchAsync();
            check(window.FolderList.Items.OfType<EntryView>().Any(entry => entry.Kind == "文件夹"),
                "The recursive search fixture includes a folder alongside its files");
            string[] order = window.FolderList.Items.OfType<EntryView>()
                .Where(entry => entry.Kind != "文件夹").Select(entry => entry.Path).ToArray();
            check(order.Length == 3, "The recursive search fixture exposes three navigable files");

            var viewer = OpenFromResult(order[0]);
            var back = FindButton(viewer, "上一个文件");
            var next = FindButton(viewer, "下一个文件");
            check(back is { IsEnabled: false } && next is { IsEnabled: true },
                "The first search result only enables next-file navigation");
            Click(next!);
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            check(viewer.Title.StartsWith(Path.GetFileName(order[1]), StringComparison.Ordinal) &&
                Application.Current.Windows.OfType<FilePreviewWindow>().Count() == 1,
                "Next file skips folders and reuses the window in search result order");
            viewer.Close();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var secondRow = window.FolderList.ItemContainerGenerator.ContainerFromItem(
                window.FolderList.Items.OfType<EntryView>().Single(entry => entry.Path == order[1])) as ListBoxItem;
            check((window.FolderList.SelectedItem as EntryView)?.Path == order[1] && secondRow?.IsKeyboardFocusWithin == true,
                "Closing preview returns focus to the last previewed result");

            viewer = OpenFromResult(order[1]);
            window.SearchBox.Text = Path.GetFileName(order[2]);
            await window.WaitForFolderSearchAsync();
            check(window.FolderList.Items.Count == 1, "Changing the search filters the live list");
            Click(FindButton(viewer, "上一个文件")!);
            check(viewer.Title.StartsWith(Path.GetFileName(order[0]), StringComparison.Ordinal),
                "An open preview preserves its original result order after the live search changes");
            viewer.Close();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(window.FolderList.Items.OfType<EntryView>().Single().Path == order[2] &&
                window.FolderList.SelectedItem == null,
                "Closing preview leaves a changed search list without selecting a different file");
        }
        finally
        {
            foreach (var viewer in Application.Current.Windows.OfType<FilePreviewWindow>().ToArray()) viewer.Close();
            window.GoHome();
            await window.ChangeShortcutAsync(ShortcutCatalog.Preview, oldBinding);
            window.SelectPage(previousPage, false);
        }

        FilePreviewWindow OpenFromResult(string path)
        {
            window.Activate();
            var entry = window.FolderList.Items.OfType<EntryView>().Single(item => item.Path == path);
            window.FolderList.ScrollIntoView(entry);
            window.UpdateLayout();
            var row = (ListBoxItem)window.FolderList.ItemContainerGenerator.ContainerFromItem(entry);
            row.Focus();
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(row)!,
                Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            row.RaiseEvent(key);
            return Application.Current.Windows.OfType<FilePreviewWindow>().Single();
        }
    }

    private static Button? FindButton(Window window, string caption)
    {
        window.UpdateLayout();
        return DisplayPowerViewVerification.Children<Button>(window)
            .SingleOrDefault(button => Equals(button.Content, caption));
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
