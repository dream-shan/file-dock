using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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
        var heading=new DockPanel();var add=Button("＋ 新建",()=>Edit(null));DockPanel.SetDock(add,Dock.Right);heading.Children.Add(add);
        var title=Label("我的计划");title.FontWeight=FontWeights.SemiBold;heading.Children.Add(title);Header(heading);
        var tabs=new UniformGridProxy();
        for(int i=0;i<4;i++){int n=i;var b=Button(Levels[i],()=>{_state.PlannerLevel=n;_date=DateTime.Today;_completed=false;_save();Render();});if(i==Level)b.SetResourceReference(ForegroundProperty,"Accent");tabs.Children.Add(b);}Header(tabs);
        var nav=new DockPanel();var next=Button("〉",()=>Shift(1));DockPanel.SetDock(next,Dock.Right);nav.Children.Add(next);
        var today=Button("今天",()=>{_date=DateTime.Today;Render();});DockPanel.SetDock(today,Dock.Right);nav.Children.Add(today);
        var prev=Button("〈",()=>Shift(-1));DockPanel.SetDock(prev,Dock.Left);nav.Children.Add(prev);
        string period=Level switch{0=>"全部长期目标",1=>_date.ToString("yyyy年M月"),2=>$"{PlanRules.Week(_date):M/d} — {PlanRules.Week(_date).AddDays(6):M/d}",_=>_date.ToString("M月d日 ddd")};
        nav.Children.Add(Label(period));Header(nav);
        var list=new StackPanel();
        var items=_state.Plans.Where(p=>PlanRules.InPeriod(p,Level,_date)).OrderBy(p=>p.Date).ThenBy(p=>p.Time=="" ? "99:99":p.Time).ToList();
        foreach(var p in items.Where(p=>!PlanRules.Complete(p,_state.Plans)))Row(list,p);
        if(!items.Any(p=>!PlanRules.Complete(p,_state.Plans)))list.Children.Add(Label("暂无待办，点击“＋ 新建”添加计划。",true));
        var done=items.Where(p=>PlanRules.Complete(p,_state.Plans)).ToList();
        list.Children.Add(Button($"已完成 {done.Count} 项 · {(_completed ? "收起":"展开")}",()=>{_completed=!_completed;Render();}));
        if(_completed)foreach(var p in done)Row(list,p);
        _root.Children.Add(new ScrollViewer{Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
    }
    private void Shift(int n){if(Level==0)return;_date=Level==1 ? _date.AddMonths(n):_date.AddDays(n*(Level==2 ? 7:1));Render();}
    private void Row(Panel list,PlanItem p)
    {
        var row=new DockPanel{Margin=new Thickness(0,4,0,4)};
        var children=_state.Plans.Where(c=>c.ParentId==p.Id && c.Level==p.Level+1).ToList();
        var check=new CheckBox{IsChecked=PlanRules.Complete(p,_state.Plans),IsEnabled=children.Count==0,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(3,0,7,0),ToolTip=children.Count>0 ? "进度由关联子项自动统计":"标记完成"};
        check.Click+=(_,_)=>{p.Done=check.IsChecked==true;_save();Render();};DockPanel.SetDock(check,Dock.Left);row.Children.Add(check);
        var content=new StackPanel();content.Children.Add(Label((p.Time.Length>0 ? p.Time+"  ":"")+p.Title));
        var parent=_state.Plans.FirstOrDefault(c=>c.Id==p.ParentId);
        if(parent!=null)content.Children.Add(Label("归属："+parent.Title,true));
        if(children.Count>0)content.Children.Add(Label($"已完成 {children.Count(c=>PlanRules.Complete(c,_state.Plans))} / {children.Count} 项",true));
        if(p.Deadline is DateTime due)content.Children.Add(Label($"截止 {due:M/d}"+(due.Date<DateTime.Today && !PlanRules.Complete(p,_state.Plans) ? " · 已逾期":""),true));
        else if(Level==3 && p.Date.Date<DateTime.Today && !p.Done)content.Children.Add(Label("已逾期",true));
        var edit=Button("",()=>Edit(p));edit.Content=content;edit.HorizontalContentAlignment=HorizontalAlignment.Stretch;row.Children.Add(edit);list.Children.Add(row);
    }
    private void Edit(PlanItem? item,string? presetParent=null)
    {
        _editing=true;
        _root.Children.Clear();
        var form=new StackPanel();var scroll=new ScrollViewer{Content=form,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        Header(Label(item==null ? "新建"+Levels[Level]:"编辑"+Levels[item.Level]));
        int level=item?.Level??Level;
        TextBox Input(string name,string value){form.Children.Add(Label(name,true));var input=new TextBox{Text=value,Tag=name,Margin=new Thickness(0,2,0,5),Padding=new Thickness(5)};input.SetResourceReference(BackgroundProperty,"Card");input.SetResourceReference(ForegroundProperty,"Ink");form.Children.Add(input);return input;}
        var title=Input("名称（必填）",item?.Title??"");title.MaxLength=200;
        form.Children.Add(Label(level==0 ? "目标起始日期":"计划日期",true));var date=new DatePicker{SelectedDate=item?.Date??_date};form.Children.Add(date);
        form.Children.Add(Label("截止日期（可选）",true));var due=new DatePicker{SelectedDate=item?.Deadline};form.Children.Add(due);
        var time=Input("时间（可选，例如 09:00 或 09:00–10:30）",item?.Time??"");
        var notes=Input("备注",item?.Notes??"");notes.AcceptsReturn=true;notes.Height=50;notes.TextWrapping=TextWrapping.Wrap;
        var parents=new List<PlanItem>{new(){Id="",Title="独立计划"}};parents.AddRange(_state.Plans.Where(p=>p.Level==level-1));
        form.Children.Add(Label("归属上级（可选）",true));var parent=new ComboBox{ItemsSource=parents,DisplayMemberPath="Title",SelectedValuePath="Id",SelectedValue=presetParent??item?.ParentId??""};form.Children.Add(parent);
        var error=Label("");form.Children.Add(error);
        var actions=new WrapPanel();actions.Children.Add(Button("保存",()=>{
            if(string.IsNullOrWhiteSpace(title.Text)||date.SelectedDate==null){error.Text="请填写名称和有效日期。";return;}
            string value=time.Text.Trim().Replace('—','–').Replace('-','–');var parts=value.Split('–');
            if(value!="" && (parts.Length>2 || parts.Any(s=>!TimeOnly.TryParseExact(s.Trim(),"HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out _)) || (parts.Length==2 && string.CompareOrdinal(parts[0].Trim(),parts[1].Trim())>=0))){error.Text="时间请使用 HH:mm，结束时间须晚于开始时间。";return;}
            var p=item??new PlanItem{Level=level};p.Title=title.Text.Trim();p.Date=date.SelectedDate.Value.Date;p.Deadline=due.SelectedDate?.Date;p.Time=value;p.Notes=notes.Text;p.ParentId=parent.SelectedValue as string;
            if(item==null)_state.Plans.Add(p);_date=p.Date;_save();Render();
        }));actions.Children.Add(Button("取消",Render));Header(actions);
        if(item!=null){
            if(level<3){form.Children.Add(Button("＋ 添加"+Levels[level+1],()=>{_state.PlannerLevel=level+1;_date=item.Date;Edit(null,item.Id);}));
                foreach(var child in _state.Plans.Where(p=>p.ParentId==item.Id && p.Level==level+1))form.Children.Add(Button("↳ "+child.Title,()=>{_state.PlannerLevel=child.Level;Edit(child);}));}
            form.Children.Add(Button("删除",()=>{var children=_state.Plans.Where(p=>p.ParentId==item.Id).ToList();_state.Plans.Remove(item);foreach(var c in children)c.ParentId=null;_save();Render();
                Header(Button("已删除 · 撤销",()=>{_state.Plans.Add(item);foreach(var c in children)c.ParentId=item.Id;_save();Render();}));}));
        }
        _root.Children.Add(scroll);title.Focus();
    }
    private sealed class UniformGridProxy : System.Windows.Controls.Primitives.UniformGrid { public UniformGridProxy(){Columns=4;} }
}
