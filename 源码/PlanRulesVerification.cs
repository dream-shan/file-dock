using System;
using System.Collections.Generic;
namespace FileDockDemo;
internal static class PlanRulesVerification
{
 internal static void Run(Action<bool,string> check){
 var d=new DateTime(2026,11,2);var p=new PlanItem{ScheduleSchema=1,ScheduledDate=d};
 check(PlanRules.Appears(p,d)&&!PlanRules.Appears(p,d.AddDays(1)),"Once appears only on scheduled date");check(!PlanRules.Appears(p,d.AddDays(-1)),"Future once remains hidden");
 p.Kind="repeat";check(PlanRules.Appears(p,d.AddYears(1)),"Daily repeat has no end");check(!PlanRules.Appears(p,d.AddDays(-1)),"Repeat respects start");
 p.RepeatRule="weekdays";p.Weekdays=31;for(int i=0;i<7;i++)check(PlanRules.Appears(p,d.AddDays(i))==(i<5),"Weekday mask "+i);
 p.RepeatRule="cycle";p.ActiveDays=2;p.RestDays=1;for(int i=0;i<9;i++)check(PlanRules.Appears(p,d.AddDays(i))==(i%3<2),"Cycle boundary "+i);
 p.ScheduledDate=new DateTime(2028,2,28);check(!PlanRules.Appears(p,new DateTime(2028,3,1)),"Cycle includes leap day");
 p.Done=true;p.DoneDate=d;check(PlanRules.DoneToday(p,d)&&!PlanRules.DoneToday(p,d.AddDays(1)),"Done is per local day");var expired=new PlanItem{ScheduleSchema=1,ScheduledDate=d};var future=new PlanItem{ScheduleSchema=1,ScheduledDate=d.AddDays(5)};var items=new List<PlanItem>{p,expired,future};check(PlanRules.Reconcile(items,d.AddDays(1))&&items.Count==2&&!p.Done&&p.DoneDate==null&&p.CompletedAt==null,"Rollover prunes once and resets repeat without history");check(!PlanRules.Reconcile(items,d.AddDays(1)),"Rollover is idempotent");
 var old=new PlanItem{Title="old",Level=0,Notes="keep",Done=true};PlanRules.Normalize(old);items.Add(old);PlanRules.Reconcile(items,PlanRules.Today.AddDays(4));check(items.Contains(old)&&old.Migrated&&old.Done&&old.Notes=="keep","Migration preserves legacy tasks until confirmation");
 bool rejected=false;try{PlanRules.Normalize(new PlanItem{ScheduleSchema=1,Weekdays=0});}catch(System.IO.InvalidDataException){rejected=true;}check(rejected,"Empty weekday mask rejected");var copy=System.Text.Json.JsonSerializer.Deserialize<PlanItem>(System.Text.Json.JsonSerializer.Serialize(p))!;check(copy.ActiveDays==2&&copy.RestDays==1&&copy.ScheduledDate==p.ScheduledDate,"Schedule JSON roundtrip");
 }
}
