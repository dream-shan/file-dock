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

public sealed class PlannerView : UserControl
{
    private static readonly string[] Levels={"长期目标","月计划","周计划","日计划"};
    private AppState _state=null!;
    private Func<bool> _save=null!;
    private DateTime _date=PlanRules.Today;
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
    private string? _revealId;
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
        var title=Label(Level switch{0=>"长期目标",1=>"本月待办",2=>"本周待办",_=>_date.Date==PlanRules.Today ? "今日待办":"每日待办"});title.FontWeight=FontWeights.SemiBold;title.FontSize=14;heading.Children.Add(title);Header(heading);
        var tabs=new UniformGridProxy();
        foreach(int i in new[]{3,2,1,0}){int n=i;var b=Button(Tabs[i],()=>{_state.PlannerLevel=n;_date=PlanRules.Today;_completed=false;_detailId=null;_save();Render();});
            var underline=new Border{BorderThickness=new Thickness(0,0,0,i==Level ? 2:0),Child=b,Margin=new Thickness(0,0,0,8)};
            if(i==Level){b.SetResourceReference(ForegroundProperty,"Accent");underline.SetResourceReference(Border.BorderBrushProperty,"Accent");}tabs.Children.Add(underline);}Header(tabs);
        if(Level!=0 && !PlanRules.InPeriod(new PlanItem{Level=Level,Date=PlanRules.Today},Level,_date))Header(Button("返回"+Tabs[Level],()=>{_date=PlanRules.Today;_detailId=null;Render();}));
        var add=Button("＋ 添加待办",()=>Edit(null));AutomationProperties.SetAutomationId(add,"PlannerQuickAdd");add.HorizontalContentAlignment=HorizontalAlignment.Left;add.Margin=new Thickness(0,8,0,2);DockPanel.SetDock(add,Dock.Bottom);_root.Children.Add(add);
        var list=new StackPanel();

