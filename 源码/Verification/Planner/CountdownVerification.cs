using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Automation;
using FileDockDemo.Features.Planner;
using FileDockDemo.Infrastructure;
namespace FileDockDemo;
internal static class CountdownVerification
{
    private static System.Collections.Generic.IEnumerable<T> All<T>(DependencyObject p) where T:DependencyObject{if(p is T t)yield return t;for(int i=0;i<VisualTreeHelper.GetChildrenCount(p);i++)foreach(var c in All<T>(VisualTreeHelper.GetChild(p,i)))yield return c;}
    internal static async Task Run(MainWindow w,string root,Action<bool,string> check)
    {
        var today=new DateTime(2026,11,4);var item=new CountdownItem{Title="考试",Date=today.AddDays(100)};
        check(item.Days(today)==100,"Countdown uses local calendar days");item.Date=today;check(item.Days(today)==0,"Target day is zero days");item.Date=today.AddDays(-1);check(item.Days(today)==-1,"Expired one-time dates retain their target date");
        item.Annual=true;check(item.Next(today)==new DateTime(2027,11,3),"Annual dates advance after their occurrence");item.Date=today;check(item.Next(today)==today,"Annual event remains today until midnight");
        item.Date=new DateTime(2024,2,29);check(item.Next(new DateTime(2025,2,1))==new DateTime(2025,2,28),"Leap-day anniversary falls on February 28 in common years");check(item.Next(new DateTime(2028,2,1))==new DateTime(2028,2,29),"Leap-day anniversary returns on leap years");
        var storage=new StorageService(Path.Combine(root,"countdown-store"));var state=new AppState();using var saves=new StateSaveCoordinator(state,storage.Save,_=>{});var store=new AppStatePlannerStore(state,saves.CommitAsync);
        check(await store.WriteCountdownsAsync(new[]{item})&&storage.Load().Countdowns.Single().Date==item.Date,"Important dates survive JSON storage round trip");var failed=new AppStatePlannerStore(state,(_,_)=>Task.FromResult(false));check(!await failed.WriteCountdownsAsync(Array.Empty<CountdownItem>())&&state.Countdowns.Count==1,"Failed countdown save restores previous data");
        w.SelectPage("Planner",false);w.Planner.Render();w.UpdateLayout();
        void Click(string id){All<Button>(w).First(b=>AutomationProperties.GetAutomationId(b)==id).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));w.UpdateLayout();}
        Click("CountdownAdd");var card=w.ActiveModuleDialog as CountdownCard;check(card!=null,"Important-date add action opens its own editor");
        var days=All<TextBox>(card!).First(b=>AutomationProperties.GetAutomationId(b)=="CountdownDays");days.Text="100";
        check(All<DatePicker>(card!).Single().SelectedDate==DateTime.Today.AddDays(100),"Entering remaining days updates the target date");
        check(All<Button>(card!).Any(b=>AutomationProperties.GetAutomationId(b)=="CountdownSave"&&b.IsVisible),"Countdown editor exposes its save button");DemoVerification.SaveRender(w,Path.Combine(root,"countdown-editor.png"));w.CloseModuleDialog();Click("CountdownAdd");check(w.ActiveModuleDialog is CountdownCard,"Closing countdown editor releases editing state");w.CloseModuleDialog();
        DemoVerification.SaveRender(w,Path.Combine(root,"countdown-home.png"));
    }
}
