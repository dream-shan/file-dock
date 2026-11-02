using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Automation;

namespace FileDockDemo;

public sealed class PlanItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Notes { get; set; } = "";
    public int Level { get; set; } = 3;
    public DateTime Date { get; set; } = DateTime.Today;
    public DateTime? Deadline { get; set; }
    public string Time { get; set; } = "";
    public string? ParentId { get; set; }
    public bool Done { get; set; }
    public override string ToString() => Title;
}

internal static class PlanRules
{
    internal static DateTime Week(DateTime d) => d.Date.AddDays(-((int)d.DayOfWeek+6)%7);
    internal static bool InPeriod(PlanItem item,int level,DateTime date) => item.Level==level && (level switch {
        0 => true, 1 => item.Date.Year==date.Year && item.Date.Month==date.Month,
        2 => Week(item.Date)==Week(date), _ => item.Date.Date==date.Date });
    internal static bool Complete(PlanItem item,List<PlanItem> all)
    {
        var children=all.Where(p=>p.ParentId==item.Id && p.Level==item.Level+1).ToList();
        return children.Count==0 ? item.Done : children.All(p=>Complete(p,all));
    }
}

public sealed class PlannerView : UserControl
{
    private static readonly string[] Levels={"长期目标","月计划","周计划","日计划"};
    private AppState _state=null!;
    private Action _save=null!;
    private DateTime _date=DateTime.Today;
    private bool _completed;
    private bool _editing;
    private string? _detailId, _newParent;
    private string _quickDraft="";
    private TextBox? _quickInput;
    private static readonly string[] Tabs={"长期","本月","本周","今天"};
    private static readonly ControlTemplate CircleTemplate=(ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="CheckBox">
          <Border x:Name="Circle" Width="19" Height="19" CornerRadius="10" BorderThickness="1.5" BorderBrush="{DynamicResource Muted}" Background="Transparent">
            <TextBlock x:Name="Tick" Text="✓" FontSize="12" FontWeight="Bold" HorizontalAlignment="Center" VerticalAlignment="Center" Foreground="{DynamicResource Surface}" Visibility="Collapsed" />
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsChecked" Value="True"><Setter TargetName="Circle" Property="Background" Value="{DynamicResource Accent}"/><Setter TargetName="Circle" Property="BorderBrush" Value="{DynamicResource Accent}"/><Setter TargetName="Tick" Property="Visibility" Value="Visible"/></Trigger>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Circle" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Circle" Property="Opacity" Value="0.55"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Circle" Property="BorderThickness" Value="2.5"/><Setter TargetName="Circle" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);
    private readonly DockPanel _root=new();
    private int Level => Math.Clamp(_state.PlannerLevel,0,3);
    internal void Initialize(AppState state,Action save)
    {
        _state=state;_save=save;Content=_root;
        IsVisibleChanged+=(_,_)=>{if(IsVisible && !_editing)Render();};
        Render();
    }
    private static TextBlock Label(string text,bool muted=false)
    {
        var t=new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,2,0,2)};
        t.SetResourceReference(TextBlock.ForegroundProperty,muted ? "Muted":"Ink");return t;
    }
    private Button Button(string text,Action action)
    {
        var b=new Button{Content=text,Padding=new Thickness(5,4,5,4),Margin=new Thickness(1)};
        b.SetResourceReference(StyleProperty,"QuietButton");b.Click+=(_,_)=>action();return b;
    }
    private void Header(UIElement element){DockPanel.SetDock(element,Dock.Top);if(_root.Children.Count>0 && _root.Children[_root.Children.Count-1] is ScrollViewer)_root.Children.Insert(_root.Children.Count-1,element);else _root.Children.Add(element);}
    internal void Render()
    {
        if(_state==null)return;
        _editing=false;
        _root.Children.Clear();
        var heading=new DockPanel{Margin=new Thickness(0,0,0,7)};
        var nav=new StackPanel{Orientation=Orientation.Horizontal};
        if(Level!=0){nav.Children.Add(Button("〈",()=>Shift(-1)));nav.Children.Add(Button("〉",()=>Shift(1)));}
        DockPanel.SetDock(nav,Dock.Right);heading.Children.Add(nav);
        string period=Level switch{0=>"持续积累，逐步完成",1=>_date.ToString("yyyy年M月"),2=>$"{PlanRules.Week(_date):M/d} — {PlanRules.Week(_date).AddDays(6):M/d}",_=>_date.ToString("M月d日 ddd")};
        var dateLabel=Label(period,true);dateLabel.FontSize=10;dateLabel.VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(dateLabel,Dock.Right);heading.Children.Add(dateLabel);
        var title=Label(Level switch{0=>"长期目标",1=>"本月待办",2=>"本周待办",_=>_date.Date==DateTime.Today ? "今日待办":"每日待办"});title.FontWeight=FontWeights.SemiBold;title.FontSize=14;heading.Children.Add(title);Header(heading);
        var tabs=new UniformGridProxy();
        foreach(int i in new[]{3,2,1,0}){int n=i;var b=Button(Tabs[i],()=>{_state.PlannerLevel=n;_date=DateTime.Today;_completed=false;_detailId=null;_quickDraft="";_save();Render();});
            var underline=new Border{BorderThickness=new Thickness(0,0,0,i==Level ? 2:0),Child=b,Margin=new Thickness(0,0,0,8)};
            if(i==Level){b.SetResourceReference(ForegroundProperty,"Accent");underline.SetResourceReference(Border.BorderBrushProperty,"Accent");}tabs.Children.Add(underline);}Header(tabs);
        if(Level!=0 && !PlanRules.InPeriod(new PlanItem{Level=Level,Date=DateTime.Today},Level,_date))Header(Button("返回"+Tabs[Level],()=>{_date=DateTime.Today;_detailId=null;Render();}));
        var quick=new DockPanel{Margin=new Thickness(0,8,0,2)};
        var add=Button("＋",QuickAdd);AutomationProperties.SetAutomationId(add,"PlannerQuickAdd");add.ToolTip="添加待办（Enter）";DockPanel.SetDock(add,Dock.Left);quick.Children.Add(add);
        add.IsEnabled=_detailId==null;
        _quickInput=new TextBox{Text=_quickDraft,MaxLength=200,Padding=new Thickness(5),BorderThickness=new Thickness(0),VerticalContentAlignment=VerticalAlignment.Center,ToolTip="输入任务名称，按回车添加"};
        _quickInput.SetResourceReference(BackgroundProperty,"Surface");_quickInput.SetResourceReference(ForegroundProperty,"Ink");AutomationProperties.SetAutomationId(_quickInput,"PlannerQuickInput");
        _quickInput.IsEnabled=_detailId==null;
        _quickInput.TextChanged+=(_,_)=>_quickDraft=_quickInput.Text;
        _quickInput.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){QuickAdd();e.Handled=true;}};
        var inputGrid=new Grid();inputGrid.Children.Add(_quickInput);
        var placeholder=Label("添加待办…",true);placeholder.Margin=new Thickness(5,0,0,0);placeholder.VerticalAlignment=VerticalAlignment.Center;placeholder.IsHitTestVisible=false;placeholder.Visibility=string.IsNullOrEmpty(_quickDraft) ? Visibility.Visible:Visibility.Collapsed;inputGrid.Children.Add(placeholder);
        _quickInput.TextChanged+=(_,_)=>placeholder.Visibility=string.IsNullOrEmpty(_quickInput.Text) ? Visibility.Visible:Visibility.Collapsed;
        quick.Children.Add(inputGrid);DockPanel.SetDock(quick,Dock.Bottom);_root.Children.Add(quick);
        var list=new StackPanel();
        if(_detailId=="")list.Children.Add(Editor(null,_newParent));
        var items=_state.Plans.Where(p=>PlanRules.InPeriod(p,Level,_date)).OrderBy(p=>p.Date).ThenBy(p=>p.Time=="" ? "99:99":p.Time).ToList();
        foreach(var p in items.Where(p=>!PlanRules.Complete(p,_state.Plans)))Row(list,p);
        if(items.Count==0 && _detailId!="")list.Children.Add(Label(Level==3 && _date.Date==DateTime.Today ? "今天暂无待办":"暂无待办",true));
        var done=items.Where(p=>PlanRules.Complete(p,_state.Plans)).ToList();
        var completed=Button($"已完成 {done.Count} 项 · {(_completed ? "收起":"展开")}",()=>{_completed=!_completed;_detailId=null;Render();});completed.HorizontalAlignment=HorizontalAlignment.Left;
        if(done.Count>0)list.Children.Add(completed);
        if(_completed)foreach(var p in done)Row(list,p);
        _root.Children.Add(new ScrollViewer{Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
    }
    private void QuickAdd()
    {
        if(string.IsNullOrWhiteSpace(_quickDraft))return;
        _state.Plans.Add(new PlanItem{Title=_quickDraft.Trim(),Level=Level,Date=_date.Date});_quickDraft="";_save();Render();_quickInput?.Focus();
    }
    private void Shift(int n){if(Level==0)return;_detailId=null;_date=Level==1 ? _date.AddMonths(n):_date.AddDays(n*(Level==2 ? 7:1));Render();}
    private void Row(Panel list,PlanItem p)
    {
        var row=new DockPanel{Margin=new Thickness(0,4,0,4)};
        var children=_state.Plans.Where(c=>c.ParentId==p.Id && c.Level==p.Level+1).ToList();
        bool done=PlanRules.Complete(p,_state.Plans);
        var check=new CheckBox{IsChecked=done,Template=CircleTemplate,IsEnabled=children.Count==0,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(3,9,10,0),ToolTip=children.Count>0 ? "进度由关联子项自动统计":"标记完成"};
        AutomationProperties.SetName(check,"完成："+p.Title);
        check.Click+=(_,_)=>{p.Done=check.IsChecked==true;_save();Render();};DockPanel.SetDock(check,Dock.Left);row.Children.Add(check);
        var content=new StackPanel();var title=Label(p.Title);title.FontSize=12;if(done){title.TextDecorations=TextDecorations.Strikethrough;title.SetResourceReference(TextBlock.ForegroundProperty,"Muted");}content.Children.Add(title);
        var parent=_state.Plans.FirstOrDefault(c=>c.Id==p.ParentId);
        var meta=new List<string>();if(p.Time.Length>0)meta.Add(p.Time);if(parent!=null)meta.Add("归属："+parent.Title);
        if(children.Count>0)meta.Add($"{children.Count(c=>PlanRules.Complete(c,_state.Plans))} / {children.Count} 已完成");
        bool overdue=!done && ((p.Deadline?.Date??(p.Level==3 ? p.Date.Date:DateTime.MaxValue))<DateTime.Today);
        if(p.Deadline is DateTime due)meta.Add(due.Date==DateTime.Today ? "截止今天":$"截止 {due:M/d}");if(overdue)meta.Add("已逾期");
        if(meta.Count>0){var subtitle=Label(string.Join(" · ",meta),true);subtitle.FontSize=10;if(overdue)subtitle.SetResourceReference(TextBlock.ForegroundProperty,"Error");content.Children.Add(subtitle);}
        var edit=Button("",()=>{if(_detailId==p.Id){_detailId=null;Render();}else Edit(p);});edit.Content=content;AutomationProperties.SetName(edit,"详情："+p.Title);edit.HorizontalContentAlignment=HorizontalAlignment.Stretch;row.Children.Add(edit);
        var separator=new Border{Child=row,BorderThickness=new Thickness(0,0,0,1),Padding=new Thickness(0,2,0,4)};separator.SetResourceReference(Border.BorderBrushProperty,"Border");list.Children.Add(separator);
        if(_detailId==p.Id)list.Children.Add(Editor(p));
    }
    private void Edit(PlanItem? item,string? presetParent=null)
    {
        _detailId=item?.Id??"";_newParent=presetParent;Render();
    }
    private UIElement Editor(PlanItem? item,string? presetParent=null)
    {
        _editing=true;
        var form=new StackPanel{Margin=new Thickness(30,4,5,12)};
        int level=item?.Level??Level;
        TextBox Input(string name,string value,Panel? destination=null){var target=destination??form;target.Children.Add(Label(name,true));var input=new TextBox{Text=value,Tag=name,Margin=new Thickness(0,2,0,5),Padding=new Thickness(5)};input.SetResourceReference(BackgroundProperty,"Card");input.SetResourceReference(ForegroundProperty,"Ink");target.Children.Add(input);return input;}
        var title=Input("名称（必填）",item?.Title??"");title.MaxLength=200;
        var time=Input("时间（可选，例如 09:00 或 09:00–10:30）",item?.Time??"");
        var details=new StackPanel();var more=new Expander{Header="日期、备注和归属",Content=details,Margin=new Thickness(0,5,0,5)};AutomationProperties.SetAutomationId(more,"PlannerMore");form.Children.Add(more);
        details.Children.Add(Label(level==0 ? "目标起始日期":"计划日期",true));var date=new DatePicker{SelectedDate=item?.Date??_date};details.Children.Add(date);
        details.Children.Add(Label("截止日期（可选）",true));var due=new DatePicker{SelectedDate=item?.Deadline};details.Children.Add(due);
        var notes=Input("备注",item?.Notes??"",details);notes.AcceptsReturn=true;notes.Height=50;notes.TextWrapping=TextWrapping.Wrap;
        var parents=new List<PlanItem>{new(){Id="",Title="独立计划"}};parents.AddRange(_state.Plans.Where(p=>p.Level==level-1));
        details.Children.Add(Label("归属上级（可选）",true));var parent=new ComboBox{ItemsSource=parents,DisplayMemberPath="Title",SelectedValuePath="Id",SelectedValue=presetParent??item?.ParentId??""};details.Children.Add(parent);
        var error=Label("");form.Children.Add(error);
        var actions=new WrapPanel();actions.Children.Add(Button("保存",()=>{
            if(string.IsNullOrWhiteSpace(title.Text)||date.SelectedDate==null){error.Text="请填写名称和有效日期。";return;}
            string value=time.Text.Trim().Replace('—','–').Replace('-','–');var parts=value.Split('–');
            if(value!="" && (parts.Length>2 || parts.Any(s=>!TimeOnly.TryParseExact(s.Trim(),"HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out _)) || (parts.Length==2 && string.CompareOrdinal(parts[0].Trim(),parts[1].Trim())>=0))){error.Text="时间请使用 HH:mm，结束时间须晚于开始时间。";return;}
            var p=item??new PlanItem{Level=level};p.Title=title.Text.Trim();p.Date=date.SelectedDate.Value.Date;p.Deadline=due.SelectedDate?.Date;p.Time=value;p.Notes=notes.Text;p.ParentId=parent.SelectedValue as string;
            if(item==null)_state.Plans.Add(p);_date=p.Date;_detailId=null;_save();Render();
        }));actions.Children.Add(Button("取消",()=>{_detailId=null;Render();}));form.Children.Add(actions);
        if(item!=null){
            if(level<3){form.Children.Add(Button("＋ 添加"+Levels[level+1],()=>{_state.PlannerLevel=level+1;_date=item.Date;Edit(null,item.Id);}));
                foreach(var child in _state.Plans.Where(p=>p.ParentId==item.Id && p.Level==level+1))form.Children.Add(Button("↳ "+child.Title,()=>{_state.PlannerLevel=child.Level;Edit(child);}));}
            form.Children.Add(Button("删除",()=>{var children=_state.Plans.Where(p=>p.ParentId==item.Id).ToList();_state.Plans.Remove(item);foreach(var c in children)c.ParentId=null;_detailId=null;_save();Render();
                Header(Button("已删除 · 撤销",()=>{_state.Plans.Add(item);foreach(var c in children)c.ParentId=item.Id;_save();Render();}));}));
        }
        return form;
    }
    private sealed class UniformGridProxy : System.Windows.Controls.Primitives.UniformGrid { public UniformGridProxy(){Columns=4;} }
}
