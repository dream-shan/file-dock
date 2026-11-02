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
    private static IEnumerable<T> All<T>(DependencyObject p) where T:DependencyObject
    {
        if(p is T t)yield return t;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(p);i++)foreach(var c in All<T>(VisualTreeHelper.GetChild(p,i)))yield return c;
    }
    internal static void Run(MainWindow w,string root,Action<bool,string> check)
    {
        PlanRulesVerification.Run(check);
        var saved=w.State.Plans;int level=w.State.PlannerLevel;
        try{
            w.State.Plans=new();w.State.PlannerLevel=3;w.Planner.Render();w.UpdateLayout();
            void Click(string name){w.UpdateLayout();string[] names={"长期","本月","本周","今天"};int scope=Array.IndexOf(names,name);if(scope>=0)All<RadioButton>(w).First(b=>System.Windows.Automation.AutomationProperties.GetAutomationId(b)=="PlanType"+scope).IsChecked=true;else All<Button>(w).First(b=>b.Content as string==name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));w.UpdateLayout();}
            TextBox Field(string id)=>All<TextBox>(w).First(t=>System.Windows.Automation.AutomationProperties.GetAutomationId(t)==id);
            Click("＋ 添加待办");Click("添加");check(w.State.Plans.Count==0,"Blank plan card cannot create a task");
            check(!All<Expander>(w).First().IsExpanded,"Supplementary fields start collapsed");
            Field("PlanTitle").Text="验证月计划";Click("本月");Click("添加");
            check(w.State.Plans.Count==1 && w.State.Plans[0].Level==1 && w.State.PlannerLevel==1,"Card creates a monthly plan and opens its destination list");
            Click("＋ 添加待办");Field("PlanTitle").Text="验证今日计划";Click("今天");
            check(All<Button>(w).Any(b=>System.Windows.Automation.AutomationProperties.GetAutomationId(b)=="PlanDate" && b.Content.ToString()!.Contains("不设截止日期")),"Card date chip reflects selected type");
            All<Expander>(w).First().IsExpanded=true;w.UpdateLayout();Field("PlanTime").Text="25:00";Click("添加");check(w.State.Plans.Count==1,"Invalid card time is rejected");
            Field("PlanTime").Text="09:00–10:30";Field("PlanNotes").Text="验证备注";Click("添加");check(w.State.Plans.Count==2 && w.State.Plans[1].Notes=="验证备注","Card saves time and notes");
            All<Button>(w).First(b=>b.Content is StackPanel).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));w.UpdateLayout();Field("PlanTitle").Text="取消的修改";Click("取消");check(w.State.Plans[1].Title=="验证今日计划","Cancel leaves stored task unchanged");
            var box=All<CheckBox>(w).First();box.IsChecked=true;box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));check(w.State.Plans[1].Done,"Card-created daily task can be completed");
            Click("已完成 1 项 · 展开");All<Button>(w).First(b=>b.Content is StackPanel).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));w.UpdateLayout();Click("删除");check(w.State.Plans.Count==1,"Card deletes its task");Click("已删除 · 撤销");check(w.State.Plans.Count==2 && w.State.Plans[1].Notes=="验证备注","Undo restores card data");
            var dateDraft=new PlanItem{Level=1,Date=new DateTime(DateTime.Today.Year,DateTime.Today.Month,1).AddMonths(-1)};
            var dateCard=new PlanCard(dateDraft,new List<PlanItem>(),false,_=>true,()=>{});dateCard.Measure(new Size(400,500));dateCard.Arrange(new Rect(0,0,400,500));dateCard.UpdateLayout();
            All<RadioButton>(dateCard).First(b=>System.Windows.Automation.AutomationProperties.GetAutomationId(b)=="PlanType3").IsChecked=true;
            check(dateDraft.Level==3 && dateDraft.Date==DateTime.Today,"Switching from a monthly date to today resets the date to today");
            DemoVerification.SaveRender(w,System.IO.Path.Combine(root,"planner-daily.png"));
            Click("＋ 添加待办");Field("PlanTitle").Text="保存失败草稿";
            using(var locked=new System.IO.FileStream(System.IO.Path.Combine(root,"data","state.json"),System.IO.FileMode.Open,System.IO.FileAccess.Read,System.IO.FileShare.Read)){
                Click("添加");check(w.State.Plans.Count==2 && Field("PlanTitle").Text=="保存失败草稿","Failed storage rolls back insertion and retains the card draft");
            }
            DemoVerification.SaveRender(w,System.IO.Path.Combine(root,"plan-card.png"));Click("取消");
            PlanDialogVerification.Run(w,root,check);
        }finally{w.ClosePlanDialog();w.State.Plans=saved;w.State.PlannerLevel=level;w.Planner.Render();w.Persist();}
    }
}
