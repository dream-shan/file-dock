using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FileDockDemo.Features.ControlCenter;

namespace FileDockDemo;

internal static class BrightnessVerification
{
    internal sealed class Backend:IBrightnessBackend
    {
        internal int Starts,Stops,Reads,Writes,Percent=50,Thread;
        internal long Connection=1;
        internal bool Fail,Disconnected=false;
        internal bool ShowSecond=false;
        internal Action? Notify;
        internal readonly ManualResetEventSlim ReadGate=new(true);
        internal readonly ManualResetEventSlim StartGate=new(true),WriteGate=new(true),WriteEntered=new(false);
        public void Start(Action changed){Starts++;StartGate.Wait();Notify=changed;Thread=Environment.CurrentManagedThreadId;}
        public BrightnessSnapshot Read(){ReadGate.Wait();Reads++;var devices=new List<BrightnessDevice>{new("screen-A","测试屏幕",true,Disconnected?null:Percent,Disconnected?"屏幕已断开":null,Connection)};if(ShowSecond)devices.Add(new("screen-B","第二屏幕",false,40,Connection:Connection));return new(devices);}
        public void Write(BrightnessTarget target,int percent){WriteEntered.Set();WriteGate.Wait();if(Fail||Disconnected||target.Id!="screen-A"||target.Connection!=Connection)throw new InvalidOperationException("模拟目标失效");Writes++;Percent=percent;}
        public void Stop(){Stops++;Notify=null;}
        public void Dispose(){Stop();ReadGate.Dispose();StartGate.Dispose();WriteGate.Dispose();WriteEntered.Dispose();}
    }
    internal static async Task Wait(Func<bool> condition)
    {
        var timer=Stopwatch.StartNew();while(!condition()&&timer.ElapsedMilliseconds<5000)await Task.Delay(15);
        if(!condition())throw new InvalidOperationException("Display/power verification timed out.");
    }
    internal static async Task Run(MainWindow window,Action<bool,string> check)
    {
        var backend=new Backend();int factories=0;using var controller=new BrightnessController(()=>{factories++;return backend;},window.Dispatcher);
        check(factories==0&&!controller.Active,"Hidden brightness module does not initialize hardware");
        controller.SetActive(true);await controller.DrainAsync();await Task.Delay(40);
        check(controller.Current.Devices.Count==1,"Brightness activation publishes device state from a worker");
        check(backend.Thread!=Environment.CurrentManagedThreadId,"Brightness hardware runs outside the UI thread");
        var target=controller.Current.Devices[0].Target;
        controller.SetBrightness(target,130);await Wait(()=>controller.Current.Devices[0].Percent==100);
        check(backend.Percent==100,"Brightness requests clamp to the supported percentage range");
        backend.Fail=true;controller.SetBrightness(target,10);await Wait(()=>controller.Current.Error!=null);
        check(controller.Current.Devices[0].Percent==100,"Brightness write failure preserves actual readback");backend.Fail=false;
        backend.Connection++;backend.Notify!();await Wait(()=>controller.Current.Devices[0].Connection==2);
        int writes=backend.Writes;controller.SetBrightness(target,20);await controller.DrainAsync();await Task.Delay(30);
        check(backend.Writes==writes,"Old connection target cannot write to a reconnected screen");
        var stale=backend.Notify;controller.SetActive(false);await controller.DrainAsync();int reads=backend.Reads;
        stale!();controller.SetBrightness(new("screen-A",2),40);await controller.DrainAsync();await Task.Delay(25);
        check(backend.Reads==reads&&backend.Writes==writes&&backend.Stops>0,"Hidden brightness module ignores stale notifications and writes");
        backend.Percent=62;controller.SetActive(true);await Wait(()=>controller.Current.Devices.Count==1&&controller.Current.Devices[0].Percent==62);
        check(backend.Writes==writes,"Brightness reopening reads without restoring an old brightness");
        check(BrightnessRange.ToPercent(20,120,220)==50&&BrightnessRange.ToRaw(20,220,25)==70,"Hardware brightness normalizes nonzero minimum and non-100 maximum");
        check(WindowsBrightnessBackend.NormalizeId(@"\\?\DISPLAY#ABC123#instance#{1234}")==WindowsBrightnessBackend.NormalizeId(@"DISPLAY\ABC123\instance_0"),"WMI brightness and GDI screen identity use the same stable monitor path");
        backend.ReadGate.Reset();backend.Notify!();await Task.Delay(30);bool responsive=false;_ = window.Dispatcher.BeginInvoke(()=>responsive=true);await Task.Delay(30);
        check(responsive,"Slow brightness driver cannot block the UI dispatcher");backend.ReadGate.Set();await controller.DrainAsync();
    }
}
