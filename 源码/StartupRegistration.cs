using System;
using System.IO;
using Microsoft.Win32;

namespace FileDockDemo;

internal static class StartupRegistration
{
    // Keep the registry value name compatible with existing installations.
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    internal static string? UpdatedCommand(string? command, string executable)
    {
        if (!string.Equals(Path.GetFileName(executable), "FileDock.exe", StringComparison.OrdinalIgnoreCase)) return null;
        string legacy = Path.Combine(Path.GetDirectoryName(executable)!, "FileDockDemo.exe");
        if (!string.Equals(command, "\"" + legacy + "\"", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(command, legacy, StringComparison.OrdinalIgnoreCase)) return null;
        return "\"" + executable + "\"";
    }

    internal static bool MigrateLegacyPath()
    {
        try
        {
            string? updated;
            using (var read = Registry.CurrentUser.OpenSubKey(RunKey))
                updated = UpdatedCommand(read?.GetValue("FileDockDemo") as string, Environment.ProcessPath!);
            if (updated is null) return true; // Never enable startup for someone who did not request it.
            using var write = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (write is null) return false;
            // Re-read in case the user changed startup while the key was being opened.
            updated = UpdatedCommand(write.GetValue("FileDockDemo") as string, Environment.ProcessPath!);
            if (updated is not null) write.SetValue("FileDockDemo", updated);
            return true;
        }
        catch { return false; } // File access and normal startup remain available without registry access.
    }
}
