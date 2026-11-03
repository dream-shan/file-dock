using System.Collections.Generic;
namespace FileDockDemo.Features.Planner;

internal interface IPlannerStore
{
    IReadOnlyList<PlanItem> Read();
    // Atomic from the caller's perspective: false leaves the previous data intact.
    bool TryWrite(IReadOnlyList<PlanItem> items, int? legacyLevel = null);
}
