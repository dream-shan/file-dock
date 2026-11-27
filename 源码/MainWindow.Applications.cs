using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Win32;

namespace FileDockDemo;

public partial class MainWindow
{
    private ApplicationsView? _applicationsView;
    private ShortcutRegistry? _shortcuts;
    private readonly AppMappingRuntime _appMappings = new();
    private readonly SemaphoreSlim _catalogGate=new(1);
    private Task _catalogOperations=Task.CompletedTask;

    private void InitializeApplications()
    {
        _applicationsView=new ApplicationsView(()=>State.Applications,AddApplicationsDialog,AddApplicationsAsync,
            app=>LaunchApplication(app.Id),app=>OpenModuleDialog(new ApplicationEditorDialog(this,app,SaveApplicationAsync)),
            app=>OpenShortcutSettings(ShortcutCatalog.Application(app.Id)),MoveApplicationAsync,RemoveApplicationAsync,
            open=>{_menuOpen=open;if(!open)QueueAfterPanelInteraction();});
        ApplicationsSection.Content=_applicationsView;
    }
    private Task<T> CatalogOperation<T>(Func<Task<T>> operation,T declined)
    {
        if(_exitSaving||_quitting)return Task.FromResult(declined);
        async Task<T> Run()
        {
            await _catalogGate.WaitAsync();
            try{return await operation();}
            finally{_catalogGate.Release();}
        }
        var task=Run();_catalogOperations=_catalogOperations.IsCompleted?task:Task.WhenAll(_catalogOperations,task);return task;
    }
    internal Task AddApplicationsAsync(string[] paths)=>CatalogOperation(async()=>{
        var additions=paths.Where(ApplicationEntry.Supported).Where(File.Exists)
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path=>!State.Applications.Any(a=>FileService.NormalizeKey(a.Path)==FileService.NormalizeKey(path)))
            .Select(path=>new ApplicationEntry{Path=path,DisplayName=Path.GetFileNameWithoutExtension(path)}).ToList();
        if(additions.Count==0){SetStatus("请选择尚未添加的 .exe 或 .lnk 文件。",true);return false;}
        bool saved=await _saves.CommitAsync(s=>s.Applications.AddRange(additions),s=>State.Applications=s.Applications);
        if(saved){RefreshApplicationActions();_applicationsView?.Render();SetStatus($"已添加 {additions.Count} 个应用入口。");}return saved;
    },false);
    private async void AddApplicationsDialog()
    {
        _inDialog=true;
        try{var dialog=new OpenFileDialog{Filter="应用或快捷方式|*.exe;*.lnk",Multiselect=true,DereferenceLinks=false};if(dialog.ShowDialog(this)==true)await AddApplicationsAsync(dialog.FileNames);}
        catch(Exception error){SetStatus("添加失败："+error.Message,true);}
        finally{_inDialog=false;QueueAfterPanelInteraction();}
    }
    internal Task<bool> SaveApplicationAsync(ApplicationEntry entry)=>CatalogOperation(async()=>{
        if(!State.Applications.Any(a=>a.Id==entry.Id))return false;
        bool saved=await _saves.CommitAsync(s=>{int index=s.Applications.FindIndex(a=>a.Id==entry.Id);s.Applications[index]=entry.Copy();},s=>State.Applications=s.Applications);
        if(saved){RefreshApplicationActions();_applicationsView?.Render();}return saved;
    },false);
    internal Task MoveApplicationAsync(string id,int position)=>CatalogOperation(async()=>{
        bool saved=await _saves.CommitAsync(s=>{int index=s.Applications.FindIndex(a=>a.Id==id);if(index<0)return;var entry=s.Applications[index];s.Applications.RemoveAt(index);s.Applications.Insert(Math.Clamp(position,0,s.Applications.Count),entry);},s=>State.Applications=s.Applications);
        if(saved)_applicationsView?.Render();return saved;
    },false);
    internal Task RemoveApplicationAsync(string id)=>CatalogOperation(async()=>{
        bool saved=await _saves.CommitAsync(s=>{s.Applications.RemoveAll(a=>a.Id==id);s.Shortcuts!.Remove(ShortcutCatalog.Application(id));},s=>{State.Applications=s.Applications;State.Shortcuts=s.Shortcuts;});
        if (saved)
        {
            _shortcuts?.Remove(ShortcutCatalog.Application(id));
            _appMappings.Update(State.AppShortcutGroups, State.Shortcuts);
            _applicationsView?.Render();
        }
        return saved;
    },false);
    internal void LaunchApplication(string id)
    {
        if(_exitSaving||_quitting||_windowPicker!=null)return;
        var app=State.Applications.FirstOrDefault(a=>a.Id==id);if(app==null)return;
        try{app.Launch();SetStatus("已请求打开："+app.DisplayName);if(IsVisible)CollapsePanel();}
        catch(Exception error){SetStatus("启动失败："+error.Message,true);if(!IsVisible&&!App.IsSelfTest&&_tray!=null){_tray.BalloonTipTitle="应用启动失败";_tray.BalloonTipText=error.Message;_tray.ShowBalloonTip(2500);}}
    }
    private void InitializeShortcuts()
    {
        _shortcuts=new ShortcutRegistry(new NativeShortcutBackend(_source!.Handle,!App.IsSelfTest));
        _shortcuts.Add(ShortcutCatalog.Wake,"唤出 / 收起文件坞",()=>{
            if(_exitSaving||_windowPicker!=null)return;RecordDiagnosticState("hotkey");if(!UpdateCaptureVisibility()){if(IsVisible)CollapsePanel();else ExpandPanel();}
        },State.Shortcuts![ShortcutCatalog.Wake]);
        _shortcuts.Add(ShortcutCatalog.Pin,"窗口置顶",()=>{
            if(!_exitSaving&&_windowPicker==null&&!_captureInteractionHeld&&_windowPinning!=null)NotifyWindowPin(_windowPinning.Toggle(NativeMethods.GetForegroundWindow()));
        },State.Shortcuts[ShortcutCatalog.Pin]);
        _shortcuts.Add(ShortcutCatalog.Screenshot,"截图",RunScreenshot,State.Shortcuts[ShortcutCatalog.Screenshot]);
        _shortcuts.Add(ShortcutCatalog.Preview,"文件预览",null,State.Shortcuts[ShortcutCatalog.Preview]);
        RefreshApplicationActions();UpdateShortcutHints();
        _appMappings.Update(State.AppShortcutGroups, State.Shortcuts);
    }
    private bool MatchesFilePreviewShortcut(KeyEventArgs e)
    {
        var key=e.Key==Key.System?e.SystemKey:e.Key;
        return MatchesFilePreviewShortcut((uint)Keyboard.Modifiers,KeyInterop.VirtualKeyFromKey(key));
    }
    private bool MatchesFilePreviewShortcut(uint modifiers,int key) =>
        IsFilesPage && !_quitting && !_exitSaving && !_inDialog && ActiveModuleDialog==null && !_captureInteractionHeld &&
        State.Shortcuts!.TryGetValue(ShortcutCatalog.Preview,out var binding) && binding.Matches(modifiers,key);
    private void RefreshApplicationActions()
    {
        if(_shortcuts==null)return;
        foreach(var app in State.Applications)
        {
            string id=app.Id,key=ShortcutCatalog.Application(id);
            _shortcuts.Add(key,"打开 "+app.DisplayName,()=>LaunchApplication(id),State.Shortcuts!.GetValueOrDefault(key)??new());
        }
    }
    internal Task<ShortcutResult> ChangeShortcutAsync(string action,ShortcutBinding binding)=>CatalogOperation(async()=>{
        if(_shortcuts==null)return new ShortcutResult(false,"快捷键管理尚未就绪。");
        var draft=binding.Copy();
        if (action != ShortcutCatalog.Preview && draft.Enabled)
        {
            var chord = new AppChord(draft.Modifiers, draft.Key);
            foreach (var group in State.AppShortcutGroups)
                if (AppMappingRules.Active(group).Any(rule => rule.Alias == chord || rule.Original == chord))
                    return new ShortcutResult(false, $"与「{group.Name}」的应用内映射冲突。请先暂停或更改该映射。");
        }
        var result=await _shortcuts.ChangeAsync(action,draft,()=>_saves.CommitAsync(s=>{
            s.Shortcuts![action]=draft.Copy();
            if(action==ShortcutCatalog.Pin){s.WindowPinHotkeyEnabled=draft.Enabled;s.WindowPinModifiers=draft.Modifiers;s.WindowPinKey=draft.Key;}
        },s=>{State.Shortcuts=s.Shortcuts;State.WindowPinHotkeyEnabled=s.WindowPinHotkeyEnabled;State.WindowPinModifiers=s.WindowPinModifiers;State.WindowPinKey=s.WindowPinKey;}));
        if (result.Success) _appMappings.Update(State.AppShortcutGroups, State.Shortcuts);
        UpdateShortcutHints();return result;
    },new ShortcutResult(false,"程序正在退出。"));
    private void UpdateShortcutHints()
    {
        if(_shortcuts==null)return;
        var wake=_shortcuts.Items.First(a=>a.Key==ShortcutCatalog.Wake);
        HotkeyHint.Text=wake.NativeId!=0?"快捷键唤出":wake.Binding.Enabled?"唤出快捷键不可用":"快捷键已禁用";
        HotkeyHint.ToolTip=wake.Binding.Label+" · "+wake.Status+"；可使用贴边入口或托盘。";
        if(_tray!=null)_tray.Text="文件坞 · "+(wake.NativeId!=0?wake.Binding.Label:"贴边入口 / 托盘唤出");
        var pin=_shortcuts.Items.First(a=>a.Key==ShortcutCatalog.Pin);
        _pinRegistrationStatus=pin.Binding.Label+" · "+pin.Status;
        _toolsView?.UpdateShortcut(pin.Binding,_pinRegistrationStatus);
    }
    internal void OpenShortcutSettings(string? action=null)
    {
        if(_shortcuts==null||_exitSaving)return;if(!IsVisible)ExpandPanel();
        OpenModuleDialog(new ShortcutSettingsView(this,_shortcuts,ChangeShortcutAsync,action,OpenAppShortcutCatalog));
    }

    private void OpenAppShortcutCatalog()
    {
        if (_exitSaving || _quitting) return;
        OpenModuleDialog(new AppShortcutCatalogDialog(this, () => State.AppShortcutGroups, () => State.Applications,
            SaveAppShortcutGroupAsync, PickAppShortcutTarget, () => OpenShortcutSettings(), () => State.Shortcuts!, PickMenuWindow,
            InstalledAppDiscovery.ReadAsync, scanOpenWindows: OpenWindowMenuScanner.ScanAsync));
    }

    internal Task<bool> SaveAppShortcutGroupAsync(AppShortcutGroup group)
    {
        if (!group.Valid()) return Task.FromResult(false);
        var draft = group.Copy();
        string key = draft.IdentityKey;
        return CatalogOperation(async () => {
            if (AppMappingRules.Conflict(draft, State.Shortcuts!) != null) return false;
            return await _saves.CommitAsync(state => {
                int index = state.AppShortcutGroups.FindIndex(item =>
                    string.Equals(item.IdentityKey, key, StringComparison.OrdinalIgnoreCase));
                if (draft.Manual.Count == 0 && draft.Sources.Count == 0 && draft.MenuSource == null && draft.LastMenuRead == null)
                {
                    if (index >= 0) state.AppShortcutGroups.RemoveAt(index);
                }
                else if (index >= 0) state.AppShortcutGroups[index] = draft;
                else state.AppShortcutGroups.Add(draft);
            }, state => {
                State.AppShortcutGroups = state.AppShortcutGroups;
                _appMappings.Update(State.AppShortcutGroups, State.Shortcuts);
            });
        }, false);
    }
}
