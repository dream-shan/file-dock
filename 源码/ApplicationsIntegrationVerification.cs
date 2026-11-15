using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace FileDockDemo;

internal static class ApplicationsIntegrationVerification
{
    internal static async Task Run(MainWindow window,string directory,Action<bool,string> check)
    {
        var oldApps=window.State.Applications;string page=window.State.SelectedPage;
        string oldTheme=window.State.ThemeMode;double oldWidth=window.Width,oldHeight=window.Height;
        var dock=window.ConfiguredShortcut(ShortcutCommand.ToggleDock);
        var pin=window.ConfiguredShortcut(ShortcutCommand.ToggleWindowPin);
        try
        {
            var store=new StorageService(Path.Combine(directory,"apps-upgrade"));
            store.Save(new AppState());
            var loaded=store.Load();
            check(loaded.Applications.Count==0 && loaded.DockHotkeyEnabled && loaded.DockHotkeyKey==0x20,"Existing config defaults to an empty applications page and original reveal shortcut");
            window.State.Applications=[new(){Path=Environment.ProcessPath!,DisplayName="文件坞测试"}];
            var copy=window.State.Copy();window.State.Applications[0].DisplayName="更名";
            check(copy.Applications[0].DisplayName=="文件坞测试","Application save snapshots detach list entries");
            string launchDirectory=Path.Combine(directory,"application launch 中文");Directory.CreateDirectory(launchDirectory);
            string marker=Path.Combine(launchDirectory,"launched.txt"),link=Path.Combine(launchDirectory,"启动测试.lnk");
            DemoVerification.CreateProbeShortcut(link,marker,launchDirectory);
            window.LaunchApplicationForVerification(new(){Path=link,DisplayName="启动验证"});
            for(int i=0;i<80&&!File.Exists(marker);i++)await Task.Delay(50);
            check(File.Exists(marker)&&File.ReadAllText(marker).Equals(launchDirectory,StringComparison.OrdinalIgnoreCase),"Applications launches a real shortcut preserving arguments and working directory");
            window.State.Applications=Enumerable.Range(0,24).Select(i=>new ApplicationEntry{Path=Environment.ProcessPath!,DisplayName=$"应用 {i+1}",Order=i}).ToList();
            window.RefreshApplicationsForVerification();
            window.SelectPage("Applications");window.UpdateLayout();
            check(window.IsApplicationsPage && window.ApplicationsSection.IsVisible && !window.FileSearchSurface.IsVisible && !window.ToolsSection.IsVisible && !window.PerformanceSection.IsVisible,"Applications has an independent page without file search or hidden-page overlap");
            await window.WaitForSavesAsync();
            check(window.Storage.Load().Applications.Count==24 && window.Storage.Load().SelectedPage=="Applications","Applications and selected page persist using the existing storage");
            foreach(string theme in new[]{"dark","light"})
            {
                window.SetThemeMode(theme=="dark"?"Dark":"Light");window.Width=380;window.Height=480;window.UpdateLayout();
                DemoVerification.SaveRender(window,Path.Combine(directory,"applications-"+theme+".png"));
            }
            var editor=new ShortcutSettingsView(window.ConfiguredShortcut,_=>"已启用",window.SaveShortcutAsync);
            var preview=new Window{Content=editor.View,Width=430,Height=520,Left=-10000,Top=-10000,ShowActivated=false};
            preview.Resources.MergedDictionaries.Add(window.Resources);
            preview.SetResourceReference(Window.BackgroundProperty,"Surface");preview.SetResourceReference(Window.ForegroundProperty,"Ink");
            try{preview.Show();DemoVerification.SaveRender(preview,Path.Combine(directory,"shortcuts.png"));}finally{preview.Close();}
            var result=await window.SaveShortcutAsync(ShortcutCommand.ToggleDock,new(true,7,0x78));
            check(result.Success && window.Storage.Load().DockHotkeyKey==0x78,"Unified shortcut save updates runtime and persisted reveal binding");
            result=await window.SaveShortcutAsync(ShortcutCommand.ToggleWindowPin,new(true,7,0x78));
            check(!result.Success && window.ConfiguredShortcut(ShortcutCommand.ToggleWindowPin)==pin,"Duplicate command binding is rejected without changing the other command");
            result=await window.SaveShortcutAsync(ShortcutCommand.ToggleDock,new(false,7,0x78));
            check(result.Success && !window.Storage.Load().DockHotkeyEnabled,"Disabling reveal persists while mouse entry stays available");
        }
        finally
        {
            window.State.Applications=oldApps;window.RefreshApplicationsForVerification();
            window.SetThemeMode(oldTheme);window.Width=oldWidth;window.Height=oldHeight;
            await window.SaveShortcutAsync(ShortcutCommand.ToggleDock,dock);
            window.SelectPage(page,false);window.Persist();
        }
    }
}
