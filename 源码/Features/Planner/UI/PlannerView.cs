using FileDockDemo.Contracts;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
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
    private Task _renderTask=Task.CompletedTask,_operationTask=Task.CompletedTask;
    private int _renderGeneration;
    internal async Task WaitForIdleAsync()
    {
        while(true){var render=_renderTask;var operation=_operationTask;var controller=_controller?.Pending??Task.CompletedTask;
            await Task.WhenAll(render,operation,controller,_editor?.Pending??Task.CompletedTask,_countdownEditor?.Pending??Task.CompletedTask);
            if(ReferenceEquals(render,_renderTask)&&ReferenceEquals(operation,_operationTask)&&ReferenceEquals(controller,_controller?.Pending??Task.CompletedTask))return;}
    }
    private ScrollViewer? _scroll;
    private ContextMenu? _addMenu;
    private Border? _listSurface;
    internal bool IsEditing=>_editor!=null||_countdownEditor!=null||_addMenu?.IsOpen==true;
    internal DateTime RenderedDay { get; private set; }=PlanRules.Today;
    private static readonly ControlTemplate CircleTemplate=(ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="CheckBox">
          <Border x:Name="Circle" Width="17" Height="17" CornerRadius="9" BorderThickness="1.3" BorderBrush="{DynamicResource Muted}" Background="Transparent">
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
        _controller=controller;_host=host;_countdownStore=store;Content=_root;Dispatcher.BeginInvoke(Render);
    }
    internal void Detach(){++_renderGeneration;if(_addMenu!=null)_addMenu.IsOpen=false;CloseEditor();_controller=null;_host=null;_root.Children.Clear();}
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
    private void Header(UIElement element)
    {
        var plate=new Border{Child=element};DockPanel.SetDock(plate,Dock.Top);
        int index=_listSurface==null ? -1:_root.Children.IndexOf(_listSurface);
        if(index>=0)_root.Children.Insert(index,plate);else _root.Children.Add(plate);
    }
    private void RenderPageHeading()
    {
        var heading=new Grid{Margin=new Thickness(0,0,0,12)};
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var text=new StackPanel();
        var title=Label("安排");title.FontSize=18;title.FontWeight=FontWeights.SemiBold;text.Children.Add(title);
        var date=Label(PlanRules.Today.ToString("M月d日 ddd",CultureInfo.GetCultureInfo("zh-CN")),true);date.FontSize=11;text.Children.Add(date);
        heading.Children.Add(text);
        var add=Button("＋ 添加",()=>{if(_addMenu!=null)_addMenu.IsOpen=true;});
        add.VerticalAlignment=VerticalAlignment.Top;add.SetResourceReference(ForegroundProperty,"Accent");
        AutomationProperties.SetAutomationId(add,"PlannerAdd");Grid.SetColumn(add,1);heading.Children.Add(add);
        _addMenu=new ContextMenu{PlacementTarget=add};
        var plan=new MenuItem{Header="添加待办"};plan.Click+=(_,_)=>{_addMenu.IsOpen=false;Edit(null);};
        var dateItem=new MenuItem{Header="添加重要日期"};dateItem.Click+=(_,_)=>{_addMenu.IsOpen=false;EditCountdown(null);};
        AutomationProperties.SetAutomationId(dateItem,"CountdownAdd");
        _addMenu.Items.Add(plan);_addMenu.Items.Add(dateItem);add.ContextMenu=_addMenu;
        Header(heading);
    }
    internal void Render()=>_renderTask=RenderAsync(++_renderGeneration);
    private async Task RenderAsync(int generation)
    {
        if(_controller==null)return;
        bool reconciled=await _controller.ReconcileAsync(PlanRules.Today);
        if(_controller==null||generation!=_renderGeneration)return;
        double offset=_scroll?.VerticalOffset??0;bool reveal=_revealId!=null;_root.Children.Clear();_listSurface=null;
        RenderPageHeading();
        RenderCountdowns();
        var heading=new DockPanel{Margin=new Thickness(0,0,0,7)};
        RenderedDay=PlanRules.Today;if(!reconciled)Header(Label("清理保存失败，原有计划已保留。",true));
        var manage=Button(_manage ? "返回今天":"全部待办",()=>{_manage=!_manage;Render();});manage.FontSize=11;AutomationProperties.SetAutomationId(manage,"PlanManage");DockPanel.SetDock(manage,Dock.Right);heading.Children.Add(manage);
        var title=Label(_manage ? "全部待办":"今日清单");title.FontSize=14;title.FontWeight=FontWeights.SemiBold;heading.Children.Add(title);Header(heading);
        var list=new StackPanel();

        var items=_controller.Items(_manage,PlanRules.Today);
        foreach(var p in items)Row(list,p);if(items.Count==0)list.Children.Add(Label(_manage ? "暂无计划":"今天暂无待办",true));
        _scroll=new ScrollViewer{Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};AutomationProperties.SetAutomationId(_scroll,"PlannerScroll");
        _listSurface=new Border{Child=_scroll,CornerRadius=new CornerRadius(10),VerticalAlignment=VerticalAlignment.Top};_listSurface.SetResourceReference(Border.BackgroundProperty,"ContentSurface");_root.Children.Add(_listSurface);
        var scroll=_scroll;if(!reveal)_ = Dispatcher.BeginInvoke(()=>{if(ReferenceEquals(_scroll,scroll))scroll.ScrollToVerticalOffset(offset);});
    }
    private void Row(Panel list,PlanItem p)
    {
        var row=new DockPanel{MinHeight=52};
        bool done=PlanRules.DoneToday(p,PlanRules.Today);
        var check=new CheckBox{IsChecked=done,Template=CircleTemplate,IsEnabled=PlanRules.Appears(p,PlanRules.Today),VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(12,12,3,0),ToolTip="标记完成"};
        AutomationProperties.SetName(check,"完成："+p.Title);
        check.Click+=(_,_)=>ToggleDone(p,check.IsChecked==true);DockPanel.SetDock(check,Dock.Left);row.Children.Add(check);
        var content=new StackPanel();var title=Label(p.Title);title.FontSize=14;if(done){title.TextDecorations=TextDecorations.Strikethrough;title.SetResourceReference(TextBlock.ForegroundProperty,"Muted");}content.Children.Add(title);
        var meta=new List<string>();
        meta.Add(PlanRules.ScheduleLabel(p));if(p.Migrated)meta.Add("旧计划 · 请确认日期");if(_manage && !PlanRules.Appears(p,PlanRules.Today))meta.Add("今天不出现");var subtitle=Label(string.Join(" · ",meta),true);subtitle.FontSize=11;content.Children.Add(subtitle);if(done)row.Opacity=.6;
        var edit=Button("",()=>Edit(p));edit.Content=content;AutomationProperties.SetName(edit,"详情："+p.Title);edit.HorizontalContentAlignment=HorizontalAlignment.Stretch;row.Children.Add(edit);
        if(_revealId==p.Id){_revealId=null;edit.SetResourceReference(BackgroundProperty,"Hover");Dispatcher.BeginInvoke(()=>{edit.BringIntoView();if(!SystemParameters.ClientAreaAnimation){edit.ClearValue(BackgroundProperty);return;}var animation=new System.Windows.Media.Animation.DoubleAnimation(.55,1,TimeSpan.FromMilliseconds(180));animation.Completed+=(_,_)=>edit.ClearValue(BackgroundProperty);edit.BeginAnimation(OpacityProperty,animation);});}
        var separator=new Border{Child=row,BorderThickness=new Thickness(0,0,0,1)};separator.SetResourceReference(Border.BorderBrushProperty,"PaneOutline");list.Children.Add(separator);

    }
    private void Edit(PlanItem? item)
    {
        if(_controller==null || _host==null || IsEditing)return;
        var draft=_controller.CreateDraft(item);
        async Task<bool> Commit(PlanItem plan){if(!await _controller.SaveAsync(plan))return false;CloseEditor();_revealId=plan.Id;Render();_host?.ShowStatus(item==null?"已添加待办":"已保存计划");return true;}
        var card=new PlanCard(draft,item!=null,Commit,()=>{CloseEditor();Render();},_host,FocusAdd,item==null?null:()=>{DeletePlan(item);return _operationTask;});
        card.Closed+=()=>{if(ReferenceEquals(_editor,card))_editor=null;};_editor=card;_host.ShowDialog(card);
    }
    internal void FocusAdd(){foreach(var child in Visuals(this))if(child is Button b && AutomationProperties.GetAutomationId(b)=="PlannerAdd"){b.Focus();break;}}
    private static IEnumerable<DependencyObject> Visuals(DependencyObject p){yield return p;for(int i=0;i<VisualTreeHelper.GetChildrenCount(p);i++)foreach(var c in Visuals(VisualTreeHelper.GetChild(p,i)))yield return c;}
    private void ToggleDone(PlanItem p,bool done)=>RunOperation(async()=>{if(_controller==null)return;bool saved=await _controller.SetDoneAsync(p.Id,done);Render();await _renderTask;if(!saved)Header(Button("保存失败 · 重试",()=>ToggleDone(p,done)));});
    private void RunOperation(Func<Task> operation){if(!_operationTask.IsCompleted)return;_operationTask=RunOperationAsync(operation);}
    private async Task RunOperationAsync(Func<Task> operation){IsEnabled=false;try{await operation();}finally{IsEnabled=true;}}
    private void DeletePlan(PlanItem p)=>RunOperation(async()=>
    {
        if(_controller==null)return;var result=await _controller.DeleteAsync(p.Id);
        if(!result.Success){_host?.ShowStatus("删除保存失败，原计划已保留，可重试。");return;}
        CloseEditor();Render();await _renderTask;Header(Button("已删除 · 撤销",()=>RunOperation(async()=>{
            bool saved=result.Undo!=null&&await _controller.UndoAsync(result.Undo);Render();await _renderTask;
            if(!saved)Header(Button("撤销保存失败 · 重试",()=>RestoreUndo(result.Undo!)));})));
    });
    private void RestoreUndo(PlannerUndo undo)=>RunOperation(async()=>{if(_controller==null)return;bool saved=await _controller.UndoAsync(undo);Render();await _renderTask;if(!saved)Header(Button("撤销保存失败 · 重试",()=>RestoreUndo(undo)));});
}
