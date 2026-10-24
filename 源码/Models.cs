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

public sealed class RecentEntry
{
    public string Path { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTime LastUsed { get; set; }
    public string Source { get; set; } = "从这里打开";
}

public sealed class AppState
{
    public List<PinnedEntry> Pins { get; set; } = [];
    public List<RecentEntry> History { get; set; } = [];
    public List<string> HiddenRecent { get; set; } = [];
    public string DockSide { get; set; } = "Right";
    public double DockTop { get; set; } = -1;
    public bool KeepOpen { get; set; }
}
