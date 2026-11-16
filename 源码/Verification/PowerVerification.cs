using System;
using System.Threading.Tasks;
using FileDockDemo.Features.ControlCenter;

namespace FileDockDemo;

internal static class PowerVerification
{
    internal sealed class Backend:IPowerBackend
    {
        internal static readonly Guid CustomPlan=Guid.Parse("30f58510-43ab-4ef5-85c7-ece1fd926a47");
        internal int Starts,Stops,Reads,Writes;
        internal PowerSupply Supply=PowerSupply.AC;
        internal bool Fail,MissingMode;
        internal Guid Plan=CustomPlan;
        internal PowerModeChoice Mode=PowerModeChoice.Balanced;
        internal Action? Notify;
        public void Start(Action changed){Starts++;Notify=changed;}
        public PowerSnapshot Read(){Reads++;return new(Supply,false,null,false,MissingMode?null:Mode,null,[new(CustomPlan,"厂商自定义计划"),new(Guid.Empty,"测试计划")],Plan,MissingMode?"模式不可用":null);}
        public void SetMode(PowerModeChoice mode,PowerSupply expectedSupply){if(Fail||Supply!=expectedSupply||Supply==PowerSupply.Unknown||MissingMode)throw new InvalidOperationException("模拟供电变化或系统拒绝");Writes++;Mode=mode;}
        public void SetPlan(Guid id){if(Fail)throw new InvalidOperationException("模拟策略拒绝");Writes++;Plan=id;}
        public void Stop(){Stops++;Notify=null;}
        public void Dispose()=>Stop();
    }
    internal static async Task Run(MainWindow window,Action<bool,string> check)
    {
        var backend=new Backend();int factories=0;using var controller=new PowerController(()=>{factories++;return backend;},window.Dispatcher);
        check(factories==0&&!controller.Active,"Hidden power module performs no initialization");
        controller.SetActive(true);await controller.DrainAsync();await Task.Delay(40);
        check(controller.Current.Plans.Count==2,"Power activation publishes actual plan list");
        check(controller.Current.HasBattery==false&&controller.Current.BatteryPercent==null,"No battery is distinct from zero percent battery");
        check(WindowsPowerBackend.Battery(128,255)==(false,null,false)&&WindowsPowerBackend.Battery(255,255)==(null,null,false)&&WindowsPowerBackend.Battery(8,0)==(true,0,true),"Native battery flags distinguish absent, unknown and empty charging battery");
        check(controller.Current.Plans[0].Id==Backend.CustomPlan&&controller.Current.Plans[0].Name=="厂商自定义计划","Custom power plan identity and name are preserved");
        controller.SetMode(PowerModeChoice.Performance,PowerSupply.AC);await BrightnessVerification.Wait(()=>controller.Current.UserMode==PowerModeChoice.Performance);
        check(backend.Writes==1,"Power mode is read back after changing the expected supply setting");
        backend.Supply=PowerSupply.Battery;controller.SetMode(PowerModeChoice.Efficiency,PowerSupply.AC);await BrightnessVerification.Wait(()=>controller.Current.Error!=null);
        check(backend.Writes==1&&controller.Current.UserMode==PowerModeChoice.Performance,"AC to battery race rejects old mode request and refreshes");
        backend.Supply=PowerSupply.Unknown;backend.Notify!();await BrightnessVerification.Wait(()=>controller.Current.Supply==PowerSupply.Unknown);
        check(!controller.Current.CanSetMode,"Unknown power supply cannot present an enabled mode control");
        backend.MissingMode=true;backend.Notify!();await BrightnessVerification.Wait(()=>controller.Current.ModeError!=null);
        check(controller.Current.Plans.Count==2&&!controller.Current.CanSetMode,"Missing mode support does not hide independent power plans");
        backend.Fail=true;controller.SetPlan(Guid.Empty);await BrightnessVerification.Wait(()=>controller.Current.Error!=null);
        check(controller.Current.ActivePlan==Backend.CustomPlan,"Rejected power plan switch preserves actual plan");
        var stale=backend.Notify;controller.SetActive(false);await controller.DrainAsync();int writes=backend.Writes,reads=backend.Reads;
        stale!();controller.SetPlan(Guid.Empty);await controller.DrainAsync();await Task.Delay(30);
        check(backend.Writes==writes&&backend.Reads==reads&&backend.Stops>0,"Power pause rejects stale callbacks and commands");
        controller.SetActive(true);await BrightnessVerification.Wait(()=>controller.Current.Plans.Count>0);
        check(backend.Writes==writes,"Power reopen never reapplies a stored plan");
    }
}
