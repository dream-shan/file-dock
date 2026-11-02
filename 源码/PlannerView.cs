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
    private Func<bool> _save=null!;
    private DateTime _date=DateTime.Today;
    private bool _completed;
    private bool _editing;
    private string? _detailId;
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
    internal void Initialize(AppState state,Func<bool> save)
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
        foreach(int i in new[]{3,2,1,0}){int n=i;var b=Button(Tabs[i],()=>{_state.PlannerLevel=n;_date=DateTime.Today;_completed=false;_detailId=null;_save();Render();});
            var underline=new Border{BorderThickness=new Thickness(0,0,0,i==Level ? 2:0),Child=b,Margin=new Thickness(0,0,0,8)};
            if(i==Level){b.SetResourceReference(ForegroundProperty,"Accent");underline.SetResourceReference(Border.BorderBrushProperty,"Accent");}tabs.Children.Add(underline);}Header(tabs);
        if(Level!=0 && !PlanRules.InPeriod(new PlanItem{Level=Level,Date=DateTime.Today},Level,_date))Header(Button("返回"+Tabs[Level],()=>{_date=DateTime.Today;_detailId=null;Render();}));
        var add=Button("＋ 添加待办",()=>Edit(null));AutomationProperties.SetAutomationId(add,"PlannerQuickAdd");add.HorizontalContentAlignment=HorizontalAlignment.Left;add.Margin=new Thickness(0,8,0,2);DockPanel.SetDock(add,Dock.Bottom);_root.Children.Add(add);
        var list=new StackPanel();

        var items=_state.Plans.Where(p=>PlanRules.InPeriod(p,Level,_date)).OrderBy(p=>p.Date).ThenBy(p=>p.Time=="" ? "99:99":p.Time).ToList();
        foreach(var p in items.Where(p=>!PlanRules.Complete(p,_state.Plans)))Row(list,p);
        if(items.Count==0 && _detailId!="")list.Children.Add(Label(Level==3 && _date.Date==DateTime.Today ? "今天暂无待办":"暂无待办",true));
        var done=items.Where(p=>PlanRules.Complete(p,_state.Plans)).ToList();
        var completed=Button($"已完成 {done.Count} 项 · {(_completed ? "收起":"展开")}",()=>{_completed=!_completed;_detailId=null;Render();});completed.HorizontalAlignment=HorizontalAlignment.Left;
        if(done.Count>0)list.Children.Add(completed);
        if(_completed)foreach(var p in done)Row(list,p);
        _root.Children.Add(new ScrollViewer{Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
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

    }
    private void Edit(PlanItem? item,string? presetParent=null)
    {
        _editing=true;
        var draft=item==null ? new PlanItem{Level=Level,Date=_date,ParentId=presetParent}:new PlanItem{Id=item.Id,Level=item.Level,Date=item.Date,Deadline=item.Deadline,Title=item.Title,Time=item.Time,Notes=item.Notes,ParentId=item.ParentId,Done=item.Done};
        bool Commit(PlanItem plan){int index=item==null ? -1:_state.Plans.IndexOf(item);int oldLevel=_state.PlannerLevel;
            if(index<0)_state.Plans.Add(plan);else _state.Plans[index]=plan;_state.PlannerLevel=plan.Level;
            if(!_save()){if(index<0)_state.Plans.Remove(plan);else _state.Plans[index]=item!;_state.PlannerLevel=oldLevel;return false;}
            _date=plan.Date;_detailId=null;_completed=PlanRules.Complete(plan,_state.Plans);Render();return true;}
        Action? delete=item==null ? null:()=>{var children=_state.Plans.Where(p=>p.ParentId==item.Id).ToList();_state.Plans.Remove(item);foreach(var c in children)c.ParentId=null;
            if(!_save()){_state.Plans.Add(item);foreach(var c in children)c.ParentId=item.Id;return;}
            _detailId=null;Render();Header(Button("已删除 · 撤销",()=>{_state.Plans.Add(item);foreach(var c in children)c.ParentId=item.Id;_save();Render();}));};
        Action? child=item==null || item.Level==3 ? null:()=>{_state.PlannerLevel=item.Level+1;_date=item.Date;Edit(null,item.Id);};
        _root.Children.Clear();_root.Children.Add(new PlanCard(draft,_state.Plans,item!=null,Commit,()=>{_detailId=null;Render();},delete,child,p=>Edit(p)));
    }
    private sealed class UniformGridProxy : System.Windows.Controls.Primitives.UniformGrid { public UniformGridProxy(){Columns=4;} }
}
