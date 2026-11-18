using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Xml.Linq;
using FileDockDemo.Features.ControlCenter;

namespace FileDockDemo;

internal static class WirelessVerification
{
    internal static async Task Run(MainWindow window,string directory,Action<bool,string> check)
    {
        var profile=new WifiNetwork("E4B8ADE69687","中文",null,WifiSecurity.Wpa2Personal,72,false,true,7,4);
        var document=XElement.Parse(WifiProfile.Create(profile,"测试 & <网络>","pas<&>s123",true));
        var ns=document.Name.Namespace;
        check(document.Element(ns+"name")?.Value=="测试 & <网络>"&&document.Descendants(ns+"hex").Single().Value==profile.HexSsid
            &&document.Descendants(ns+"keyMaterial").Single().Value=="pas<&>s123","New Wi-Fi profile escapes input and preserves the precise SSID bytes");
        check(document.Element(ns+"connectionMode")?.Value=="auto","Wi-Fi profile requests auto-connect only when explicitly selected");
        var sae=XElement.Parse(WifiProfile.Create(profile with{Security=WifiSecurity.Wpa3Personal},"SAE","safe1234",false));
        check(sae.Descendants(ns+"authentication").Single().Value=="WPA3SAE"&&sae.Element(ns+"connectionMode")?.Value=="manual","WPA3 uses SAE and a temporary manual connection profile");
        var open=XElement.Parse(WifiProfile.Create(profile with{Security=WifiSecurity.Open},"Open","unused",false));
        check(!open.Descendants(ns+"sharedKey").Any()&&open.Descendants(ns+"encryption").Single().Value=="none","Open networks never put password material into the connection profile");
        bool rejected=false;try{WifiProfile.Create(profile,"bad","short",false);}catch(WirelessException){rejected=true;}
        check(rejected,"Invalid personal-network password is rejected before the native request");
        check(!profile.SameTarget(profile with{Authentication=9})&&!profile.SameTarget(profile with{Profile="another"})
            &&!profile.SameTarget(profile with{HexSsid="4142"})&&profile.SameTarget(profile with{Signal=11}),"Wi-Fi target binds exact SSID, security, and saved profile across refreshes");
        check(WindowsBluetooth.DeviceType(5,16,-1)=="键盘"&&WindowsBluetooth.DeviceType(5,32,-1)=="鼠标"
            &&WindowsBluetooth.DeviceType(-1,-1,961)=="键盘","Bluetooth input classification handles Classic and LE appearances");

        using var scope=new WirelessOperationScope();int calls=0;
        await scope.RunAsync(_=>{calls++;return Task.FromResult<string?>(null);});
        check(calls==0,"Hidden wireless scope never starts a queued radio or network operation");
        scope.SetActive(true);
        var started=new TaskCompletionSource<CancellationToken>();var finish=new TaskCompletionSource<string?>();
        var pending=scope.RunAsync(token=>{started.TrySetResult(token);return finish.Task;});
        var session=await started.Task;scope.SetActive(false);scope.SetActive(true);
        await scope.RunAsync(_=>{calls++;return Task.FromResult<string?>(null);});
        finish.SetResult("stale completion");await pending;
        check(session.IsCancellationRequested&&scope.Message==null&&calls==0,"Reopening wireless control cancels old context, ignores its result, and does not queue concurrent writes");
        using(var detail=new CancellationTokenSource())
        {
            Task<string?> FinishOnCancel(CancellationToken token)=>Task.Delay(Timeout.Infinite,token).ContinueWith<string?>(_=>"stale detail",TaskScheduler.Default);
            var detailWork=scope.RunAsync(FinishOnCancel,detail.Token);detail.Cancel();await detailWork;
            check(scope.Message==null,"Closing a wireless detail panel suppresses late scan or connection results");
        }

        var view=(ControlCenterView)window.ControlSection.Content;
        Button Name(string value)=>DisplayPowerViewVerification.Children<Button>(view).Single(b=>AutomationProperties.GetName(b)==value);
        void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var oldTheme=window.State.ThemeMode;
        try
        {
            foreach(string theme in new[]{"Light","Dark"})
            {
                window.SetThemeMode(theme);window.UpdateLayout();
                var wifi=Name("Wi-Fi 详情");var bluetooth=Name("蓝牙 详情");
                check(wifi.IsVisible&&bluetooth.IsVisible&&wifi.ActualWidth>=55&&bluetooth.ActualWidth>=55,
                    "Both wireless detail actions are available at the compact dock width in "+theme);
                DemoVerification.SaveRender(window,Path.Combine(directory,"control-wireless-"+theme+".png"));
            }
        }
        finally{window.SetThemeMode(oldTheme);}
        Click(Name("Wi-Fi 详情"));window.UpdateLayout();
        check(DisplayPowerViewVerification.Children<Button>(view).Any(b=>b.IsVisible&&b.Content?.ToString()=="查看附近网络"),"Wi-Fi details offer on-demand scanning and a separate radio switch");
        Button Back()=>DisplayPowerViewVerification.Children<Button>(view).Single(b=>b.Content?.ToString()=="← 控制");
        Click(Back());Click(Name("蓝牙 详情"));window.UpdateLayout();
        check(DisplayPowerViewVerification.Children<Button>(view).Any(b=>b.IsVisible&&b.Content?.ToString()=="添加设备／管理蓝牙"),"Bluetooth detail page exposes the Windows pairing entry");
        check(!DisplayPowerViewVerification.Children<PasswordBox>(view).Any(p=>p.IsVisible),"Switching detail pages keeps Wi-Fi credentials out of the Bluetooth page");
        Click(Back());
    }

    internal static async Task NativeRead(string directory)
    {
        Directory.CreateDirectory(directory);
        var results=new List<string>();
        try
        {
            var wifi=await Task.Run(NativeWifi.ReadAdapters);
            results.Add($"PASS: Windows WLAN adapter/status read succeeded ({wifi.Count} interface(s); {wifi.Count(a=>a.Enabled.HasValue)} with radio status).");
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var radios=await WindowsBluetooth.RadiosAsync(timeout.Token);
            results.Add($"PASS: Windows Bluetooth radio enumeration succeeded ({radios.Count} adapter(s)).");
            var paired=await WindowsBluetooth.PairedAsync(timeout.Token);
            results.Add($"PASS: Paired Bluetooth enumeration succeeded ({paired.Count} record(s); {paired.Count(d=>d.Connected.HasValue)} with a connection state).");
        }
        finally{await File.WriteAllLinesAsync(Path.Combine(directory,"wireless-read.txt"),results);}
    }
}
