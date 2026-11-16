using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace FileDockDemo.Features.ControlCenter;

internal sealed class PowerView:IDisposable
{
    private readonly PowerController _controller;
    private readonly StackPanel _home=new(),_details=new();
    private readonly TextBlock _supply=Ui.Text("",11),_state=Ui.Text(""),_error=Ui.Text("",11);
    private readonly ComboBox _plans=new(){MinHeight=32,DisplayMemberPath="Name"};
    private readonly Dictionary<PowerModeChoice,Button> _modes=new();
    private bool _binding,_disposed;
    internal FrameworkElement Home=>_home;
    internal FrameworkElement Details=>_details;
    internal event Action? DetailsRequested;
    internal PowerView(PowerController controller,Action openSettings)
    {
        _controller=controller;
        var header=new DockPanel();var more=Ui.Button("电源详情 ›",()=>DetailsRequested?.Invoke());more.SetResourceReference(FrameworkElement.StyleProperty,"QuietButton");DockPanel.SetDock(more,Dock.Right);header.Children.Add(more);header.Children.Add(Ui.Text("电源",14));_home.Children.Add(header);
        var choices=new UniformGrid{Columns=3};
        foreach(var mode in Enum.GetValues<PowerModeChoice>())
        {
            var button=Ui.Button(ModeName(mode),()=>_controller.SetMode(mode,_controller.Current.Supply));button.SetResourceReference(FrameworkElement.StyleProperty,"QuietButton");button.SetResourceReference(Control.ForegroundProperty,"Ink");button.Margin=new Thickness(1,0,1,0);button.MinHeight=30;
            AutomationProperties.SetName(button,"电源模式："+ModeName(mode));_modes.Add(mode,button);choices.Children.Add(button);
        }
        _home.Children.Add(choices);_supply.SetResourceReference(TextBlock.ForegroundProperty,"Muted");_home.Children.Add(_supply);
        _details.Children.Add(_state);_details.Children.Add(Ui.Text("电源计划"));AutomationProperties.SetName(_plans,"电源计划选择");_details.Children.Add(_plans);_details.Children.Add(Ui.Text("保留电脑已有计划；厂商专用硬件模式可能需要其软件支持。",11));
        _error.SetResourceReference(TextBlock.ForegroundProperty,"Muted");_details.Children.Add(_error);
        _details.Children.Add(Ui.Button("打开 Windows 电源设置",()=>{try{openSettings();}catch(Exception error){_error.Text="无法打开系统设置："+error.Message;}}));
        _plans.SelectionChanged+=(_,_)=>{if(!_binding&&_plans.SelectedItem is PowerPlan plan&&plan.Id!=_controller.Current.ActivePlan)_controller.SetPlan(plan.Id);};
        _controller.Changed+=Render;Render(_controller.Current);
    }
    internal static string ModeName(PowerModeChoice mode)=>mode switch{PowerModeChoice.Efficiency=>"最佳能效",PowerModeChoice.Balanced=>"平衡",_=>"最佳性能"};
    internal static string SupplyText(PowerSnapshot snapshot)
    {
        string text=snapshot.Supply switch{PowerSupply.AC=>"交流供电",PowerSupply.Battery=>"电池供电",_=>"供电状态未知"};
        if(snapshot.HasBattery==true)text+=" · "+(snapshot.BatteryPercent.HasValue?snapshot.BatteryPercent+"%":"电量未知")+(snapshot.Charging?" · 充电中":"");
        else if(snapshot.HasBattery==null)text+=" · 电池状态未知";
        return text;
    }
    private void Render(PowerSnapshot snapshot)
    {
        if(_disposed)return;
        _supply.Text=SupplyText(snapshot);_supply.ToolTip=snapshot.Error??snapshot.ModeError;
        foreach(var pair in _modes){pair.Value.IsEnabled=snapshot.CanSetMode;pair.Value.SetResourceReference(Control.BackgroundProperty,snapshot.UserMode==pair.Key?"Selection":"PaneCard");pair.Value.ToolTip=snapshot.ModeError??"当前供电状态的系统电源偏好";}
        _state.Text=SupplyText(snapshot)+"\n用户选择："+(snapshot.UserMode.HasValue?ModeName(snapshot.UserMode.Value):"暂不可读")+"\n当前生效："+(snapshot.EffectiveMode??"暂不可读");
        _error.Text=snapshot.Error??snapshot.PlanError??snapshot.ModeError??"";
        _binding=true;
        try{_plans.ItemsSource=snapshot.Plans;_plans.SelectedValuePath="Id";_plans.SelectedValue=snapshot.ActivePlan;_plans.IsEnabled=snapshot.Plans.Count>0&&snapshot.PlanError==null;}finally{_binding=false;}
    }
    public void Dispose(){if(_disposed)return;_disposed=true;_controller.Changed-=Render;_controller.Dispose();}
}
