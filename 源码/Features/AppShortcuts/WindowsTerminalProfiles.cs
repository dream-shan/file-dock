#nullable enable
using System;
using System.IO;
using System.Linq;

namespace FileDockDemo;

internal static class AppShortcutSourceKinds
{
    internal const string VsCode = "";
    internal const string WindowsTerminal = "windows-terminal";
}

internal enum WindowsTerminalInstallKind
{
    None,
    StoreStable,
    StorePreview,
    StoreCanary,
    Portable,
    Unpackaged,
    Ambiguous
}

internal sealed record WindowsTerminalIdentity(
    WindowsTerminalInstallKind Kind,
    string EditionLabel,
    string? SettingsPath,
    string? AmbiguityReason)
{
    internal bool IsWindowsTerminal => Kind != WindowsTerminalInstallKind.None;
    internal bool CanAutoLocate => SettingsPath != null && AmbiguityReason == null;
}

internal static class WindowsTerminalProfiles
{
    internal const string StablePackageFamily = "Microsoft.WindowsTerminal_8wekyb3d8bbwe";
    internal const string PreviewPackageFamily = "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe";
    internal const string CanaryPackageFamily = "Microsoft.WindowsTerminalCanary_8wekyb3d8bbwe";

    internal const string StableAppId = StablePackageFamily + "!App";
    internal const string PreviewAppId = PreviewPackageFamily + "!App";
    internal const string CanaryAppId = CanaryPackageFamily + "!App";

    internal static bool IsKnownStoreAppId(string? appId) =>
        ClassifyAppId(appId) is WindowsTerminalInstallKind.StoreStable
            or WindowsTerminalInstallKind.StorePreview
            or WindowsTerminalInstallKind.StoreCanary;

    internal static WindowsTerminalIdentity Resolve(string? executablePath, string? appId, string localAppData,
        string? displayName = null, bool hasTerminalSource = false)
    {
        var byAppId = ClassifyAppId(appId);
        // An explicit packaged AppUserModelID that is not one of the known Windows Terminal
        // packages must never fall through to path or name heuristics.
        if (appId != null && appId.Contains('!') && byAppId == WindowsTerminalInstallKind.None)
            return None();

        var (byPath, portableDir, pathReason) = ClassifyPath(executablePath);

        if (byAppId != WindowsTerminalInstallKind.None && byPath != WindowsTerminalInstallKind.None)
        {
            if (byPath == WindowsTerminalInstallKind.Ambiguous)
                return Build(byAppId, localAppData, null);
            if (byPath != byAppId)
            {
                return new(WindowsTerminalInstallKind.Ambiguous, "Windows 终端（版本不一致）", null,
                    "应用标识与程序路径对应的 Windows 终端版本不一致，为避免混用配置已停止自动查找，请手动选择 settings.json。");
            }
            return Build(byAppId, localAppData, null);
        }

        if (byAppId != WindowsTerminalInstallKind.None)
            return Build(byAppId, localAppData, null);

        if (byPath == WindowsTerminalInstallKind.Ambiguous)
            return new(WindowsTerminalInstallKind.Ambiguous, "Windows 终端", null, pathReason);

        if (byPath != WindowsTerminalInstallKind.None)
            return Build(byPath, localAppData, portableDir);

        if (IsTerminalShortcutOrName(executablePath, displayName, hasTerminalSource))
        {
            return new(WindowsTerminalInstallKind.Ambiguous, "Windows 终端", null,
                "当前入口无法确认 Windows 终端的安装类型（稳定版、预览版、便携版或未打包版），请手动选择 settings.json，或从应用列表/运行窗口选择具体实例。");
        }

        return None();
    }

    private static WindowsTerminalIdentity None() =>
        new(WindowsTerminalInstallKind.None, "", null, null);

    private static WindowsTerminalIdentity Build(WindowsTerminalInstallKind kind, string localAppData, string? portableDir)
    {
        string? PackageSettings(string family) => string.IsNullOrWhiteSpace(localAppData) ? null :
            Path.Combine(localAppData, "Packages", family, "LocalState", "settings.json");

        return kind switch
        {
            WindowsTerminalInstallKind.StoreStable => new(kind, "Windows 终端（商店稳定版）",
                PackageSettings(StablePackageFamily),
                string.IsNullOrWhiteSpace(localAppData) ? "无法读取 LocalAppData 目录，请手动选择 settings.json。" : null),
            WindowsTerminalInstallKind.StorePreview => new(kind, "Windows 终端 Preview（商店预览版）",
                PackageSettings(PreviewPackageFamily),
                string.IsNullOrWhiteSpace(localAppData) ? "无法读取 LocalAppData 目录，请手动选择 settings.json。" : null),
            WindowsTerminalInstallKind.StoreCanary => new(kind, "Windows 终端 Canary（商店 Canary 版）",
                PackageSettings(CanaryPackageFamily),
                string.IsNullOrWhiteSpace(localAppData) ? "无法读取 LocalAppData 目录，请手动选择 settings.json。" : null),
            WindowsTerminalInstallKind.Portable => new(kind, "Windows 终端（便携版）",
                portableDir == null ? null : Path.Combine(portableDir, "settings", "settings.json"),
                portableDir == null ? "无法定位便携版目录，请手动选择 settings.json。" : null),
            WindowsTerminalInstallKind.Unpackaged => new(kind, "Windows 终端（未打包版）",
                string.IsNullOrWhiteSpace(localAppData) ? null :
                    Path.Combine(localAppData, "Microsoft", "Windows Terminal", "settings.json"),
                string.IsNullOrWhiteSpace(localAppData) ? "无法读取 LocalAppData 目录，请手动选择 settings.json。" : null),
            _ => None()
        };
    }

