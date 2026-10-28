using System;
using System.IO;
using System.Reflection;

namespace FileDockDemo;

internal static class ReleaseVerification
{
    internal static void Run(MainWindow window, Action<bool, string> check)
    {
        check(Path.GetFileName(Environment.ProcessPath) == "FileDock.exe" &&
            typeof(App).Assembly.GetName().Name == "FileDock", "Candidate executable and assembly use the FileDock name");
        check(typeof(App).Assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product == "文件坞 File Dock",
            "Executable metadata identifies the public product");
        check(window.ReleaseBadge.Text == "RC" && App.VersionLabel.Contains("0.8.0-rc.3"),
            "Candidate badge identifies the actual release version");
        string exe = @"C:\File Dock\FileDock.exe";
        check(StartupRegistration.UpdatedCommand(null, exe) is null, "Upgrade does not enable disabled startup");
        check(StartupRegistration.UpdatedCommand(@"""C:\File Dock\FileDockDemo.exe""", exe) == "\"" + exe + "\"",
            "Upgrade migrates the quoted legacy executable in the same folder");
        check(StartupRegistration.UpdatedCommand(@"c:\file dock\filedockdemo.exe", exe) == "\"" + exe + "\"",
            "Legacy startup migration respects Windows path casing and quotes the new command");
        check(StartupRegistration.UpdatedCommand(@"""D:\Other\FileDockDemo.exe""", exe) is null &&
            StartupRegistration.UpdatedCommand(@"""C:\File Dock\FileDockDemo.exe"" --custom", exe) is null,
            "Upgrade preserves another installation or a customized startup command");
        check(StartupRegistration.UpdatedCommand("\"" + exe + "\"", exe) is null,
            "Already migrated startup needs no further writes");
    }
}
