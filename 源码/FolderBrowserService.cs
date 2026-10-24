using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileDockDemo;

internal sealed record FolderItem(
    string Path,
    string Name,
    bool IsDirectory,
    long? Length,
    DateTime? LastModified);

internal static class FolderBrowserService
{
    private static readonly StringComparer NameComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), ignoreCase: true);

    public static Task<bool> IsDirectoryAsync(string path) => Task.Run(() =>
        (File.GetAttributes(path) & FileAttributes.Directory) != 0);

    public static Task<IReadOnlyList<FolderItem>> LoadAsync(string path, CancellationToken token)
    {
        return Task.Run<IReadOnlyList<FolderItem>>(() =>
        {
            token.ThrowIfCancellationRequested();
            string fullPath = System.IO.Path.GetFullPath(path);
            if ((File.GetAttributes(fullPath) & FileAttributes.Directory) == 0)
                throw new IOException("所选位置不是文件夹。");

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
            };
            var items = new List<FolderItem>();

            // Keep enumeration errors outside the per-item handler: an inaccessible
            // root must reach the panel as an error, rather than an empty folder.
            foreach (FileSystemInfo entry in new DirectoryInfo(fullPath).EnumerateFileSystemInfos("*", options))
            {
                token.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try
                {
                    attributes = entry.Attributes;
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    continue;

                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                long? length = null;
                DateTime? lastModified = null;
                try
                {
                    lastModified = entry.LastWriteTime;
                    if (!isDirectory && entry is FileInfo file)
                        length = file.Length;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

                // A missing optional timestamp/size should not hide an otherwise
                // identifiable entry. Shortcuts remain files; no targets are read.
                items.Add(new FolderItem(entry.FullName, entry.Name, isDirectory, length, lastModified));
            }

            token.ThrowIfCancellationRequested();
            items.Sort((left, right) =>
            {
                int byKind = right.IsDirectory.CompareTo(left.IsDirectory);
                if (byKind != 0) return byKind;
                int byName = NameComparer.Compare(left.Name, right.Name);
                return byName != 0 ? byName : StringComparer.Ordinal.Compare(left.Name, right.Name);
            });
            token.ThrowIfCancellationRequested();
            return items;
        }, token);
    }
}
