using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
        public Task<bool> TryWriteAsync(IReadOnlyList<PlanItem> items,int? level=null){if(Fail)return Task.FromResult(false);Items=items.Select(p=>p.Copy()).ToList();return Task.FromResult(true);}
    }
    private sealed class Host : IModuleHost
    {
        public void ShowDialog(IModuleDialog dialog){}
        public void CloseDialog(IModuleDialog dialog)=>dialog.OnClosed();
        public void ShowStatus(string text){}
        public bool ConfirmDiscardChanges()=>true;
        public bool CanRestorePopupFocus=>false;
    }
    internal static async Task Run(Action<bool,string> check)
    {
        var today=PlanRules.Today;var parent=new PlanItem{Title="parent",ScheduleSchema=1,Kind="repeat"};
        var child=new PlanItem{Title="child",ParentId=parent.Id,ScheduleSchema=1,Kind="repeat",Notes="keep",Priority="high"};
        var store=new MemoryStore{Items=new(){parent,child}};var controller=new PlannerController(store);
        var draft=controller.CreateDraft(child);draft.Title="changed";check(child.Title=="child" && draft.Id==child.Id,"Planner draft is detached from stored data");
        store.Fail=true;check(!await controller.SaveAsync(draft) && store.Items[1].Title=="child","Controller failed edit leaves original untouched");
        check(!await controller.SetDoneAsync(child.Id,true) && !store.Items[1].Done,"Controller failed completion rolls back");
        var failed=await controller.DeleteAsync(parent.Id);check(!failed.Success && failed.Undo==null && store.Items.Count==2 && store.Items[1].ParentId==parent.Id,"Controller failed deletion preserves items and legacy references");
        store.Fail=false;check(await controller.SaveAsync(draft) && store.Items[1].Title=="changed" && store.Items[1].Notes=="keep" && store.Items[1].Priority=="high","Controller save preserves hidden legacy fields");
        check(await controller.SetDoneAsync(child.Id,true) && PlanRules.DoneToday(store.Items[1],today) && !store.Items[0].Done,"Controller completion remains independent");
        var deleted=await controller.DeleteAsync(parent.Id);check(deleted.Success && store.Items.Count==1 && store.Items[0].ParentId==null,"Controller deletes only parent and clears direct references");
        var extra=new PlanItem{Title="new",ScheduleSchema=1};await controller.SaveAsync(extra);
        check(await controller.UndoAsync(deleted.Undo!) && store.Items.Count==3 && store.Items.Single(p=>p.Id==child.Id).ParentId==parent.Id && store.Items.Any(p=>p.Id==extra.Id),"Undo preserves subsequent additions and restores relation");
        var expired=new PlanItem{Title="expired",ScheduleSchema=1,ScheduledDate=today.AddDays(-1)};store.Items.Add(expired);store.Fail=true;
        check(!await controller.ReconcileAsync(today) && store.Items.Any(p=>p.Id==expired.Id),"Failed rollover leaves expired records recoverable");store.Fail=false;
        check(await controller.ReconcileAsync(today) && !store.Items.Any(p=>p.Id==expired.Id),"Successful rollover commits through store");
        store.Items.Add(new PlanItem{Title="future",ScheduleSchema=1,ScheduledDate=today.AddDays(1)});
        check(controller.Items(true,today).Count==controller.Items(false,today).Count+1,"Management includes future item without changing today list");
        check(!await controller.SetDoneAsync("missing",true) && !(await controller.DeleteAsync("missing")).Success,"Missing IDs cannot silently mutate plans");
        var state=new AppState{Plans=new(){parent.Copy()},PlannerLevel=1};var previous=state.Plans;var adapter=new AppStatePlannerStore(state,(_,_)=>Task.FromResult(false));
        check(!await adapter.TryWriteAsync(new[]{child},3) && ReferenceEquals(state.Plans,previous) && state.PlannerLevel==1,"JSON adapter failed save restores list identity and legacy selection");
        adapter=new AppStatePlannerStore(state,(_,_)=>throw new InvalidOperationException("test"));check(!await adapter.TryWriteAsync(new[]{child}) && ReferenceEquals(state.Plans,previous),"JSON adapter exception also restores state");
        bool persisted=false;adapter=new AppStatePlannerStore(state,(change,apply)=>{var candidate=state.Copy();change(candidate);persisted=true;apply(candidate);return Task.FromResult(true);});check(await adapter.TryWriteAsync(new[]{child},3) && persisted && state.Plans.Single().Id==child.Id && state.PlannerLevel==3,"JSON adapter uses existing persistence callback");
        var view=new PlannerView();using(var module=new PlannerModule(view,store,new Host())){
            check(!module.IsMonitoring,"Unshown module starts without polling");module.OnShown();check(module.IsMonitoring,"Shown module enables date monitoring");module.OnHidden();check(!module.IsMonitoring,"Hidden module stops date monitoring");module.OnShown();module.Dispose();module.Dispose();module.OnShown();check(!module.IsMonitoring && !view.IsEditing,"Disposed module cannot restart and tolerates repeated disposal");
        }
    }
}
