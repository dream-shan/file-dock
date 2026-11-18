using FileDockDemo.Features.ControlCenter;
using System.Diagnostics;
using System.Threading.Tasks;

namespace FileDockDemo;

public partial class MainWindow
{
    private ControlCenterView? _controlCenter;
    private Task _controlPreferenceSave=Task.CompletedTask;
    private void InitializeControlCenter()
    {
        var audio=new AudioController(()=>new CoreAudioBackend(),Dispatcher);
        var brightness=new BrightnessController(()=>new WindowsBrightnessBackend(!App.IsSelfTest),Dispatcher);
        var power=new PowerController(()=>new WindowsPowerBackend(!App.IsSelfTest),Dispatcher);
        _controlCenter=new ControlCenterView(audio,brightness,power,State.PreferredBrightnessDisplayId,id=>{
            var task=SavePreferredBrightnessAsync(id);_controlPreferenceSave=task;return task;
        },()=>Process.Start(new ProcessStartInfo("ms-settings:powersleep"){UseShellExecute=true}),wireless:true,nativeWireless:!App.IsSelfTest);ControlSection.Content=_controlCenter;
        Activated+=(_,_)=>_controlCenter?.RefreshWireless();
    }
    internal Task<bool> SavePreferredBrightnessAsync(string? id)=>_exitSaving?Task.FromResult(false):_saves.CommitAsync(s=>s.PreferredBrightnessDisplayId=id,s=>State.PreferredBrightnessDisplayId=s.PreferredBrightnessDisplayId);
}
