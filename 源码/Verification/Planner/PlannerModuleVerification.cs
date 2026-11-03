using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using FileDockDemo.Contracts;
using FileDockDemo.Features.Planner;
using FileDockDemo.Infrastructure;
namespace FileDockDemo;

internal static class PlannerModuleVerification
{
    private sealed class MemoryStore : IPlannerStore
    {
        internal List<PlanItem> Items=new();internal bool Fail;
        public IReadOnlyList<PlanItem> Read()=>Items.AsReadOnly();
        public bool TryWrite(IReadOnlyList<PlanItem> items,int? level=null){if(Fail)return false;Items=items.Select(p=>p.Copy()).ToList();return true;}
    }
    private sealed class Host : IModuleHost
    {
        public void ShowDialog(IModuleDialog dialog){}
        public void CloseDialog(IModuleDialog dialog)=>dialog.OnClosed();
        public void ShowStatus(string text){}
        public bool ConfirmDiscardChanges()=>true;
        public bool CanRestorePopupFocus=>false;
    }
    internal static void Run(Action<bool,string> check)
    {
        var today=PlanRules.Today;var parent=new PlanItem{Title="parent",ScheduleSchema=1,Kind="repeat"};
        var child=new PlanItem{Title="child",ParentId=parent.Id,ScheduleSchema=1,Kind="repeat",Notes="keep",Priority="high"};
        var store=new MemoryStore{Items=new(){parent,child}};var controller=new PlannerController(store);
        var draft=controller.CreateDraft(child);draft.Title="changed";check(child.Title=="child" && draft.Id==child.Id,"Planner draft is detached from stored data");
        store.Fail=true;check(!controller.Save(draft) && store.Items[1].Title=="child","Controller failed edit leaves original untouched");
        check(!controller.SetDone(child.Id,true) && !store.Items[1].Done,"Controller failed completion rolls back");
        check(!controller.Delete(parent.Id,out var failedUndo) && failedUndo==null && store.Items.Count==2 && store.Items[1].ParentId==parent.Id,"Controller failed deletion preserves items and legacy references");
        store.Fail=false;check(controller.Save(draft) && store.Items[1].Title=="changed" && store.Items[1].Notes=="keep" && store.Items[1].Priority=="high","Controller save preserves hidden legacy fields");
        check(controller.SetDone(child.Id,true) && PlanRules.DoneToday(store.Items[1],today) && !store.Items[0].Done,"Controller completion remains independent");
        check(controller.Delete(parent.Id,out var undo) && store.Items.Count==1 && store.Items[0].ParentId==null,"Controller deletes only parent and clears direct references");
        var extra=new PlanItem{Title="new",ScheduleSchema=1};controller.Save(extra);
        check(controller.Undo(undo!) && store.Items.Count==3 && store.Items.Single(p=>p.Id==child.Id).ParentId==parent.Id && store.Items.Any(p=>p.Id==extra.Id),"Undo preserves subsequent additions and restores relation");
        var expired=new PlanItem{Title="expired",ScheduleSchema=1,ScheduledDate=today.AddDays(-1)};store.Items.Add(expired);store.Fail=true;
        check(!controller.Reconcile(today) && store.Items.Any(p=>p.Id==expired.Id),"Failed rollover leaves expired records recoverable");store.Fail=false;
        check(controller.Reconcile(today) && !store.Items.Any(p=>p.Id==expired.Id),"Successful rollover commits through store");
        store.Items.Add(new PlanItem{Title="future",ScheduleSchema=1,ScheduledDate=today.AddDays(1)});
        check(controller.Items(true,today).Count==controller.Items(false,today).Count+1,"Management includes future item without changing today list");
        check(!controller.SetDone("missing",true) && !controller.Delete("missing",out _),"Missing IDs cannot silently mutate plans");
        var state=new AppState{Plans=new(){parent.Copy()},PlannerLevel=1};var previous=state.Plans;var adapter=new AppStatePlannerStore(state,()=>false);
        check(!adapter.TryWrite(new[]{child},3) && ReferenceEquals(state.Plans,previous) && state.PlannerLevel==1,"JSON adapter failed save restores list identity and legacy selection");
        adapter=new AppStatePlannerStore(state,()=>throw new InvalidOperationException("test"));check(!adapter.TryWrite(new[]{child}) && ReferenceEquals(state.Plans,previous),"JSON adapter exception also restores state");
        bool persisted=false;adapter=new AppStatePlannerStore(state,()=>{persisted=true;return true;});check(adapter.TryWrite(new[]{child},3) && persisted && state.Plans.Single().Id==child.Id && state.PlannerLevel==3,"JSON adapter uses existing persistence callback");
        var view=new PlannerView();using(var module=new PlannerModule(view,store,new Host())){
            check(!module.IsMonitoring,"Unshown module starts without polling");module.OnShown();check(module.IsMonitoring,"Shown module enables date monitoring");module.OnHidden();check(!module.IsMonitoring,"Hidden module stops date monitoring");module.OnShown();module.Dispose();module.Dispose();module.OnShown();check(!module.IsMonitoring && !view.IsEditing,"Disposed module cannot restart and tolerates repeated disposal");
        }
    }
}
