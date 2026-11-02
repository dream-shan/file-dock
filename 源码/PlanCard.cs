using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Markup;

namespace FileDockDemo;
internal sealed class PlanCard : UserControl
{
    private readonly PlanItem _draft;
    private readonly List<PlanItem> _all;
    private readonly Func<PlanItem,bool> _commit;
    private readonly Action _cancel;
    private readonly string _original;
    private readonly TextBox _title=new(),_notes=new(),_time=new(),_search=new();
    private readonly TextBlock _titleError=new(),_error=new(),_warning=new(),_parentHint=new(),_counter=new(),_summary=new();
    private readonly Button _submit,_date;
    private readonly RadioButton[] _types=new RadioButton[4];
    private readonly StackPanel _body=new(),_parents=new(),_warningArea=new();
    private readonly Button _parentButton;
    private readonly Button _clearParent;
    private readonly Expander _more;
    private readonly Grid _moreBody=new();
    private readonly List<Popup> _popups=new();
    private string? _parentId;
    private bool _touched,_busy,_committed,_initializing=true;
    private readonly bool _editing;
    internal bool HasChanges=>Snapshot()!=_original;
    internal bool IsBusy=>_busy;
    private string Snapshot()=>string.Join("\u001f",_title.Text,_draft.Level,_draft.ScopePeriod,_draft.Deadline?.ToString("yyyy-MM-dd"),_parentId,_draft.Priority,_notes.Text,_time.Text);
    internal PlanCard(PlanItem draft,List<PlanItem> all,bool editing,Func<PlanItem,bool> commit,Action cancel,Action? delete=null,Action? addChild=null,Action<PlanItem>? editChild=null)
    {
        _draft=draft;_all=all;_commit=commit;_cancel=cancel;_editing=editing;_parentId=draft.ParentId;
        PlanRules.AssignPeriod(_draft,draft.Level,draft.Date);
        SetResourceReference(BackgroundProperty,"Surface");SetResourceReference(ForegroundProperty,"Ink");
        RefreshTheme();
        // Explicit local resources keep controls/popovers readable even under system control templates.
        KeyboardNavigation.SetTabNavigation(this,KeyboardNavigationMode.Cycle);KeyboardNavigation.SetControlTabNavigation(this,KeyboardNavigationMode.Cycle);
        AutomationProperties.SetName(this,editing ? "编辑计划对话框":"新建待办对话框");AutomationProperties.SetAutomationId(this,"PlanDialog");
        var root=new Grid();root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});Content=root;
        var header=new DockPanel{Margin=new Thickness(0,0,0,16)};var close=Btn("×",RequestClose);close.Width=32;close.Height=32;AutomationProperties.SetName(close,"关闭");AutomationProperties.SetAutomationId(close,"PlanClose");DockPanel.SetDock(close,Dock.Right);header.Children.Add(close);
        var heading=Text(editing ? "编辑计划":"新建待办");heading.FontSize=16;heading.FontWeight=FontWeights.SemiBold;heading.VerticalAlignment=VerticalAlignment.Center;header.Children.Add(heading);root.Children.Add(header);
        var scroll=new ScrollViewer{Content=_body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,CanContentScroll=false};AutomationProperties.SetAutomationId(scroll,"PlanBody");Grid.SetRow(scroll,1);root.Children.Add(scroll);
        var footer=new Border{BorderThickness=new Thickness(0,1,0,0),Padding=new Thickness(0,14,0,0),Margin=new Thickness(0,12,0,0)};footer.SetResourceReference(Border.BorderBrushProperty,"Border");footer.SetResourceReference(Border.BackgroundProperty,"Surface");AutomationProperties.SetAutomationId(footer,"PlanFooter");Grid.SetRow(footer,2);root.Children.Add(footer);
        var actions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};footer.Child=actions;if(delete!=null)actions.Children.Add(Btn("删除",delete));actions.Children.Add(Btn("取消",RequestClose));_submit=Btn(editing ? "保存":"添加",Save);_submit.SetResourceReference(BackgroundProperty,"PlanAccent");_submit.SetResourceReference(ForegroundProperty,"PlanOnAccent");AutomationProperties.SetAutomationId(_submit,"PlanSubmit");actions.Children.Add(_submit);
        var titleBlock=Section("计划（必填）");_title.Text=draft.Title;_title.AcceptsReturn=true;_title.TextWrapping=TextWrapping.Wrap;_title.MinLines=1;_title.MaxLines=4;_title.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;_title.MaxLength=200;Input(_title,"PlanTitle");
        var input=new Grid();input.Children.Add(_title);var placeholder=Text("输入计划内容",true);placeholder.Margin=new Thickness(9,8,9,8);placeholder.IsHitTestVisible=false;placeholder.Visibility=_title.Text.Length==0 ? Visibility.Visible:Visibility.Collapsed;input.Children.Add(placeholder);titleBlock.Children.Add(input);_counter.HorizontalAlignment=HorizontalAlignment.Right;_counter.SetResourceReference(TextBlock.ForegroundProperty,"PlanSecondary");_counter.Visibility=Visibility.Collapsed;titleBlock.Children.Add(_counter);_titleError.SetResourceReference(TextBlock.ForegroundProperty,"Error");_titleError.TextWrapping=TextWrapping.Wrap;titleBlock.Children.Add(_titleError);
        _title.TextChanged+=(_,_)=>{if(_parentHint.Text.StartsWith("已取消关联："))_parentHint.Text=_draft.Level==0 ? "长期待办没有上级":"";placeholder.Visibility=_title.Text.Length==0 ? Visibility.Visible:Visibility.Collapsed;_counter.Text=_title.Text.Length>160 ? $"{_title.Text.Length}/200":"";_counter.Visibility=_title.Text.Length>160 ? Visibility.Visible:Visibility.Collapsed;ValidateTitle();};_title.LostKeyboardFocus+=(_,_)=>{_touched=true;ValidateTitle();};
        var scopes=Section("范围");var row=new ScopeGrid{Columns=4};AutomationProperties.SetName(row,"范围，单选");scopes.Children.Add(row);string group=Guid.NewGuid().ToString();
        for(int i=0;i<4;i++){int level=i;var b=new RadioButton{GroupName=group,Margin=new Thickness(0,0,i==3 ? 0:4,0),Padding=new Thickness(4,8,4,8),HorizontalContentAlignment=HorizontalAlignment.Center,IsChecked=i==draft.Level};b.SetResourceReference(ForegroundProperty,"PlanSecondary");b.Template=ScopeTemplate();AutomationProperties.SetAutomationId(b,"PlanType"+i);b.Checked+=(_,_)=>{if(!_initializing)ChangeLevel(level);};b.KeyDown+=(_,e)=>{if(e.Key is Key.Left or Key.Right){int next=(level+(e.Key==Key.Right ? 1:3))%4;_types[next].IsChecked=true;_types[next].Focus();e.Handled=true;}};_types[i]=b;row.Children.Add(b);}
        var due=Section("截止日期（可选）");var dueRow=new DockPanel();var clear=Btn("×",()=>{_draft.Deadline=null;Refresh();});AutomationProperties.SetName(clear,"清除截止日期");DockPanel.SetDock(clear,Dock.Right);dueRow.Children.Add(clear);_date=Btn("",OpenCalendar);_date.HorizontalContentAlignment=HorizontalAlignment.Left;AutomationProperties.SetAutomationId(_date,"PlanDate");dueRow.Children.Add(_date);due.Children.Add(dueRow);
        due.Children.Add(_warningArea);
        var moreHead=new DockPanel();_summary.SetResourceReference(TextBlock.ForegroundProperty,"PlanSecondary");_summary.FontSize=10;_summary.VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(_summary,Dock.Right);moreHead.Children.Add(_summary);moreHead.Children.Add(Text("补充信息"));
        _more=new Expander{Header=moreHead,Content=_moreBody,HorizontalContentAlignment=HorizontalAlignment.Stretch,Margin=new Thickness(0,0,0,20)};AutomationProperties.SetAutomationId(_moreBody,"PlanMoreBody");_more.SetResourceReference(ForegroundProperty,"Ink");_more.Template=MoreTemplate();AutomationProperties.SetName(_more,"补充信息");AutomationProperties.SetAutomationId(_more,"PlanMore");_body.Children.Add(_more);
        var moreStack=new StackPanel{Margin=new Thickness(0,12,0,0)};_moreBody.Children.Add(moreStack);_more.Expanded+=(_,_)=>{_summary.Visibility=Visibility.Collapsed;_moreBody.BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(150)));};_more.Collapsed+=(_,_)=>_summary.Visibility=Visibility.Visible;
        moreStack.Children.Add(Text("关联上级"));_parentButton=Btn("独立（不关联）",OpenParents);_parentButton.HorizontalContentAlignment=HorizontalAlignment.Left;AutomationProperties.SetAutomationId(_parentButton,"PlanParent");moreStack.Children.Add(_parentButton);_clearParent=Btn("清除关联",()=>{_parentId=null;Refresh();});moreStack.Children.Add(_clearParent);_parentHint.TextWrapping=TextWrapping.Wrap;_parentHint.SetResourceReference(TextBlock.ForegroundProperty,"PlanSecondary");moreStack.Children.Add(_parentHint);
        moreStack.Children.Add(new Border{Height=20});moreStack.Children.Add(Text("优先级（可选）"));var priorities=new UniformGrid{Columns=4};string[] priority={"none","low","medium","high"},priorityLabels={"无","低","中","高"};for(int i=0;i<4;i++){string value=priority[i];var b=new RadioButton{Content=priorityLabels[i],GroupName=group+"priority",IsChecked=draft.Priority==value,Template=ScopeTemplate(),Padding=new Thickness(4,6,4,6),Margin=new Thickness(0,6,4,8)};b.SetResourceReference(ForegroundProperty,"Ink");b.Checked+=(_,_)=>{if(_parentHint.Text.StartsWith("已取消关联："))_parentHint.Text=_draft.Level==0 ? "长期待办没有上级":"";_draft.Priority=value;RefreshSummary();};priorities.Children.Add(b);}moreStack.Children.Add(priorities);
        moreStack.Children.Add(new Border{Height=12});moreStack.Children.Add(Text("备注（可选）"));_notes.Text=draft.Notes;_notes.AcceptsReturn=true;_notes.TextWrapping=TextWrapping.Wrap;_notes.MaxLength=500;_notes.Height=80;_notes.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;Input(_notes,"PlanNotes");moreStack.Children.Add(_notes);_notes.TextChanged+=(_,_)=>{if(_parentHint.Text.StartsWith("已取消关联："))_parentHint.Text=_draft.Level==0 ? "长期待办没有上级":"";RefreshSummary();};
        moreStack.Children.Add(Text("具体时间（可选，HH:mm 或 HH:mm–HH:mm）",true));_time.Text=draft.Time;Input(_time,"PlanTime");moreStack.Children.Add(_time);
        if(editing && addChild!=null)moreStack.Children.Add(Btn("＋ 添加下级计划",addChild));if(editChild!=null)foreach(var p in all.Where(p=>p.ParentId==draft.Id))moreStack.Children.Add(Btn("↳ "+p.Title,()=>editChild(p)));
        _error.TextWrapping=TextWrapping.Wrap;_error.SetResourceReference(TextBlock.ForegroundProperty,"Error");_body.Children.Add(_error);_initializing=false;Refresh();_original=Snapshot();_counter.Text=_title.Text.Length>160 ? $"{_title.Text.Length}/200":"";_counter.Visibility=_title.Text.Length>160 ? Visibility.Visible:Visibility.Collapsed;ValidateTitle();Loaded+=(_,_)=>FocusTitle();Unloaded+=(_,_)=>{foreach(var popup in _popups.ToArray())popup.IsOpen=false;};
    }
    internal void RefreshTheme(){bool dark=ThemePalette.Brush("Surface").Color.R<128;Resources["PlanSecondary"]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#A4AEBE":"#536177"));var accent=(Color)ColorConverter.ConvertFromString(dark ? "#82B3FF":"#3554CC");Resources["PlanAccent"]=new SolidColorBrush(accent);Resources["PlanSelection"]=new SolidColorBrush(Color.FromArgb(31,accent.R,accent.G,accent.B));Resources["PlanOnAccent"]=new SolidColorBrush(dark ? Color.FromRgb(32,35,41):Colors.White);}
    private static TextBlock Text(string s,bool secondary=false){var t=new TextBlock{Text=s,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)};t.SetResourceReference(TextBlock.ForegroundProperty,secondary ? "PlanSecondary":"Ink");return t;}
    private StackPanel Section(string name){var p=new StackPanel{Margin=new Thickness(0,0,0,20)};p.Children.Add(Text(name));_body.Children.Add(p);return p;}
    private static Button Btn(string text,Action action){var b=new Button{Opacity=1,Content=text,Padding=new Thickness(10,7,10,7),Margin=new Thickness(2),MinHeight=32};b.SetResourceReference(StyleProperty,"QuietButton");b.SetResourceReference(ForegroundProperty,"PlanSecondary");b.Click+=(_,_)=>action();return b;}
    private static void Input(TextBox b,string id){b.Padding=new Thickness(8);b.Margin=new Thickness(0,2,0,5);b.SetResourceReference(BackgroundProperty,"Input");b.SetResourceReference(ForegroundProperty,"Ink");b.SetResourceReference(BorderBrushProperty,"Border");b.BorderThickness=new Thickness(1);AutomationProperties.SetAutomationId(b,id);}
    private static ControlTemplate ScopeTemplate()=>(ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="RadioButton"><Border x:Name="B" Padding="{TemplateBinding Padding}" CornerRadius="6" BorderThickness="1" BorderBrush="{DynamicResource Border}" Background="Transparent"><ContentPresenter HorizontalAlignment="Center"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="B" Property="Background" Value="#0DFFFFFF"/></Trigger><Trigger Property="IsChecked" Value="True"><Setter TargetName="B" Property="Background" Value="{DynamicResource PlanSelection}"/><Setter TargetName="B" Property="BorderBrush" Value="{DynamicResource PlanAccent}"/><Setter Property="Foreground" Value="{DynamicResource PlanAccent}"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="B" Property="BorderThickness" Value="2"/></Trigger></ControlTemplate.Triggers></ControlTemplate>
        """);
    private static ControlTemplate MoreTemplate()=>(ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Expander"><StackPanel><ToggleButton x:Name="Toggle" IsChecked="{Binding IsExpanded,RelativeSource={RelativeSource TemplatedParent},Mode=TwoWay}" HorizontalContentAlignment="Stretch" Background="Transparent" Foreground="{DynamicResource Ink}" BorderThickness="0" Padding="0,8"><ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border Background="Transparent"><DockPanel><TextBlock x:Name="Arrow" Text="›" Foreground="{DynamicResource Ink}" Margin="0,0,10,0" VerticalAlignment="Center"/><ContentPresenter Content="{TemplateBinding Content}"/></DockPanel></Border><ControlTemplate.Triggers><Trigger Property="IsChecked" Value="True"><Setter TargetName="Arrow" Property="Text" Value="⌄"/></Trigger></ControlTemplate.Triggers></ControlTemplate></ToggleButton.Template><ContentPresenter ContentSource="Header"/></ToggleButton><ContentPresenter x:Name="Body" Visibility="Collapsed" ContentSource="Content"/></StackPanel><ControlTemplate.Triggers><Trigger Property="IsExpanded" Value="True"><Setter TargetName="Body" Property="Visibility" Value="Visible"/></Trigger></ControlTemplate.Triggers></ControlTemplate>
        """);
    private void ValidateTitle(){bool invalid=string.IsNullOrWhiteSpace(_title.Text);_submit.IsEnabled=!invalid&&!_busy&&!_committed;_submit.SetResourceReference(BackgroundProperty,invalid||_busy ? "Track":"PlanAccent");_submit.SetResourceReference(ForegroundProperty,invalid||_busy ? "Ink":"PlanOnAccent");_titleError.Text=_touched&&invalid ? "请输入计划内容。":"";_titleError.Visibility=_touched&&invalid ? Visibility.Visible:Visibility.Collapsed;_title.SetResourceReference(BorderBrushProperty,_touched&&invalid ? "Error":"Border");}
    internal void FocusTitle()=>_title.Focus();
    internal void HandleWindowKey(KeyEventArgs e)
    {
        if(e.Key==Key.Escape){var open=_popups.LastOrDefault(p=>p.IsOpen);if(open!=null)open.IsOpen=false;else RequestClose();e.Handled=true;}
        else if(e.Key==Key.Enter && _title.IsKeyboardFocusWithin && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)){Save();e.Handled=true;}
    }
    internal void RequestClose(){if(_busy)return;if(HasChanges && !App.IsSelfTest && MessageBox.Show(Window.GetWindow(this),"放弃已填写的内容？","文件坞",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;_cancel();}
    private void ChangeLevel(int level)
    {
        string? old=_parentId;PlanRules.AssignPeriod(_draft,level,PlanRules.Today);
        if(old!=null && old!="" && !_all.Any(p=>p.Id==old && PlanRules.ParentAllowed(p,_draft))){_parentId=null;_parentHint.Text="已取消关联："+(_all.FirstOrDefault(p=>p.Id==old)?.Title??old);}
        else _parentHint.Text="";Refresh(false);
    }
    private void Refresh(bool resetHint=true)
    {
        for(int i=0;i<4;i++){
            var sample=new PlanItem();PlanRules.AssignPeriod(sample,i,PlanRules.Today);if(i==_draft.Level)sample=_draft;string hint=i switch{0=>"无期限",1=>PlanRules.Start(sample).ToString("MM月"),2=>$"{PlanRules.Start(sample):MM/dd}–{PlanRules.End(sample):MM/dd}",_=>PlanRules.Start(sample).ToString("MM/dd ddd")};
            var text=new StackPanel();var name=Text(PlanRules.Names[i]);name.HorizontalAlignment=HorizontalAlignment.Center;name.Margin=new Thickness(0,0,0,5);name.SetResourceReference(TextBlock.ForegroundProperty,i==_draft.Level ? "PlanAccent":"PlanSecondary");text.Children.Add(name);var sub=Text(hint,true);sub.FontSize=9;sub.TextWrapping=TextWrapping.NoWrap;sub.Margin=new Thickness(0);text.Children.Add(sub);_types[i].Content=text;
        }
        _date.Content=_draft.Deadline is DateTime due ? "▦  "+due.ToString("MM月d日 ddd")+"  ▾":"▦  不设截止日期  ▾";
        var parent=_all.FirstOrDefault(p=>p.Id==_parentId);_parentButton.Content=parent==null ? "独立（不关联）":parent.Title;_parentButton.IsEnabled=_draft.Level!=0;
        if(resetHint)_parentHint.Text=_draft.Level==0 ? "长期待办没有上级":parent!=null && !PlanRules.ParentAllowed(parent,_draft) ? "原关联已不符合规则，请重新选择上级。":"可选未完成、层级更高且周期包含本计划的待办。";
        if(_draft.Level==0)_parentHint.Text=_parentHint.Text.StartsWith("已取消关联：") ? _parentHint.Text+"\n长期待办没有上级":"长期待办没有上级";
        _warningArea.Children.Clear();int? suggested=PlanRules.SuggestedLevel(_draft,PlanRules.Today);
        if(suggested.HasValue){_warningArea.Children.Add(Text($"截止日期已超出「{PlanRules.Names[_draft.Level]}」，是否改为「{PlanRules.Names[suggested.Value]}」？",true));_warningArea.Children.Add(Btn("改为"+PlanRules.Names[suggested.Value],()=>{int n=suggested.Value;_types[n].IsChecked=true;}));}
        if(parent?.Deadline is DateTime parentDue && _draft.Deadline is DateTime childDue && childDue.Date>parentDue.Date)_warningArea.Children.Add(Text("截止日期晚于上级计划的截止日期。",true));
        _clearParent.Visibility=string.IsNullOrEmpty(_parentId) ? Visibility.Collapsed:Visibility.Visible;RefreshSummary();
    }
    private void RefreshSummary(){var parts=new List<string>();if(!string.IsNullOrEmpty(_parentId))parts.Add("已关联");if(_draft.Priority!="none")parts.Add(_draft.Priority switch{"high"=>"高优先级","medium"=>"中优先级",_=>"低优先级"});if(!string.IsNullOrWhiteSpace(_notes.Text))parts.Add("有备注");_summary.Text=string.Join(" · ",parts);}
    private Popup Popup(UIElement content,UIElement target)
    {
        var border=new Border{Child=content,CornerRadius=new CornerRadius(8),Padding=new Thickness(10),BorderThickness=new Thickness(1)};border.Resources=Resources;border.SetResourceReference(Border.BackgroundProperty,"Surface");border.SetResourceReference(Border.BorderBrushProperty,"Border");
        KeyboardNavigation.SetTabNavigation(border,KeyboardNavigationMode.Cycle);var popup=new Popup{Child=border,PlacementTarget=target,Placement=PlacementMode.Bottom,StaysOpen=false,AllowsTransparency=true};border.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){popup.IsOpen=false;e.Handled=true;}};popup.Closed+=(_,_)=>{_popups.Remove(popup);if(Window.GetWindow(this) is MainWindow owner && owner.IsActive && !owner.CaptureInteractionHeld && target is Control c)c.Focus();};_popups.Add(popup);popup.IsOpen=true;border.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));return popup;
    }
    private void OpenCalendar()
    {
        _parentHint.Text="";var box=new StackPanel{Width=280};var quick=new WrapPanel();box.Children.Add(quick);Popup? popup=null;
        void Select(DateTime? date){_draft.Deadline=date?.Date;popup!.IsOpen=false;Refresh();}
        var today=PlanRules.Today;string[] names={"今天","明天","本周末","本月底"};DateTime[] dates={today,today.AddDays(1),PlanRules.Week(today).AddDays(6),new DateTime(today.Year,today.Month,DateTime.DaysInMonth(today.Year,today.Month))};for(int i=0;i<4;i++){var d=dates[i];quick.Children.Add(Btn(names[i],()=>Select(d)));}
        var calendarHost=new StackPanel();box.Children.Add(calendarHost);DateTime month=new DateTime((_draft.Deadline??today).Year,(_draft.Deadline??today).Month,1);
        void Calendar(){calendarHost.Children.Clear();var nav=new DockPanel();var prev=Btn("〈",()=>{if(month.Year>1 || month.Month>1){month=month.AddMonths(-1);Calendar();}});DockPanel.SetDock(prev,Dock.Left);nav.Children.Add(prev);var next=Btn("〉",()=>{if(month.Year<9999 || month.Month<12){month=month.AddMonths(1);Calendar();}});DockPanel.SetDock(next,Dock.Right);nav.Children.Add(next);nav.Children.Add(Text(month.ToString("yyyy年MM月")));calendarHost.Children.Add(nav);
            var days=new UniformGrid{Columns=7};calendarHost.Children.Add(days);for(int i=0;i<7;i++)days.Children.Add(Text(CultureInfo.CurrentCulture.DateTimeFormat.ShortestDayNames[((int)PlanRules.WEEK_START+i)%7],true));int offset=((int)month.DayOfWeek-(int)PlanRules.WEEK_START+7)%7;for(int i=0;i<offset;i++)days.Children.Add(new Border());
            for(int i=1;i<=DateTime.DaysInMonth(month.Year,month.Month);i++){DateTime date=new DateTime(month.Year,month.Month,i);var b=Btn(i.ToString(),()=>Select(date));b.Padding=new Thickness(2);b.MinWidth=30;b.IsEnabled=date>=today;b.SetResourceReference(ForegroundProperty,date<today ? "PlanSecondary":"Ink");if(date==_draft.Deadline?.Date){b.SetResourceReference(BackgroundProperty,"PlanSelection");b.SetResourceReference(ForegroundProperty,"PlanAccent");}AutomationProperties.SetName(b,date.ToString("yyyy年M月d日"));days.Children.Add(b);}}
        Calendar();box.Children.Add(Btn("清除",()=>Select(null)));popup=Popup(box,_date);
    }
    private void OpenParents()
    {
        _parentHint.Text="";var box=new StackPanel{Width=Math.Max(220,Math.Min(340,ActualWidth-20))};Input(_search,"PlanParentSearch");_search.Text="";box.Children.Add(_search);var parentScroll=new ScrollViewer{Content=_parents,MaxHeight=220,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};box.Children.Add(parentScroll);Popup? popup=null;
        void Fill(){_parents.Children.Clear();_parents.Children.Add(Btn("独立（不关联）",()=>{_parentId=null;popup!.IsOpen=false;Refresh();}));var candidates=PlanRules.Parents(_draft,_all).Where(p=>p.Title.Contains(_search.Text,StringComparison.CurrentCultureIgnoreCase)).ToList();if(candidates.Count==0)_parents.Children.Add(Text("暂无可关联的上级",true));foreach(var group in candidates.GroupBy(p=>p.Level)){_parents.Children.Add(Text(PlanRules.Names[group.Key],true));foreach(var p in group)_parents.Children.Add(Btn(p.Title+"  ["+PlanRules.Names[p.Level]+"]",()=>{_parentId=p.Id;popup!.IsOpen=false;Refresh();}));}}
        // Fresh popup owns the search control only while open.
        var handler=new TextChangedEventHandler((_,_)=>Fill());_search.TextChanged+=handler;Fill();popup=Popup(box,_parentButton);popup.Closed+=(_,_)=>{_search.TextChanged-=handler;box.Children.Remove(_search);parentScroll.Content=null;};_search.Focus();
    }
    private void Save()
    {
        if(_busy||_committed)return;_touched=true;ValidateTitle();if(string.IsNullOrWhiteSpace(_title.Text))return;
        if(_title.Text.Trim().Length>200 || _notes.Text.Length>500){_error.Text="计划最多200字，备注最多500字。";return;}
        if(_parentId!=null && _parentId!="" && !_all.Any(p=>p.Id==_parentId && PlanRules.ParentAllowed(p,_draft))){_error.Text="请选择符合层级和周期规则的上级，或选择独立。";_more.IsExpanded=true;return;}
        var descendants=PlanRules.Descendants(_draft,_all);if(descendants.Any(p=>p.Level<=_draft.Level)){_error.Text="当前范围会使已有子项的层级失效，请先调整子项关联。";return;}
        string time=_time.Text.Trim().Replace('—','–').Replace('-','–');var parts=time.Split('–');if(time!="" && (parts.Length>2 || parts.Any(s=>!TimeOnly.TryParseExact(s.Trim(),"HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out _)) || parts.Length==2 && string.CompareOrdinal(parts[0].Trim(),parts[1].Trim())>=0)){_error.Text="时间请使用 HH:mm，结束时间须晚于开始时间。";_more.IsExpanded=true;return;}
        _draft.Title=_title.Text.Trim();_draft.Time=time;_draft.Notes=_notes.Text;_draft.ParentId=string.IsNullOrEmpty(_parentId) ? null:_parentId;_draft.CreatedAt??=PlanRules.Now;_draft.UpdatedAt=PlanRules.Now;_draft.PlanSchema=1;
        _busy=true;_submit.Content="保存中…";ValidateTitle();
        try{if(_commit(_draft))_committed=true;else _error.Text="保存失败，填写内容已保留。请检查文件权限或稍后重试。";}catch(Exception ex){_error.Text="保存失败："+ex.Message;}finally{_busy=false;_submit.Content=_editing ? "保存":"添加";ValidateTitle();}
    }
    private sealed class ScopeGrid : UniformGrid
    {
        protected override AutomationPeer OnCreateAutomationPeer()=>new ScopePeer(this);
        private sealed class ScopePeer : FrameworkElementAutomationPeer{internal ScopePeer(ScopeGrid grid):base(grid){}protected override AutomationControlType GetAutomationControlTypeCore()=>AutomationControlType.Group;protected override string GetNameCore()=>"范围，单选";}
    }
    protected override AutomationPeer OnCreateAutomationPeer()=>new DialogPeer(this);
    private sealed class DialogPeer : FrameworkElementAutomationPeer,IWindowProvider
    {
        private readonly PlanCard _card;internal DialogPeer(PlanCard card):base(card){_card=card;}
        protected override string GetClassNameCore()=>"PlanDialog";
        protected override AutomationControlType GetAutomationControlTypeCore()=>AutomationControlType.Window;
        public override object? GetPattern(PatternInterface p)=>p==PatternInterface.Window ? this:base.GetPattern(p);
        public bool IsModal=>true;public bool IsTopmost=>true;public bool Maximizable=>false;public bool Minimizable=>false;public WindowInteractionState InteractionState=>WindowInteractionState.ReadyForUserInteraction;public WindowVisualState VisualState=>WindowVisualState.Normal;
        public void Close()=>_card.Dispatcher.BeginInvoke(_card.RequestClose);public void SetVisualState(WindowVisualState state){}public bool WaitForInputIdle(int milliseconds)=>true;
    }
}