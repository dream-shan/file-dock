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
        var saved=w.State.Plans;int level=w.State.PlannerLevel;
        try{
            w.State.Plans=new();w.State.PlannerLevel=3;w.Planner.Render();w.UpdateLayout();
            void Click(string name){w.UpdateLayout();All<Button>(w.Planner).First(b=>b.Content as string==name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));w.UpdateLayout();}
            Click("＋ 新建");Click("保存");check(w.State.Plans.Count==0,"Planner rejects blank titles without creating empty records");
            var inputs=All<TextBox>(w.Planner).Where(t=>t.Tag is string).ToList();inputs[0].Text="验证今日计划";inputs[1].Text="25:00";Click("保存");
            check(w.State.Plans.Count==0,"Planner rejects invalid time values");
            inputs[1].Text="09:00–10:30";inputs[2].Text="验证备注";Click("保存");
            check(w.State.Plans.Count==1 && w.State.Plans[0].Time=="09:00–10:30","Planner editor saves a daily task with time and notes");
            var box=All<CheckBox>(w.Planner).First();box.IsChecked=true;box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            check(w.State.Plans[0].Done,"Planner checkbox completes the existing task");
            Click("已完成 1 项 · 展开");
            var row=All<Button>(w.Planner).First(b=>b.Content is StackPanel);row.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));w.UpdateLayout();Click("删除");
            check(w.State.Plans.Count==0,"Planner deletion removes the task");Click("已删除 · 撤销");
            check(w.State.Plans.Count==1 && w.State.Plans[0].Notes=="验证备注","Planner undo restores task data");
            DemoVerification.SaveRender(w,System.IO.Path.Combine(root,"planner-daily.png"));
        }finally{w.State.Plans=saved;w.State.PlannerLevel=level;w.Planner.Render();w.Persist();}
    }
}
