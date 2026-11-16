using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using FileDockDemo.Features.ControlCenter;

namespace FileDockDemo;

internal static class DisplayPowerViewVerification
{
    internal static IEnumerable<T> Children<T>(DependencyObject root) where T:DependencyObject
    {
        for(int index=0;index<VisualTreeHelper.GetChildrenCount(root);index++)
        {
            var child=VisualTreeHelper.GetChild(root,index);if(child is T match)yield return match;
            foreach(var descendant in Children<T>(child))yield return descendant;
        }
    }
    private sealed class AudioBackend:IAudioBackend
    {
        public void Start(Action<AudioChange> changed){}
        public AudioSnapshot Read(string? output,string? microphone,AudioChange change)=>AudioSnapshot.Empty();
        public void Write(AudioTarget target,int? volume,bool? mute){}
        public void Stop(){}
        public void Dispose(){}
    }
    private static void Click(DependencyObject root,string content)=>Children<Button>(root).First(b=>b.Content?.ToString()==content).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    internal static async Task Run(MainWindow window,string directory,Action<bool,string> check)
    {
        window.UpdateLayout();
        check(Children<Slider>(window.ControlSection).Any(s=>AutomationProperties.GetName(s)=="亮度百分比"),"Control home contains a directly accessible brightness slider");
        object original=window.ControlSection.Content;string theme=window.State.ThemeMode;double height=window.Height;string? preferred=window.State.PreferredBrightnessDisplayId;
        var backend=new BrightnessVerification.Backend{ShowSecond=true};var powerBackend=new PowerVerification.Backend();
        using var brightness=new BrightnessController(()=>backend,window.Dispatcher);using var power=new PowerController(()=>powerBackend,window.Dispatcher);using var audio=new AudioController(()=>new AudioBackend(),window.Dispatcher);
        bool save=false;int settings=0;using var view=new ControlCenterView(audio,brightness,power,"screen-A",_=>Task.FromResult(save),()=>settings++);
        try
        {
            window.ControlSection.Content=view;window.UpdateLayout();await BrightnessVerification.Wait(()=>brightness.Current.Devices.Count==2&&power.Current.Plans.Count==2);
            int writes=backend.Writes;var slider=Children<Slider>(view).First(s=>AutomationProperties.GetName(s)=="亮度百分比");
            slider.Value=36;await BrightnessVerification.Wait(()=>brightness.Current.Devices[0].Percent==36);
            check(backend.Writes>writes,"Brightness slider submits the selected screen and reads its value back");
            Click(view,"显示器 ›");window.UpdateLayout();
            var selector=Children<ComboBox>(view).First(c=>AutomationProperties.GetName(c)=="亮度屏幕选择");writes=backend.Writes;selector.SelectedIndex=1;await Task.Delay(30);
            check(((BrightnessDevice)selector.SelectedItem).Id=="screen-A"&&backend.Writes==writes,"Failed screen preference save restores the original choice and never changes brightness");
            save=true;selector.SelectedIndex=1;await Task.Delay(30);Click(view,"← 控制");
            check(slider.Value==40&&backend.Writes==writes,"Choosing another screen changes the control target without writing brightness");
            Click(view,"电源详情 ›");window.UpdateLayout();
            var plans=Children<ComboBox>(view).First(c=>AutomationProperties.GetName(c)=="电源计划选择");
            check(plans.Items.Count==2&&((PowerPlan)plans.SelectedItem).Id==PowerVerification.Backend.CustomPlan,"Power detail preserves custom plan selection");
            plans.SelectedIndex=1;await BrightnessVerification.Wait(()=>power.Current.ActivePlan==Guid.Empty);
            check(powerBackend.Plan==Guid.Empty,"Power plan selection writes and reads the actual plan");
            Click(view,"打开 Windows 电源设置");check(settings==1,"System power settings entry invokes its host action");Click(view,"← 控制");
            check(PowerView.SupplyText(power.Current)=="交流供电","Power home does not present a missing battery as zero percent");
            save=true;Click(view,"显示器 ›");selector.SelectedIndex=0;Click(view,"← 控制");
            writes=backend.Writes;slider.Value=21;view.SetPaused(true);await brightness.DrainAsync();await power.DrainAsync();await Task.Delay(150);
            check(!brightness.Active&&!power.Active&&backend.Writes==writes,"Pause cancels unsubmitted brightness and stops both workers' device subscriptions");
            view.SetPaused(false);await BrightnessVerification.Wait(()=>brightness.Current.Devices.Count==2&&power.Current.Plans.Count==2);
            check(backend.Writes==writes,"Resuming control does not replay a pending slider change");
            foreach(string next in new[]{"Light","Dark"})
            {
                window.SetThemeMode(next);window.Height=480;window.UpdateLayout();
                check(Children<Slider>(view).All(s=>s.ActualWidth>=0&&s.ActualWidth<=342),"Display/power sliders remain within compact content width in "+next);
                DemoVerification.SaveRender(window,System.IO.Path.Combine(directory,"control-complete-"+next+".png"));
            }
            backend.Disconnected=true;backend.Notify!();await BrightnessVerification.Wait(()=>brightness.Current.Devices[0].Percent==null);
            check(!slider.IsEnabled,"Unreadable brightness disables adjustment instead of showing a valid zero");
            check(await window.SavePreferredBrightnessAsync("test-monitor")&&window.State.Copy().PreferredBrightnessDisplayId=="test-monitor"&&window.Storage.Load().PreferredBrightnessDisplayId=="test-monitor","Preferred display persists through the existing transactional state store");
        }
        finally{window.ControlSection.Content=original;window.Height=height;window.SetThemeMode(theme);await window.SavePreferredBrightnessAsync(preferred);}
    }
}
