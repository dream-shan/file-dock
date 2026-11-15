using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileDockDemo.Features.ControlCenter;

namespace FileDockDemo;

internal static class AudioControlVerification
{
    private sealed class Backend : IAudioBackend
    {
        internal int Reads,Writes,Starts,Stops,Thread;
        internal int Volume=63;internal bool Muted,Fail;
        internal Action<AudioChange>? Notify;
        internal AudioTarget? LastTarget;
        internal string? OutputSelection;
        public void Start(Action<AudioChange> changed){Thread=Environment.CurrentManagedThreadId;Starts++;Notify=changed;}
        public AudioSnapshot Read(string? output,string? microphone,AudioChange change)
        {
            Reads++;OutputSelection=output;return new([new("out","测试扬声器",false,true),new("mic","测试麦克风",true,true)],
                new(new("out"),"测试扬声器",Volume,Muted),new(new("mic"),"测试麦克风",47,false),
                [new(new("out","session"),"测试应用",35,false)]);
        }
        public void Write(AudioTarget target,int? volume,bool? mute){if(Fail)throw new InvalidOperationException("模拟设备断开");LastTarget=target;Writes++;if(volume.HasValue)Volume=volume.Value;if(mute.HasValue)Muted=mute.Value;}
        public void Stop(){Stops++;Notify=null;}
        public void Dispose()=>Stop();
    }
    private static async Task Wait(Func<bool> condition)
    {
        var watch=Stopwatch.StartNew();while(!condition()&&watch.ElapsedMilliseconds<4000)await Task.Delay(15);
        if(!condition())throw new InvalidOperationException("Audio control verification timed out.");
    }
    private static IEnumerable<T> Children<T>(DependencyObject root) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);if(child is T match)yield return match;
            foreach(var descendant in Children<T>(child))yield return descendant;
        }
    }
    private static void Click(ButtonBase button)=>button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    internal static async Task Run(MainWindow window,string directory,Action<bool,string> check)
    {
        var backend=new Backend();int factories=0;using var controller=new AudioController(()=>{factories++;return backend;},window.Dispatcher);
        check(factories==0&&!controller.Active,"Hidden control center performs no device initialization");
        controller.SetActive(true);await Wait(()=>controller.Current.Output!=null);
        check(factories==1&&backend.Thread!=Environment.CurrentManagedThreadId,"Audio initialization runs on a dedicated worker");
        check(controller.Current.Output!.Volume==63&&controller.Current.Microphone!.Volume==47&&controller.Current.Sessions.Count==1,"Audio snapshot separates output, microphone and app session values");
        controller.SetLevel(new("out"),volume:130);await Wait(()=>controller.Current.Output!.Volume==100);
        check(backend.Volume==100,"Volume requests are clamped before reaching the backend");
        backend.Fail=true;controller.SetLevel(new("out"),volume:10);await Wait(()=>controller.Current.Error!=null);
        check(controller.Current.Output!.Volume==100&&controller.Current.Error!.Contains("失败"),"Failed audio writes report actual readback instead of an optimistic value");
        backend.Fail=false;backend.Volume=42;backend.Notify!(AudioChange.Values);await Wait(()=>controller.Current.Output!.Volume==42);
        check(controller.Current.Output!.Volume==42,"External audio notifications update displayed state");
        var stale=backend.Notify;controller.SetActive(false);await controller.DrainAsync();int reads=backend.Reads,writes=backend.Writes;
        stale!(AudioChange.All);controller.SetLevel(new("out"),volume:20);await controller.DrainAsync();await Task.Delay(30);
        check(backend.Reads==reads&&backend.Writes==writes&&backend.Stops>0,"Hidden page stops device monitoring and ignores stale notifications and commands");
        controller.SetActive(true);await Wait(()=>backend.Starts==2&&controller.Current.Output!=null);
        check(controller.Current.Output!.Volume==42,"Returning to audio reads current state without replaying old values");
        using var view=new ControlCenterView(controller);
        string theme=window.State.ThemeMode;
        foreach(string next in new[]{"Light","Dark"})
        {
            window.SetThemeMode(next);backend.Notify!(AudioChange.Values);await Task.Delay(40);
            var frame=new System.Windows.Controls.Border{Width=342,Height=360,Child=view,Padding=new Thickness(0)};frame.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty,"Surface");
            frame.Resources["QuietButton"]=window.FindResource("QuietButton");
            frame.Measure(new Size(342,360));frame.Arrange(new Rect(0,0,342,360));frame.UpdateLayout();
            var bitmap=new RenderTargetBitmap(342,360,96,96,PixelFormats.Pbgra32);bitmap.Render(frame);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
            using(var stream=File.Create(Path.Combine(directory,"control-audio-"+next+".png")))png.Save(stream);
            check(view.ActualWidth<=342,"Audio control content stays within compact width in "+next);
            if(next=="Light")
            {
                var slider=Children<Slider>(view).First();slider.Value=31;await Wait(()=>backend.Volume==31&&controller.Current.Output?.Volume==31);
                check(backend.LastTarget==new AudioTarget("out"),"Audio slider debounces writes to its bound endpoint");
                var mute=Children<ToggleButton>(view).First();mute.IsChecked=true;Click(mute);await Wait(()=>backend.Muted&&controller.Current.Output?.Muted==true);
                check(controller.Current.Output!.Muted,"Audio mute button writes and displays backend readback");
                Click(Children<Button>(view).First(b=>b.Content?.ToString()?.StartsWith("应用音量")==true));
                check(Children<TextBlock>(view).Any(t=>t.Text=="应用音量"&&t.Parent is StackPanel panel&&panel.Visibility==Visibility.Visible),"App volume entry opens an internal detail page");
                Click(Children<Button>(view).First(b=>b.Content?.ToString()=="← 控制"));
                Click(Children<Button>(view).First(b=>b.Content?.ToString()=="音频设备  ›"));
                var selector=Children<ComboBox>(view).First();selector.SelectedIndex=1;await Wait(()=>backend.OutputSelection=="out");
                check(true,"Device selector passes the chosen endpoint to the audio controller");
                selector.SelectedIndex=0;await Wait(()=>backend.OutputSelection==null);
                check(true,"Device selector can return to following the Windows default");
                Click(Children<Button>(view).First(b=>b.Content?.ToString()=="← 控制"));
            }
            frame.Child=null;
        }
        window.SetThemeMode(theme);
        view.Dispose();int finalReads=backend.Reads;stale!(AudioChange.All);await Task.Delay(30);
        check(backend.Reads==finalReads,"Disposed audio module ignores delayed callbacks");
    }
}
