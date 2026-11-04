using System.Windows.Input;
using FileDockDemo.Features.Planner;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace FileDockDemo;
internal static class PagesVerification
{
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window,int attribute,out int value,int size);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int a,int b,int c,int d);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr region);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr region,int x,int y);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window,IntPtr region);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out NativeRect rect);
    private static T? Find<T>(DependencyObject parent) where T:DependencyObject
    {
        if(parent is T match)return match;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            if(Find<T>(VisualTreeHelper.GetChild(parent,i)) is T child)return child;
        return null;
    }
    private static async Task VerifyPlannerPage(MainWindow window,string root,Action<bool,string> check)
    {
        var original=window.State.Plans;
        try
        {
            window.State.Plans=Enumerable.Range(0,40).Select(i=>new PlanItem{Title=$"待办测试 {i:00}",Kind="repeat",RepeatRule="daily",ScheduledDate=PlanRules.Today.AddDays(1)}).ToList();
            window.SelectPage("Planner");window.UpdateLayout();await Task.Delay(100);
            check(window.IsPlannerPage && window.PlannerSection.IsVisible && !window.PinsSection.IsVisible && !window.BrowserPanel.IsVisible && !window.PerformanceSection.IsVisible && !window.FileSearchSurface.IsVisible,"Planner owns a full independent page without file search or performance cards");
            check(window.PlannerPageTab.IsChecked==true && window.FilesPageTab.IsChecked==false && window.PerformancePageTab.IsChecked==false,"Exactly the planner tab is selected");
            check(window.Storage.Load().SelectedPage=="Planner","Last planner page persists without altering plan schema");
            check(window.PlannerMonitoring && !window.PerformanceActive && window.MemoryOnlyActive && !window.FolderWatcherActive,"Planner runs only its date timer and lightweight memory checks");
            Visuals<Button>(window.Planner).First(b=>(b.Content as string)=="管理").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));window.UpdateLayout();await Task.Delay(100);
            var scroll=Visuals<ScrollViewer>(window.Planner).Single(v=>System.Windows.Automation.AutomationProperties.GetAutomationId(v)=="PlannerScroll");scroll.ScrollToVerticalOffset(500);window.UpdateLayout();double offset=scroll.VerticalOffset;
            check(offset>0,"Independent planner scrolls a long management list");
            window.SelectPage("Files",false);await Task.Delay(100);
            check(!window.PlannerMonitoring && !window.Planner.IsVisible,"Leaving planner stops the date timer");
            window.SelectPage("Planner",false);window.UpdateLayout();await Task.Delay(100);window.UpdateLayout();
            scroll=Visuals<ScrollViewer>(window.Planner).Single(v=>System.Windows.Automation.AutomationProperties.GetAutomationId(v)=="PlannerScroll");
            check(Visuals<Button>(window.Planner).Any(b=>(b.Content as string)=="返回今天") && Math.Abs(scroll.VerticalOffset-offset)<1,"Planner retains management mode and scroll after switching pages");
            window.CollapsePanel();check(!window.PlannerMonitoring,"Collapsed planner pauses date checks");window.ExpandPanel();await Task.Delay(100);
            check(window.IsPlannerPage && window.PlannerMonitoring,"Revealing planner restores its selected page and date timer");
            window.Width=380;window.Height=480;window.UpdateLayout();
            var add=Visuals<Button>(window.Planner).First(b=>System.Windows.Automation.AutomationProperties.GetAutomationId(b)=="PlannerQuickAdd");
            var point=add.TranslatePoint(new Point(),window.Planner);
            check(point.Y>=0 && point.Y+add.ActualHeight<=window.Planner.ActualHeight+1 && scroll.ViewportHeight>100,"Compact planner keeps the add action visible outside the scrolling list");
            DemoVerification.SaveRender(window,Path.Combine(root,"pages-planner-small.png"));
            window.Width=430;window.Height=650;
            Visuals<Button>(window.Planner).First(b=>(b.Content as string)=="返回今天").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
        finally{window.State.Plans=original;window.Planner.Render();window.Persist();}
    }
    private static System.Collections.Generic.IEnumerable<T> Visuals<T>(DependencyObject p) where T:DependencyObject
    {if(p is T item)yield return item;for(int i=0;i<VisualTreeHelper.GetChildrenCount(p);i++)foreach(var child in Visuals<T>(VisualTreeHelper.GetChild(p,i)))yield return child;}
    internal static async Task Run(MainWindow window,string root,Action<bool,string> check)
    {
        var rightPosition=new AppState{DockSide="Right",PanelLeft=100,PanelTop=200};PageRailWindow.MigratePosition(rightPosition);PageRailWindow.MigratePosition(rightPosition);
        check(rightPosition.PanelLeft==132 && rightPosition.PanelTop==200 && rightPosition.PanelGeometryVersion==1,"Old right-docked body position migrates once without vertical shift");
        var leftPosition=new AppState{DockSide="Left",PanelLeft=100,PanelTop=200};PageRailWindow.MigratePosition(leftPosition);check(leftPosition.PanelLeft==100,"Old left-docked body position is retained");
        var goal=new PlanItem{Level=0,Title="Goal"};
        check(goal.ToString()=="Goal","Planner parent selectors show titles in the existing themed combo template");
        var month=new PlanItem{Level=1,Title="Month",ParentId=goal.Id};
        var week=new PlanItem{Level=2,Title="Week",ParentId=month.Id};
        var day=new PlanItem{Level=3,Title="Day",ParentId=week.Id,Done=true,Notes="Notes",Time="09:00"};
        var plans=new System.Collections.Generic.List<PlanItem>{goal,month,week,day};
        check(!PlanRules.Complete(goal,plans),"Completed daily task does not implicitly complete an ancestor");
        day.Done=false;check(!PlanRules.Complete(goal,plans),"Reopening daily task updates ancestor completion");
        check(PlanRules.Week(new DateTime(2026,11,1))==new DateTime(2026,10,26),"Planner weeks start Monday across month boundaries");
        day.Date=new DateTime(2026,11,2);check(PlanRules.InPeriod(day,3,day.Date) && !PlanRules.InPeriod(day,3,day.Date.AddDays(1)),"Daily plans remain on their assigned date");
        month.Date=new DateTime(2026,12,31);check(PlanRules.InPeriod(month,1,new DateTime(2026,12,1)) && !PlanRules.InPeriod(month,1,new DateTime(2027,12,1)),"Monthly filters distinguish years");
        var plannerStore=new StorageService(Path.Combine(root,"planner-persistence"));
        plannerStore.Save(new AppState{Plans=plans,PlannerLevel=1});var restored=plannerStore.Load();
        check(restored.Plans.Count==4 && restored.Plans[3].ParentId==week.Id && restored.Plans[3].Notes=="Notes" && restored.PlannerLevel==1,"Plan hierarchy, notes and selected level survive storage reload");
        check(new AppState().Plans.Count==0 && new AppState().PlannerLevel==3,"Existing settings default to empty daily planner");
        check(new AppState().SelectedPage=="Performance","New settings default to Performance without modifying existing pins or theme");
        window.ExpandPanel();window.GoHome();window.SelectPage("Performance");window.EnablePerformanceForVerification();window.EnableMemoryForVerification();
        check(!window.IsFilesPage && window.PerformanceSection.Visibility==Visibility.Visible && window.PinsSection.Visibility==Visibility.Collapsed && window.FileSearchSurface.Visibility==Visibility.Collapsed,
            "Performance tab hides file controls and gives metrics an independent page");
        check(window.PerformancePageTab.IsChecked==true && window.FilesPageTab.IsChecked==false,"Exactly one navigation tab is selected");
        window.PerformancePageTab.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check(!window.IsFilesPage && window.PerformancePageTab.IsChecked==true,"Clicking the current tab leaves the page expanded and selected");
        window.CollapsePanel();window.ExpandPanel();
        check(window.IsVisible && !window.IsFilesPage,"Collapse and reveal retain the performance page");
        window.BeginCaptureShortcut();window.SelectPage("Files",false);
        check(window.CaptureInteractionHeld && !window.IsFilesPage,"Screenshot selection prevents side tabs from switching pages");
        window.CancelCaptureShortcutPending();
        window.FilesPageTab.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));window.UpdateLayout();
        check(!window.CaptureInteractionHeld && window.IsFilesPage,"Side tabs become usable after screenshot selection ends");
        check(window.IsFilesPage && window.PinsSection.Visibility==Visibility.Visible && window.PerformanceSection.Visibility==Visibility.Collapsed && window.FileSearchSurface.Visibility==Visibility.Visible,"File tab shows favorites and search without performance cards");
        check(window.Storage.Load().SelectedPage=="Files","Last selected page survives settings reload");
        check(window.PlannerSection.Visibility==Visibility.Collapsed && window.PinsList.ActualHeight>300,"File favorites use the full file page without a planner reservation");
        CountdownVerification.Run(window,root,check);PlannerVerification.Run(window,root,check);
        await VerifyPlannerPage(window,root,check);window.SelectPage("Files",false);
        string folder=Path.Combine(root,"page-switch-files");Directory.CreateDirectory(folder);
        for(int i=0;i<50;i++)File.WriteAllText(Path.Combine(folder,$"item-{i:00}.txt"),"fixture");
        await window.NavigateToFolderAsync(folder);window.SearchBox.Text="item";
        await window.WaitForFolderSearchAsync();
        window.UpdateLayout();var scroll=Find<ScrollViewer>(window.FolderList)!;scroll.ScrollToVerticalOffset(12);window.UpdateLayout();double offset=scroll.VerticalOffset;
        window.SelectFile((EntryView)window.FolderList.Items[2],false);
        var entries=window.FolderList.ItemsSource;
        window.SelectPage("Planner",false);window.SelectPage("Performance",false);await Task.Delay(1200);
        check(window.CurrentFolderPath==folder && window.SearchBox.Text=="item" && ReferenceEquals(entries,window.FolderList.ItemsSource),"Switching to performance preserves folder results and search text");
        check(window.PerformanceActive && !window.MemoryOnlyActive && !window.FolderWatcherActive,"Performance page samples metrics even when a folder is retained, without a hidden folder watcher");
        window.SelectPage("Files",false);window.UpdateLayout();await Task.Delay(150);
        check(window.BrowserPanel.Visibility==Visibility.Visible && window.SearchBox.Text=="item" && window.FolderList.Items.Count==50 && window.SelectedFileCount==1,"Returning to files restores folder view, results and selection");
        check(Math.Abs(scroll.VerticalOffset-offset)<1,"Folder scroll position survives a page round trip");
        await Task.Delay(700);int samples=window.PerformanceSamples;await Task.Delay(1200);
        check(!window.PerformanceActive && window.MemoryOnlyActive && window.PerformanceSamples==samples,"File page pauses full hardware sampling and keeps only lightweight memory checks");
        window.LiveFolderUpdatesEnabled=true;window.SelectPage("Performance",false);
        string added=Path.Combine(folder,"item-added.txt");File.WriteAllText(added,"new while away");
        window.SelectPage("Files",false);
        for(int i=0;i<100 && window.FolderList.Items.Count!=51;i++)await Task.Delay(50);
        check(window.FolderList.Items.Count==51 && window.FolderWatcherActive,"Returning from performance refreshes changes made while folder monitoring was paused");
        window.LiveFolderUpdatesEnabled=false;window.GoHome();
        string side=window.State.DockSide,theme=window.State.ThemeMode;
        window.Width=430;window.Height=650;
        foreach(string nextSide in new[]{"Right","Left"})
        {
            window.State.DockSide=nextSide;window.UpdatePageRail();window.UpdateLayout();
            bool right=nextSide=="Left";
            check(window.PageTabsWindow!=null && window.PageTabsWindow.Owner==window && Grid.GetColumn(window.PanelSurface)==0 && Math.Abs(window.PanelSurface.ActualWidth-430)<1,
                $"Side tabs mirror on {nextSide} dock without narrowing the original content");
            window.SelectPage("Files",false);window.UpdateLayout();window.UpdatePageRail();
            IntPtr region=CreateRectRgn(0,0,0,0);IntPtr handle=new WindowInteropHelper(window).Handle;double scale=NativeMethods.WindowDpi(handle)/96d;
            await Task.Delay(100);
            int borderStatus=DwmGetWindowAttribute(handle,34,out int borderColor,sizeof(int));
            check(borderStatus<0 || borderColor==unchecked((int)0xFFFFFFFE),"Supported DWM border rendering stays suppressed after layout settles");
            check(Window.GetWindow(window.PageJoin)==window.PageTabsWindow && !window.PageJoin.IsHitTestVisible && window.PageJoin.Height==44,"Selected tab joins the body without an interactive seam overlay");
            var tabBorder=(Border)window.FilesPageTab.Template.FindName("TabSurface",window.FilesPageTab);
            check(right ? tabBorder.CornerRadius.TopLeft==0 && tabBorder.CornerRadius.TopRight==8 : tabBorder.CornerRadius.TopRight==0 && tabBorder.CornerRadius.TopLeft==8,"Tab connection has square inner corners and rounded outer corners");
            int P(double p)=>(int)Math.Round(p*scale);int x=P(right ? window.ActualWidth+2:-2);
            try{check(GetWindowRgn(handle,region)>0 && !PtInRegion(region,x,P(112)) && PtInRegion(region,P(window.ActualWidth/2),P(112)),
                $"Main window shape excludes the separate side tabs on {nextSide} dock");}finally{DeleteObject(region);}
            var bodyBounds=window.PanelSurface.PointToScreen(new System.Windows.Point());
            var mainHandle=new WindowInteropHelper(window).Handle;var railHandle=new WindowInteropHelper(window.PageTabsWindow!).Handle;
            GetWindowRect(mainHandle,out var mainRect);GetWindowRect(railHandle,out var railRect);
            check(mainHandle!=railHandle && Math.Abs(mainRect.Left-bodyBounds.X)<=1 && Math.Abs(mainRect.Right-mainRect.Left-window.PanelSurface.ActualWidth*scale)<=1,"Capture HWND rectangle exactly matches main body and has a separate rail HWND");
            check(right ? Math.Abs(railRect.Left-mainRect.Right+P(2))<=1 : Math.Abs(railRect.Right-mainRect.Left-P(2))<=1,"Separate tabs attach to the main body on the correct side");
            window.SelectPage("Planner",false);window.UpdateLayout();
            check(Math.Abs(window.PageTabsWindow!.ActualHeight-148)<1 && window.PageJoin.Margin.Top==52,"Rail height and selected seam derive from all three tabs");
            IntPtr railRegion=CreateRectRgn(0,0,0,0);
            try{check(GetWindowRgn(railHandle,railRegion)>0 && PtInRegion(railRegion,P(16),P(126)) && !PtInRegion(railRegion,P(16),P(48)) && !PtInRegion(railRegion,P(16),P(100)),"Rail shape contains the third tab and leaves both gaps transparent");}finally{DeleteObject(railRegion);}
            window.BeginCaptureShortcut();check(window.PageTabsWindow!.IsVisible && !window.PageTabsWindow!.IsHitTestVisible,"Screenshot leaves side icons visible while blocking tab clicks");window.CancelCaptureShortcutPending();
            window.CollapsePanel();check(!window.PageTabsWindow!.IsVisible,"Collapsed main panel hides its attached tabs");window.ExpandPanel();check(window.PageTabsWindow!.IsVisible,"Revealed main panel restores its attached tabs");
            double oldLeft=window.Left,oldTop=window.Top;window.Left+=18;window.Top+=12;window.UpdateLayout();GetWindowRect(railHandle,out var movedRail);check(Math.Abs(movedRail.Left-railRect.Left-P(18))<=1 && Math.Abs(movedRail.Top-railRect.Top-P(12))<=1,"Separate rail follows both axes of panel movement");window.Left=oldLeft;window.Top=oldTop;
            long railStyle=NativeMethods.GetExtendedWindowStyle(railHandle);check((railStyle&0x80)!=0 && (railStyle&0x40000)==0,"Attached rail stays out of taskbar and Alt-Tab");
            var escape=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window.PageTabsWindow!)!,Environment.TickCount,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};window.PageTabsWindow!.RaiseEvent(escape);check(escape.Handled && !window.IsVisible,"Escape in the attached rail reaches main panel commands");window.ExpandPanel();
            check(!MainWindow.IsMovementSurface(window.FilesPageTab) && !MainWindow.IsMovementSurface(window.PanelResizeGrip),"Navigation and resize controls cannot accidentally drag the panel");
            foreach(string nextTheme in new[]{"Light","Dark"})
            {
                window.SetThemeMode(nextTheme);
                window.SelectPage("Files",false);DemoVerification.SaveRender(window,Path.Combine(root,$"pages-files-{nextSide}-{nextTheme}.png"));
                window.SelectPage("Planner",false);window.UpdateLayout();DemoVerification.SaveRender(window,Path.Combine(root,$"pages-planner-{nextSide}-{nextTheme}.png"));
                window.SelectPage("Performance",false);DemoVerification.SaveRender(window,Path.Combine(root,$"pages-performance-{nextSide}-{nextTheme}.png"));
            }
        }
        window.Width=380;window.Height=480;window.UpdateLayout();
        check(window.PerformanceScroll.ExtentHeight<=window.PerformanceScroll.ViewportHeight+1 && window.MemoryCard.CapacityFits && window.VramCard.CapacityFits,"Independent performance page fits the compact window with complete capacity labels");
        DemoVerification.SaveRender(window,Path.Combine(root,"pages-performance-small.png"));
        window.Width=430;window.Height=650;window.State.DockSide=side;window.SetThemeMode(theme);window.UpdatePageRail();window.SelectPage("Performance");
    }
}
