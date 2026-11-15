using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace FileDockDemo;

internal static class ApplicationsVerification
{
    internal static async Task Run(MainWindow window,string directory,Action<bool,string> check)
    {
        var original=window.State.Applications.Select(a=>a.Copy()).ToList();string page=window.State.SelectedPage;
        string marker=Path.Combine(directory,"application launch marker.txt");
        try
        {
            window.SelectPage("Applications");window.UpdateLayout();
            check(window.IsApplicationsPage&&window.ApplicationsSection.Visibility==Visibility.Visible&&window.FileSearchSurface.Visibility==Visibility.Collapsed,"Application page is independent of file search and tools");
            await window.AddApplicationsAsync(new[]{Environment.ProcessPath!,Environment.ProcessPath!,Path.Combine(directory,"no.exe")});
            check(window.State.Applications.Count==original.Count+1,"Application adding deduplicates paths and rejects missing targets");
            var app=window.State.Applications.Last().Copy();app.DisplayName="中文启动测试";app.Arguments="--launch-probe \""+marker+"\"";app.WorkingDirectory=directory;
            check(await window.SaveApplicationAsync(app),"Application edits save through existing state coordinator");
            window.LaunchApplication(app.Id);
            for(int i=0;i<80&&!File.Exists(marker);i++)await Task.Delay(50);
            check(File.Exists(marker)&&FileService.NormalizeKey(File.ReadAllText(marker))==FileService.NormalizeKey(directory),"Pinned application launches with Unicode arguments and working directory");
            var shortcut=new ApplicationEntry{Path=Path.Combine(directory,"entry.lnk"),Arguments="do not override",WorkingDirectory="invalid"};
            File.WriteAllText(shortcut.Path,"");var start=shortcut.BuildStartInfo();
            check(start.UseShellExecute&&start.Arguments==""&&start.WorkingDirectory=="","Shortcut launch delegates arguments and working directory unchanged to Windows shell");
            File.Delete(shortcut.Path);
            bool rejected=false;try{shortcut.BuildStartInfo();}catch(FileNotFoundException){rejected=true;}
            check(rejected,"Missing application target produces recoverable failure without removing its entry");
            var binding=new ShortcutBinding{Enabled=true,Modifiers=7,Key=0x75};var result=await window.ChangeShortcutAsync(ShortcutCatalog.Application(app.Id),binding);
            check(result.Success&&window.Storage.Load().Shortcuts!.ContainsKey(ShortcutCatalog.Application(app.Id)),"App shortcut saves in unified settings");
            string second=Path.Combine(directory,"排序测试.exe");File.WriteAllText(second,"test fixture, never launched");
            await window.AddApplicationsAsync(new[]{second});var secondId=window.State.Applications.Last().Id;
            await window.MoveApplicationAsync(secondId,0);
            check(window.State.Applications[0].Id==secondId&&window.Storage.Load().Applications[0].Id==secondId,"Application reordering survives persistence without launching an app");
            var copied=window.State.Copy();copied.Applications.Last().DisplayName="copy";check(window.State.Applications.Last().DisplayName=="中文启动测试","Application snapshots preserve live entry data");
            window.SelectPage("Applications",false);window.ExpandPanel();window.Width=380;window.Height=500;
            foreach(string theme in new[]{"Light","Dark"})
            {
                window.SetThemeMode(theme);window.UpdateLayout();await Task.Delay(120);DemoVerification.SaveRender(window,Path.Combine(directory,"applications-"+theme+".png"));
            }
            await window.RemoveApplicationAsync(app.Id);check(!window.State.Applications.Any(a=>a.Id==app.Id)&&!window.State.Shortcuts!.ContainsKey(ShortcutCatalog.Application(app.Id)),"Removing an app removes its stored shortcut and preserves original program");
            check(File.Exists(Environment.ProcessPath!),"Removing app entry does not remove the application file");
            await window.RemoveApplicationAsync(secondId);
        }
        finally{window.SelectPage(page,false);window.Width=380;window.Height=500;}
    }
}
