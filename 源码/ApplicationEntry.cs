using System;
using System.Diagnostics;
using System.IO;

namespace FileDockDemo;

public sealed class ApplicationEntry
{
    public string Id {get;set;}=Guid.NewGuid().ToString("N");
    public string DisplayName {get;set;}="";
    public string Path {get;set;}="";
    public string Arguments {get;set;}="";
    public string WorkingDirectory {get;set;}="";
    internal ApplicationEntry Copy()=>(ApplicationEntry)MemberwiseClone();
    internal static bool Supported(string path)=>System.IO.Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".lnk";
    internal ProcessStartInfo BuildStartInfo()
    {
        if(!Supported(Path)||!File.Exists(Path))throw new FileNotFoundException("应用入口已失效，请重新定位程序。",Path);
        var start=new ProcessStartInfo(Path){UseShellExecute=true};
        // The shell must process a shortcut unchanged, including its own arguments/work directory.
        if(System.IO.Path.GetExtension(Path).Equals(".exe",StringComparison.OrdinalIgnoreCase))
        {
            start.Arguments=Arguments;
            if(!string.IsNullOrWhiteSpace(WorkingDirectory))
            {if(!Directory.Exists(WorkingDirectory))throw new DirectoryNotFoundException("工作目录不存在，请编辑应用入口。");start.WorkingDirectory=WorkingDirectory;}
            else start.WorkingDirectory=System.IO.Path.GetDirectoryName(Path)!;
        }
        return start;
    }
    internal void Launch(){using var process=Process.Start(BuildStartInfo());}
}
