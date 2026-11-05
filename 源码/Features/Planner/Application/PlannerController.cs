using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace FileDockDemo.Features.Planner;

internal sealed class PlannerUndo
{
    internal readonly IReadOnlyList<PlanItem> Before;
    internal readonly HashSet<string> Children;
    internal PlannerUndo(IReadOnlyList<PlanItem> before,IEnumerable<string> children){Before=before;Children=new(children);}
}

// Application operations: no Window, controls, AppState or file-system access.
internal sealed class PlannerController
{
    private readonly IPlannerStore _store;
    private readonly SemaphoreSlim _operations=new(1);
    internal Task Pending {get;private set;}=Task.CompletedTask;
    internal PlannerController(IPlannerStore store)=>_store=store;
    private List<PlanItem> Copy()=>_store.Read().Select(p=>p.Copy()).ToList();
    internal IReadOnlyList<PlanItem> Items(bool managing,DateTime today)=>_store.Read()
        .Where(p=>managing || PlanRules.Appears(p,today))
        .OrderBy(p=>PlanRules.DoneToday(p,today)).ThenBy(p=>p.CreatedAt).ToArray();
    internal PlanItem CreateDraft(PlanItem? item)
    {
        var draft=item?.Copy()??new PlanItem{ScheduleSchema=1,Level=3,ScheduledDate=PlanRules.Today};
        if(draft.Migrated && draft.ScheduledDate<PlanRules.Today)draft.ScheduledDate=PlanRules.Today;
        return draft;
    }
    private Task<T> Execute<T>(Func<Task<T>> operation)
    {
        var task=RunAsync(operation);Pending=Pending.IsCompleted?task:Task.WhenAll(Pending,task);return task;
    }
    private async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        await _operations.WaitAsync();try{return await operation();}finally{_operations.Release();}
    }
    internal Task<bool> SaveAsync(PlanItem plan)
    {
        var draft=plan.Copy();
        return Execute(()=>{var items=Copy();int index=items.FindIndex(p=>p.Id==draft.Id);
            if(index<0)items.Add(draft);else items[index]=draft;return _store.TryWriteAsync(items,draft.Level);});
    }
    internal Task<bool> ReconcileAsync(DateTime today)=>Execute(()=>{
        var items=Copy();return PlanRules.Reconcile(items,today)?_store.TryWriteAsync(items):Task.FromResult(true);
    });
    internal Task<bool> SetDoneAsync(string id,bool done)=>Execute(()=>{
        var items=Copy();var p=items.FirstOrDefault(p=>p.Id==id);if(p==null)return Task.FromResult(false);
        PlanRules.SetDone(p,done);return _store.TryWriteAsync(items);
    });
    internal Task<(bool Success,PlannerUndo? Undo)> DeleteAsync(string id)=>Execute<(bool,PlannerUndo?)>(async ()=>
    {
        var before=Copy();if(!before.Any(p=>p.Id==id))return (false,null);
        var children=before.Where(p=>p.ParentId==id).Select(p=>p.Id).ToArray();
        var items=before.Where(p=>p.Id!=id).Select(p=>p.Copy()).ToList();
        foreach(var child in items.Where(p=>p.ParentId==id))child.ParentId=null;
        return await _store.TryWriteAsync(items)?(true,new PlannerUndo(before,children)):(false,null);
    });
    internal Task<bool> UndoAsync(PlannerUndo undo)=>Execute(()=>
    {
        var items=Copy();foreach(var old in undo.Before){var now=items.FirstOrDefault(p=>p.Id==old.Id);
            if(now==null)items.Add(old.Copy());else if(undo.Children.Contains(now.Id))now.ParentId=old.ParentId;}
        return _store.TryWriteAsync(items);
    });
}
