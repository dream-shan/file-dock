using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FileDockDemo.Features.Planner;
namespace FileDockDemo.Infrastructure;

// Keeps the existing JSON envelope, migration, atomic replacement and backup policy.
internal sealed class AppStatePlannerStore : IPlannerStore
{
    private readonly AppState _state;
    private readonly Func<Action<AppState>,Action<AppState>,Task<bool>> _commit;
    internal AppStatePlannerStore(AppState state, Func<Action<AppState>,Action<AppState>,Task<bool>> commit){_state=state;_commit=commit;}
    public IReadOnlyList<CountdownItem> ReadCountdowns()=>_state.Countdowns.Select(p=>p.Copy()).ToArray();
    public async Task<bool> WriteCountdownsAsync(IReadOnlyList<CountdownItem> items)
    {
        var copy=items.Select(p=>p.Copy()).ToList();
        try{return await _commit(candidate=>candidate.Countdowns=copy,candidate=>_state.Countdowns=candidate.Countdowns);}catch{return false;}
    }
    public IReadOnlyList<PlanItem> Read()=>_state.Plans.Select(p=>p.Copy()).ToArray();
    public async Task<bool> TryWriteAsync(IReadOnlyList<PlanItem> items,int? legacyLevel=null)
    {
        var copy=items.Select(p=>p.Copy()).ToList();
        try{return await _commit(candidate=>{candidate.Plans=copy;if(legacyLevel.HasValue)candidate.PlannerLevel=legacyLevel.Value;},
            candidate=>{_state.Plans=candidate.Plans;if(legacyLevel.HasValue)_state.PlannerLevel=candidate.PlannerLevel;});}catch{return false;}
    }
}
