using System;
using System.Collections.Generic;

namespace FileDockDemo;

public sealed class PinnedEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Order { get; set; }
}

public sealed class AppState
{
    public List<PinnedEntry> Pins { get; set; } = [];
    public string DockSide { get; set; } = "Right";
    public double DockTop { get; set; } = -1;
    public double? PanelLeft { get; set; }
    public double? PanelTop { get; set; }
    public string? PanelScreen { get; set; }
    public bool KeepOpen { get; set; }
    public string ThemeMode { get; set; } = "System";
}
