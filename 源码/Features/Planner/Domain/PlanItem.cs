using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileDockDemo.Features.Planner;
public sealed class PlanItem
{
    public int ScheduleSchema { get; set; }
    public string Kind { get; set; }="once";
    public string RepeatRule { get; set; }="daily";
    public int Weekdays { get; set; }=127; // Monday = bit 0.
    public int ActiveDays { get; set; }=2;
    public int RestDays { get; set; }=1;
    public DateTime ScheduledDate { get; set; }=PlanRules.Today;
    public DateTime? DoneDate { get; set; }
    public bool Migrated { get; set; }
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
