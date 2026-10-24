using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace FileDockDemo;

internal sealed record FolderSearchResult(
    IReadOnlyList<FolderItem> Items,
    int SkippedDirectories,
    bool Truncated);

internal static class FolderSearchService
{
    private const int ResultLimit = 1000;
    private static readonly StringComparer NameComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), ignoreCase: true);

    public static Task<FolderSearchResult> SearchAsync(string root, string query, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(query);
        string search = query.Trim();

        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            // An empty search does not enumerate the tree, including its root.
            if (search.Length == 0)
                return new FolderSearchResult(Array.Empty<FolderItem>(), 0, false);

            string fullRoot = Path.GetFullPath(root);
            FileAttributes rootAttributes = File.GetAttributes(fullRoot);
            if ((rootAttributes & FileAttributes.Directory) == 0)
                throw new IOException("所选位置不是文件夹。");
            // The user explicitly chose this root, so allow a redirected root
            // (for example, Desktop in OneDrive). Descendant links are not entered.

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
            };
            var items = new List<FolderItem>();
            int skippedDirectories = 0;
            bool truncated = false;

            // Keep only the current branch's enumerators in memory. A broad
            // folder tree therefore does not queue every pending directory.
            var cursors = new Stack<(IEnumerator<FileSystemInfo> Entries, bool IsRoot)>();
            cursors.Push((new DirectoryInfo(fullRoot)
                .EnumerateFileSystemInfos("*", options).GetEnumerator(), true));
            try
            {
                while (cursors.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    var cursor = cursors.Peek();
                    FileSystemInfo entry;
                    try
                    {
                        if (!cursor.Entries.MoveNext())
                        {
                            cursors.Pop().Entries.Dispose();
                            continue;
                        }
                        entry = cursor.Entries.Current;
                    }
                    catch (Exception error) when (!cursor.IsRoot && IsReadError(error))
                    {
                        skippedDirectories++;
                        cursors.Pop().Entries.Dispose();
                        continue;
                    }

                    token.ThrowIfCancellationRequested();
                    FileAttributes attributes;
                    try
                    {
                        attributes = entry.Attributes;
                    }
                    catch (Exception error) when (IsReadError(error))
                    {
                        if (entry is DirectoryInfo)
                            skippedDirectories++;
                        continue;
                    }
                    if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                        continue;

                    bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                    // Match only the entry name: matching a parent's name must
                    // not make every descendant a search result.
                    if (entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                    {
                        long? length = null;
                        DateTime? lastModified = null;
                        try
                        {
                            lastModified = entry.LastWriteTime;
                            if (!isDirectory && entry is FileInfo file)
                                length = file.Length;
                        }
                        catch (Exception error) when (IsReadError(error)) { }
                        items.Add(new FolderItem(entry.FullName, entry.Name, isDirectory, length, lastModified));
                        if (items.Count >= ResultLimit)
                        {
                            // The flag means traversal stopped at the limit;
                            // there may be additional matching entries.
                            truncated = true;
                            break;
                        }
                    }

                    if (!isDirectory || (attributes & FileAttributes.ReparsePoint) != 0)
                        continue;

                    token.ThrowIfCancellationRequested();
                    try
                    {
                        cursors.Push((new DirectoryInfo(entry.FullName)
                            .EnumerateFileSystemInfos("*", options).GetEnumerator(), false));
                    }
                    catch (Exception error) when (IsReadError(error))
                    {
                        skippedDirectories++;
                    }
                }
            }
            finally
            {
                while (cursors.Count > 0)
                    cursors.Pop().Entries.Dispose();
            }

            token.ThrowIfCancellationRequested();
            items.Sort((left, right) =>
            {
                int byKind = right.IsDirectory.CompareTo(left.IsDirectory);
                if (byKind != 0) return byKind;
                int byName = NameComparer.Compare(left.Name, right.Name);
                return byName != 0 ? byName : StringComparer.Ordinal.Compare(left.Path, right.Path);
            });
            token.ThrowIfCancellationRequested();
            return new FolderSearchResult(items, skippedDirectories, truncated);
        }, token);
    }

    private static bool IsReadError(Exception error) =>
        error is IOException or UnauthorizedAccessException or SecurityException;
}
