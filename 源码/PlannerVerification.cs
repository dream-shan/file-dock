using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
namespace FileDockDemo;
internal static class PlannerVerification
{
    private static IEnumerable<T> All<T>(DependencyObject p) where T:DependencyObject{if(p is T t)yield return t;for(int i=0;i<VisualTreeHelper.GetChildrenCount(p);i++)foreach(var c in All<T>(VisualTreeHelper.GetChild(p,i)))yield return c;}
    internal static void Run(MainWindow w,string root,Action<bool,string> check)
    {
        PlanRulesVerification.Run(check);var saved=w.State.Plans;int level=w.State.PlannerLevel;
        try{
            var parent=new PlanItem{Title="旧目标",Level=0,Notes="保留备注",Priority="high",Time="09:00"};var child=new PlanItem{Title="旧任务",Level=3,ParentId=parent.Id,Notes="原备注",Priority="low",Time="10:00"};w.State.Plans=new(){parent,child};w.State.PlannerLevel=3;w.Planner.Render();w.UpdateLayout();
            void Click(string name){w.UpdateLayout();int n=Array.IndexOf(PlanRules.Names,name);if(w.ActivePlanCard!=null && n>=0)All<RadioButton>(w).First(b=>System.Windows.Automation.AutomationProperties.GetAutomationId(b)=="PlanType"+n).IsChecked=true;else All<Button>(w).First(b=>b.Content as string==name || System.Windows.Automation.AutomationProperties.GetName(b)==name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));w.UpdateLayout();}
            TextBox Title()=>All<TextBox>(w).First(t=>System.Windows.Automation.AutomationProperties.GetAutomationId(t)=="PlanTitle");
            Click("详情：旧任务");Title().Text="旧任务编辑";Click("保存");child=w.State.Plans.First(p=>p.Id==child.Id);
            check(child.Notes=="原备注" && child.Priority=="low" && child.Time=="10:00" && child.ParentId==parent.Id,"Simplified editor retains legacy hidden data");
            var box=All<CheckBox>(w.Planner).First();box.IsChecked=true;box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));check(child.Done && !parent.Done,"Completing an old linked child leaves the parent unchanged");
            Click("已完成 1 项 · 展开");box=All<CheckBox>(w.Planner).First();box.IsChecked=false;box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));Click("长期");box=All<CheckBox>(w.Planner).First();box.IsChecked=true;box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));check(parent.Done && !child.Done,"Completing an old parent does not cascade");
            Click("已完成 1 项 · 展开");Click("详情：旧目标");Click("删除");check(w.State.Plans.Count==1 && w.State.Plans[0].Id==child.Id && child.ParentId==null,"Deleting old parent retains child as independent");Click("已删除 · 撤销");check(w.State.Plans.Count==2 && child.ParentId==parent.Id,"Undo restores legacy relation without losing tasks");
            Click("今天");Click("＋ 添加待办");Click("添加");check(w.State.Plans.Count==2,"Blank simplified plan is rejected");
            check(!All<Expander>(w.ActivePlanCard!).Any() && !All<TextBox>(w.ActivePlanCard!).Any(t=>System.Windows.Automation.AutomationProperties.GetAutomationId(t)=="PlanNotes"),"Supplementary section and old edit fields are absent");
            Title().Text="简化新任务";Click("本月");Click("添加");var added=w.State.Plans.First(p=>p.Title=="简化新任务");check(added.Level==1 && added.ParentId==null && added.Priority=="none" && added.Notes=="","New plans default to independent with no hidden user fields");
            Click("详情：简化新任务");Title().Text="取消编辑";Click("取消");check(added.Title=="简化新任务","Cancel preserves original title");
            Click("＋ 添加待办");Title().Text="保存失败草稿";using(var locked=new System.IO.FileStream(System.IO.Path.Combine(root,"data","state.json"),System.IO.FileMode.Open,System.IO.FileAccess.Read,System.IO.FileShare.Read)){Click("添加");check(w.State.Plans.Count==3 && Title().Text=="保存失败草稿","Failed save retains simplified draft and rolls back insertion");}Click("取消");
            DemoVerification.SaveRender(w,System.IO.Path.Combine(root,"planner-simple.png"));PlanDialogVerification.Run(w,root,check);
        }finally{w.ClosePlanDialog();w.State.Plans=saved;w.State.PlannerLevel=level;w.Planner.Render();w.Persist();}
    }
}