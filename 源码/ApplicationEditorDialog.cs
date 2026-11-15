using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FileDockDemo.Contracts;
using Microsoft.Win32;

namespace FileDockDemo;

internal sealed class ApplicationEditorDialog : IModuleDialog
{
    private readonly IModuleHost _host;
    private readonly ApplicationEntry _original;
    private readonly TextBox _name=new(),_path=new(),_arguments=new(),_directory=new();
    private readonly TextBlock _message=Ui.Text("");
    private readonly Func<ApplicationEntry,Task<bool>> _save;
    private bool _busy;
    public FrameworkElement View {get;}
    public bool HasChanges=>_busy||_name.Text!=_original.DisplayName||_path.Text!=_original.Path||_arguments.Text!=_original.Arguments||_directory.Text!=_original.WorkingDirectory;
    internal ApplicationEditorDialog(IModuleHost host,ApplicationEntry entry,Func<ApplicationEntry,Task<bool>> save)
    {
        _host=host;_original=entry.Copy();_save=save;
        var root=new StackPanel();root.Children.Add(Ui.Text("编辑应用入口",18));
        Field(root,"显示名称",_name,entry.DisplayName);Field(root,"程序或快捷方式",_path,entry.Path);
        root.Children.Add(Ui.Button("重新定位…",()=>{
            var dialog=new OpenFileDialog{Filter="应用或快捷方式|*.exe;*.lnk",DereferenceLinks=false};
            if(dialog.ShowDialog()==true)_path.Text=dialog.FileName;
        }));
        Field(root,"启动参数（仅程序）",_arguments,entry.Arguments);Field(root,"工作目录（留空使用程序目录）",_directory,entry.WorkingDirectory);
        void UpdateLink(){bool link=Path.GetExtension(_path.Text).Equals(".lnk",StringComparison.OrdinalIgnoreCase);_arguments.IsEnabled=_directory.IsEnabled=!link;}
        _path.TextChanged+=(_,_)=>UpdateLink();UpdateLink();
        root.Children.Add(Ui.Text("快捷方式使用其原有参数、工作目录和权限设置。"));root.Children.Add(_message);
        root.Children.Add(Ui.Button("保存入口",async()=>await Save()));root.Children.Add(Ui.Button("取消",RequestClose));View=Ui.Scroll(root);
    }
    private static void Field(Panel root,string label,TextBox input,string value)
    {
        root.Children.Add(Ui.Text(label));input.Text=value;input.MinHeight=30;System.Windows.Automation.AutomationProperties.SetName(input,label);root.Children.Add(input);
    }
    private async Task Save()
    {
        if(_busy)return;
        var entry=_original.Copy();entry.DisplayName=_name.Text.Trim();entry.Path=_path.Text.Trim();entry.Arguments=_arguments.Text;entry.WorkingDirectory=_directory.Text.Trim();
        if(entry.DisplayName.Length==0||!ApplicationEntry.Supported(entry.Path)||!File.Exists(entry.Path)){_message.Text="请填写名称并选择存在的 .exe 或 .lnk 文件。";return;}
        if(Path.GetExtension(entry.Path).Equals(".lnk",StringComparison.OrdinalIgnoreCase)){entry.Arguments="";entry.WorkingDirectory="";}
        _busy=true;View.IsEnabled=false;
        try{if(await _save(entry)){_host.CloseDialog(this);return;}_message.Text="保存失败，输入已保留，请重试。";}
        catch(Exception error){_message.Text="保存失败："+error.Message;}
        finally{_busy=false;View.IsEnabled=true;}
    }
    public void RequestClose(){if(_busy)return;if(!HasChanges||_host.ConfirmDiscardChanges())_host.CloseDialog(this);}
    public void FocusInitial()=>_name.Focus();
    public void RestoreFocus(){}
    public void OnClosed(){}
    public void HandleWindowKey(KeyEventArgs e){if(e.Key==Key.Escape){RequestClose();e.Handled=true;}}
    public void RefreshTheme(){}
}
