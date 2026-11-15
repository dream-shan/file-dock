using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FileDockDemo.Contracts;

namespace FileDockDemo;

// Opt-in only, called from the isolated interactive desktop suite.
internal static class ShortcutDesktopVerification
{
    [StructLayout(LayoutKind.Explicit,Size=40)]private struct Input
    {
        [FieldOffset(0)]internal uint Type;
        [FieldOffset(8)]internal ushort Key;
        [FieldOffset(12)]internal uint Flags;
    }
    [DllImport("user32.dll",SetLastError=true)]private static extern uint SendInput(uint count,Input[] inputs,int size);
    private static void Chord(ushort key)
    {
        var keys=new ushort[]{0x11,0x12,0x10,key};
        var inputs=keys.Select(k=>new Input{Type=1,Key=k}).Concat(keys.Reverse().Select(k=>new Input{Type=1,Key=k,Flags=2})).ToArray();
        if(SendInput((uint)inputs.Length,inputs,Marshal.SizeOf<Input>())!=inputs.Length)throw new InvalidOperationException("真实快捷键输入失败。");
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T found)yield return found;
        foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var item in Find<T>(child))yield return item;
    }
    internal static async Task Run(MainWindow window,string directory,Action<bool,string> check)
    {
        var wake=new ShortcutBinding{Enabled=true,Modifiers=7,Key=0x84};
        check((await window.ChangeShortcutAsync(ShortcutCatalog.Wake,wake)).Success,"Desktop: custom wake shortcut registers through production settings");
        await window.AddApplicationsAsync(new[]{Environment.ProcessPath!});var app=window.State.Applications.Last().Copy();
        string marker=Path.Combine(directory,"shortcut-app-marker.txt");app.Arguments="--launch-probe \""+marker+"\"";app.WorkingDirectory=directory;
        await window.SaveApplicationAsync(app);
        check((await window.ChangeShortcutAsync(ShortcutCatalog.Application(app.Id),new(){Enabled=true,Modifiers=7,Key=0x83})).Success,"Desktop: app launch shortcut registers through production settings");
        window.ExpandPanel();window.Activate();window.OpenShortcutSettings(ShortcutCatalog.Wake);await Task.Delay(180);
        var dialog=window.ActiveModuleDialog!;var input=Find<TextBox>(dialog.View).Single();input.Focus();
        check(input.IsKeyboardFocusWithin,"Desktop: shortcut recorder holds keyboard focus");
        Chord(0x84);await Task.Delay(180);
        check(window.IsVisible&&input.Text==wake.Label,"Desktop: recording current wake combination does not hide dock");
        Chord(0x83);await Task.Delay(180);
        check(!File.Exists(marker)&&input.Text.Contains("F20"),"Desktop: recording an app combination does not launch the app");
        Find<Button>(dialog.View).Single(b=>Equals(b.Content,"保存快捷键")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));await Task.Delay(150);
        check(Find<TextBlock>(dialog.View).Any(t=>t.Text.Contains("冲突")),"Desktop: saving conflicting combination reports the named conflict");
        DemoVerification.SaveRender(window,Path.Combine(directory,"shortcut-manager-desktop.png"));
        ((IModuleHost)window).CloseDialog(dialog);window.Activate();Chord(0x83);
        for(int i=0;i<80&&!File.Exists(marker);i++)await Task.Delay(50);
        check(File.Exists(marker),"Desktop: original app shortcut remains active after rejected conflict");
        window.ExpandPanel();window.Activate();await Task.Delay(120);Chord(0x84);await Task.Delay(180);
        check(!window.IsVisible,"Desktop: original wake shortcut remains active after recording");
        await window.RemoveApplicationAsync(app.Id);
        await window.ChangeShortcutAsync(ShortcutCatalog.Wake,new(){Enabled=false,Modifiers=7,Key=0x84});
    }
}
