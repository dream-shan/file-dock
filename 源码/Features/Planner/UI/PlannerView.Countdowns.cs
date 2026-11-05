using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
namespace FileDockDemo.Features.Planner;
public sealed partial class PlannerView
{
    private IPlannerStore? _countdownStore;private CountdownCard? _countdownEditor;private bool _datesCollapsed,_allDates;
    private void RenderCountdowns()
    {
        if(_countdownStore==null)return;
        var panel=new StackPanel{Margin=new Thickness(0,0,0,12)};
        var heading=new DockPanel();var add=Button("＋ 添加",()=>EditCountdown(null));AutomationProperties.SetAutomationId(add,"CountdownAdd");DockPanel.SetDock(add,Dock.Right);heading.Children.Add(add);
        var toggle=Button((_datesCollapsed?"› ":"⌄ ")+"重要日",()=>{_datesCollapsed=!_datesCollapsed;Render();});toggle.FontWeight=FontWeights.SemiBold;toggle.FontSize=14;toggle.HorizontalContentAlignment=HorizontalAlignment.Left;heading.Children.Add(toggle);panel.Children.Add(heading);
        if(!_datesCollapsed)
        {
            var items=_countdownStore.ReadCountdowns().Where(p=>_allDates||p.Annual||p.Days(PlanRules.Today)>=0).OrderBy(p=>p.Days(PlanRules.Today)<0).ThenBy(p=>p.Next(PlanRules.Today)).ToArray();
            var rows=new StackPanel();
            foreach(var item in (_allDates?items:items.Take(2)))
            {
                int days=item.Days(PlanRules.Today);var row=new DockPanel();var value=Label(days==0?"就是今天":days<0?"已到期":$"{days} 天");value.FontSize=19;value.FontWeight=FontWeights.SemiBold;value.SetResourceReference(TextBlock.ForegroundProperty,"Accent");DockPanel.SetDock(value,Dock.Right);row.Children.Add(value);
                var text=new StackPanel();var name=Label(item.Title);name.TextWrapping=TextWrapping.NoWrap;name.TextTrimming=TextTrimming.CharacterEllipsis;text.Children.Add(name);var date=Label(item.Next(PlanRules.Today).ToString("yyyy/MM/dd")+(item.Annual?" · 每年重复":""),true);date.FontSize=11;text.Children.Add(date);row.Children.Add(text);
                var edit=Button("",()=>EditCountdown(item));edit.Content=row;edit.HorizontalContentAlignment=HorizontalAlignment.Stretch;edit.Margin=new Thickness(0,2,0,2);edit.Padding=new Thickness(10,8,10,8);edit.SetResourceReference(BackgroundProperty,"PaneCard");AutomationProperties.SetName(edit,"重要日："+item.Title);rows.Children.Add(edit);
            }
            if(items.Length==0)rows.Children.Add(Label("添加考试、生日等重要日期",true));
            panel.Children.Add(new ScrollViewer{Content=rows,MaxHeight=_allDates?150:112,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
            if(_countdownStore.ReadCountdowns().Count>2||_allDates||_countdownStore.ReadCountdowns().Any(p=>!p.Annual&&p.Days(PlanRules.Today)<0))panel.Children.Add(Button(_allDates?"收起":"查看全部 / 管理",()=>{_allDates=!_allDates;Render();}));
        }
        Header(panel);
    }
    private void EditCountdown(CountdownItem? item)
    {
        if(_host==null||_countdownStore==null||IsEditing)return;
        var card=new CountdownCard(item,_host,async value=>{
            var list=_countdownStore.ReadCountdowns().Where(p=>p.Id!=item?.Id).ToList();if(value!=null)list.Add(value);
            if(!await _countdownStore.WriteCountdownsAsync(list))return false;
            _host.CloseDialog(_countdownEditor!);_countdownEditor=null;Render();_host.ShowStatus(value==null?"已删除重要日":"已保存重要日");return true;
        },()=>FocusAdd());card.Closed+=()=>{if(ReferenceEquals(_countdownEditor,card))_countdownEditor=null;};_countdownEditor=card;_host.ShowDialog(card);
    }
}
