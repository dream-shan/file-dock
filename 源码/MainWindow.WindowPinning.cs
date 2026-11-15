using System;
using System.Windows.Interop;
using FileDockDemo.Features.WindowPinning;

namespace FileDockDemo;

public partial class MainWindow
{
    private WindowPinningModule? _windowPinning;
    private WindowPinningView? _toolsView;
    private WindowPicker? _windowPicker;
    private string _pinRegistrationStatus="";
    private void InitializeWindowPinning()
    {
        _windowPinning=new WindowPinningModule(new PinNative(),!App.IsSelfTest);
        _toolsView=new WindowPinningView(this,_windowPinning,PickPinWindow,State.WindowPinHotkeyEnabled,
            State.WindowPinModifiers,State.WindowPinKey,(_,_,_)=>"请在统一快捷键管理中修改。",_pinRegistrationStatus,
            ()=>OpenShortcutSettings(ShortcutCatalog.Pin),()=>OpenShortcutSettings());
        ToolsSection.Content=_toolsView.View;
        ToolsSection.IsVisibleChanged+=(_,_)=>{if(ToolsSection.IsVisible)_toolsView?.OnShown();else _toolsView?.OnHidden();};
        if(ToolsSection.IsVisible)_toolsView.OnShown();
    }
    private void OpenWindowPinning()
    {
        if(_windowPinning==null||_windowPicker!=null)return;
        if(!IsVisible)ExpandPanel();
        SelectPage("Tools");
        if(IsToolsPage)_toolsView?.ShowDetails();
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
    }
}
