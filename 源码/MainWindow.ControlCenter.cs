using FileDockDemo.Features.ControlCenter;

namespace FileDockDemo;

public partial class MainWindow
{
    private ControlCenterView? _controlCenter;
    private void InitializeControlCenter()
    {
        var audio=new AudioController(()=>new CoreAudioBackend(),Dispatcher);
        _controlCenter=new ControlCenterView(audio);ControlSection.Content=_controlCenter;
    }
}
