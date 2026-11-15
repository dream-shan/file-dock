using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Interop;

namespace FileDockDemo;

internal static class ShortcutDesktopVerification
{
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key,Scan;public uint Flags,Time;public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit,Size=40)] private struct Input { [FieldOffset(0)] public uint Type;[FieldOffset(8)] public KeyboardInput Keyboard; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count,Input[] inputs,int size);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    private static void Chord(ushort key)
    {
        Input Key(ushort code,bool up=false)=>new(){Type=1,Keyboard=new(){Key=code,Flags=up?2u:0u}};
        var keys=new[]{Key(0x11),Key(0x12),Key(0x10),Key(key),Key(key,true),Key(0x10,true),Key(0x12,true),Key(0x11,true)};
        if(SendInput((uint)keys.Length,keys,Marshal.SizeOf<Input>())!=keys.Length)throw new InvalidOperationException("Keyboard input blocked");
    }
    internal static async Task Run(MainWindow window,IntPtr external,Func<Func<bool>,string,Task> wait)
    {
        var dock=window.ConfiguredShortcut(ShortcutCommand.ToggleDock);var pin=window.ConfiguredShortcut(ShortcutCommand.ToggleWindowPin);
        var mode=window.State.DisplayMode!.Value;
        async Task Save(ShortcutCommand command,ShortcutBinding binding)
        {var result=await window.SaveShortcutAsync(command,binding);if(!result.Success)throw new InvalidOperationException(result.Error);}
        try
        {
            await Save(ShortcutCommand.ToggleDock,new(true,7,0x78));
            await Save(ShortcutCommand.ToggleWindowPin,new(true,7,0x79));
            window.SetDisplayMode(PanelDisplayMode.Manual);window.CollapsePanel();SetForegroundWindow(external);
            Chord(0x78);await wait(()=>window.IsVisible,"Real custom reveal shortcut opens the dock");
            Chord(0x78);await wait(()=>!window.IsVisible,"Real custom reveal shortcut collapses the dock");
            SetForegroundWindow(external);Chord(0x79);
            await wait(()=>(NativeMethods.GetExtendedWindowStyle(external)&8)!=0,"Real custom pin shortcut makes the foreground application topmost");
            Chord(0x79);await wait(()=>(NativeMethods.GetExtendedWindowStyle(external)&8)==0,"Real custom pin shortcut restores foreground application stacking");
            var conflict=await window.SaveShortcutAsync(ShortcutCommand.ToggleDock,new(true,7,0x7A));
            if(conflict.Success)throw new InvalidOperationException("Reserved external shortcut unexpectedly registered");
            Chord(0x78);await wait(()=>window.IsVisible,"System shortcut conflict preserves the working reveal key");
            window.CollapsePanel();await Save(ShortcutCommand.ToggleDock,new(false,7,0x78));
            SetForegroundWindow(external);Chord(0x78);await Task.Delay(400);
            await wait(()=>!window.IsVisible,"Disabled reveal shortcut no longer opens the dock");
        }
        finally
        {
            NativeMethods.SetCaptureTopmost(external,false);
            var result=await window.SaveShortcutAsync(ShortcutCommand.ToggleDock,dock);
            if(!result.Success)await window.SaveShortcutAsync(ShortcutCommand.ToggleDock,dock with{Enabled=false});
            result=await window.SaveShortcutAsync(ShortcutCommand.ToggleWindowPin,pin);
            if(!result.Success)await window.SaveShortcutAsync(ShortcutCommand.ToggleWindowPin,pin with{Enabled=false});
            window.SetDisplayMode(mode);
        }
    }
}
