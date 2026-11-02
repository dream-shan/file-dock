using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileDockDemo;
public sealed class PlanItem
{
    public string Id { get; set; }=Guid.NewGuid().ToString("N");
    public string Title { get; set; }="";
    public string Notes { get; set; }="";
    public int Level { get; set; }=3; // Existing file format: long=0, month=1, week=2, day=3.
    public DateTime Date { get; set; }=PlanRules.Today; // Compatibility mirror of ScopePeriod.
    public string? ScopePeriod { get; set; }
    [JsonConverter(typeof(PlanDateConverter))]
    public DateTime? Deadline { get; set; }
    public string Time { get; set; }="";
    public string? ParentId { get; set; }
    public string Priority { get; set; }="none";
    public bool Done { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public bool CreatedAtEstimated { get; set; }
    public int PlanSchema { get; set; }
    public override string ToString()=>Title;
    internal PlanItem Copy()=>(PlanItem)MemberwiseClone();
}

public sealed class PlanDateConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)
    {
        if(reader.TokenType==JsonTokenType.Null)return null;
        if(reader.TokenType==JsonTokenType.String && DateTime.TryParse(reader.GetString(),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var date))return date.Date;
        throw new JsonException("截止日期无效。");
    }
    public override void Write(Utf8JsonWriter writer,DateTime? value,JsonSerializerOptions options){if(value.HasValue)writer.WriteStringValue(value.Value.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));else writer.WriteNullValue();}
}

internal static class PlanRules
{
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
    }
    internal static void SetDone(PlanItem p,bool done){p.Done=done;p.CompletedAt=done ? Now:null;p.UpdatedAt=Now;}
}
