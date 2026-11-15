using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace FileDockDemo;

public partial class MainWindow
{
    private ShortcutRegistry? _shortcuts;
    private Window? _shortcutWindow;
    private ShortcutSettingsView? _shortcutEditor;
    private Task _shortcutOperations=Task.CompletedTask;
    internal ShortcutBinding ConfiguredShortcut(ShortcutCommand command)=>command==ShortcutCommand.ToggleDock
        ?new(State.DockHotkeyEnabled,State.DockHotkeyModifiers,State.DockHotkeyKey)
        :new(State.WindowPinHotkeyEnabled,State.WindowPinModifiers,State.WindowPinKey);
    private void InitializeShortcuts()
    {
        _shortcuts=new ShortcutRegistry((id,mods,key)=>App.IsSelfTest||NativeMethods.RegisterHotKey(_source!.Handle,id,mods,key),
            id=>{if(!App.IsSelfTest&&_source!=null)NativeMethods.UnregisterHotKey(_source.Handle,id);});
        foreach(var command in Enum.GetValues<ShortcutCommand>())_shortcuts.Initialize(command,ConfiguredShortcut(command));
        UpdateShortcutHints();
    }
    private string ShortcutStatus(ShortcutCommand command)
    {
        if(!ConfiguredShortcut(command).Enabled)return "已禁用，仍可使用鼠标入口。";
        return _shortcuts?.IsRegistered(command)==true?"已启用："+ShortcutSettingsView.Format(ConfiguredShortcut(command)):
            "未生效："+(_shortcuts?.GetError(command)??"快捷键未初始化");
    }
    private void UpdateShortcutHints()
    {
        string text=ShortcutStatus(ShortcutCommand.ToggleDock);
        HotkeyHint.Text=State.DockHotkeyEnabled&&_shortcuts?.IsRegistered(ShortcutCommand.ToggleDock)==true?ShortcutSettingsView.Format(ConfiguredShortcut(ShortcutCommand.ToggleDock)):"贴边入口 / 托盘唤出";
        HotkeyHint.ToolTip=text;
        if(_tray!=null)_tray.Text="文件坞";
        if(State.DockHotkeyEnabled&&_shortcuts?.IsRegistered(ShortcutCommand.ToggleDock)!=true)SetStatus(text,true);
    }
    internal Task<ShortcutResult> SaveShortcutAsync(ShortcutCommand command,ShortcutBinding binding)
    {
        if(_shortcuts==null||_exitSaving)return Task.FromResult(new ShortcutResult(false,"程序暂不可修改快捷键。"));
        async Task<ShortcutResult> Save()
        {
            var result=await _shortcuts.ChangeAsync(command,binding,()=>_saves.CommitAsync(candidate=>ApplyShortcut(candidate,command,binding),candidate=>ApplyShortcut(State,command,binding)));
            UpdateShortcutHints();return result;
        }
        var operation=Save();_shortcutOperations=_shortcutOperations.IsCompleted?operation:Task.WhenAll(_shortcutOperations,operation);
        return operation;
    }
    private static void ApplyShortcut(AppState state,ShortcutCommand command,ShortcutBinding binding)
    {
        if(command==ShortcutCommand.ToggleDock){state.DockHotkeyEnabled=binding.Enabled;state.DockHotkeyModifiers=binding.Modifiers;state.DockHotkeyKey=binding.Key;}
        else{state.WindowPinHotkeyEnabled=binding.Enabled;state.WindowPinModifiers=binding.Modifiers;state.WindowPinKey=binding.Key;}
    }
    internal void OpenShortcutSettings()
    {
        if(_shortcutWindow!=null){_shortcutWindow.Activate();return;}
        _inDialog=true;
        try
        {
            _shortcutEditor=new ShortcutSettingsView(ConfiguredShortcut,ShortcutStatus,SaveShortcutAsync);
            _shortcutWindow=new Window{Title="文件坞 · 快捷键",Content=_shortcutEditor.View,Owner=this,Width=430,Height=520,MinHeight=420,MinWidth=380,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.CenterOwner};
            _shortcutWindow.Resources.MergedDictionaries.Add(Resources);
            _shortcutWindow.SetResourceReference(Window.BackgroundProperty,"Surface");_shortcutWindow.SetResourceReference(Window.ForegroundProperty,"Ink");
            _shortcutWindow.Closing+=(_,e)=>{if(_shortcutEditor.Busy)e.Cancel=true;};
            _shortcutWindow.ShowDialog();
        }
        finally{_shortcutWindow=null;_shortcutEditor=null;_inDialog=false;QueueDesktopPresence();}
    }
}
