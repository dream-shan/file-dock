using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FileDockDemo;

internal static class ShortcutRegistryVerification
{
    private sealed class Backend : IShortcutBackend
    {
        internal readonly Dictionary<int,ShortcutBinding> Active=new();
        internal bool Fail;
        public bool Register(int id,ShortcutBinding binding)
        {
            if(Fail)return false;
            foreach(var value in Active.Values)if(value.Modifiers==binding.Modifiers&&value.Key==binding.Key)return false;
            Active.Add(id,binding.Copy());return true;
        }
        public void Unregister(int id)=>Active.Remove(id);
    }
    internal static async Task Run(Action<bool,string> check)
    {
        var backend=new Backend();using var registry=new ShortcutRegistry(backend);
        int calls=0;registry.Add("wake","唤出",()=>calls++,new(){Enabled=true,Modifiers=3,Key=32});
        var oldId=registry.Items[0].NativeId;
        backend.Fail=true;bool saved=false;
        var result=await registry.ChangeAsync("wake",new(){Enabled=true,Modifiers=3,Key=0x51},()=>{saved=true;return Task.FromResult(true);});
        check(!result.Success&&!saved&&registry.Dispatch(oldId)&&calls==1,"External registration failure leaves original shortcut active without saving");
        backend.Fail=false;
        result=await registry.ChangeAsync("wake",new(){Enabled=true,Modifiers=3,Key=0x51},()=>Task.FromResult(false));
        check(!result.Success&&backend.Active.Count==1&&registry.Dispatch(oldId),"Failed persistence releases reserved shortcut and preserves original binding");
        result=await registry.ChangeAsync("wake",new(){Enabled=true,Modifiers=3,Key=0x51},()=>Task.FromException<bool>(new InvalidOperationException()));
        check(!result.Success&&backend.Active.Count==1,"Thrown persistence failure rolls back reserved registration");
        registry.Add("app:a","应用",()=>{},new(){Enabled=true,Modifiers=3,Key=0x52});
        result=await registry.ChangeAsync("wake",new(){Enabled=true,Modifiers=3,Key=0x52},()=>Task.FromResult(true));
        check(!result.Success&&result.Message.Contains("应用"),"Known shortcut conflicts name the conflicting action");
        result=await registry.ChangeAsync("wake",new(){Enabled=true,Modifiers=3,Key=0x51},()=>Task.FromResult(true));
        check(result.Success&&!registry.Dispatch(oldId)&&backend.Active.Count==2,"Successful shortcut replacement releases the old registration");
        result=await registry.ChangeAsync("wake",new(){Enabled=false,Modifiers=3,Key=0x51},()=>Task.FromResult(true));
        check(result.Success&&backend.Active.Count==1,"Disabling shortcut releases only its registration");
        registry.Remove("app:a");check(backend.Active.Count==0,"Removing app action unregisters its shortcut");
        using(var stale=new ShortcutRegistry(new Backend()))
        {
            stale.Add("old","旧入口",()=>{},new(){Enabled=true,Modifiers=3,Key=0x51});int removedId=stale.Items[0].NativeId;
            stale.Remove("old");stale.Add("new","新入口",()=>{},new(){Enabled=true,Modifiers=3,Key=0x52});
            check(!stale.Dispatch(removedId),"Queued hotkeys from a removed action never dispatch a newly added action");
        }
        var legacy=new AppState{WindowPinHotkeyEnabled=false,WindowPinModifiers=6,WindowPinKey=0x50};ShortcutCatalog.Normalize(legacy);
        check(!legacy.Shortcuts![ShortcutCatalog.Pin].Enabled&&legacy.Shortcuts[ShortcutCatalog.Pin].Key==0x50,"Shortcut migration preserves user's previous pin binding");
        var copy=legacy.Copy();copy.Shortcuts![ShortcutCatalog.Pin].Key=0x51;
        check(legacy.Shortcuts[ShortcutCatalog.Pin].Key==0x50,"Detached shortcut state snapshot cannot mutate live settings");
        using(var window=new System.Windows.Forms.Form{Text="Shortcut registration verification"})
        {
            var handle=window.Handle;using var native=new ShortcutRegistry(new NativeShortcutBackend(handle));
            native.Add("native","原生测试",()=>{},new(){Enabled=true,Modifiers=7,Key=0x87});
            check(native.Items[0].NativeId!=0,"Native backend registers a real global shortcut");
            bool reserved=NativeMethods.RegisterHotKey(handle,0x6200,7,0x86);
            try
            {
                check(reserved,"External shortcut conflict fixture is registered");
                var blocked=await native.ChangeAsync("native",new(){Enabled=true,Modifiers=7,Key=0x86},()=>Task.FromResult(true));
                check(!blocked.Success&&native.Items[0].Binding.Key==0x87,"Real external hotkey conflict preserves old native binding");
            }
            finally{if(reserved)NativeMethods.UnregisterHotKey(handle,0x6200);}
        }
    }
}
