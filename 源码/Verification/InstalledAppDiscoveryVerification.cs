using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace FileDockDemo;

internal static class InstalledAppDiscoveryVerification
{
    internal static void Run(Action<bool, string> check)
    {
        var executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var app = InstalledAppDiscovery.CreateEntry("工具", "example.primary", executable, "");
        var profile = InstalledAppDiscovery.CreateEntry("工具", "example.profile", executable, "/K profile");
        var package = InstalledAppDiscovery.CreateEntry("商店工具", "Example_123!App", null, null);
        check(app?.Path == executable && profile is { Path: null } && app.Id != profile.Id,
            "Equal display names keep distinct app identities and parameterized shortcuts do not match their host");
        check(package is { Id: "Example_123!App", Path: null } &&
            InstalledAppDiscovery.CreateEntry("商店工具", "Example_123!App", executable, "") is { Path: null } &&
            InstalledAppDiscovery.CreateEntry("缺失", "app.missing", executable + ".missing.exe", "") is { Path: null } &&
            InstalledAppDiscovery.CreateEntry("相对路径", "app.relative", "cmd.exe", "") is { Path: null },
            "Package identities, missing executables and relative paths remain read-only");
        check(InstalledAppDiscovery.CreateEntry("帮助", "help", "https://example.invalid/help", "") == null,
            "A web help shortcut is excluded from the installed application list");
        var ambiguous = InstalledAppDiscovery.Finish([app!, app! with { Id = "example.other" }, profile!, package!]);
        check(ambiguous.Count == 4 && ambiguous.All(item => item.Path == null),
            "Two application identities pointing at the same executable remain separate and read-only");
        check(InstalledAppDiscovery.ReadAsync(new CancellationToken(true)).IsCanceled,
            "A canceled request never starts a Shell enumeration");
    }
}
