using System.Collections.Generic;
namespace FileDockDemo.Features.Planner;

internal interface IPlannerStore
{
    IReadOnlyList<CountdownItem> ReadCountdowns()=>System.Array.Empty<CountdownItem>();
    bool WriteCountdowns(IReadOnlyList<CountdownItem> items)=>false;
    IReadOnlyList<PlanItem> Read();
    // Atomic from the caller's perspective: false leaves the previous data intact.
    bool TryWrite(IReadOnlyList<PlanItem> items, int? legacyLevel = null);
}
