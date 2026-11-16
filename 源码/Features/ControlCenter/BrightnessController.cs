using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace FileDockDemo.Features.ControlCenter;

internal sealed record BrightnessTarget(string Id,long Connection);
internal sealed record BrightnessDevice(string Id,string Name,bool Primary,int? Percent,string? Error=null,long Connection=0)
{
    internal BrightnessTarget Target=>new(Id,Connection);
    public string Label=>Name+(Primary?"（主屏）":"");
}
internal sealed record BrightnessSnapshot(IReadOnlyList<BrightnessDevice> Devices,string? Error=null)
{
    internal static BrightnessSnapshot Empty(string? error=null)=>new([],error);
}
internal interface IBrightnessBackend:IControlBackend<BrightnessSnapshot>
{
    void Write(BrightnessTarget target,int percent);
}
internal sealed class BrightnessController:IDisposable
{
    private readonly ControlChannel<IBrightnessBackend,BrightnessSnapshot> _channel;
    internal BrightnessSnapshot Current=>_channel.Current;
    internal event Action<BrightnessSnapshot>? Changed{add=>_channel.Changed+=value;remove=>_channel.Changed-=value;}
    internal bool Active=>_channel.Active;
    internal BrightnessController(Func<IBrightnessBackend> factory,Dispatcher dispatcher)=>_channel=new("FileDock brightness",factory,dispatcher,BrightnessSnapshot.Empty,(snapshot,error)=>snapshot with{Error=error});
    internal void SetActive(bool active)=>_channel.SetActive(active);
    internal void Refresh()=>_channel.Refresh();
    internal void SetBrightness(BrightnessTarget target,int percent)=>_channel.Command(backend=>backend.Write(target,Math.Clamp(percent,0,100)));
    internal Task DrainAsync()=>_channel.DrainAsync();
    public void Dispose()=>_channel.Dispose();
}
