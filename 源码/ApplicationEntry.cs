using System;

namespace FileDockDemo;

public sealed class ApplicationEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Order { get; set; }
    public ApplicationEntry Copy() => (ApplicationEntry)MemberwiseClone();
}
