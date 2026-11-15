using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileDockDemo;

public sealed class ShortcutBinding
{
    public bool Enabled {get;set;}
    public uint Modifiers {get;set;}
    public int Key {get;set;}
    internal ShortcutBinding Copy()=>(ShortcutBinding)MemberwiseClone();
    internal string Label=>Key==0?"未设置":Features.WindowPinning.WindowPinningView.Format(Modifiers,Key);
    internal bool Valid=>Key==0?!Enabled:Key>=8&&Key<=254&&(Modifiers&3)!=0&&(Modifiers&~15u)==0&&Key is not (0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5);
}

internal static class ShortcutCatalog
{
    internal const string Wake="dock.toggle",Pin="window.pin";
    internal static string Application(string id)=>"app:"+id;
    internal static ShortcutBinding Default(string action)=>action switch {
        Wake=>new(){Enabled=true,Modifiers=3,Key=32},
        Pin=>new(){Enabled=true,Modifiers=3,Key=0x54},_=>new()};
    internal static void Normalize(AppState state)
    {
        if(state.Shortcuts==null)state.Shortcuts=new(){[Wake]=Default(Wake),[Pin]=new(){Enabled=state.WindowPinHotkeyEnabled,Modifiers=state.WindowPinModifiers,Key=state.WindowPinKey}};
        foreach(var action in new[]{Wake,Pin})if(!state.Shortcuts.ContainsKey(action))state.Shortcuts[action]=Default(action);
        if(state.Shortcuts.Any(pair=>string.IsNullOrWhiteSpace(pair.Key)||pair.Value==null||!pair.Value.Valid))throw new System.IO.InvalidDataException("快捷键配置无效。");
    }
}

internal interface IShortcutBackend
{
    bool Register(int id,ShortcutBinding binding);
    void Unregister(int id);
}

internal sealed class NativeShortcutBackend(IntPtr window,bool enabled=true) : IShortcutBackend
{
    public bool Register(int id,ShortcutBinding binding)=>!enabled||NativeMethods.RegisterHotKey(window,id,binding.Modifiers|0x4000,(uint)binding.Key);
    public void Unregister(int id){if(enabled)NativeMethods.UnregisterHotKey(window,id);}
}

internal sealed class ShortcutAction(string key,string name,Action run,ShortcutBinding binding)
{
    internal string Key {get;}=key;
    internal string Name {get;set;}=name;
    internal Action Run {get;}=run;
    internal ShortcutBinding Binding {get;set;}=binding.Copy();
    internal int NativeId {get;set;}
    internal string Status {get;set;}="未设置";
}
internal readonly record struct ShortcutResult(bool Success,string Message);

// Accessed on the UI context. Newly reserved IDs do not dispatch until persistence commits.
internal sealed class ShortcutRegistry(IShortcutBackend backend) : IDisposable
{
    private readonly Dictionary<string,ShortcutAction> _actions=new();
    private readonly Stack<int> _free=new();
    private readonly SemaphoreSlim _changes=new(1);
    private int _next=0x5100;
    private bool _disposed;
    internal IReadOnlyList<ShortcutAction> Items=>_actions.Values.ToArray();
    private int Allocate()=>_free.Count>0?_free.Pop():_next<0xBFFF?_next++:throw new InvalidOperationException("快捷键数量超出限制。");
    // UnregisterHotKey does not discard queued WM_HOTKEY messages. Never reuse a
    // successfully registered ID within this session, even for a different chord.
    private void Release(int id){if(id==0)return;backend.Unregister(id);}
    internal void Add(string key,string name,Action action,ShortcutBinding binding)
    {
        if(_disposed)throw new ObjectDisposedException(nameof(ShortcutRegistry));
        if(_actions.TryGetValue(key,out var existing)){existing.Name=name;return;}
        var item=new ShortcutAction(key,name,action,binding);_actions.Add(key,item);
        if(binding.Enabled&&binding.Valid)
        {
            int id=Allocate();if(backend.Register(id,binding)){item.NativeId=id;item.Status="已生效";}else{_free.Push(id);item.Status="注册失败，可能被占用";}
        }
        else item.Status=binding.Key==0?"未设置":"已禁用";
    }
    internal bool Dispatch(int id)
    {
        var item=_actions.Values.FirstOrDefault(a=>a.NativeId==id&&id!=0);if(item==null)return false;
        item.Run();return true;
    }
    internal async Task<ShortcutResult> ChangeAsync(string key,ShortcutBinding proposed,Func<Task<bool>> persist)
    {
        var binding=proposed.Copy();if(!binding.Valid)return new(false,"请选择包含 Ctrl 或 Alt 的有效组合键。");
        await _changes.WaitAsync();int reserved=0;
        try
        {
            if(_disposed||!_actions.TryGetValue(key,out var item))return new(false,"此动作已移除。");
            var conflict=_actions.Values.FirstOrDefault(a=>a.Key!=key&&a.Binding.Enabled&&binding.Enabled&&a.Binding.Modifiers==binding.Modifiers&&a.Binding.Key==binding.Key);
            if(conflict!=null)return new(false,"与“"+conflict.Name+"”冲突，请选择其他组合键。");
            bool reuse=binding.Enabled&&item.NativeId!=0&&binding.Modifiers==item.Binding.Modifiers&&binding.Key==item.Binding.Key;
            if(binding.Enabled&&!reuse)
            {
                reserved=Allocate();if(!backend.Register(reserved,binding)){_free.Push(reserved);reserved=0;return new(false,"注册失败，可能被其他程序占用或受系统限制；原快捷键保留。");}
            }
            bool saved;try{saved=await persist();}catch{saved=false;}
            if(!saved)return new(false,"保存失败，原快捷键保留；请重试。");
            if(_disposed)return new(false,"程序正在退出。");
            if(!reuse){Release(item.NativeId);item.NativeId=reserved;reserved=0;}
            item.Binding=binding;item.Status=binding.Enabled?"已生效":binding.Key==0?"未设置":"已禁用";
            return new(true,"已保存："+item.Name+" · "+item.Status);
        }
        finally{if(reserved!=0)Release(reserved);_changes.Release();}
    }
    internal void Remove(string key){if(_actions.Remove(key,out var item))Release(item.NativeId);}
    public void Dispose(){if(_disposed)return;_disposed=true;foreach(var item in _actions.Values)Release(item.NativeId);_actions.Clear();}
}
