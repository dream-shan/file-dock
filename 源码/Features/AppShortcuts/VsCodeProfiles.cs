using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace FileDockDemo;

internal static class VsCodeProfiles
{
    internal static IReadOnlyList<string> Find(string executable, string appData)
    {
        string portable = Path.Combine(Path.GetDirectoryName(executable)!, "data");
        string edition = Path.GetFileNameWithoutExtension(executable).Equals("Code - Insiders",
            StringComparison.OrdinalIgnoreCase) ? "Code - Insiders" : "Code";
        string user = Directory.Exists(portable) ? Path.Combine(portable,"user-data","User") : Path.Combine(appData, edition, "User");
        var result = new List<string>();
        void Add(string folder)
        {
            var path = Path.Combine(folder, "keybindings.json");
            if (File.Exists(path)) result.Add(path);
        }
        Add(user);
        string profiles = Path.Combine(user, "profiles");
        if (Directory.Exists(profiles))
            foreach (string folder in Directory.EnumerateDirectories(profiles).Order(StringComparer.OrdinalIgnoreCase))
                Add(folder);
        return result;
    }
}

internal sealed partial class AppShortcutCatalogDialog
{
    private void AddDetectedProfiles(AppShortcutGroup group)
    {
        var results = new StackPanel();
        var find = CompactButton("查找本机键位配置", async () => {
            if (_busy || _closed) return;
            BeginWork("正在查找默认配置和个人配置档…");
            try
            {
                var profiles = await Task.Run(() => VsCodeProfiles.Find(group.Path,
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));
                if (_closed) return;
                results.Children.Clear();
                foreach (var path in profiles)
                    results.Children.Add(Ui.Button(path, async () => await ReadSource(path,false)));
                SetStatus(profiles.Count == 0 ? "常用位置未发现自定义键位文件。可手动选择配置或导入默认键位。" :
                    "请选择要读取的配置文件。");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!_closed) SetStatus("部分配置目录无法访问。可手动选择配置文件。");
            }
            finally { EndWork(); }
        });
        _body.Children.Add(find);
        _body.Children.Add(results);
    }
}
