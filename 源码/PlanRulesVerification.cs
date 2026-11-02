using System;
using System.Collections.Generic;
using System.IO;
namespace FileDockDemo;
internal static class PlanRulesVerification
{
    internal static void Run(Action<bool,string> check)
    {
        var today=new DateTime(2026,11,2);
        PlanItem P(int level,DateTime date){var p=new PlanItem{Title="fixture",PlanSchema=1};PlanRules.AssignPeriod(p,level,date);return p;}
        check(PlanRules.WEEK_START==DayOfWeek.Monday && PlanRules.Week(new DateTime(2027,1,3))==new DateTime(2026,12,28),"Week boundaries use Monday across years");
        var leap=P(1,new DateTime(2028,2,12));check(leap.ScopePeriod=="2028-02" && PlanRules.End(leap)==new DateTime(2028,2,29),"Monthly period includes leap day");
        var goal=P(0,today);var month=P(1,today);var week=P(2,today);var day=P(3,today);
        check(goal.ScopePeriod==null && week.ScopePeriod=="2026-11-02" && day.ScopePeriod=="2026-11-02","Scope periods persist local calendar dates");
        check(PlanRules.ParentAllowed(goal,day) && PlanRules.ParentAllowed(month,day) && PlanRules.ParentAllowed(week,day),"Daily tasks can choose any higher containing level");
        check(!PlanRules.ParentAllowed(day,goal) && !PlanRules.ParentAllowed(month,month),"Same or lower levels cannot be parents");
        goal.Done=true;check(!PlanRules.ParentAllowed(goal,day),"Completed tasks are excluded from parent candidates");goal.Done=false;
        var crossing=P(2,new DateTime(2026,11,30));check(!PlanRules.ParentAllowed(month,crossing),"A cross-month week cannot attach to a partially containing month");
        var prior=P(2,today.AddDays(-7));check(!PlanRules.ParentAllowed(prior,day),"Parent candidates exclude a different week");
        check(PlanRules.EffectiveDue(goal)==null && PlanRules.EffectiveDue(month)==new DateTime(2026,11,30) && PlanRules.EffectiveDue(week)==new DateTime(2026,11,8),"Unset deadlines derive from period end except long-term");
        check(!PlanRules.Overdue(day,today) && PlanRules.Overdue(day,today.AddDays(1)),"Overdue comparison treats today as inclusive");day.Done=true;check(!PlanRules.Overdue(day,today.AddDays(1)),"Completed tasks are not overdue");day.Done=false;
        day.Deadline=today.AddDays(1);check(PlanRules.SuggestedLevel(day,today)==2,"Out-of-day due date suggests the containing week");day.Deadline=new DateTime(2026,12,1);check(PlanRules.SuggestedLevel(day,today)==0,"A date beyond the current month suggests long-term");
        day.Deadline=today.AddDays(5);check(!PlanRules.Overdue(day,today.AddDays(1)),"Explicit deadline overrides implicit period end");
        var legacy=new PlanItem{Title="legacy",Level=2,Date=new DateTime(2026,11,4),Done=true,ParentId=month.Id};PlanRules.Normalize(legacy);
        check(legacy.ScopePeriod=="2026-11-02" && legacy.Done && legacy.ParentId==month.Id && legacy.Priority=="none" && legacy.CompletedAt==null && legacy.CreatedAtEstimated,"Legacy migration retains data and does not invent a completion timestamp");
        var items=new List<PlanItem>{goal,month,week,day};month.ParentId=goal.Id;week.ParentId=month.Id;day.ParentId=week.Id;
        check(PlanRules.Descendants(goal,items).Count==3,"Descendant traversal includes all hierarchy levels");
        day.Done=true;check(!PlanRules.Complete(goal,items),"Child progress and parent completion remain independent");
    }
}
