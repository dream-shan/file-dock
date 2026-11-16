using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FileDockDemo.Features.ControlCenter;

namespace FileDockDemo;

internal static class ControlReviewRegression
{
    internal static async Task Run(MainWindow window,Action<bool,string> check)
    {
        var failed=new List<string>();void Case(bool success,string name){if(success)check(true,name);else failed.Add(name);}
        var writes=new List<BrightnessTarget>();var row=new BrightnessRow("测试",(target,_)=>writes.Add(target),()=>{},_=>false);var frame=new Border{Width=300,Child=row};
        row.Bind(new("A","A屏幕",true,50,Connection:1));frame.Measure(new Size(300,200));frame.Arrange(new Rect(0,0,300,200));frame.UpdateLayout();
        var slider=DisplayPowerViewVerification.Children<Slider>(row).Single();
        slider.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});
        row.Bind(new("B","B屏幕",true,40,Connection:2));slider.Value=20;
        Case(writes.Count==0,"A disconnected screen cannot retarget an ongoing brightness drag to another screen");
        slider.RaiseEvent(new DragCompletedEventArgs(0,0,true){RoutedEvent=Thumb.DragCompletedEvent});
        slider.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});slider.Value=30;
        Case(writes.LastOrDefault()==new BrightnessTarget("B",2),"A fresh brightness gesture can adjust the new screen after cancellation");
        var starting=new BrightnessVerification.Backend();starting.StartGate.Reset();
        using(var controller=new BrightnessController(()=>starting,window.Dispatcher))
        {
            controller.SetActive(true);await BrightnessVerification.Wait(()=>starting.Starts==1);controller.SetActive(false);starting.StartGate.Set();await controller.DrainAsync();
            Case(starting.Reads==0,"Pausing during backend initialization does not start a later hardware read");
        }
        var writing=new BrightnessVerification.Backend();
        using(var controller=new BrightnessController(()=>writing,window.Dispatcher))
        {
            controller.SetActive(true);await BrightnessVerification.Wait(()=>controller.Current.Devices.Count==1);int reads=writing.Reads;writing.WriteGate.Reset();
            controller.SetBrightness(controller.Current.Devices[0].Target,35);await BrightnessVerification.Wait(()=>writing.WriteEntered.IsSet);controller.SetActive(false);writing.WriteGate.Set();await controller.DrainAsync();
            Case(writing.Reads==reads,"Pausing during a write prevents a new hardware read after it completes");
        }
        var access=new PowerModeAccess();access.Report(PowerSupply.AC,5);
        Case(access.Error(PowerSupply.AC)!=null&&access.Error(PowerSupply.Battery)==null,"A confirmed policy denial disables only the affected supply mode capability");
        access.Reset();access.Report(PowerSupply.AC,170);
        Case(access.Error(PowerSupply.AC)==null,"Transient mode failures and refresh do not permanently disable controls");
        foreach(int count in new[]{2,3,4})
        {
            var values=Enumerable.Range(0,count).Select(index=>(@"DISPLAY\ABC\INSTANCE_"+index,50));
            Case(WindowsBrightnessBackend.UniqueWmiValues(values).Count==0,$"All {count} conflicting WMI identities are rejected");
        }
        if(failed.Count>0)throw new InvalidOperationException("Review regression failures:\n"+string.Join("\n",failed));
    }
}
