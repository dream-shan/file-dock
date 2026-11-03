using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
namespace FileDockDemo;
internal static class PlanDialogVerification
{
    private static IEnumerable<T> All<T>(DependencyObject p) where T:DependencyObject{if(p is T t)yield return t;for(int i=0;i<VisualTreeHelper.GetChildrenCount(p);i++)foreach(var c in All<T>(VisualTreeHelper.GetChild(p,i)))yield return c;}
    private static T Id<T>(DependencyObject p,string id) where T:DependencyObject=>All<T>(p).First(v=>System.Windows.Automation.AutomationProperties.GetAutomationId(v)==id);
    private static double Contrast(Color a,Color b){double L(Color c){double C(byte v){double x=v/255d;return x<=.04045 ? x/12.92:Math.Pow((x+.055)/1.055,2.4);}return .2126*C(c.R)+.7152*C(c.G)+.0722*C(c.B);}double x=L(a),y=L(b);return (Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);}
    internal static void Run(MainWindow w,string root,Action<bool,string> check)
    {
        double width=w.Width,height=w.Height;string theme=w.State.ThemeMode;
        try{
            w.Width=462;w.Height=650;w.Planner.Render();w.UpdateLayout();Id<Button>(w,"PlannerQuickAdd").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));w.UpdateLayout();var card=w.ActivePlanCard??throw new InvalidOperationException("Dialog was not opened");
            check(!w.ContentGrid.IsEnabled && !w.PageRail.IsEnabled,"Plan dialog disables background controls");
            check(KeyboardNavigation.GetTabNavigation(card)==KeyboardNavigationMode.Cycle,"Plan dialog contains keyboard tab navigation");
            var peer=UIElementAutomationPeer.CreatePeerForElement(card)!;check(peer.GetPattern(PatternInterface.Window) is IWindowProvider provider && provider.IsModal,"Native accessibility exposes the plan card as a modal dialog");
            check(!Id<Button>(card,"PlanSubmit").IsEnabled && !card.HasChanges,"Empty dialog starts disabled without a dirty draft");
            var title=Id<TextBox>(card,"PlanTitle");title.Text=new string('测',161);w.UpdateLayout();check(All<TextBlock>(card).Any(t=>t.Text=="161/200" && t.IsVisible),"Title counter appears above 160 characters");
            title.Text="整理本周学习计划";w.UpdateLayout();check(!All<Expander>(card).Any(),"Simple dialog contains no supplementary section");
            title.Text="";w.UpdateLayout();var caret=title.GetRectFromCharacterIndex(0,true);check(!caret.IsEmpty && caret.Left>=title.Padding.Left && caret.Height>0,"Empty input exposes the native caret rectangle used by the placeholder");title.Text="整理本周学习计划";
            Id<RadioButton>(card,"PlanType0").IsChecked=true;Id<RadioButton>(card,"PlanType1").IsChecked=true;
            Id<RadioButton>(card,"PlanRule2").IsChecked=true;w.UpdateLayout();
            var active=Id<TextBox>(card,"PlanActiveDays");var rest=Id<TextBox>(card,"PlanRestDays");
            check(Math.Abs(active.ActualWidth-rest.ActualWidth)<1 && active.ActualHeight<=36,"Cycle inputs have equal width and compact height");
            Id<Button>(card,"PlanActiveDaysPlus").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));check(active.Text=="3","Cycle stepper increases editable day count");
            Id<Button>(card,"PlanActiveDaysMinus").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));check(active.Text=="2","Cycle stepper decreases editable day count");
            rest.Text="1";Id<Button>(card,"PlanRestDaysMinus").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));check(rest.Text=="1","Cycle stepper respects minimum one day");
            check(Id<UniformGrid>(card,"PlanPreview").Children.Count==7,"Preview renders seven separate day tiles");
            Id<RadioButton>(card,"PlanRule1").IsChecked=true;w.UpdateLayout();
            var weekdays=All<CheckBox>(card).Where(c=>System.Windows.Automation.AutomationProperties.GetAutomationId(c).StartsWith("PlanWeekday")).ToArray();
            check(weekdays.Length==7 && weekdays.Max(c=>c.TranslatePoint(new Point(),card).Y)-weekdays.Min(c=>c.TranslatePoint(new Point(),card).Y)<1,"Weekday buttons stay on one row");
            Id<RadioButton>(card,"PlanRule2").IsChecked=true;w.UpdateLayout();
            foreach(string mode in new[]{"Dark","Light"}){
                w.SetThemeMode(mode);w.UpdateLayout();var surface=ThemePalette.Brush("Surface").Color;var ink=ThemePalette.Brush("Ink").Color;var secondary=((SolidColorBrush)card.Resources["PlanSecondary"]).Color;var accent=((SolidColorBrush)card.Resources["PlanAccent"]).Color;var onAccent=((SolidColorBrush)card.Resources["PlanOnAccent"]).Color;
                check(Contrast(ink,surface)>=4.5 && Contrast(secondary,surface)>=4.5 && Contrast(accent,onAccent)>=4.5,$"Plan text and primary action meet 4.5:1 in {mode}");
                Color Blend(Color c){double a=c.A/255d;return Color.FromRgb((byte)Math.Round(c.R*a+surface.R*(1-a)),(byte)Math.Round(c.G*a+surface.G*(1-a)),(byte)Math.Round(c.B*a+surface.B*(1-a)));}
                check(All<Button>(card).Where(b=>b.IsVisible).All(b=>b.Foreground is SolidColorBrush fg && Contrast(fg.Color,b.Background is SolidColorBrush bg ? Blend(bg.Color):surface)>=4.5),$"Every visible dialog action meets 4.5:1 in {mode}");
                var body=Id<ScrollViewer>(card,"PlanBody");check(body.ExtentHeight<=body.ViewportHeight+1,$"Default {mode} cycle form fits without scrolling ({body.ExtentHeight:F0}/{body.ViewportHeight:F0})");var footer=Id<Border>(card,"PlanFooter");body.ScrollToEnd();w.UpdateLayout();
                var bodyBottom=body.TranslatePoint(new Point(0,body.ActualHeight),card).Y;var footerTop=footer.TranslatePoint(new Point(),card).Y;
                check(bodyBottom<=footerTop+1 && footer.ActualHeight>=32,$"Simplified {mode} dialog keeps body above a visible footer");
                DemoVerification.SaveRender(w,System.IO.Path.Combine(root,"plan-dialog-"+mode.ToLowerInvariant()+".png"));
            }
            w.Width=412;w.Height=480;w.UpdateLayout();var smallBody=Id<ScrollViewer>(card,"PlanBody");var smallFooter=Id<Border>(card,"PlanFooter");smallBody.ScrollToEnd();w.UpdateLayout();
            check(smallBody.ViewportHeight>30 && smallBody.TranslatePoint(new Point(0,smallBody.ActualHeight),card).Y<=smallFooter.TranslatePoint(new Point(),card).Y+1,"Compact window keeps body and footer separate");
            DemoVerification.SaveRender(w,System.IO.Path.Combine(root,"plan-dialog-small.png"));
        }finally{w.ClosePlanDialog();w.Width=width;w.Height=height;w.SetThemeMode(theme);w.Planner.Render();}
    }
}