    private static WindowsTerminalInstallKind ClassifyAppId(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return WindowsTerminalInstallKind.None;
        return appId.Trim().ToLowerInvariant() switch
        {
            "microsoft.windowsterminal_8wekyb3d8bbwe!app" => WindowsTerminalInstallKind.StoreStable,
            "microsoft.windowsterminalpreview_8wekyb3d8bbwe!app" => WindowsTerminalInstallKind.StorePreview,
            "microsoft.windowsterminalcanary_8wekyb3d8bbwe!app" => WindowsTerminalInstallKind.StoreCanary,
            _ => WindowsTerminalInstallKind.None
        };
    }

    private static (WindowsTerminalInstallKind Kind, string? PortableDir, string? Reason) ClassifyPath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath))
            return (WindowsTerminalInstallKind.None, null, null);

        string fullPath;
        try { fullPath = Path.GetFullPath(executablePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (WindowsTerminalInstallKind.None, null, null);
        }

        string extension = Path.GetExtension(fullPath);
        if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return (WindowsTerminalInstallKind.None, null, null);

        string fileName = Path.GetFileName(fullPath);
        bool isTerminalExe = fileName.Equals("WindowsTerminal.exe", StringComparison.OrdinalIgnoreCase);
        bool isWtExe = fileName.Equals("wt.exe", StringComparison.OrdinalIgnoreCase);
        if (!isTerminalExe && !isWtExe)
            return (WindowsTerminalInstallKind.None, null, null);

        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
            return (WindowsTerminalInstallKind.Ambiguous, null, "无法定位程序所在目录，请手动选择 settings.json。");

        string[] segments = directory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        bool underWindowsApps = segments.Any(segment => segment.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase));
        if (underWindowsApps)
        {
            foreach (string segment in segments)
            {
                if (MatchesPackageSegment(segment, "Microsoft.WindowsTerminal", StablePackageFamily))
                    return (WindowsTerminalInstallKind.StoreStable, null, null);
                if (MatchesPackageSegment(segment, "Microsoft.WindowsTerminalPreview", PreviewPackageFamily))
                    return (WindowsTerminalInstallKind.StorePreview, null, null);
                if (MatchesPackageSegment(segment, "Microsoft.WindowsTerminalCanary", CanaryPackageFamily))
                    return (WindowsTerminalInstallKind.StoreCanary, null, null);
            }

            return (WindowsTerminalInstallKind.Ambiguous, null,
                "无法仅凭通用执行别名或当前 WindowsApps 路径区分稳定版、预览版或 Canary 版，请从应用列表选择具体版本或手动选择 settings.json。");
        }

        if (isWtExe && !File.Exists(Path.Combine(directory, "WindowsTerminal.exe")))
        {
            return (WindowsTerminalInstallKind.Ambiguous, null,
                "当前选择的是独立 wt.exe 入口，无法确认实际安装目录及是否为便携版，请手动选择 settings.json 或选择 WindowsTerminal.exe。");
        }

        if (File.Exists(Path.Combine(directory, ".portable")))
            return (WindowsTerminalInstallKind.Portable, directory, null);

        return (WindowsTerminalInstallKind.Unpackaged, null, null);
    }

    private static bool MatchesPackageSegment(string segment, string packagePrefix, string exactFamily) =>
        segment.Equals(exactFamily, StringComparison.OrdinalIgnoreCase) ||
        (segment.StartsWith(packagePrefix + "_", StringComparison.OrdinalIgnoreCase) &&
         segment.EndsWith("_8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase));

    private static bool IsTerminalShortcutOrName(string? executablePath, string? displayName, bool hasTerminalSource)
    {
        if (hasTerminalSource) return true;
        if (!string.IsNullOrWhiteSpace(executablePath) &&
            Path.GetExtension(executablePath).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            string stem = Path.GetFileNameWithoutExtension(executablePath).Trim();
            if (MatchesTerminalLabel(stem)) return true;
        }
        return !string.IsNullOrWhiteSpace(displayName) && MatchesTerminalLabel(displayName.Trim());
    }

    private static bool MatchesTerminalLabel(string value) =>
        value.Equals("Windows Terminal", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Windows Terminal Preview", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Windows Terminal Canary", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Windows 终端", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Windows 终端预览版", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Windows 终端 Preview", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Windows 终端 Canary", StringComparison.OrdinalIgnoreCase);
}
