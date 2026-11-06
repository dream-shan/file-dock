using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
namespace FileDockDemo.Features.Planner;
public sealed partial class PlannerView
{
    private IPlannerStore? _countdownStore;private CountdownCard? _countdownEditor;private bool _datesCollapsed,_allDates;
    private void RenderCountdowns()
    {
        if(_countdownStore==null)return;
        var panel=new StackPanel{Margin=new Thickness(0,0,0,12)};
        var heading=new DockPanel();
        var items=_countdownStore.ReadCountdowns().Where(p=>_allDates||p.Annual||p.Days(PlanRules.Today)>=0).OrderBy(p=>p.Days(PlanRules.Today)<0).ThenBy(p=>p.Next(PlanRules.Today)).ToArray();
        if(_countdownStore.ReadCountdowns().Count>2||_allDates||_countdownStore.ReadCountdowns().Any(p=>!p.Annual&&p.Days(PlanRules.Today)<0))
        {
            var all=Button(_allDates?"收起":"查看全部",()=>{_allDates=!_allDates;_datesCollapsed=false;Render();});
            all.FontSize=11;DockPanel.SetDock(all,Dock.Right);heading.Children.Add(all);
        }
        var toggle=Button("重要日期",()=>{_datesCollapsed=!_datesCollapsed;Render();});toggle.Padding=new Thickness(0,5,0,7);toggle.SetResourceReference(ForegroundProperty,"Ink");toggle.ToolTip=_datesCollapsed?"展开重要日期":"折叠重要日期";toggle.FontWeight=FontWeights.SemiBold;toggle.FontSize=14;toggle.HorizontalContentAlignment=HorizontalAlignment.Left;heading.Children.Add(toggle);panel.Children.Add(heading);
        if(!_datesCollapsed)
        {
            var rows=new StackPanel();
            foreach(var item in (_allDates?items:items.Take(2)))
            {
                int days=item.Days(PlanRules.Today);var target=item.Next(PlanRules.Today);
                var row=new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(42)});
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
                var calendar=new StackPanel{VerticalAlignment=VerticalAlignment.Center};
                var month=Label(target.Month+"月",true);month.FontSize=10;month.TextAlignment=TextAlignment.Center;month.Margin=new Thickness(0);
                var day=Label(target.Day.ToString());day.FontSize=21;day.FontWeight=FontWeights.SemiBold;day.TextAlignment=TextAlignment.Center;day.Margin=new Thickness(0);
                calendar.Children.Add(month);calendar.Children.Add(day);
                var calendarPlate=new Border{Child=calendar,BorderThickness=new Thickness(0,0,1,0),Margin=new Thickness(0,0,10,0)};
                calendarPlate.SetResourceReference(Border.BorderBrushProperty,"PaneOutline");row.Children.Add(calendarPlate);
                var text=new StackPanel{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(2,0,8,0)};Grid.SetColumn(text,1);
                var name=Label(item.Title);name.FontSize=14;name.FontWeight=FontWeights.Medium;name.TextWrapping=TextWrapping.NoWrap;name.TextTrimming=TextTrimming.CharacterEllipsis;text.Children.Add(name);
                var date=Label(target.ToString("dddd",System.Globalization.CultureInfo.GetCultureInfo("zh-CN"))+(item.Annual?" · 每年重复":""),true);date.FontSize=11;text.Children.Add(date);row.Children.Add(text);
                var remaining=new StackPanel{VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Right};Grid.SetColumn(remaining,2);
                if(days>0)
                {
                    var hint=Label("还有",true);hint.FontSize=10;hint.TextAlignment=TextAlignment.Right;hint.Margin=new Thickness(0);remaining.Children.Add(hint);
                    var value=Label(days.ToString());value.FontFamily=new FontFamily("Segoe UI");value.FontSize=24;value.FontWeight=FontWeights.SemiBold;value.Margin=new Thickness(0);value.SetResourceReference(TextBlock.ForegroundProperty,"Accent");
                    value.Inlines.Add(new System.Windows.Documents.Run(" 天"){FontSize=11,FontWeight=FontWeights.Normal});remaining.Children.Add(value);
                }
                else {var value=Label(days==0?"就是今天":"已到期");value.SetResourceReference(TextBlock.ForegroundProperty,"Accent");remaining.Children.Add(value);}
                row.Children.Add(remaining);
                var edit=Button("",()=>EditCountdown(item));edit.Content=row;edit.MinHeight=60;edit.HorizontalContentAlignment=HorizontalAlignment.Stretch;edit.Margin=new Thickness(0,0,0,4);edit.Padding=new Thickness(10,6,10,6);edit.SetResourceReference(BackgroundProperty,"PaneCard");edit.ToolTip=target.ToString("yyyy年M月d日");AutomationProperties.SetName(edit,"重要日期："+item.Title+"，"+target.ToString("yyyy年M月d日")+"，"+(days>0?$"还有{days}天":days==0?"就是今天":"已到期"));rows.Children.Add(edit);
            }
            if(items.Length==0)rows.Children.Add(Label("添加考试、生日等重要日期",true));
            panel.Children.Add(new ScrollViewer{Content=rows,MaxHeight=_allDates?150:128,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
        }
        Header(panel);
    }
    private void EditCountdown(CountdownItem? item)
    {
        if(_host==null||_countdownStore==null||IsEditing)return;
        var card=new CountdownCard(item,_host,async value=>{
            var list=_countdownStore.ReadCountdowns().Where(p=>p.Id!=item?.Id).ToList();if(value!=null)list.Add(value);
            if(!await _countdownStore.WriteCountdownsAsync(list))return false;
            _host.CloseDialog(_countdownEditor!);_countdownEditor=null;Render();_host.ShowStatus(value==null?"已删除重要日期":"已保存重要日期");return true;
        },()=>FocusAdd());card.Closed+=()=>{if(ReferenceEquals(_countdownEditor,card))_countdownEditor=null;};_countdownEditor=card;_host.ShowDialog(card);
    }
}
