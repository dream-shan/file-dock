using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FileDockDemo;

internal static class ApplicationsVerification
{
    internal static void Run(Action<bool, string> check)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FileDock-apps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var exe = System.IO.Path.Combine(directory, "First.exe");
        var link = System.IO.Path.Combine(directory, "Second.lnk");
        var text = System.IO.Path.Combine(directory, "readme.txt");
        File.WriteAllText(exe, "fixture"); File.WriteAllText(link, "fixture"); File.WriteAllText(text, "fixture");
        var owner = new Window();
        try
        {
            var entries = new List<ApplicationEntry>(); bool reject = false; string launched = ""; bool error = false;
            Task<bool> Save(Action<List<ApplicationEntry>> mutation)
            {
                var candidate = entries.Select(e => e.Copy()).ToList(); mutation(candidate);
                if (reject) return Task.FromResult(false);
                entries = candidate; return Task.FromResult(true);
            }
            var view = new AppLauncherView(owner, () => entries, Save, (_, failed) => error = failed, _ => { }, path => launched = path);
            bool Add(string path) => view.AddAsync(path).GetAwaiter().GetResult();
            check(Add(exe) && Add(link) && entries.Count == 2, "Applications accept existing exe and lnk files");
            check(!Add(text) && !Add(directory) && !Add(System.IO.Path.Combine(directory, "missing.exe")) && entries.Count == 2,
                "Applications reject unsupported, directory and missing paths");
            check(!Add(exe.ToUpperInvariant()) && entries.Count == 2, "Applications reject duplicate paths without changing entries");
            var first = entries[0].Id; var second = entries[1].Id;
            var copy = entries[0].Copy(); copy.DisplayName = "copy";
            check(entries[0].DisplayName == "First", "Application snapshot isolates entry changes");
            entries.Reverse();
            check(view.RenameAsync(first, "  Editor  ").Result && entries[0].Id == first && entries[0].DisplayName == "Editor", "Application rename trims the display name and preserves persisted order");
            check(!view.RenameAsync(first, "   ").Result && entries[0].DisplayName == "Editor", "Application rename rejects blank names");
            check(view.MoveAsync(second, -1).Result && entries[0].Id == second && entries.Select(e => e.Order).SequenceEqual(new[] { 0, 1 }),
                "Applications move within explicit saved order");
            view.Launch(entries.Single(e => e.Id == second));
            check(launched == link, "Application launcher preserves the original shortcut path");
            reject = true;
            check(!view.RemoveAsync(first).Result && entries.Count == 2, "Failed application save preserves live entries");
            reject = false;
            File.Delete(link); launched = ""; view.Launch(entries.Single(e => e.Id == second));
            check(launched == "" && error, "Missing application reports failure without launching a process");
            check(view.RelocateAsync(second, exe).Result == false, "Relocation rejects another entry's path");
            File.WriteAllText(link, "fixture");
            check(view.RemoveAsync(first).Result && view.RelocateAsync(second, exe).Result && entries[0].Id == second && entries[0].Path == exe,
                "Relocation preserves entry identity and remove affects only its entry");
            view.Refresh(); view.View.Measure(new Size(330, 400)); view.View.Arrange(new Rect(0, 0, 330, 400));
            var grid = Descendants(view.View).OfType<UniformGrid>().Single();
            check(grid.Columns == 3 && Descendants(grid).OfType<Button>().Any(b => b.ToolTip?.ToString() == exe),
                "Applications render keyboard reachable launch buttons in a compact three-column grid");
            var pending = new TaskCompletionSource<bool>();
            var guarded = new AppLauncherView(owner, () => entries, _ => pending.Task, (_, _) => { }, _ => { });
            var firstSave = guarded.RenameAsync(second, "Pending");
            check(!guarded.RemoveAsync(second).Result, "Application mutations reject overlapping save requests");
            pending.SetResult(false); firstSave.GetAwaiter().GetResult();
        }
        finally { owner.Close(); Directory.Delete(directory, true); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i); yield return child;
            foreach (var next in Descendants(child)) yield return next;
        }
    }
}
