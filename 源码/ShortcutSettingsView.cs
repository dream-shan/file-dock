using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FileDockDemo.Contracts;

namespace FileDockDemo;

internal sealed class ShortcutSettingsView : IModuleDialog
{
    private readonly IModuleHost _host;
    private readonly ShortcutRegistry _registry;
    private readonly Func<string,ShortcutBinding,Task<ShortcutResult>> _save;
    private readonly StackPanel _body=new();
    private readonly TextBlock _message=Ui.Text("");
    private ShortcutAction? _editing;
    private ShortcutBinding _draft=new();
    private bool _dirty,_busy;
    private TextBox? _input;
    private CheckBox? _enabled;
    public FrameworkElement View {get;}
    public bool HasChanges=>_dirty||_busy;
    internal bool Recording => !_busy && _input?.IsKeyboardFocusWithin==true;
    internal ShortcutSettingsView(IModuleHost host,ShortcutRegistry registry,Func<string,ShortcutBinding,Task<ShortcutResult>> save,string? action=null)
    {
        _host=host;_registry=registry;_save=save;
        var root=new StackPanel();root.Children.Add(Ui.Text("快捷键管理",18));root.Children.Add(_body);root.Children.Add(_message);root.Children.Add(Ui.Button("关闭",RequestClose));
        View=Ui.Scroll(root);ShowList();
        if(action!=null&&registry.Items.FirstOrDefault(a=>a.Key==action) is {} item)Edit(item);
    }
    private void ShowList()
    {
        _body.Children.Clear();_editing=null;_dirty=false;_input=null;_enabled=null;
        foreach(var item in _registry.Items)
        {
            string label=item.Name+"\n"+item.Scope+" · "+(item.Binding.Key==0?"未设置":item.Binding.Label+" · "+item.Status);
            var row=Ui.Button(label,()=>Edit(item));
            row.Content=new TextBlock{Text=label,TextWrapping=TextWrapping.Wrap};
            row.HorizontalContentAlignment=HorizontalAlignment.Left;row.ToolTip=item.Scope+"快捷键 · 文件坞管理";_body.Children.Add(row);
        }
    }
    private void Edit(ShortcutAction item)
    {
        _body.Children.Clear();_editing=item;_draft=item.Binding.Copy();_dirty=false;
        _body.Children.Add(Ui.Button("← 全部快捷键",()=>{if(!_dirty||_host.ConfirmDiscardChanges())ShowList();}));
        _body.Children.Add(Ui.Text(item.Name,15));_body.Children.Add(Ui.Text(item.Scope+"生效 · 文件坞管理"));
        var enabled=new CheckBox{Content="启用快捷键",IsChecked=_draft.Enabled,Margin=new Thickness(0,8,0,8)};enabled.SetResourceReference(Control.ForegroundProperty,"Ink");
        enabled.Checked+=(_,_)=>{_draft.Enabled=true;_dirty=true;};enabled.Unchecked+=(_,_)=>{_draft.Enabled=false;_dirty=true;};_body.Children.Add(enabled);
        _body.Children.Add(Ui.Text(item.Key==ShortcutCatalog.Preview
            ?"点击下框后按空格或包含 Ctrl、Alt 之一的组合键":"点击下框后按组合键（包含 Ctrl 或 Alt）"));
        var input=new TextBox{IsReadOnly=true,Text=_draft.Label,MinHeight=34};_input=input;_enabled=enabled;System.Windows.Automation.AutomationProperties.SetName(input,"快捷键组合");
        input.PreviewKeyDown+=(_,e)=>{
            var key=e.Key==Key.System?e.SystemKey:e.Key;if(key is Key.Tab or Key.Escape)return;e.Handled=true;
            var proposed=new ShortcutBinding{Enabled=true,Modifiers=(uint)Keyboard.Modifiers,Key=KeyInterop.VirtualKeyFromKey(key)};
            RecordBinding(proposed);
        };_body.Children.Add(input);
        _body.Children.Add(Ui.Button("恢复默认",()=>{_draft=ShortcutCatalog.Default(item.Key);enabled.IsChecked=_draft.Enabled;input.Text=_draft.Label;_dirty=true;}));
        _body.Children.Add(Ui.Button("清除快捷键",()=>{_draft=new();enabled.IsChecked=false;input.Text=_draft.Label;_dirty=true;}));
        _body.Children.Add(Ui.Button("保存快捷键",async()=>await Save()));
        input.Dispatcher.BeginInvoke(()=>input.Focus());
    }
    internal bool TryCaptureHotkey(uint modifiers,int key)
    {
        if(!Recording||_editing==null)return false;
        var proposed=new ShortcutBinding{Enabled=true,Modifiers=modifiers&15,Key=key};
        RecordBinding(proposed);
        return true;
    }
    private void RecordBinding(ShortcutBinding proposed)
    {
        if (_editing == null || _input == null || _enabled == null) return;
        if (!proposed.IsValidFor(_editing.Key))
        {
            if (_editing.Key == ShortcutCatalog.Preview)
                _message.Text="请选择空格或有效组合键，避开文件列表已有操作。";
            return;
        }
        _draft=proposed;
        _enabled.IsChecked=true;
        _input.Text=_draft.Label;
        _message.Text="";
        _dirty=true;
    }
    private async Task Save()
    {
        if(_busy||_editing==null)return;_busy=true;_body.IsEnabled=false;
        try{var result=await _save(_editing.Key,_draft.Copy());_message.Text=result.Message;if(result.Success)ShowList();}
        catch(Exception error){_message.Text="保存失败："+error.Message;}
        finally{_busy=false;_body.IsEnabled=true;}
    }
    public void RequestClose(){if(_busy){_message.Text="正在保存，请稍候。";return;}if(!_dirty||_host.ConfirmDiscardChanges())_host.CloseDialog(this);}
    public void OnClosed(){}
    public void FocusInitial(){if(_input!=null)_input.Focus();else View.Focus();}
    public void RestoreFocus(){}
    public void HandleWindowKey(KeyEventArgs e){if(e.Key==Key.Escape){RequestClose();e.Handled=true;}}
    public void RefreshTheme(){}
}

internal static class Ui
{
    internal static TextBlock Text(string text,double size=13)
    {
        var block=new TextBlock{Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,6)};
        block.SetResourceReference(TextBlock.ForegroundProperty,"Ink");return block;
    }
    internal static Button Button(string text,Action action)
    {
        var button=new Button{Content=text,MinHeight=34,Margin=new Thickness(0,4,0,0),Padding=new Thickness(9,6,9,6)};
        button.SetResourceReference(Control.BackgroundProperty,"Surface");button.SetResourceReference(Control.ForegroundProperty,"Ink");button.SetResourceReference(Control.BorderBrushProperty,"Border");
        button.Click+=(_,_)=>action();return button;
    }
    internal static ScrollViewer Scroll(object content)=>new(){Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
}
