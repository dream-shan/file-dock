using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FileDockDemo;

internal sealed record DiscoveredApp(string Name, string Id, string? Path);

internal static class InstalledAppDiscovery
{
    internal static Task<IReadOnlyList<DiscoveredApp>> ReadAsync(CancellationToken token)
    {
        if (token.IsCancellationRequested) return Task.FromCanceled<IReadOnlyList<DiscoveredApp>>(token);
        var completion = new TaskCompletionSource<IReadOnlyList<DiscoveredApp>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            // Shell calls themselves are synchronous. Release the caller immediately on cancellation,
            // then let the owning thread release its COM objects when the current call returns.
            using var registration = token.Register(() => completion.TrySetCanceled(token));
            try { completion.TrySetResult(Read(token)); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { completion.TrySetCanceled(token); }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "File Dock installed application discovery" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static IReadOnlyList<DiscoveredApp> Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var entries = new List<DiscoveredApp>();
        dynamic? shell = null, folder = null, items = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!);
            folder = shell!.NameSpace("shell:AppsFolder");
            if (folder == null) throw new IOException("Windows 无法打开已安装应用列表。");
            items = folder.Items();
            int count = items.Count;
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                dynamic? item = null;
                try
                {
                    item = items.Item(i);
                    if (item == null) continue;
                    string? name = item.Name;
                    // AppsFolder's parsing name is the application identity, including for packaged apps.
                    string? id = item.Path;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                    DiscoveredApp? entry;
                    try
                    {
                        string? target = item.ExtendedProperty("System.Link.TargetParsingPath") as string;
                        object? arguments = item.ExtendedProperty("System.Link.Arguments");
                        // Unknown property types cannot establish that this is an argument-free launch.
                        entry = CreateEntry(name, id, target, arguments is null or DBNull ? null : arguments.ToString());
                    }
                    catch (COMException) { entry = new(name, id, null); }
                    if (entry != null) entries.Add(entry);
                }
                // An application can be removed during enumeration.
                catch (COMException) { }
                finally { Release((object?)item); }
            }
        }
        finally
        {
            Release((object?)items);
            Release((object?)folder);
            Release((object?)shell);
        }
        token.ThrowIfCancellationRequested();
        return Finish(entries);
    }

    private static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    internal static DiscoveredApp? CreateEntry(string name, string id, string? target, string? arguments)
    {
        var entry = new DiscoveredApp(name, id, null);
        if (id.Contains('!')) return entry;
        if (string.IsNullOrWhiteSpace(target)) return entry;
        if (target.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("http:", StringComparison.OrdinalIgnoreCase)) return null;

        // Never probe remote targets. For virtual Shell items the parsing name is sufficient.
        if (!Path.IsPathFullyQualified(target) || !char.IsAsciiLetter(target[0]) || target[1] != ':') return entry;
        string extension = Path.GetExtension(target).ToLowerInvariant();
        if (extension is ".txt" or ".rtf" or ".pdf" or ".chm" or ".htm" or ".html" or ".url" or ".msi" ||
            (extension != ".exe" && Directory.Exists(target))) return null;
        if (extension != ".exe" || !string.IsNullOrWhiteSpace(arguments) || !File.Exists(target)) return entry;
        return entry with { Path = Path.GetFullPath(target) };
    }

    internal static IReadOnlyList<DiscoveredApp> Finish(IReadOnlyList<DiscoveredApp> entries)
    {
        var unique = entries.DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        var shared = unique.Where(item => item.Path != null).GroupBy(item => item.Path!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return unique.Select(item => item.Path != null && shared.Contains(item.Path) ? item with { Path = null } : item)
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
