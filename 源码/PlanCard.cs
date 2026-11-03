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
    private readonly Func<PlanItem,bool> _commit;
    private readonly Action _cancel;
    private readonly string _original;
    private readonly PlanTitleBox _title=new();
    private readonly TextBlock _titleError=new(),_error=new(),_counter=new();
    private readonly Button _submit,_date;
    private readonly RadioButton[] _types=new RadioButton[2];
    private readonly StackPanel _body=new(),_warningArea=new(); private readonly StackPanel _schedule=new();
    private readonly List<Popup> _popups=new();
    private bool _touched,_busy,_committed,_initializing=true;
    private readonly bool _editing;
    internal bool HasChanges=>Snapshot()!=_original;
    internal bool IsBusy=>_busy;
    private string Snapshot()=>string.Join("\u001f",_title.Text,_draft.Kind,_draft.RepeatRule,_draft.Weekdays,_draft.ActiveDays,_draft.RestDays,_draft.ScheduledDate.ToString("yyyy-MM-dd"));
    internal PlanCard(PlanItem draft,bool editing,Func<PlanItem,bool> commit,Action cancel,Action? delete=null)
    {
        _draft=draft;_commit=commit;_cancel=cancel;_editing=editing;

        SetResourceReference(BackgroundProperty,"Surface");SetResourceReference(ForegroundProperty,"Ink");
        RefreshTheme();
        // Explicit local resources keep controls/popovers readable even under system control templates.
        KeyboardNavigation.SetTabNavigation(this,KeyboardNavigationMode.Cycle);KeyboardNavigation.SetControlTabNavigation(this,KeyboardNavigationMode.Cycle);
        AutomationProperties.SetName(this,editing ? "编辑计划对话框":"新建待办对话框");AutomationProperties.SetAutomationId(this,"PlanDialog");
        var root=new Grid();root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});Content=root;
        var header=new DockPanel{Margin=new Thickness(0,0,0,10)};var close=Btn("×",RequestClose);close.Width=32;close.Height=32;AutomationProperties.SetName(close,"关闭");AutomationProperties.SetAutomationId(close,"PlanClose");DockPanel.SetDock(close,Dock.Right);header.Children.Add(close);
        var heading=Text(editing ? "编辑计划":"新建待办");heading.FontSize=16;heading.FontWeight=FontWeights.SemiBold;heading.VerticalAlignment=VerticalAlignment.Center;header.Children.Add(heading);root.Children.Add(header);
        var scroll=new ScrollViewer{Content=_body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,CanContentScroll=false};AutomationProperties.SetAutomationId(scroll,"PlanBody");Grid.SetRow(scroll,1);root.Children.Add(scroll);
        var footer=new Border{BorderThickness=new Thickness(0,1,0,0),Padding=new Thickness(0,10,0,0),Margin=new Thickness(0,8,0,0)};footer.SetResourceReference(Border.BorderBrushProperty,"Border");footer.SetResourceReference(Border.BackgroundProperty,"Surface");AutomationProperties.SetAutomationId(footer,"PlanFooter");Grid.SetRow(footer,2);root.Children.Add(footer);
        var actions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};footer.Child=actions;if(delete!=null)actions.Children.Add(Btn("删除",delete));actions.Children.Add(Btn("取消",RequestClose));_submit=Btn(editing ? "保存":"添加",Save);_submit.SetResourceReference(BackgroundProperty,"PlanAccent");_submit.SetResourceReference(ForegroundProperty,"PlanOnAccent");AutomationProperties.SetAutomationId(_submit,"PlanSubmit");actions.Children.Add(_submit);
        var titleBlock=Section("计划 *");_title.Text=draft.Title;_title.AcceptsReturn=true;_title.TextWrapping=TextWrapping.Wrap;_title.MinLines=1;_title.MaxLines=4;_title.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;_title.MaxLength=200;Input(_title,"PlanTitle");_title.Padding=new Thickness(6);AutomationProperties.SetName(_title,"计划");
        titleBlock.Children.Add(new System.Windows.Documents.AdornerDecorator{Child=_title});_counter.HorizontalAlignment=HorizontalAlignment.Right;_counter.SetResourceReference(TextBlock.ForegroundProperty,"PlanSecondary");_counter.Visibility=Visibility.Collapsed;titleBlock.Children.Add(_counter);_titleError.SetResourceReference(TextBlock.ForegroundProperty,"Error");_titleError.TextWrapping=TextWrapping.Wrap;titleBlock.Children.Add(_titleError);
        _title.TextChanged+=(_,_)=>{_counter.Text=_title.Text.Length>160 ? $"{_title.Text.Length}/200":"";_counter.Visibility=_title.Text.Length>160 ? Visibility.Visible:Visibility.Collapsed;ValidateTitle();};_title.LostKeyboardFocus+=(_,_)=>{_touched=true;ValidateTitle();};
        var scopes=Section("类型");var row=new ScopeGrid{Columns=2};scopes.Children.Add(Frame(row,"PlanTypeGroup"));string group=Guid.NewGuid().ToString();
        for(int i=0;i<2;i++){int n=i;var b=new RadioButton{GroupName=group,Content=i==0 ? "一次性":"重复",Margin=new Thickness(2),Padding=new Thickness(8,3,8,3),IsChecked=(draft.Kind=="repeat" ? 1:0)==i};b.SetResourceReference(ForegroundProperty,"PlanSecondary");b.Template=ScopeTemplate();AutomationProperties.SetAutomationId(b,"PlanType"+i);b.Checked+=(_,_)=>{if(!_initializing){_draft.Kind=n==0 ? "once":"repeat";Refresh();}};b.KeyDown+=(_,e)=>{if(e.Key is Key.Left or Key.Right){_types[1-n].IsChecked=true;_types[1-n].Focus();e.Handled=true;}};row.Children.Add(b);_types[i]=b;}
        _body.Children.Add(_schedule);
        _date=Btn("",OpenCalendar);AutomationProperties.SetAutomationId(_date,"PlanDate");_date.HorizontalContentAlignment=HorizontalAlignment.Stretch;_date.Padding=new Thickness(10,7,10,7);
        var errorStyle=new Style(typeof(TextBlock));var emptyError=new Trigger{Property=TextBlock.TextProperty,Value=""};emptyError.Setters.Add(new Setter(VisibilityProperty,Visibility.Collapsed));errorStyle.Triggers.Add(emptyError);_error.Style=errorStyle;_error.TextWrapping=TextWrapping.Wrap;_error.SetResourceReference(TextBlock.ForegroundProperty,"Error");_body.Children.Add(_error);_initializing=false;Refresh();_original=Snapshot();_counter.Text=_title.Text.Length>160 ? $"{_title.Text.Length}/200":"";_counter.Visibility=_title.Text.Length>160 ? Visibility.Visible:Visibility.Collapsed;ValidateTitle();Loaded+=(_,_)=>FocusTitle();Unloaded+=(_,_)=>{foreach(var popup in _popups.ToArray())popup.IsOpen=false;};
    }
    internal void RefreshTheme(){bool dark=ThemePalette.Brush("Surface").Color.R<128;Resources["PlanSecondary"]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#A4AEBE":"#536177"));var accent=(Color)ColorConverter.ConvertFromString(dark ? "#82B3FF":"#3554CC");Resources["PlanAccent"]=new SolidColorBrush(accent);Resources["PlanSelection"]=new SolidColorBrush(Color.FromArgb(31,accent.R,accent.G,accent.B));Resources["PlanOnAccent"]=new SolidColorBrush(dark ? Color.FromRgb(32,35,41):Colors.White);}
    private static TextBlock Text(string s,bool secondary=false){var t=new TextBlock{Text=s,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,4)};t.SetResourceReference(TextBlock.ForegroundProperty,secondary ? "PlanSecondary":"Ink");return t;}
    private StackPanel Section(string name){var p=new StackPanel{Margin=new Thickness(0,0,0,10)};p.Children.Add(Text(name));_body.Children.Add(p);return p;}
    private static Button Btn(string text,Action action){var b=new Button{Opacity=1,Content=text,Padding=new Thickness(10,7,10,7),Margin=new Thickness(2),MinHeight=32};b.SetResourceReference(StyleProperty,"QuietButton");b.SetResourceReference(ForegroundProperty,"PlanSecondary");b.Click+=(_,_)=>action();return b;}
    private static void Input(TextBox b,string id){b.SetResourceReference(StyleProperty,typeof(TextBox));b.Padding=new Thickness(8);b.Margin=new Thickness(0,2,0,5);b.SetResourceReference(BackgroundProperty,"Input");b.SetResourceReference(ForegroundProperty,"Ink");b.SetResourceReference(BorderBrushProperty,"Border");b.BorderThickness=new Thickness(1);AutomationProperties.SetAutomationId(b,id);}
    private static ControlTemplate ScopeTemplate()=>(ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ToggleButton"><Border x:Name="B" Padding="{TemplateBinding Padding}" CornerRadius="4" BorderThickness="1" BorderBrush="Transparent" Background="Transparent"><ContentPresenter HorizontalAlignment="Center"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="B" Property="Background" Value="#0DFFFFFF"/></Trigger><Trigger Property="IsChecked" Value="True"><Setter TargetName="B" Property="BorderThickness" Value="1"/><Setter TargetName="B" Property="Background" Value="{DynamicResource PlanSelection}"/><Setter TargetName="B" Property="BorderBrush" Value="{DynamicResource PlanAccent}"/><Setter Property="Foreground" Value="{DynamicResource PlanAccent}"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="B" Property="BorderBrush" Value="{DynamicResource PlanAccent}"/></Trigger></ControlTemplate.Triggers></ControlTemplate>
        """);
    private void ValidateTitle(){bool invalid=string.IsNullOrWhiteSpace(_title.Text);_submit.IsEnabled=!invalid&&!_busy&&!_committed;_submit.SetResourceReference(BackgroundProperty,invalid||_busy ? "Track":"PlanAccent");_submit.SetResourceReference(ForegroundProperty,invalid||_busy ? "Ink":"PlanOnAccent");_titleError.Text=_touched&&invalid ? "请输入计划内容。":"";_titleError.Visibility=_touched&&invalid ? Visibility.Visible:Visibility.Collapsed;_title.SetResourceReference(BorderBrushProperty,_touched&&invalid ? "Error":"Border");}
    internal void FocusTitle()=>_title.Focus();
    internal void HandleWindowKey(KeyEventArgs e)
    {
        if(e.Key==Key.Escape){var open=_popups.LastOrDefault(p=>p.IsOpen);if(open!=null)open.IsOpen=false;else RequestClose();e.Handled=true;}
        else if(e.Key==Key.Enter && _title.IsKeyboardFocusWithin && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)){Save();e.Handled=true;}
    }
    internal void RequestClose(){if(_busy)return;if(HasChanges && !App.IsSelfTest && MessageBox.Show(Window.GetWindow(this),"放弃已填写的内容？","文件坞",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;_cancel();}
    private static Border Frame(UIElement child,string id="")
    {
        var frame=new Border{Child=child,CornerRadius=new CornerRadius(7),BorderThickness=new Thickness(1),Padding=new Thickness(2)};
        frame.SetResourceReference(Border.BackgroundProperty,"Input");frame.SetResourceReference(Border.BorderBrushProperty,"Border");if(id.Length>0)AutomationProperties.SetAutomationId(frame,id);return frame;
    }
    private void Refresh()
    {
        if(_date.Parent is Border previousDate)previousDate.Child=null;_schedule.Children.Clear();
        if(_draft.Kind=="repeat"){
            var choices=new StackPanel{Margin=new Thickness(0,0,0,8)};choices.Children.Add(Text("出现规律"));var rules=new UniformGrid{Columns=3};choices.Children.Add(Frame(rules,"PlanRuleGroup"));
            string[] values={"daily","weekdays","cycle"},names={"每天","指定星期","循环安排"};
            for(int i=0;i<3;i++){int index=i;string rule=values[i];var b=new RadioButton{Content=names[i],GroupName="RepeatRule",IsChecked=_draft.RepeatRule==rule,Margin=new Thickness(2),Padding=new Thickness(3,5,3,5),Template=ScopeTemplate()};b.SetResourceReference(ForegroundProperty,"PlanSecondary");AutomationProperties.SetAutomationId(b,"PlanRule"+i);b.Checked+=(_,_)=>{_draft.RepeatRule=rule;Refresh();};b.KeyDown+=(_,e)=>{if(e.Key is Key.Left or Key.Right){int next=(index+(e.Key==Key.Right?1:2))%3;_draft.RepeatRule=values[next];Refresh();Dispatcher.BeginInvoke(()=>FocusRule(next));e.Handled=true;}};rules.Children.Add(b);}_schedule.Children.Add(choices);
            if(_draft.RepeatRule=="weekdays"){
                var days=new UniformGrid{Columns=7,Margin=new Thickness(0,0,0,10)};string[] labels={"一","二","三","四","五","六","日"};for(int i=0;i<7;i++){int bit=1<<i;var c=new CheckBox{Content=labels[i],Margin=new Thickness(1),Padding=new Thickness(0,7,0,7),Template=ScopeTemplate(),IsChecked=(_draft.Weekdays&bit)!=0};c.SetResourceReference(ForegroundProperty,"PlanSecondary");AutomationProperties.SetName(c,"周"+labels[i]);AutomationProperties.SetAutomationId(c,"PlanWeekday"+i);c.Click+=(_,_)=>{if(c.IsChecked==true)_draft.Weekdays|=bit;else _draft.Weekdays&=~bit;RefreshPreview();};days.Children.Add(c);}_schedule.Children.Add(days);
            }
            if(_draft.RepeatRule=="cycle"){
                var row=new Grid{Margin=new Thickness(0,0,0,10)};row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(10)});row.ColumnDefinitions.Add(new ColumnDefinition());
                var active=NumberCard("连续出现","PlanActiveDays",()=>_draft.ActiveDays,n=>_draft.ActiveDays=n);row.Children.Add(active);var rest=NumberCard("连续休息","PlanRestDays",()=>_draft.RestDays,n=>_draft.RestDays=n);Grid.SetColumn(rest,2);row.Children.Add(rest);_schedule.Children.Add(row);
            }
        }
        var label=Text(_draft.Kind=="once" ? "执行日期":"开始日期");_schedule.Children.Add(label);
        var dateRow=new DockPanel();var icon=new TextBlock{Text="\uE787",FontFamily=new FontFamily("Segoe MDL2 Assets"),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0)};DockPanel.SetDock(icon,Dock.Left);dateRow.Children.Add(icon);var arrow=new TextBlock{Text="\uE70D",FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=10,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,0,0)};DockPanel.SetDock(arrow,Dock.Right);dateRow.Children.Add(arrow);var dateText=Text(_draft.ScheduledDate.ToString("yyyy年M月d日 ddd"));dateText.Margin=new Thickness(0);dateText.VerticalAlignment=VerticalAlignment.Center;dateRow.Children.Add(dateText);_date.Content=dateRow;AutomationProperties.SetName(_date,_draft.ScheduledDate.ToString("yyyy年M月d日 ddd"));_schedule.Children.Add(Frame(_date));_schedule.Children.Add(_warningArea);RefreshPreview();
    }
    private void FocusRule(int index){_schedule.UpdateLayout();foreach(var child in VisualChildren(this))if(child is RadioButton b && AutomationProperties.GetAutomationId(b)=="PlanRule"+index){b.Focus();break;}}
    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject p){yield return p;for(int i=0;i<VisualTreeHelper.GetChildrenCount(p);i++)foreach(var c in VisualChildren(VisualTreeHelper.GetChild(p,i)))yield return c;}
    private Border NumberCard(string caption,string id,Func<int> read,Action<int> write)
    {
        var content=new StackPanel{Margin=new Thickness(8,3,8,3)};var label=Text(caption,true);label.FontSize=10;label.Margin=new Thickness(0,0,0,4);content.Children.Add(label);
        var row=new Grid();row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(28)});row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(28)});content.Children.Add(row);
        var input=new TextBox{Text=read().ToString(),MaxLength=3,MinWidth=24,FontSize=14,TextAlignment=TextAlignment.Center,VerticalContentAlignment=VerticalAlignment.Center};Input(input,id);input.Padding=new Thickness(0,3,0,3);input.Margin=new Thickness(1,0,1,0);input.BorderThickness=new Thickness(0);AutomationProperties.SetName(input,caption+"天数");Grid.SetColumn(input,1);row.Children.Add(input);
        var minus=Btn("−",()=>input.Text=Math.Clamp(read()-1,1,365).ToString());var plus=Btn("+",()=>input.Text=Math.Clamp(read()+1,1,365).ToString());minus.Padding=plus.Padding=new Thickness(0);minus.MinWidth=plus.MinWidth=0;minus.Margin=plus.Margin=new Thickness(0);AutomationProperties.SetName(minus,"减少"+caption+"天数");AutomationProperties.SetName(plus,"增加"+caption+"天数");AutomationProperties.SetAutomationId(minus,id+"Minus");AutomationProperties.SetAutomationId(plus,id+"Plus");row.Children.Add(minus);Grid.SetColumn(plus,2);row.Children.Add(plus);
        input.TextChanged+=(_,_)=>{write(int.TryParse(input.Text,out int n)?n:0);RefreshPreview();};return Frame(content);
    }
    private void RefreshPreview()
    {
        _warningArea.Children.Clear();if(_draft.Kind!="repeat")return;
        var heading=Text("近期安排",true);heading.FontSize=10;heading.Margin=new Thickness(0,10,0,6);_warningArea.Children.Add(heading);
        if((_draft.RepeatRule=="weekdays" && _draft.Weekdays==0) || (_draft.RepeatRule=="cycle" && (_draft.ActiveDays<1 || _draft.RestDays<1 || _draft.ActiveDays>365 || _draft.RestDays>365))){_warningArea.Children.Add(Text("请选择至少一天；出现和休息天数须为 1–365。",true));return;}
        var days=new UniformGrid{Columns=7};AutomationProperties.SetAutomationId(days,"PlanPreview");_warningArea.Children.Add(days);
        foreach(var d in Enumerable.Range(0,7).Select(i=>_draft.ScheduledDate.Date.AddDays(i))){bool appears=PlanRules.Appears(_draft,d);var content=new StackPanel{Margin=new Thickness(1,4,1,4)};var date=Text(d.ToString("M/d"),true);date.FontSize=10;date.HorizontalAlignment=HorizontalAlignment.Center;date.Margin=new Thickness(0,0,0,3);content.Children.Add(date);var state=Text(appears?"出现":"休息",true);state.FontSize=9;state.HorizontalAlignment=HorizontalAlignment.Center;state.Margin=new Thickness(0);if(appears)state.SetResourceReference(TextBlock.ForegroundProperty,"PlanAccent");content.Children.Add(state);var tile=new Border{Child=content,CornerRadius=new CornerRadius(5),Margin=new Thickness(1)};if(appears)tile.SetResourceReference(Border.BackgroundProperty,"PlanSelection");AutomationProperties.SetName(tile,d.ToString("M月d日")+(appears?"出现":"休息"));days.Children.Add(tile);}
    }
    private Popup Popup(UIElement content,UIElement target)
    {
        var border=new Border{Child=content,CornerRadius=new CornerRadius(8),Padding=new Thickness(10),BorderThickness=new Thickness(1)};border.Resources=Resources;border.SetResourceReference(Border.BackgroundProperty,"Surface");border.SetResourceReference(Border.BorderBrushProperty,"Border");
        KeyboardNavigation.SetTabNavigation(border,KeyboardNavigationMode.Cycle);var popup=new Popup{Child=border,PlacementTarget=target,Placement=PlacementMode.Bottom,StaysOpen=false,AllowsTransparency=true};border.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){popup.IsOpen=false;e.Handled=true;}};popup.Closed+=(_,_)=>{_popups.Remove(popup);if(Window.GetWindow(this) is MainWindow owner && owner.IsActive && !owner.CaptureInteractionHeld && target is Control c)c.Focus();};_popups.Add(popup);popup.IsOpen=true;border.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));return popup;
    }
    private void OpenCalendar()
    {
        var box=new StackPanel{Width=280};var quick=new WrapPanel();box.Children.Add(quick);Popup? popup=null;
        void Select(DateTime? date){_draft.ScheduledDate=(date??PlanRules.Today).Date;popup!.IsOpen=false;Refresh();}
        var today=PlanRules.Today;string[] names={"今天","明天","本周末","本月底"};DateTime[] dates={today,today.AddDays(1),PlanRules.Week(today).AddDays(6),new DateTime(today.Year,today.Month,DateTime.DaysInMonth(today.Year,today.Month))};for(int i=0;i<4;i++){var d=dates[i];quick.Children.Add(Btn(names[i],()=>Select(d)));}
        var calendarHost=new StackPanel();box.Children.Add(calendarHost);DateTime month=new DateTime(_draft.ScheduledDate.Year,_draft.ScheduledDate.Month,1);
        void Calendar(){calendarHost.Children.Clear();var nav=new DockPanel();var prev=Btn("〈",()=>{if(month.Year>1 || month.Month>1){month=month.AddMonths(-1);Calendar();}});DockPanel.SetDock(prev,Dock.Left);nav.Children.Add(prev);var next=Btn("〉",()=>{if(month.Year<9999 || month.Month<12){month=month.AddMonths(1);Calendar();}});DockPanel.SetDock(next,Dock.Right);nav.Children.Add(next);nav.Children.Add(Text(month.ToString("yyyy年MM月")));calendarHost.Children.Add(nav);
            var days=new UniformGrid{Columns=7};calendarHost.Children.Add(days);for(int i=0;i<7;i++)days.Children.Add(Text(CultureInfo.CurrentCulture.DateTimeFormat.ShortestDayNames[((int)PlanRules.WEEK_START+i)%7],true));int offset=((int)month.DayOfWeek-(int)PlanRules.WEEK_START+7)%7;for(int i=0;i<offset;i++)days.Children.Add(new Border());
            for(int i=1;i<=DateTime.DaysInMonth(month.Year,month.Month);i++){DateTime date=new DateTime(month.Year,month.Month,i);var b=Btn(i.ToString(),()=>Select(date));b.Padding=new Thickness(2);b.MinWidth=30;b.IsEnabled=date>=today;b.SetResourceReference(ForegroundProperty,date<today ? "PlanSecondary":"Ink");if(date==_draft.ScheduledDate.Date){b.SetResourceReference(BackgroundProperty,"PlanSelection");b.SetResourceReference(ForegroundProperty,"PlanAccent");}AutomationProperties.SetName(b,date.ToString("yyyy年M月d日"));days.Children.Add(b);}}
        Calendar();popup=Popup(box,_date);
    }
    private void Save()
    {
        if(_busy||_committed)return;_touched=true;ValidateTitle();if(string.IsNullOrWhiteSpace(_title.Text))return;
        if(_title.Text.Trim().Length>200){_error.Text="计划最多200字。";return;}
                if(_draft.Kind=="repeat" && ((_draft.RepeatRule=="weekdays" && _draft.Weekdays==0) || (_draft.RepeatRule=="cycle" && (_draft.ActiveDays<1 || _draft.RestDays<1 || _draft.ActiveDays>365 || _draft.RestDays>365)))){_error.Text="请选择出现日期，天数须为 1–365。";return;}
        if(_draft.RepeatRule!="weekdays")_draft.Weekdays=127;if(_draft.RepeatRule!="cycle"){_draft.ActiveDays=2;_draft.RestDays=1;} _draft.ScheduleSchema=1;_draft.Migrated=false;_draft.Deadline=null;_draft.Level=3;_draft.Date=_draft.ScheduledDate;_draft.ScopePeriod=PlanRules.Period(3,_draft.Date);
        _draft.Title=_title.Text.Trim();_draft.CreatedAt??=PlanRules.Now;_draft.UpdatedAt=PlanRules.Now;_draft.PlanSchema=1;
        _busy=true;_submit.Content="保存中…";ValidateTitle();
        try{if(_commit(_draft))_committed=true;else _error.Text="保存失败，填写内容已保留。请检查文件权限或稍后重试。";}catch(Exception ex){_error.Text="保存失败："+ex.Message;}finally{_busy=false;_submit.Content=_editing ? "保存":"添加";ValidateTitle();}
    }
    private sealed class ScopeGrid : UniformGrid
    {
        protected override AutomationPeer OnCreateAutomationPeer()=>new ScopePeer(this);
        private sealed class ScopePeer : FrameworkElementAutomationPeer{internal ScopePeer(ScopeGrid grid):base(grid){}protected override AutomationControlType GetAutomationControlTypeCore()=>AutomationControlType.Group;protected override string GetNameCore()=>"类型，单选";}
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
