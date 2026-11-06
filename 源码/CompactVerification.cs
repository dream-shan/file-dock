using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using FileDockDemo.Features.Planner;
namespace FileDockDemo;
internal static class CompactVerification
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h,int m,IntPtr w,IntPtr l);
    private static System.Collections.Generic.IEnumerable<T> All<T>(DependencyObject p) where T:DependencyObject{if(p is T t)yield return t;for(int i=0;i<VisualTreeHelper.GetChildrenCount(p);i++)foreach(var c in All<T>(VisualTreeHelper.GetChild(p,i)))yield return c;}
    internal static async Task Run(MainWindow w,string root,Action<bool,string> check)
    {
        string legacy=Path.Combine(root,"compact-legacy");Directory.CreateDirectory(legacy);File.WriteAllText(Path.Combine(legacy,"state.json"),"""{"Version":1,"State":{}}""");
        var loaded=new StorageService(legacy).Load();check(loaded.PanelWidth==380&&loaded.PanelHeight==500,"Old settings without dimensions adopt compact defaults");
        var plans=w.State.Plans;var dates=w.State.Countdowns;double width=w.Width,height=w.Height,sw=w.State.PanelWidth,sh=w.State.PanelHeight;string page=w.State.SelectedPage;
        try{
            w.ResetPanelSize();w.UpdateLayout();check(w.Width==380&&w.Height==500,"Reset restores the compact 380 by 500 logical size");
            w.Width=420;w.Height=580;w.UpdateLayout();SendMessage(new System.Windows.Interop.WindowInteropHelper(w).Handle,0x0232,IntPtr.Zero,IntPtr.Zero);await w.WaitForSavesAsync();loaded=w.Storage.Load();check(loaded.PanelWidth==420&&loaded.PanelHeight==580,"Native resize-end dimensions survive JSON reload");w.ResetPanelSize();w.UpdateLayout();
            foreach(string p in new[]{"Files","Planner","Performance"}){w.SelectPage(p,false);w.UpdateLayout();check(w.Width==380&&w.Height==500,$"Switching to {p} does not resize the compact panel");DemoVerification.SaveRender(w,Path.Combine(root,"compact-"+p.ToLowerInvariant()+".png"));}
            check(w.PerformanceScroll.ExtentHeight<=w.PerformanceScroll.ViewportHeight+1&&w.MemoryCard.CapacityFits&&w.VramCard.CapacityFits,"Compact performance page fits all cards and capacity labels");
            w.State.Countdowns=new(){new CountdownItem{Title="考试",Date=DateTime.Today.AddDays(100)},new CountdownItem{Title="生日",Date=DateTime.Today.AddDays(200),Annual=true}};
            w.State.Plans=Enumerable.Range(0,12).Select(i=>new PlanItem{Title="待办 "+i,ScheduleSchema=1,Kind="repeat"}).ToList();w.SelectPage("Planner",false);w.Planner.Render();w.UpdateLayout();
            var scroll=All<ScrollViewer>(w.Planner).Single(v=>AutomationProperties.GetAutomationId(v)=="PlannerScroll");var add=All<Button>(w.Planner).Single(v=>AutomationProperties.GetAutomationId(v)=="PlannerQuickAdd");var point=add.TranslatePoint(new Point(),w.Planner);
            check(scroll.ViewportHeight>45&&point.Y+add.ActualHeight<=w.Planner.ActualHeight+1,"Two important dates leave a usable todo viewport and visible add action");DemoVerification.SaveRender(w,Path.Combine(root,"compact-planner-filled.png"));
            w.Height=480;w.UpdateLayout();point=add.TranslatePoint(new Point(),w.Planner);
            check(scroll.ViewportHeight>30 && point.Y+add.ActualHeight<=w.Planner.ActualHeight+1,
                "Minimum-height agenda keeps two date cards, a scrollable checklist and the add action accessible");
            check(All<TextBlock>(w.Planner).Any(t=>t.Text=="安排") && All<TextBlock>(w.Planner).Any(t=>t.Text=="今日清单") &&
                All<TextBlock>(w.Planner).Count(t=>t.Text==DateTime.Today.ToString("M月d日 ddd",System.Globalization.CultureInfo.GetCultureInfo("zh-CN")))==1,
                "Agenda has a single date heading and separate checklist hierarchy");
            DemoVerification.SaveRender(w,Path.Combine(root,"agenda-minimum.png"));
        }finally{w.State.Plans=plans;w.State.Countdowns=dates;w.State.PanelWidth=sw;w.State.PanelHeight=sh;w.Width=width;w.Height=height;w.Planner.Render();w.SelectPage(page,false);w.Persist();}
    }
}
