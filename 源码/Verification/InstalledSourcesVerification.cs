using System;
using System.Linq;

namespace FileDockDemo;

internal static class InstalledSourcesVerification
{
    internal static void Run(Action<bool, string> check)
    {
        var edge = AppMenuCatalog.OfficialReference(@"C:\Apps\Microsoft Edge\MSEDGE.EXE");
        var terminal = AppMenuCatalog.OfficialReference("", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App");
        var calculator = AppMenuCatalog.OfficialReference("", "microsoft.windowscalculator_8wekyb3d8bbwe!app");
        check(edge?.Rules.Any(rule => rule.Key == "Ctrl+Shift+T") == true &&
              AppMenuCatalog.OfficialReference("", "MSEdge")?.Title == edge.Title,
              "Edge 的桌面程序和开始菜单标识可查同一份官方参考");
        check(terminal?.Rules.Any(rule => rule.Key == "Ctrl+Shift+D") == true &&
              AppMenuCatalog.OfficialReference(@"C:\Apps\WindowsTerminal.exe")?.Title == terminal.Title,
              "Windows 终端的商店标识和桌面程序均可读取官方参考");
        check(calculator?.Rules.Any(rule => rule.Key == "Alt+Up" && rule.Command.Contains("置顶")) == true &&
              calculator.Scope.Contains("标准模式"),
              "计算器按完整商店标识匹配并说明置顶键的适用模式");
        check(AppMenuCatalog.OfficialReference(null, "Vendor.Tool") == null &&
              AppMenuCatalog.OfficialReference("", "Other.WindowsCalculator_8wekyb3d8bbwe!App") == null &&
              AppMenuCatalog.OfficialReference("", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!Other") == null &&
              AppMenuCatalog.OfficialReference("", "计算器") == null,
              "相似名称、同包其他入口和显示名称不匹配官方参考");
        check(AppMenuCatalog.OfficialReference(@"C:\Windows\ApplicationFrameHost.exe") == null &&
              AppMenuCatalog.OfficialReference(@"C:\Apps\msedge_proxy.exe") == null,
              "通用宿主和浏览器网页应用代理没有专属软件默认键位");
        var sources = new[] { edge, terminal, calculator };
        check(sources.All(source => source != null && Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) &&
              uri.Scheme == Uri.UriSchemeHttps && uri.Host is "support.microsoft.com" or "learn.microsoft.com" &&
              source.Rules.All(rule => rule.Mapping == null) && source.Scope.Contains("默认")),
              "新增官方参考均带有微软文档链接和默认标注且不含映射");
    }
}
