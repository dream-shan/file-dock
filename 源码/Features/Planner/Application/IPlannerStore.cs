using System.Collections.Generic;
using System.Threading.Tasks;
namespace FileDockDemo.Features.Planner;

internal interface IPlannerStore
{
    IReadOnlyList<CountdownItem> ReadCountdowns()=>System.Array.Empty<CountdownItem>();
    Task<bool> WriteCountdownsAsync(IReadOnlyList<CountdownItem> items)=>Task.FromResult(false);
    IReadOnlyList<PlanItem> Read();
    // Atomic from the caller's perspective: false leaves the previous data intact.
    Task<bool> TryWriteAsync(IReadOnlyList<PlanItem> items, int? legacyLevel = null);
}
