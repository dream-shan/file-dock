using System;
namespace FileDockDemo.Features.Planner;
public sealed class CountdownItem
{
    public string Id {get;set;}=Guid.NewGuid().ToString("N");
    public string Title {get;set;}="";
    public DateTime Date {get;set;}=DateTime.Today;
    public bool Annual {get;set;}
    internal CountdownItem Copy() => (CountdownItem)MemberwiseClone();
    // February 29 anniversaries use February 28 in non-leap years.
    public DateTime Next(DateTime today)
    {
        if(!Annual)return Date.Date;
        DateTime At(int year)=>new(year,Date.Month,Math.Min(Date.Day,DateTime.DaysInMonth(year,Date.Month)));
        var next=At(today.Year);return next<today.Date && today.Year<9999?At(today.Year+1):next;
    }
    public int Days(DateTime today)=>(Next(today)-today.Date).Days;
}
