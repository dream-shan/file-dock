using FileDockDemo.Features.Planner;
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
 internal static void Run(MainWindow w,string root,Action<bool,string> check){PlannerModuleVerification.Run(check);PlanRulesVerification.Run(check);var saved=w.State.Plans;w.SelectPage("Planner",false);try{
 var old=new PlanItem{Title="旧任务",Notes="原备注",Priority="low",Time="10:00"};PlanRules.Normalize(old);w.State.Plans=new(){old};w.Planner.Render();w.UpdateLayout();
 void Click(string name){w.UpdateLayout();All<Button>(w).First(b=>b.Content as string==name || System.Windows.Automation.AutomationProperties.GetName(b)==name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));w.UpdateLayout();}
 T Id<T>(string id) where T:DependencyObject=>All<T>(w).First(t=>System.Windows.Automation.AutomationProperties.GetAutomationId(t)==id);
 Click("详情：旧任务");Id<TextBox>("PlanTitle").Text="已确认旧任务";Click("保存");old=w.State.Plans.First();check(!old.Migrated&&old.Notes=="原备注"&&old.Priority=="low","Edit confirms old schedule and retains hidden data");
 var box=All<CheckBox>(w.Planner).First();box.IsChecked=true;box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));w.UpdateLayout();old=w.State.Plans.First(p=>p.Id==old.Id);check(old.Done&&All<TextBlock>(w.Planner).Any(t=>t.Text==old.Title&&t.TextDecorations.Any(d=>d.Location==TextDecorationLocation.Strikethrough)),"Completed remains visible and struck through");
 Click("＋ 添加待办");Click("添加");check(w.State.Plans.Count==1,"Blank plan rejected");Id<TextBox>("PlanTitle").Text="重复测试";Id<RadioButton>("PlanType1").IsChecked=true;Id<RadioButton>("PlanRule2").IsChecked=true;Id<TextBox>("PlanActiveDays").Text="0";Click("添加");check((w.ActiveModuleDialog as PlanCard)!=null&&w.State.Plans.Count==1,"Invalid cycle cannot save");Id<TextBox>("PlanActiveDays").Text="2";Click("添加");var repeat=w.State.Plans.Single(p=>p.Title=="重复测试");check(repeat.Kind=="repeat"&&repeat.RepeatRule=="cycle"&&repeat.ActiveDays==2&&repeat.RestDays==1,"Cycle persists");
 Click("详情：重复测试");Id<TextBox>("PlanTitle").Text="取消编辑";Click("取消");check(repeat.Title=="重复测试","Cancel preserves task");
 Click("＋ 添加待办");Id<TextBox>("PlanTitle").Text="保存失败草稿";using(var locked=new System.IO.FileStream(System.IO.Path.Combine(root,"data","state.json"),System.IO.FileMode.Open,System.IO.FileAccess.Read,System.IO.FileShare.Read)){Click("添加");check(w.State.Plans.Count==2&&Id<TextBox>("PlanTitle").Text=="保存失败草稿","Failed save retains draft and rolls back");}Click("取消");
 repeat.ScheduledDate=PlanRules.Today.AddDays(1);w.Planner.Render();w.UpdateLayout();check(!All<TextBlock>(w.Planner).Any(t=>t.Text==repeat.Title),"Future repeat hidden today");Click("管理");check(All<TextBlock>(w.Planner).Any(t=>t.Text==repeat.Title),"Management includes hidden schedules");Click("详情：重复测试");Click("删除");check(w.State.Plans.Count==1,"Management deletes schedule");Click("已删除 · 撤销");check(w.State.Plans.Count==2,"Undo restores schedule");Click("返回今天");DemoVerification.SaveRender(w,System.IO.Path.Combine(root,"planner-simple.png"));PlanDialogVerification.Run(w,root,check);
 }finally{w.CloseModuleDialog();w.State.Plans=saved;w.Planner.Render();w.Persist();}}
}
