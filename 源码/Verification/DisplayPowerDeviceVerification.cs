using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Text.Json;
using FileDockDemo.Features.ControlCenter;

namespace FileDockDemo;

internal static class DisplayPowerDeviceVerification
{
    private static bool Readback(Func<bool> expected)
    {
        var watch=Stopwatch.StartNew();do{if(expected())return true;Thread.Sleep(60);}while(watch.ElapsedMilliseconds<4000);return false;
    }
    internal static Task Run(string directory)=>Task.Run(()=>{
        Directory.CreateDirectory(directory);var report=new List<string>();
        void Check(bool success,string message){if(!success)throw new InvalidOperationException("FAIL: "+message);report.Add("PASS: "+message);}
        try
        {
            using(var brightness=new WindowsBrightnessBackend())
            {
                brightness.Start(()=>{});var snapshot=brightness.Read();Check(snapshot.Devices.Count>0,"Real Windows display enumeration returns monitor identities");int tested=0;
                foreach(var device in snapshot.Devices)
                {
                    if(device.Percent==null||device.Error!=null){report.Add("SKIP: Uncontrollable monitor: "+device.Error);continue;}
                    var original=brightness.Capture(device.Target);Check(original.Id==device.Id&&original.Maximum>original.Minimum,"Brightness restore point captures the original raw range and identity");
                    File.WriteAllText(Path.Combine(directory,"brightness-original.json"),JsonSerializer.Serialize(original));
                    try
                    {
                        int next=device.Percent==0?2:device.Percent.Value-1;brightness.Write(device.Target,next);
                        Check(Readback(()=>brightness.Capture(device.Target).Raw==BrightnessRange.ToRaw(original.Minimum,original.Maximum,next)),"Real monitor brightness write reads back at the expected hardware value");tested++;
                    }
                    finally
                    {
                        brightness.Restore(original);
                        Check(Readback(()=>{var current=brightness.Read().Devices.Single(d=>d.Id==original.Id);return brightness.Capture(current.Target).Raw==original.Raw;}),"Original raw brightness restored and independently read back");
                    }
                }
                if(tested==0)report.Add("SKIP: No supported monitor available for brightness writes");
                brightness.Stop();brightness.Start(()=>{});Check(brightness.Read().Devices.Count>0,"Brightness monitor resources reopen after stopping");
            }
            using(var power=new WindowsPowerBackend())
            {
                power.Start(()=>{});var snapshot=power.Read();Check(snapshot.Plans.Count>0&&snapshot.ActivePlan.HasValue,"Real power plan enumeration and active plan read succeed");
                var original=power.Capture();Check(original.Plan==snapshot.ActivePlan,"Power restore point records the original custom plan GUID");
                File.WriteAllText(Path.Combine(directory,"power-original.json"),JsonSerializer.Serialize(original));
                // Without complete mode capture, a plan switch could lose an unknown OS preference.
                if(original.AC==null||original.DC==null){report.Add("SKIP: Full AC/DC preference capture unavailable; no power writes performed");return;}
                try
                {
                    if(snapshot.CanSetMode)
                    {
                        var next=snapshot.UserMode==PowerModeChoice.Efficiency?PowerModeChoice.Balanced:PowerModeChoice.Efficiency;
                        power.SetMode(next,snapshot.Supply);Check(Readback(()=>power.Read().UserMode==next),"Real power mode writes and reads the user preference for the current supply");
                    }
                    else report.Add("SKIP: User power mode is unavailable");
                    var alternate=snapshot.Plans.FirstOrDefault(p=>p.Id!=original.Plan);
                    if(alternate!=null){power.SetPlan(alternate.Id);Check(Readback(()=>power.Read().ActivePlan==alternate.Id),"Switching an existing power plan reads back its exact GUID");}
                    else report.Add("SKIP: No alternative plan exists");
                }
                finally
                {
                    power.Restore(original);
                    Check(Readback(()=>power.Capture()==original),"Original plan and both AC/DC preferences restored and verified");
                }
                power.Stop();power.Start(()=>{});Check(power.Read().ActivePlan==original.Plan,"Power notifications can reopen without changing the plan");
            }
        }
        finally{File.WriteAllLines(Path.Combine(directory,"display-power-verification.txt"),report);}
    });
}
