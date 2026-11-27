using FileDockDemo.Features.ControlCenter;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        var hardware=new HardwareController(()=>new WindowsHardwareBackend(!App.IsSelfTest),Dispatcher);
        _controlCenter=new ControlCenterView(audio,brightness,power,State.PreferredBrightnessDisplayId,id=>{
            var task=SavePreferredBrightnessAsync(id);_controlPreferenceSave=task;return task;
        },()=>Process.Start(new ProcessStartInfo("ms-settings:powersleep"){UseShellExecute=true}),wireless:true,nativeWireless:!App.IsSelfTest,hardware:hardware,
            getHardwarePresets:()=>State.HardwarePresets,
            saveHardwarePresets:presets=>{var task=SaveHardwarePresetsAsync(presets);_controlPreferenceSave=task;return task;});ControlSection.Content=_controlCenter;
        Activated+=(_,_)=>_controlCenter?.RefreshWireless();
    }
    internal Task<bool> SavePreferredBrightnessAsync(string? id)=>_exitSaving?Task.FromResult(false):_saves.CommitAsync(s=>s.PreferredBrightnessDisplayId=id,s=>State.PreferredBrightnessDisplayId=s.PreferredBrightnessDisplayId);
    internal Task<bool> SaveHardwarePresetsAsync(IReadOnlyList<HardwarePerformancePreset> presets)=>
        _exitSaving?Task.FromResult(false):_saves.CommitAsync(s=>s.HardwarePresets=presets.Select(p=>p.Copy()).ToList(),s=>State.HardwarePresets=s.HardwarePresets);
}
