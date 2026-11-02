using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;

namespace FileDockDemo;
internal sealed class PlanCard : UserControl
{
    private readonly List<PlanItem> _all;
    private readonly PlanItem _draft;
    private readonly Func<PlanItem,bool> _commit;
    private readonly StackPanel _fields=new();
    private readonly TextBox _title=new(),_time=new(),_notes=new();
    private readonly ComboBox _parent=new(){DisplayMemberPath="Title",SelectedValuePath="Id"};
    private readonly DatePicker _due=new();
    private readonly TextBlock _error=new(){TextWrapping=TextWrapping.Wrap};
    private readonly Button _date=new();
    private readonly Button[] _types=new Button[4];
    private readonly bool _hasChildren;
    private readonly int _originalLevel;
    private readonly StackPanel _timePanel=new(),_duePanel=new();
    internal PlanCard(PlanItem draft,List<PlanItem> all,bool editing,Func<PlanItem,bool> commit,Action cancel,Action? delete=null,Action? addChild=null,Action<PlanItem>? editChild=null)
    {
        _draft=draft;_all=all;_commit=commit;_originalLevel=draft.Level;_hasChildren=all.Any(p=>p.ParentId==draft.Id);
        var root=new DockPanel{Margin=new Thickness(8)};Content=root;
        var top=new DockPanel();var close=Btn("×",cancel);DockPanel.SetDock(close,Dock.Right);top.Children.Add(close);top.Children.Add(Label(editing ? "编辑计划":"新建待办"));DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,8,0,0)};
        if(delete!=null)actions.Children.Add(Btn("删除",delete));actions.Children.Add(Btn("取消",cancel));actions.Children.Add(Btn(editing ? "保存":"添加",Save));DockPanel.SetDock(actions,Dock.Bottom);root.Children.Add(actions);
        root.Children.Add(new ScrollViewer{Content=_fields,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
        _fields.Children.Add(Label("计划"));_title.Text=draft.Title;_title.MaxLength=200;Input(_title,"PlanTitle");_title.ToolTip="输入计划内容";
        var input= new Grid();_fields.Children.Remove(_title);input.Children.Add(_title);var placeholder=Label("输入计划内容");placeholder.Margin=new Thickness(8,0,0,0);placeholder.VerticalAlignment=VerticalAlignment.Center;placeholder.IsHitTestVisible=false;placeholder.SetResourceReference(TextBlock.ForegroundProperty,"Muted");input.Children.Add(placeholder);_title.TextChanged+=(_,_)=>placeholder.Visibility=_title.Text.Length==0 ? Visibility.Visible:Visibility.Collapsed;placeholder.Visibility=_title.Text.Length==0 ? Visibility.Visible:Visibility.Collapsed;_fields.Children.Add(input);
        var tabs=new UniformGrid{Columns=4,Margin=new Thickness(0,8,0,8)};string[] labels={"长期","本月","本周","今天"};for(int i=0;i<4;i++){int n=i;_types[i]=Btn(labels[i],()=>ChangeLevel(n));AutomationProperties.SetAutomationId(_types[i],"PlanType"+i);tabs.Children.Add(_types[i]);}_fields.Children.Add(tabs);
        _date.SetResourceReference(StyleProperty,"QuietButton");_date.HorizontalContentAlignment=HorizontalAlignment.Left;_date.Click+=(_,_)=>ChooseDate();AutomationProperties.SetAutomationId(_date,"PlanDate");_fields.Children.Add(_date);
        var more=new StackPanel();var expander=new Expander{Header="补充信息",Content=more,Margin=new Thickness(0,10,0,6)};AutomationProperties.SetAutomationId(expander,"PlanMore");_fields.Children.Add(expander);
        _timePanel.Children.Add(Label("具体时间（可选，例如 09:00–10:30）"));_time.Text=draft.Time;Input(_time,"PlanTime",_timePanel);more.Children.Add(_timePanel);
        _duePanel.Children.Add(Label("截止日期（可选）"));_due.SelectedDate=draft.Deadline;_duePanel.Children.Add(_due);more.Children.Add(_duePanel);
        more.Children.Add(Label("关联上级（默认独立）"));more.Children.Add(_parent);AutomationProperties.SetAutomationId(_parent,"PlanParent");
        more.Children.Add(Label("备注"));_notes.Text=draft.Notes;_notes.AcceptsReturn=true;_notes.TextWrapping=TextWrapping.Wrap;_notes.Height=60;Input(_notes,"PlanNotes",more);
        if(editing && addChild!=null)more.Children.Add(Btn("＋ 添加下级计划",addChild));
        if(editChild!=null)foreach(var child in all.Where(p=>p.ParentId==draft.Id))more.Children.Add(Btn("↳ "+child.Title,()=>editChild(child)));
        _error.SetResourceReference(TextBlock.ForegroundProperty,"Error");_fields.Children.Add(_error);
        RefreshTypes(draft.ParentId);Loaded+=(_,_)=>_title.Focus();
    }
    private static TextBlock Label(string s)=>new(){Text=s,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,5,0,5)};
    private static Button Btn(string s,Action action){var b=new Button{Content=s,Padding=new Thickness(8,5,8,5),Margin=new Thickness(1)};b.SetResourceReference(StyleProperty,"QuietButton");b.Click+=(_,_)=>action();return b;}
    private void Input(TextBox box,string id,Panel? panel=null){box.Padding=new Thickness(7);box.Margin=new Thickness(0,2,0,5);box.SetResourceReference(BackgroundProperty,"Card");box.SetResourceReference(ForegroundProperty,"Ink");AutomationProperties.SetAutomationId(box,id);(panel??_fields).Children.Add(box);}
    private void ChangeLevel(int level){if(_hasChildren && level!=_originalLevel){_error.Text="此计划有关联子计划，请先解除子计划关联后再更改类型。";return;}string? old=_parent.SelectedValue as string;_draft.Level=level;RefreshTypes(old);if(!string.IsNullOrEmpty(old) && _parent.SelectedValue as string!=old)_error.Text="原上级不适用于此类型，请重新选择；也可以保持独立。";}
    private void RefreshTypes(string? parent)
    {
        for(int i=0;i<4;i++)_types[i].SetResourceReference(ForegroundProperty,i==_draft.Level ? "Accent":"Muted");
        var parents=new List<PlanItem>{new(){Id="",Title="独立计划"}};parents.AddRange(_all.Where(p=>p.Level==_draft.Level-1 && p.Id!=_draft.Id));_parent.ItemsSource=parents;_parent.SelectedValue=parents.Any(p=>p.Id==parent) ? parent:"";
        _timePanel.Visibility=_draft.Level==3 ? Visibility.Visible:Visibility.Collapsed;_duePanel.Visibility=_draft.Level==0 ? Visibility.Collapsed:Visibility.Visible;UpdateDate();
    }
    private void UpdateDate(){_date.Content="▦  "+(_draft.Level switch{0=>_draft.Deadline is DateTime d ? d.ToString("yyyy年MM月dd日"):"不设截止日期",1=>_draft.Date.ToString("yyyy年MM月"),2=>PlanRules.Week(_draft.Date).Month==PlanRules.Week(_draft.Date).AddDays(6).Month ? $"{PlanRules.Week(_draft.Date):MM月dd日}—{PlanRules.Week(_draft.Date).AddDays(6):dd日}":$"{PlanRules.Week(_draft.Date):MM月dd日}—{PlanRules.Week(_draft.Date).AddDays(6):MM月dd日}",_=>_draft.Date.ToString("MM月dd日 ddd")})+"  ▾";}
    private void ChooseDate()
    {
        var body=new StackPanel();var border=new Border{Child=body,BorderThickness=new Thickness(1),Padding=new Thickness(8)};border.SetResourceReference(Border.BackgroundProperty,"Card");border.SetResourceReference(Border.BorderBrushProperty,"Border");
        var popup=new Popup{Child=border,PlacementTarget=_date,Placement=PlacementMode.Bottom,StaysOpen=false};
        if(_draft.Level==1){var years=new ComboBox{ItemsSource=Enumerable.Range(1900,201),SelectedItem=_draft.Date.Year};var months=new ComboBox{ItemsSource=Enumerable.Range(1,12).Select(i=>i.ToString("00")+"月").ToArray(),SelectedIndex=_draft.Date.Month-1};body.Children.Add(years);body.Children.Add(months);body.Children.Add(Btn("应用",()=>{if(years.SelectedItem is int year && months.SelectedIndex>=0){_draft.Date=new DateTime(year,months.SelectedIndex+1,1);UpdateDate();popup.IsOpen=false;}}));}
        else{var calendar=new System.Windows.Controls.Calendar{SelectedDate=_draft.Level==0 ? _draft.Deadline:_draft.Date,DisplayDate=_draft.Deadline??_draft.Date};body.Children.Add(calendar);calendar.SelectedDatesChanged+=(_,_)=>{if(calendar.SelectedDate is DateTime day){if(_draft.Level==0)_draft.Deadline=day;else _draft.Date=day;UpdateDate();popup.IsOpen=false;}};if(_draft.Level==0)body.Children.Add(Btn("不设截止日期",()=>{_draft.Deadline=null;UpdateDate();popup.IsOpen=false;}));}
        popup.IsOpen=true;
    }
    private void Save()
    {
        if(string.IsNullOrWhiteSpace(_title.Text)){_error.Text="请输入计划内容。";_title.Focus();return;}
        string time=_time.Text.Trim().Replace('—','–').Replace('-','–');var parts=time.Split('–');
        if(time!="" && (parts.Length>2 || parts.Any(s=>!TimeOnly.TryParseExact(s.Trim(),"HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out _)) || parts.Length==2 && string.CompareOrdinal(parts[0].Trim(),parts[1].Trim())>=0)){_error.Text="时间请使用 HH:mm，结束时间须晚于开始时间。";return;}
        _draft.Title=_title.Text.Trim();_draft.Time=time;_draft.Notes=_notes.Text;_draft.ParentId=_parent.SelectedValue as string;if(_draft.Level!=0)_draft.Deadline=_due.SelectedDate;
        if(!_commit(_draft))_error.Text="保存失败，填写内容已保留，请检查底部提示后重试。";
    }
}
