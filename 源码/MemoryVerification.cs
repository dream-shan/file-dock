using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;

namespace FileDockDemo;

internal static class MemoryVerification
{
    internal static async Task Run(MainWindow window,string directory,Action<bool,string> check)
    {
        foreach(bool dark in new[]{false,true})
        {
            var colors=new[]{50d,55,60,65,70,75,80}.Select(p=>MemoryPressure.ColorFor(p,dark)).ToArray();
            check(colors.Distinct().Count()==colors.Length && MemoryPressure.ColorFor(81,dark)==colors[^1] && MemoryPressure.ColorFor(30,dark)==colors[0],
                $"Memory colors interpolate continuously and clamp outside thresholds ({dark})");
        }
        check(!MemoryPressure.Warning(false,80) && MemoryPressure.Warning(false,80.01) && MemoryPressure.Warning(true,78) && !MemoryPressure.Warning(true,77.99),
            "Memory warning uses strict eighty-percent entry and seventy-eight-percent recovery");
        check(new MemoryReading(0,0).Percent==null && new MemoryReading(50,100).Percent==50 && !MemoryPressure.Warning(true,null),
            "Unavailable memory remains unknown and clears stale warnings");
        var gate=new MemoryActionGate();
        check(gate.TryStart(1000) && !gate.TryStart(2000),"Memory action cannot overlap another active request");
        gate.Complete(3000);
        check(gate.Remaining(3000)==10 && gate.Remaining(12999)==1 && !gate.TryStart(12999),"Cooldown starts after completion and lasts ten seconds");
        check(gate.TryStart(13000),"A new manual memory action becomes available at the cooldown boundary");
        int other=Environment.ProcessId+1000000;
        bool Allowed(bool user=true,bool elevated=false,bool critical=false,bool system=false,int session=3,uint saved=0,uint foreground=0) => MemoryTools.Eligible(other,session,3,user,elevated,critical,system,saved,foreground);
        check(Allowed() && !Allowed(user:false) && !Allowed(elevated:true) && !Allowed(critical:true) && !Allowed(system:true) && !Allowed(session:4),
            "Memory cleanup policy excludes other users, sessions, elevated and system processes");
        check(!Allowed(saved:(uint)other) && !Allowed(foreground:(uint)other) && !MemoryTools.TryTrim(Environment.ProcessId,0),
            "Memory cleanup protects both foreground identities and File Dock itself");
        var real=MemoryTools.Read(); check(real.Total>0 && real.Used<=real.Total,"Memory-only native read returns valid physical capacity");
        string probeDir=Path.Combine(directory,"memory-probe"); Directory.CreateDirectory(probeDir);
        using(var probe=Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=false,CreateNoWindow=true,Arguments="--memory-trim-probe \""+probeDir+"\"" })!)
        {
            try
            {
                for(int i=0;i<100 && !File.Exists(Path.Combine(probeDir,"ready"));i++)await Task.Delay(50);
                if(!File.Exists(Path.Combine(probeDir,"ready")))throw new InvalidOperationException("Memory fixture did not start");
                probe.Refresh();long before=probe.WorkingSet64;
                bool trimmed=MemoryTools.TryTrim(probe.Id,0);probe.Refresh();long after=probe.WorkingSet64;
                using var identity=System.Security.Principal.WindowsIdentity.GetCurrent();
                bool elevated=new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                check(elevated ? !trimmed : trimmed && after<before,
                    elevated ? "Elevated isolated fixture is correctly excluded from working-set cleanup" : "Real working-set cleanup reduces the isolated fixture without terminating it");
                check(!probe.HasExited && !MemoryTools.TryTrim(probe.Id,(uint)probe.Id),"Protected process is skipped while the fixture remains running");
            }
            finally { File.WriteAllText(Path.Combine(probeDir,"stop"),"stop");if(!probe.WaitForExit(5000))probe.Kill(); }
        }
        await window.NavigateToFolderAsync(directory); window.EnableMemoryForVerification();window.CollapsePanel();
        int full=window.PerformanceSamples, memory=window.MemoryReadCount;
        await Task.Delay(4300);
        check(window.MemoryOnlyActive && window.MemoryReadCount-memory is >=2 and <=3 && window.PerformanceSamples==full,
            "Collapsed panel checks only memory every two seconds without sampling hardware");
        window.SetMemoryPauseForVerification(true);memory=window.MemoryReadCount;await Task.Delay(2200);
        check(!window.MemoryOnlyActive && !window.PerformanceActive && memory==window.MemoryReadCount,"Locked session pauses memory checks and full performance sampling");
        window.SetMemoryPauseForVerification(false);await Task.Delay(2200);
        check(window.MemoryOnlyActive && window.MemoryReadCount>memory,"Unlock resumes the lightweight memory check");
        window.ExpandPanel();window.GoHome();await Task.Delay(100);
        check(window.PerformanceActive && !window.MemoryOnlyActive,"Expanded home reuses one-second samples without a second memory timer");
        window.SetMemoryPauseForVerification(true);
        string original=window.State.ThemeMode;
        foreach(string theme in new[]{"Light","Dark"})
        {
            window.SetThemeMode(theme);window.ApplyMemoryReading(new(85,100));await Task.Delay(350);
            check(window.MemoryCard.Notice=="占用偏高" && window.MemoryCard.ValueText=="85%" &&
                ((System.Windows.Media.SolidColorBrush)window.MemoryCard.Accent).Color==MemoryPressure.ColorFor(85,theme=="Dark"),
                $"Memory card uses readable high-pressure color and warning in {theme} theme");
            window.DockEntry.SetMemoryPressure(85,false);
            var color=window.DockEntry.MemoryAccentColor;var expected=MemoryPressure.ColorFor(85,theme=="Dark");
            if(color.R!=expected.R || color.G!=expected.G || color.B!=expected.B)throw new InvalidOperationException("Dock color mismatch");
            DemoVerification.SaveRender(window,Path.Combine(directory,"memory-high-"+theme.ToLowerInvariant()+".png"));
        }
        window.ApplyMemoryReading(new(77,100));
        check(window.MemoryCard.Notice.Length==0,"Memory warning clears after utilization falls below the recovery threshold");
        window.SetMemoryPauseForVerification(false);
        var finish=new TaskCompletionSource<MemoryCleanupResult>();int calls=0;
        window.MemoryCleanupForVerification=()=>{calls++;return finish.Task;};
        Task first=window.OrganizeMemoryAsync();await window.OrganizeMemoryAsync();
        check(calls==1 && !window.MemoryCard.ActionAvailable && window.MemoryCard.ActionHint=="正在整理…","Repeated clicks produce one asynchronous cleanup operation");
        finish.SetResult(new(new(85,100),new(79,100),1,0));await first;
        check(!window.MemoryCard.ActionAvailable && window.MemoryCard.ActionHint.Contains("秒后"),"Completed cleanup displays a short cooldown and actual memory readings");
        var peer=UIElementAutomationPeer.CreatePeerForElement(window.MemoryCard);
        check(peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider && window.MemoryCard.Focusable && !MainWindow.IsMovementSurface(window.MemoryCard),
            "Memory action supports keyboard and automation without starting a window drag");
        window.MemoryCleanupForVerification=null;window.SetThemeMode(original);window.ApplyMemoryReading(MemoryTools.Read());
    }
}
