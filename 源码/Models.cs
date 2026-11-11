using FileDockDemo.Features.Planner;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FileDockDemo;

public sealed class PinnedEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Order { get; set; }
    internal PinnedEntry Copy() => (PinnedEntry)MemberwiseClone();
}

public sealed class AppState
{
    public List<CountdownItem> Countdowns {get;set;}=[];
    public List<PlanItem> Plans { get; set; } = [];
    public int EmptyAreaTransparency {get;set;}=60;
    public int PanelGeometryVersion {get;set;}
    public int PlannerLevel { get; set; } = 3;
    public List<PinnedEntry> Pins { get; set; } = [];
    public string DockSide { get; set; } = "Right";
    public double DockTop { get; set; } = -1;
    public double PanelWidth {get;set;}=380;
    public double PanelHeight {get;set;}=500;
    public bool? PanelDocked {get;set;}
    public double? PanelLeft { get; set; }
    public double? PanelTop { get; set; }
    public string? PanelScreen { get; set; }
    public bool KeepOpen { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool AutoDesktop { get; set; } = true;
    public PanelDisplayMode? DisplayMode { get; set; }
    public PanelDisplayMode UnpinnedMode { get; set; } = PanelDisplayMode.Automatic;
    public string SelectedPage { get; set; } = "Performance";
    public string ThemeMode { get; set; } = "System";
    internal AppState Copy()
    {
        var copy = (AppState)MemberwiseClone();
        copy.Pins = Pins.Select(p => p.Copy()).ToList();
        copy.Plans = Plans.Select(p => p.Copy()).ToList();
        copy.Countdowns = Countdowns.Select(p => p.Copy()).ToList();
        return copy;
    }
}
