using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FileDockDemo.Features.ControlCenter;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FileDockDemo;

internal static class AudioDeviceVerification
{
    private sealed record DeviceState(string Id,float Volume,bool Mute);
    private static async Task Wait(Func<bool> condition)
    {
        var watch=Stopwatch.StartNew();while(!condition()&&watch.ElapsedMilliseconds<6000)await Task.Delay(20);
        if(!condition())throw new InvalidOperationException("Actual audio state did not reach the expected value.");
    }
    private static Task<DeviceState> Capture(string id)=>Task.Run(()=>{
        using var enumerator=new MMDeviceEnumerator();using var device=enumerator.GetDevice(id);var volume=device.AudioEndpointVolume;
        return new DeviceState(id,volume.MasterVolumeLevelScalar,volume.Mute);
    });
    private static Task Restore(DeviceState state)=>Task.Run(()=>{
        using var enumerator=new MMDeviceEnumerator();using var device=enumerator.GetDevice(state.Id);var volume=device.AudioEndpointVolume;
        volume.MasterVolumeLevelScalar=state.Volume;volume.Mute=state.Mute;
        if(Math.Abs(volume.MasterVolumeLevelScalar-state.Volume)>.001||volume.Mute!=state.Mute)throw new InvalidOperationException("Audio restoration readback mismatch for "+state.Id);
    });
    private static Task<string?> DefaultOutput()=>Task.Run(()=>{using var enumerator=new MMDeviceEnumerator();if(!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia,out var device))return null;using(device)return device.ID;});
    internal static async Task Run(MainWindow window,string directory)
    {
        Directory.CreateDirectory(directory);var report=new List<string>();
        void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);report.Add("PASS: "+message);}
        using var audio=new AudioController(()=>new CoreAudioBackend(),window.Dispatcher);
        var states=new List<DeviceState>();string? beforeDefault=await DefaultOutput();
        try
        {
            audio.SetActive(true);await Wait(()=>audio.Current.Error!="正在读取音频设备…");
            Check(audio.Current.Devices.Count>0,"Real Windows audio device enumeration succeeds: "+audio.Current.Error);
            foreach(bool input in new[]{false,true})
            {
                var level=input?audio.Current.Microphone:audio.Current.Output;
                if(level==null||level.Error!=null){report.Add("SKIP: "+(input?"microphone":"output")+" has no controllable endpoint");continue;}
                var original=await Capture(level.Target.DeviceId);states.Add(original);
                try
                {
                    audio.SetLevel(level.Target,mute:true);
                    await Wait(()=>(input?audio.Current.Microphone:audio.Current.Output)?.Muted==true);
                    Check(true,(input?"Microphone":"Output")+" mute writes and reads back without opening a capture stream");
                    int lower=Math.Max(0,level.Volume-2);audio.SetLevel(level.Target,volume:lower);
                    await Wait(()=>Math.Abs((input?audio.Current.Microphone:audio.Current.Output)!.Volume-lower)<=1);
                    Check(true,(input?"Microphone":"Output")+" volume writes and reads back");
                    // A second, independent Core Audio client simulates a system-volume change.
                    await Task.Run(()=>{using var enumerator=new MMDeviceEnumerator();using var device=enumerator.GetDevice(original.Id);device.AudioEndpointVolume.MasterVolumeLevelScalar=original.Volume;});
                    int expected=(int)Math.Round(original.Volume*100);
                    await Wait(()=>Math.Abs((input?audio.Current.Microphone:audio.Current.Output)!.Volume-expected)<=1);
                    Check(true,(input?"Microphone":"Output")+" external change reaches UI snapshot through native notification");
                }
                finally{await Restore(original);}
                Check(true,(input?"Microphone":"Output")+" original scalar volume and mute restored and verified");
            }
            if(audio.Current.Output is {} output)
            {
                await VerifySilentSession(audio,output.Target.DeviceId,report);
            }
            Check(await DefaultOutput()==beforeDefault,"Audio tests do not change Windows default output device");
            audio.SetActive(false);await audio.DrainAsync();Check(!audio.Active,"Hiding control page stops native audio monitoring");
            audio.SetActive(true);await Wait(()=>audio.Current.Devices.Count>0);Check(true,"Native audio resources can reopen after hiding");
        }
        finally
        {
            // Retry every captured endpoint even if any verification or first restoration failed.
            Exception? restoreError=null;
            foreach(var state in states){try{await Restore(state);}catch(Exception error){restoreError=error;report.Add("RESTORE FAILED: "+state.Id+" · "+error.Message);}}
            audio.SetActive(false);await audio.DrainAsync();File.WriteAllLines(Path.Combine(directory,"audio-verification.txt"),report);
            if(restoreError!=null)throw new InvalidOperationException("One or more original audio settings could not be restored.",restoreError);
        }
    }
    private static async Task VerifySilentSession(AudioController audio,string deviceId,List<string> report)
    {
        // Generate a silent playback session owned only by this test process, never microphone capture.
        using var enumerator=new MMDeviceEnumerator();using var device=enumerator.GetDevice(deviceId);
        using var client=device.CreateAudioClient();
        using var player=new WasapiPlayerBuilder().WithDevice(device).WithSharedMode().WithEventSync().WithLatency(100).Build();
        player.Init(new SilenceProvider(client.MixFormat));player.Play();
        AudioSessionControl? session=null;float originalVolume=0;bool originalMute=false,captured=false;
        try
        {
            var watch=Stopwatch.StartNew();
            while(session==null&&watch.ElapsedMilliseconds<5000)
            {
                device.AudioSessionManager.RefreshSessions();var sessions=device.AudioSessionManager.Sessions;
                for(int i=0;i<sessions.Count;i++){var candidate=sessions[i];if(candidate.GetProcessID==Environment.ProcessId){session=candidate;break;}candidate.Dispose();}
                if(session==null)await Task.Delay(40);
            }
            if(session==null)throw new InvalidOperationException("Silent test audio session was not created.");
            originalVolume=session.SimpleAudioVolume.Volume;originalMute=session.SimpleAudioVolume.Mute;captured=true;string id=session.GetSessionInstanceIdentifier;
            await Wait(()=>audio.Current.Sessions.Any(s=>s.Target.SessionId==id));report.Add("PASS: New silent app session appears through session notification");
            var level=audio.Current.Sessions.First(s=>s.Target.SessionId==id);audio.SetLevel(level.Target,volume:17,mute:true);
            await Wait(()=>audio.Current.Sessions.Any(s=>s.Target.SessionId==id&&s.Muted&&Math.Abs(s.Volume-17)<=1));report.Add("PASS: App-session volume and mute write and read back on a real session");
        }
        finally
        {
            if(session!=null){try{if(captured){session.SimpleAudioVolume.Volume=originalVolume;session.SimpleAudioVolume.Mute=originalMute;if(Math.Abs(session.SimpleAudioVolume.Volume-originalVolume)>.001||session.SimpleAudioVolume.Mute!=originalMute)throw new InvalidOperationException("Silent test session restoration readback mismatch.");report.Add("PASS: Silent test session state restored");}}finally{session.Dispose();}}
            player.Stop();
        }
    }
}
