using FileDockDemo.Contracts;
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

namespace FileDockDemo.Features.Planner;

public sealed partial class PlannerView : UserControl
{
    private PlannerController? _controller;
    private IModuleHost? _host;
    private PlanCard? _editor;
    private bool _manage;
    private ScrollViewer? _scroll;
    internal bool IsEditing=>_editor!=null||_countdownEditor!=null;
    internal DateTime RenderedDay { get; private set; }=PlanRules.Today;
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
    internal void Initialize(PlannerController controller,IModuleHost host,IPlannerStore? store=null)
    {
        _controller=controller;_host=host;_countdownStore=store;Content=_root;Render();
    }
    internal void Detach(){CloseEditor();_controller=null;_host=null;_root.Children.Clear();}
    private void CloseEditor(){var editor=_editor;_editor=null;if(editor!=null)_host?.CloseDialog(editor);}
    private static TextBlock Label(string text,bool muted=false)
    {
        var t=new TextBlock{Text=text,FontSize=13,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,2,0,2)};
        t.SetResourceReference(TextBlock.ForegroundProperty,muted ? "Muted":"Ink");return t;
    }
    private Button Button(string text,Action action)
    {
        var b=new Button{Content=text,Padding=new Thickness(8,6,8,6),Margin=new Thickness(1)};
        b.SetResourceReference(StyleProperty,"QuietButton");b.Click+=(_,_)=>action();return b;
    }
    private void Header(UIElement element,bool background=true){var plate=new Border{Child=element,CornerRadius=new CornerRadius(6)};if(background)plate.SetResourceReference(Border.BackgroundProperty,"ContentSurface");DockPanel.SetDock(plate,Dock.Top);if(_root.Children.Count>0 && _root.Children[_root.Children.Count-1] is ScrollViewer)_root.Children.Insert(_root.Children.Count-1,plate);else _root.Children.Add(plate);}
    internal void Render()
    {
        if(_controller==null)return;
        double offset=_scroll?.VerticalOffset??0;bool reveal=_revealId!=null;_root.Children.Clear();
        RenderCountdowns();
        var heading=new DockPanel{Margin=new Thickness(0,0,0,7)};
        RenderedDay=PlanRules.Today;if(!_controller.Reconcile(RenderedDay))Header(Label("清理保存失败，原有计划已保留。",true));
        var manage=Button(_manage ? "返回今天":"管理",()=>{_manage=!_manage;Render();});AutomationProperties.SetAutomationId(manage,"PlanManage");DockPanel.SetDock(manage,Dock.Right);heading.Children.Add(manage);
        var dateLabel=Label(PlanRules.Today.ToString("M月d日 ddd"),true);DockPanel.SetDock(dateLabel,Dock.Right);heading.Children.Add(dateLabel);var title=Label(_manage ? "管理计划":"今日待办");title.FontSize=14;title.FontWeight=FontWeights.SemiBold;heading.Children.Add(title);Header(heading);
        var add=Button("＋ 添加待办",()=>Edit(null));AutomationProperties.SetAutomationId(add,"PlannerQuickAdd");add.SetResourceReference(BackgroundProperty,"Selection");add.SetResourceReference(ForegroundProperty,"Accent");add.HorizontalAlignment=HorizontalAlignment.Stretch;add.HorizontalContentAlignment=HorizontalAlignment.Left;add.Margin=new Thickness(0,8,0,2);DockPanel.SetDock(add,Dock.Bottom);_root.Children.Add(add);
        var list=new StackPanel();

        var items=_controller.Items(_manage,PlanRules.Today);
        foreach(var p in items)Row(list,p);if(items.Count==0)list.Children.Add(Label(_manage ? "暂无计划":"今天暂无待办",true));
        _scroll=new ScrollViewer{Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};AutomationProperties.SetAutomationId(_scroll,"PlannerScroll");_root.Children.Add(_scroll);var scroll=_scroll;if(!reveal)Dispatcher.BeginInvoke(()=>{if(ReferenceEquals(_scroll,scroll))scroll.ScrollToVerticalOffset(offset);});
    }
    private void Row(Panel list,PlanItem p)
    {
        var row=new DockPanel{Margin=new Thickness(0,4,0,4)};
        bool done=PlanRules.DoneToday(p,PlanRules.Today);
        var check=new CheckBox{IsChecked=done,Template=CircleTemplate,IsEnabled=PlanRules.Appears(p,PlanRules.Today),VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(3,9,10,0),ToolTip="标记完成"};
        AutomationProperties.SetName(check,"完成："+p.Title);
        check.Click+=(_,_)=>ToggleDone(p,check.IsChecked==true);DockPanel.SetDock(check,Dock.Left);row.Children.Add(check);
        var content=new StackPanel();var title=Label(p.Title);title.FontSize=13;if(done){title.TextDecorations=TextDecorations.Strikethrough;title.SetResourceReference(TextBlock.ForegroundProperty,"Muted");}content.Children.Add(title);
        var meta=new List<string>();
        meta.Add(PlanRules.ScheduleLabel(p));if(p.Migrated)meta.Add("旧计划 · 请确认日期");if(_manage && !PlanRules.Appears(p,PlanRules.Today))meta.Add("今天不出现");var subtitle=Label(string.Join(" · ",meta),true);subtitle.FontSize=11;content.Children.Add(subtitle);if(done)row.Opacity=.6;
        var edit=Button("",()=>Edit(p));edit.Content=content;AutomationProperties.SetName(edit,"详情："+p.Title);edit.HorizontalContentAlignment=HorizontalAlignment.Stretch;row.Children.Add(edit);
        if(_revealId==p.Id){_revealId=null;edit.SetResourceReference(BackgroundProperty,"Hover");Dispatcher.BeginInvoke(()=>{edit.BringIntoView();if(!SystemParameters.ClientAreaAnimation){edit.ClearValue(BackgroundProperty);return;}var animation=new System.Windows.Media.Animation.DoubleAnimation(.55,1,TimeSpan.FromMilliseconds(180));animation.Completed+=(_,_)=>edit.ClearValue(BackgroundProperty);edit.BeginAnimation(OpacityProperty,animation);});}
        var separator=new Border{Child=row,CornerRadius=new CornerRadius(8),Margin=new Thickness(0,0,0,4),Padding=new Thickness(4,2,4,4)};separator.SetResourceReference(Border.BackgroundProperty,"ContentSurface");list.Children.Add(separator);

    }
    private void Edit(PlanItem? item)
    {
        if(_controller==null || _host==null || IsEditing)return;
        var draft=_controller.CreateDraft(item);
        bool Commit(PlanItem plan){if(!_controller.Save(plan))return false;CloseEditor();_revealId=plan.Id;Render();_host.ShowStatus(item==null?"已添加待办":"已保存计划");return true;}
        var card=new PlanCard(draft,item!=null,Commit,()=>{CloseEditor();Render();},_host,FocusAdd,item==null?null:()=>DeletePlan(item));
        card.Closed+=()=>{if(ReferenceEquals(_editor,card))_editor=null;};_editor=card;_host.ShowDialog(card);
    }
    internal void FocusAdd(){foreach(var child in Visuals(this))if(child is Button b && AutomationProperties.GetAutomationId(b)=="PlannerQuickAdd"){b.Focus();break;}}
    private static IEnumerable<DependencyObject> Visuals(DependencyObject p){yield return p;for(int i=0;i<VisualTreeHelper.GetChildrenCount(p);i++)foreach(var c in Visuals(VisualTreeHelper.GetChild(p,i)))yield return c;}
    private void ToggleDone(PlanItem p,bool done){_controller?.SetDone(p.Id,done);Render();}
    private void DeletePlan(PlanItem p)
    {
        if(_controller==null || !_controller.Delete(p.Id,out var undo))return;
        CloseEditor();Render();Header(Button("已删除 · 撤销",()=>{if(undo!=null)_controller.Undo(undo);Render();}));
    }
}