        var items=_state.Plans.Where(p=>PlanRules.InPeriod(p,Level,_date)).OrderBy(p=>p.Date).ToList();
        foreach(var p in items.Where(p=>!PlanRules.Complete(p,_state.Plans)))Row(list,p);
        if(items.Count==0 && _detailId!="")list.Children.Add(Label(Level==3 && _date.Date==PlanRules.Today ? "今天暂无待办":"暂无待办",true));
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
        bool done=PlanRules.Complete(p,_state.Plans);
        var check=new CheckBox{IsChecked=done,Template=CircleTemplate,IsEnabled=true,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(3,9,10,0),ToolTip="标记完成"};
        AutomationProperties.SetName(check,"完成："+p.Title);
        check.Click+=(_,_)=>ToggleDone(p,check.IsChecked==true);DockPanel.SetDock(check,Dock.Left);row.Children.Add(check);
        var content=new StackPanel();var title=Label(p.Title);title.FontSize=12;if(done){title.TextDecorations=TextDecorations.Strikethrough;title.SetResourceReference(TextBlock.ForegroundProperty,"Muted");}content.Children.Add(title);
        var meta=new List<string>();
        bool overdue=PlanRules.Overdue(p,PlanRules.Today);
        if(PlanRules.EffectiveDue(p) is DateTime due)meta.Add(due.Date==PlanRules.Today ? "截止今天":$"截止 {due:M/d}");if(overdue)meta.Add("已逾期");
        if(meta.Count>0){var subtitle=Label(string.Join(" · ",meta),true);subtitle.FontSize=10;if(overdue)subtitle.SetResourceReference(TextBlock.ForegroundProperty,"Error");content.Children.Add(subtitle);}
        var edit=Button("",()=>{if(_detailId==p.Id){_detailId=null;Render();}else Edit(p);});edit.Content=content;AutomationProperties.SetName(edit,"详情："+p.Title);edit.HorizontalContentAlignment=HorizontalAlignment.Stretch;row.Children.Add(edit);
        if(_revealId==p.Id){_revealId=null;edit.SetResourceReference(BackgroundProperty,"Hover");Dispatcher.BeginInvoke(()=>{edit.BringIntoView();var animation=new System.Windows.Media.Animation.DoubleAnimation(.55,1,TimeSpan.FromMilliseconds(800));animation.Completed+=(_,_)=>edit.ClearValue(BackgroundProperty);edit.BeginAnimation(OpacityProperty,animation);});}
        var separator=new Border{Child=row,BorderThickness=new Thickness(0,0,0,1),Padding=new Thickness(0,2,0,4)};separator.SetResourceReference(Border.BorderBrushProperty,"Border");list.Children.Add(separator);

    }
    private void Edit(PlanItem? item)
    {
        _editing=true;
        var owner=Window.GetWindow(this) as MainWindow;
        var draft=item?.Copy()??new PlanItem{Level=Level,Date=PlanRules.Today,ScopePeriod=PlanRules.Period(Level,PlanRules.Today)};
        bool Commit(PlanItem plan){int index=item==null ? -1:_state.Plans.FindIndex(v=>v.Id==item.Id);var previous=index<0 ? null:_state.Plans[index];int oldLevel=_state.PlannerLevel;
            if(index<0)_state.Plans.Add(plan);else _state.Plans[index]=plan;_state.PlannerLevel=plan.Level;
            if(!_save()){if(index<0)_state.Plans.Remove(plan);else _state.Plans[index]=previous!;_state.PlannerLevel=oldLevel;return false;}
            _date=PlanRules.Start(plan);_detailId=null;_completed=plan.Done;owner?.ClosePlanDialog();_revealId=plan.Id;Render();owner?.SetStatus((item==null ? "已添加到「":"已保存到「")+PlanRules.Names[plan.Level]+"」");return true;}
        Action? delete=item==null ? null:()=>DeletePlan(item,owner);
        var card=new PlanCard(draft,item!=null,Commit,()=>{owner?.ClosePlanDialog();_detailId=null;Render();},delete);
        owner?.OpenPlanDialog(card);
    }
    internal void FocusAdd(){foreach(var child in Visuals(this))if(child is Button b && AutomationProperties.GetAutomationId(b)=="PlannerQuickAdd"){b.Focus();break;}}
    private static IEnumerable<DependencyObject> Visuals(DependencyObject p){yield return p;for(int i=0;i<VisualTreeHelper.GetChildrenCount(p);i++)foreach(var c in Visuals(VisualTreeHelper.GetChild(p,i)))yield return c;}
    private void ToggleDone(PlanItem p,bool done)
    {
        var before=_state.Plans.Select(v=>v.Copy()).ToList();PlanRules.SetDone(p,done);
        if(!_save()){_state.Plans.Clear();_state.Plans.AddRange(before);}Render();
    }
    private void DeletePlan(PlanItem p,MainWindow? owner)
    {
        var before=_state.Plans.Select(v=>v.Copy()).ToList();var children=_state.Plans.Where(v=>v.ParentId==p.Id).ToList();
        _state.Plans.RemoveAll(v=>v.Id==p.Id);foreach(var child in children)child.ParentId=null;
        if(!_save()){_state.Plans.Clear();_state.Plans.AddRange(before);return;}owner?.ClosePlanDialog();Render();
        Header(Button("已删除 · 撤销",()=>{var current=_state.Plans.Select(v=>v.Copy()).ToList();foreach(var old in before){var now=_state.Plans.FirstOrDefault(v=>v.Id==old.Id);if(now==null)_state.Plans.Add(old);else if(children.Any(c=>c.Id==now.Id))now.ParentId=old.ParentId;}if(!_save()){_state.Plans.Clear();_state.Plans.AddRange(current);}Render();}));
    }
    private sealed class UniformGridProxy : System.Windows.Controls.Primitives.UniformGrid { public UniformGridProxy(){Columns=4;} }
}
