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
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int a,int b,int c,int d);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr region);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr region,int x,int y);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window,IntPtr region);
    private static T? Find<T>(DependencyObject parent) where T:DependencyObject
    {
        if(parent is T match)return match;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            if(Find<T>(VisualTreeHelper.GetChild(parent,i)) is T child)return child;
        return null;
    }
    internal static async Task Run(MainWindow window,string root,Action<bool,string> check)
    {
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
        check(window.PinsSection.ActualHeight>350,"Favorites receive the full available file-page height");
        string folder=Path.Combine(root,"page-switch-files");Directory.CreateDirectory(folder);
        for(int i=0;i<50;i++)File.WriteAllText(Path.Combine(folder,$"item-{i:00}.txt"),"fixture");
        await window.NavigateToFolderAsync(folder);window.SearchBox.Text="item";
        await window.WaitForFolderSearchAsync();
        window.UpdateLayout();var scroll=Find<ScrollViewer>(window.FolderList)!;scroll.ScrollToVerticalOffset(12);window.UpdateLayout();double offset=scroll.VerticalOffset;
        window.SelectFile((EntryView)window.FolderList.Items[2],false);
        var entries=window.FolderList.ItemsSource;
        window.SelectPage("Performance",false);await Task.Delay(1200);
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
        window.Width=462;window.Height=650;
        foreach(string nextSide in new[]{"Right","Left"})
        {
            window.State.DockSide=nextSide;window.UpdatePageRail();window.UpdateLayout();
            bool right=nextSide=="Left";
            check(Grid.GetColumn(window.PageRail)==(right ? 1:0) && Grid.GetColumn(window.PanelSurface)==(right ? 0:1) && Math.Abs(window.PanelSurface.ActualWidth-430)<1,
                $"Side tabs mirror on {nextSide} dock without narrowing the original content");
            window.SelectPage("Files",false);window.UpdateLayout();window.UpdatePageRail();
            IntPtr region=CreateRectRgn(0,0,0,0);IntPtr handle=new WindowInteropHelper(window).Handle;double scale=NativeMethods.WindowDpi(handle)/96d;
            int P(double p)=>(int)Math.Round(p*scale);int x=P(right ? window.ActualWidth-2:2);
            try{check(GetWindowRgn(handle,region)>0 && PtInRegion(region,x,P(112)) && !PtInRegion(region,x,P(70)) && !PtInRegion(region,x,P(200)),
                $"Native window shape includes side tabs and excludes empty gaps on {nextSide} dock");}finally{DeleteObject(region);}
            check(!MainWindow.IsMovementSurface(window.FilesPageTab) && !MainWindow.IsMovementSurface(window.PanelResizeGrip),"Navigation and resize controls cannot accidentally drag the panel");
            foreach(string nextTheme in new[]{"Light","Dark"})
            {
                window.SetThemeMode(nextTheme);
                window.SelectPage("Files",false);DemoVerification.SaveRender(window,Path.Combine(root,$"pages-files-{nextSide}-{nextTheme}.png"));
                window.SelectPage("Performance",false);DemoVerification.SaveRender(window,Path.Combine(root,$"pages-performance-{nextSide}-{nextTheme}.png"));
            }
        }
        window.Width=412;window.Height=480;window.UpdateLayout();
        check(window.PerformanceScroll.ExtentHeight<=window.PerformanceScroll.ViewportHeight+1 && window.MemoryCard.CapacityFits && window.VramCard.CapacityFits,"Independent performance page fits the compact window with complete capacity labels");
        DemoVerification.SaveRender(window,Path.Combine(root,"pages-performance-small.png"));
        window.Width=462;window.Height=650;window.State.DockSide=side;window.SetThemeMode(theme);window.UpdatePageRail();window.SelectPage("Performance");
    }
}