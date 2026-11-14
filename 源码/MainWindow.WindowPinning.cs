using System;
using System.Windows.Interop;
using FileDockDemo.Features.WindowPinning;

namespace FileDockDemo;

public partial class MainWindow
{
    private WindowPinningModule? _windowPinning;
    private WindowPinningView? _toolsView;
    private WindowPicker? _windowPicker;
    private const int WindowPinHotkeyId=0x4F12;
    private bool _pinHotkeyRegistered;
    private string _pinRegistrationStatus="";
    private void InitializeWindowPinning()
    {
        _windowPinning=new WindowPinningModule(new PinNative(),!App.IsSelfTest);
        RegisterPinHotkey();
        _toolsView=new WindowPinningView(this,_windowPinning,PickPinWindow,State.WindowPinHotkeyEnabled,
            State.WindowPinModifiers,State.WindowPinKey,SavePinHotkey,_pinRegistrationStatus);
        ToolsSection.Content=_toolsView.View;
        ToolsSection.IsVisibleChanged+=(_,_)=>{if(ToolsSection.IsVisible)_toolsView?.OnShown();else _toolsView?.OnHidden();};
        if(ToolsSection.IsVisible)_toolsView.OnShown();
    }
    private void RegisterPinHotkey()
    {
        if(_source==null)return;
        if(_pinHotkeyRegistered)NativeMethods.UnregisterHotKey(_source.Handle,WindowPinHotkeyId);
        _pinHotkeyRegistered=false;
        if(!State.WindowPinHotkeyEnabled){_pinRegistrationStatus="快捷键已禁用，仍可通过菜单选择窗口。";return;}
        if(App.IsSelfTest)return;
        _pinHotkeyRegistered=NativeMethods.RegisterHotKey(_source.Handle,WindowPinHotkeyId,State.WindowPinModifiers|0x4000,(uint)State.WindowPinKey);
        _pinRegistrationStatus=_pinHotkeyRegistered?"快捷键已启用："+WindowPinningView.Format(State.WindowPinModifiers,State.WindowPinKey):"快捷键注册失败，可能已被占用；仍可使用鼠标入口。";
    }
    private string SavePinHotkey(bool enabled,uint mods,int key)
    {
        if(key<1||key>254||(mods&3)==0||(mods&~15u)!=0)return "请选择包含 Ctrl 或 Alt 的有效组合键。";
        if(mods==3&&key==0x20)return "此组合键用于唤出文件坞，请选择其他按键。";
        State.WindowPinHotkeyEnabled=enabled;State.WindowPinModifiers=mods;State.WindowPinKey=key;
        RegisterPinHotkey();Persist();return _pinRegistrationStatus;
    }
    private void OpenWindowPinning()
    {
        if(_windowPinning==null||_windowPicker!=null)return;
        if(!IsVisible)ExpandPanel();
        SelectPage("Tools");
    }
    private void NotifyWindowPin(string message)
    {
        SetStatus(message);
        if(!IsVisible&&!App.IsSelfTest&&_tray!=null){_tray.BalloonTipTitle="窗口置顶";_tray.BalloonTipText=message;_tray.ShowBalloonTip(2200);}
    }
    private void PickPinWindow()
    {
        if(_windowPicker!=null||_windowPinning==null||_exitSaving)return;
        CloseModuleDialog();CollapsePanel();_inDialog=true;
        _windowPicker=new WindowPicker(hwnd=>NotifyWindowPin(_windowPinning.Toggle(hwnd)));
        _windowPicker.Closed+=(_,_)=>{_windowPicker=null;_inDialog=false;QueueDesktopPresence();};
        _windowPicker.Show();
    }
    private void UnpinAllWindows()
    {
        int failures=_windowPinning?.RestoreAll()??0;
        NotifyWindowPin(failures==0?"已取消全部受管理的窗口置顶。":$"仍有 {failures} 个窗口取消失败，请在管理面板重试。");
    }
    private bool RestorePinnedWindowsForExit()
    {
        _windowPicker?.Close();int failures=_windowPinning?.RestoreAll()??0;
        return failures==0||System.Windows.MessageBox.Show(this,$"仍有 {failures} 个窗口未能取消置顶。继续退出后这些窗口可能保持置顶。是否继续退出？",
            "文件坞",System.Windows.MessageBoxButton.YesNo,System.Windows.MessageBoxImage.Warning)==System.Windows.MessageBoxResult.Yes;
    }
    private void DisposeWindowPinning()
    {
        _windowPicker?.Close();_toolsView?.OnClosed();_windowPinning?.Dispose();
        if(_pinHotkeyRegistered&&_source!=null)NativeMethods.UnregisterHotKey(_source.Handle,WindowPinHotkeyId);
        _pinHotkeyRegistered=false;
    }
}
