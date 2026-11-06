using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileDockDemo.Features.Planner;
internal static class PlanRules
{
    internal static bool Appears(PlanItem p,DateTime day)
    {
        day=day.Date;var start=p.ScheduledDate.Date;if(day<start)return false;
        if(p.Kind=="once")return day==start;
        if(p.RepeatRule=="weekdays")return (p.Weekdays & (1<<(((int)day.DayOfWeek+6)%7)))!=0;
        if(p.RepeatRule=="cycle")return (day-start).Days%(p.ActiveDays+p.RestDays)<p.ActiveDays;
        return true;
    }
    internal static bool DoneToday(PlanItem p,DateTime day)=>p.Done && p.DoneDate?.Date==day.Date;
    internal static string ScheduleLabel(PlanItem p)=>p.Kind=="once" ? "一次性 · "+p.ScheduledDate.ToString("M月d日") : p.RepeatRule switch {
        "weekdays"=>"每周 "+string.Join("、",Enumerable.Range(0,7).Where(i=>(p.Weekdays&(1<<i))!=0).Select(i=>new[]{"一","二","三","四","五","六","日"}[i])),
        "cycle"=>$"连续 {p.ActiveDays} 天 · 休息 {p.RestDays} 天",_=>"每天"};
    internal static bool Reconcile(List<PlanItem> items,DateTime today)
    {
        bool changed=items.RemoveAll(p=>p.ScheduleSchema==1 && !p.Migrated && p.Kind=="once" && p.ScheduledDate.Date<today.Date)>0;
        foreach(var p in items)if(p.ScheduleSchema==1 && !p.Migrated && p.Done && !DoneToday(p,today)){p.Done=false;p.DoneDate=null;p.CompletedAt=null;changed=true;}
        return changed;
    }
    internal const DayOfWeek WEEK_START=DayOfWeek.Monday;
    internal static readonly string[] Names={"长期","本月","本周","今天"};
    internal static DateTime Today=>DateTime.Today;
    internal static DateTimeOffset Now=>DateTimeOffset.Now;
    internal static DateTime Week(DateTime d)=>d.Date.AddDays(-((int)d.DayOfWeek-(int)WEEK_START+7)%7);
    internal static string? Period(int level,DateTime date)=>level switch{0=>null,1=>date.ToString("yyyy-MM",CultureInfo.InvariantCulture),2=>Week(date).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),_=>date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)};
    internal static void AssignPeriod(PlanItem p,int level,DateTime date){p.Level=level;p.ScopePeriod=Period(level,date);p.Date=Start(p);}
    internal static DateTime Start(PlanItem p)
    {
        if(p.Level==0)return p.Date.Date;
        if(p.ScopePeriod is string period && DateTime.TryParseExact(period,p.Level==1 ? "yyyy-MM":"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))return p.Level==2 ? Week(date):date.Date;
        return p.Level==1 ? new DateTime(p.Date.Year,p.Date.Month,1):p.Level==2 ? Week(p.Date):p.Date.Date;
    }
    internal static DateTime? End(PlanItem p){var d=Start(p);return p.Level switch{0=>null,1=>new DateTime(d.Year,d.Month,DateTime.DaysInMonth(d.Year,d.Month)),2=>d.AddDays(6),_=>d};}
    internal static DateTime? EffectiveDue(PlanItem p)=>p.Deadline?.Date??End(p);
    internal static bool Overdue(PlanItem p,DateTime today)=>!p.Done && EffectiveDue(p) is DateTime d && d<today.Date;
    internal static bool InPeriod(PlanItem p,int level,DateTime date)=>p.Level==level && (level==0 || Period(level,Start(p))==Period(level,date));
    internal static bool Complete(PlanItem p,List<PlanItem> all)=>p.Done;
    internal static bool ParentAllowed(PlanItem parent,PlanItem child)=>parent.Id!=child.Id && !parent.Done && parent.Level<child.Level && (parent.Level==0 || Start(parent)<=Start(child) && End(parent)>=End(child));
    internal static IEnumerable<PlanItem> Parents(PlanItem child,IEnumerable<PlanItem> all)=>all.Where(p=>ParentAllowed(p,child)).OrderBy(p=>p.Level).ThenBy(p=>p.Title);
    internal static int? SuggestedLevel(PlanItem p,DateTime today)
    {
        if(p.Deadline is not DateTime due || End(p) is not DateTime end || due.Date<=end)return null;
        for(int level=p.Level-1;level>=0;level--){var candidate=new PlanItem();AssignPeriod(candidate,level,today);if(End(candidate) is not DateTime bound || due.Date<=bound)return level;}
        return null;
    }
    internal static List<PlanItem> Descendants(PlanItem item,List<PlanItem> all)
    {
        var ids=new HashSet<string>{item.Id};var result=new List<PlanItem>();bool added;
        do{added=false;foreach(var p in all)if(p.ParentId!=null && ids.Contains(p.ParentId) && ids.Add(p.Id)){result.Add(p);added=true;}}while(added);return result;
    }
    internal static void Normalize(PlanItem p)
    {
        if(p.PlanSchema==0){p.ScopePeriod=Period(p.Level,p.Date);p.CreatedAt??=Now;p.CreatedAtEstimated=true;p.UpdatedAt??=p.CreatedAt;p.PlanSchema=1;}
        p.ScopePeriod=Period(p.Level,Start(p));p.Date=Start(p);
        p.Priority=p.Priority is "low" or "medium" or "high" ? p.Priority:"none";
        if(!p.Done)p.CompletedAt=null;
        if(p.ScheduleSchema==0){p.Kind="once";p.ScheduledDate=(p.Deadline??(p.Level==3 ? Start(p):Today)).Date;p.DoneDate=p.Done ? p.ScheduledDate:null;p.Migrated=true;p.ScheduleSchema=1;}
        if(p.Kind is not ("once" or "repeat") || p.RepeatRule is not ("daily" or "weekdays" or "cycle") || p.Weekdays<1 || p.Weekdays>127 || p.ActiveDays<1 || p.ActiveDays>365 || p.RestDays<1 || p.RestDays>365 || p.ScheduleSchema!=1)throw new System.IO.InvalidDataException("待办重复规则无效。");
    }
    internal static void SetDone(PlanItem p,bool done){p.Done=done;p.DoneDate=done ? Today:null;p.CompletedAt=done ? Now:null;p.UpdatedAt=Now;}
}
