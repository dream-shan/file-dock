using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using FileDockDemo.Contracts;
namespace FileDockDemo.Features.Planner;
internal sealed class CountdownCard : UserControl, IModuleDialog
{
    private readonly TextBox _title=new(){MaxLength=80},_days=new(){MaxLength=7};
    private readonly DatePicker _date=new();
    private readonly CheckBox _annual=new(){Content="每年重复"};
    private readonly TextBlock _error=new(){TextWrapping=TextWrapping.Wrap};
    private readonly IModuleHost _host;private readonly CountdownItem _item;
    private readonly Action _return;private readonly Func<CountdownItem?,Task<bool>> _save;
    internal Task Pending {get;private set;}=Task.CompletedTask;
    private readonly string _original;private bool _sync,_busy,_invalidDays;
    private string Snapshot()=>_title.Text+"|"+_date.SelectedDate+"|"+_annual.IsChecked+"|"+_days.Text;
    public FrameworkElement View=>this;public bool HasChanges=>Snapshot()!=_original;
    internal CountdownCard(CountdownItem? item,IModuleHost host,Func<CountdownItem?,Task<bool>> save,Action restore)
    {
        _host=host;_save=save;_return=restore;_item=item??new();
        SetResourceReference(ForegroundProperty,"Ink");KeyboardNavigation.SetTabNavigation(this,KeyboardNavigationMode.Cycle);
        AutomationProperties.SetAutomationId(this,"CountdownDialog");AutomationProperties.SetName(this,item==null?"添加重要日":"编辑重要日");
        var root=new DockPanel();Content=root;
        Button Btn(string text,Action action){var b=new Button{Content=text,Padding=new Thickness(10,6,10,6),Margin=new Thickness(3)};b.SetResourceReference(StyleProperty,"QuietButton");b.Click+=(_,_)=>action();return b;}
        var head=new DockPanel{Margin=new Thickness(0,0,0,14)};var close=Btn("×",RequestClose);AutomationProperties.SetName(close,"关闭");DockPanel.SetDock(close,Dock.Right);head.Children.Add(close);head.Children.Add(new TextBlock{Text=item==null?"添加重要日":"编辑重要日",FontSize=16,FontWeight=FontWeights.SemiBold});DockPanel.SetDock(head,Dock.Top);root.Children.Add(head);
        var footer=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,16,0,0)};
        if(item!=null)footer.Children.Add(Btn("删除",()=>Commit(null)));
        footer.Children.Add(Btn("取消",RequestClose));var submit=Btn("保存",()=>{if(_invalidDays||string.IsNullOrWhiteSpace(_title.Text)||_date.SelectedDate==null||!DateTime.TryParse(_date.Text,out _)){_error.Text="请输入名称并选择有效日期。";return;}Commit(new CountdownItem{Id=_item.Id,Title=_title.Text.Trim(),Date=_date.SelectedDate.Value.Date,Annual=_annual.IsChecked==true});});AutomationProperties.SetAutomationId(submit,"CountdownSave");submit.SetResourceReference(ForegroundProperty,"Accent");footer.Children.Add(submit);DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var body=new StackPanel();root.Children.Add(new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        void Field(string label,Control control){body.Children.Add(new TextBlock{Text=label,Margin=new Thickness(0,12,0,6)});control.Padding=new Thickness(8);control.SetResourceReference(ForegroundProperty,"Ink");control.SetResourceReference(BackgroundProperty,"Card");control.SetResourceReference(BorderBrushProperty,"Border");body.Children.Add(control);}
                var dateTextStyle=new Style(typeof(DatePickerTextBox));dateTextStyle.Setters.Add(new Setter(TemplateProperty,(ControlTemplate)XamlReader.Parse("""<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="DatePickerTextBox"><Border Background="{DynamicResource Card}" Padding="4"><ScrollViewer x:Name="PART_ContentHost" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"/></Border></ControlTemplate>""")));_date.Resources[typeof(DatePickerTextBox)]=dateTextStyle;Field("名称",_title);Field("目标日期（公历）",_date);Field("剩余天数（与日期同步）",_days);body.Children.Add(_annual);_annual.Margin=new Thickness(0,16,0,8);_annual.SetResourceReference(ForegroundProperty,"Ink");_error.SetResourceReference(ForegroundProperty,"Error");body.Children.Add(_error);
        AutomationProperties.SetAutomationId(_title,"CountdownTitle");AutomationProperties.SetAutomationId(_date,"CountdownDate");AutomationProperties.SetAutomationId(_days,"CountdownDays");
        _title.Text=_item.Title;_date.SelectedDate=_item.Date;_annual.IsChecked=_item.Annual;
        void UpdateDays(){if(_sync)return;_invalidDays=false;_sync=true;_days.Text=_date.SelectedDate is DateTime d?new CountdownItem{Date=d,Annual=_annual.IsChecked==true}.Days(DateTime.Today).ToString():"";_sync=false;}
        _date.SelectedDateChanged+=(_,_)=>UpdateDays();_annual.Checked+=(_,_)=>UpdateDays();_annual.Unchecked+=(_,_)=>UpdateDays();
        _days.TextChanged+=(_,_)=>{if(_sync)return;_invalidDays=false;if(int.TryParse(_days.Text,out int days)&&days>=0 && days<=(DateTime.MaxValue.Date-DateTime.Today).Days){_sync=true;_date.SelectedDate=DateTime.Today.AddDays(days);_sync=false;_error.Text="";}else {_invalidDays=true;_error.Text="请输入有效的非负天数，或直接选择日期。";}};
        _date.DateValidationError+=(_,e)=>{e.ThrowException=false;_error.Text="日期格式无效，请重新选择。";};UpdateDays();_original=Snapshot();
    }
    private void Commit(CountdownItem? item){if(!_busy)Pending=CommitAsync(item);}
    private async Task CommitAsync(CountdownItem? item){_busy=true;IsEnabled=false;try{if(!await _save(item))_error.Text="保存失败，已填写的内容已保留。";}catch(Exception){_error.Text="保存失败，已填写的内容已保留。";}finally{_busy=false;IsEnabled=true;}}
    public void RequestClose(){if(!_busy&&(!HasChanges||_host.ConfirmDiscardChanges()))_host.CloseDialog(this);}
    internal event Action? Closed;public void OnClosed()=>Closed?.Invoke();public void FocusInitial()=>_title.Focus();public void RestoreFocus()=>_return();public void RefreshTheme(){}
    public void HandleWindowKey(KeyEventArgs e){if(e.Key==Key.Escape){RequestClose();e.Handled=true;}}
}
