using System;
using System.Collections.Generic;
using System.Linq;
using FileDockDemo.Features.Planner;
namespace FileDockDemo.Infrastructure;

// Keeps the existing JSON envelope, migration, atomic replacement and backup policy.
internal sealed class AppStatePlannerStore : IPlannerStore
{
    private readonly AppState _state;
    private readonly Func<bool> _persist;
    internal AppStatePlannerStore(AppState state, Func<bool> persist){_state=state;_persist=persist;}
    public IReadOnlyList<CountdownItem> ReadCountdowns()=>_state.Countdowns.AsReadOnly();
    public bool WriteCountdowns(IReadOnlyList<CountdownItem> items){var before=_state.Countdowns;bool saved=false;try{_state.Countdowns=items.ToList();saved=_persist();return saved;}catch{return false;}finally{if(!saved)_state.Countdowns=before;}}
    public IReadOnlyList<PlanItem> Read()=>_state.Plans.AsReadOnly();
    public bool TryWrite(IReadOnlyList<PlanItem> items,int? legacyLevel=null)
    {
        var previous=_state.Plans;int previousLevel=_state.PlannerLevel;bool saved=false;
        try{
            _state.Plans=items.ToList();if(legacyLevel.HasValue)_state.PlannerLevel=legacyLevel.Value;
            saved=_persist();return saved;
        }
        catch{return false;}
        finally{if(!saved){_state.Plans=previous;_state.PlannerLevel=previousLevel;}}
    }
}
