using System;
using System.Collections.Generic;
using System.Linq;
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
    internal bool Save(PlanItem plan)
    {
        var items=Copy();int index=items.FindIndex(p=>p.Id==plan.Id);
        if(index<0)items.Add(plan.Copy());else items[index]=plan.Copy();
        return _store.TryWrite(items,plan.Level);
    }
    internal bool Reconcile(DateTime today)
    {
        var items=Copy();return !PlanRules.Reconcile(items,today) || _store.TryWrite(items);
    }
    internal bool SetDone(string id,bool done)
    {
        var items=Copy();var p=items.FirstOrDefault(p=>p.Id==id);if(p==null)return false;
        PlanRules.SetDone(p,done);return _store.TryWrite(items);
    }
    internal bool Delete(string id,out PlannerUndo? undo)
    {
        undo=null;var before=Copy();if(!before.Any(p=>p.Id==id))return false;
        var children=before.Where(p=>p.ParentId==id).Select(p=>p.Id).ToArray();
        var items=before.Where(p=>p.Id!=id).Select(p=>p.Copy()).ToList();
        foreach(var child in items.Where(p=>p.ParentId==id))child.ParentId=null;
        if(!_store.TryWrite(items))return false;undo=new PlannerUndo(before,children);return true;
    }
    internal bool Undo(PlannerUndo undo)
    {
        var items=Copy();foreach(var old in undo.Before){var now=items.FirstOrDefault(p=>p.Id==old.Id);
            if(now==null)items.Add(old.Copy());else if(undo.Children.Contains(now.Id))now.ParentId=old.ParentId;}
        return _store.TryWrite(items);
    }
}
