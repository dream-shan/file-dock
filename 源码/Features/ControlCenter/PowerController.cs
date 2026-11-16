using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace FileDockDemo.Features.ControlCenter;

internal enum PowerSupply { Unknown,AC,Battery }
internal enum PowerModeChoice { Efficiency,Balanced,Performance }
internal sealed record PowerPlan(Guid Id,string Name);
internal sealed record PowerSnapshot(PowerSupply Supply,bool? HasBattery,int? BatteryPercent,bool Charging,PowerModeChoice? UserMode,string? EffectiveMode,IReadOnlyList<PowerPlan> Plans,Guid? ActivePlan,string? ModeError=null,string? PlanError=null,string? Error=null)
{
    internal bool CanSetMode=>Supply!=PowerSupply.Unknown&&UserMode.HasValue&&ModeError==null;
    internal static PowerSnapshot Empty(string? error=null)=>new(PowerSupply.Unknown,null,null,false,null,null,[],null,Error:error);
}
internal interface IPowerBackend:IControlBackend<PowerSnapshot>
{
    void SetMode(PowerModeChoice mode,PowerSupply expectedSupply);
    void SetPlan(Guid id);
}
internal sealed class PowerController:IDisposable
{
    private readonly ControlChannel<IPowerBackend,PowerSnapshot> _channel;
    internal PowerSnapshot Current=>_channel.Current;
    internal event Action<PowerSnapshot>? Changed{add=>_channel.Changed+=value;remove=>_channel.Changed-=value;}
    internal bool Active=>_channel.Active;
    internal PowerController(Func<IPowerBackend> factory,Dispatcher dispatcher)=>_channel=new("FileDock power",factory,dispatcher,PowerSnapshot.Empty,(snapshot,error)=>snapshot with{Error=error});
    internal void SetActive(bool active)=>_channel.SetActive(active);
    internal void Refresh()=>_channel.Refresh();
    internal void SetMode(PowerModeChoice mode,PowerSupply expectedSupply)=>_channel.Command(backend=>backend.SetMode(mode,expectedSupply));
    internal void SetPlan(Guid id)=>_channel.Command(backend=>backend.SetPlan(id));
    internal Task DrainAsync()=>_channel.DrainAsync();
    public void Dispose()=>_channel.Dispose();
}
