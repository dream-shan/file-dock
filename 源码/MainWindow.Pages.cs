using System.Linq;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace FileDockDemo;
public partial class MainWindow
{
    internal bool IsFilesPage => State.SelectedPage == "Files";
    internal bool IsPlannerPage=>State.SelectedPage=="Planner";
    internal bool IsPerformancePage=>State.SelectedPage=="Performance";
    private ToggleButton[] PageButtons=>PageRail.Children.OfType<ToggleButton>().ToArray();
    private int SelectedPageIndex=>Math.Max(0,Array.FindIndex(PageButtons,b=>b.Tag as string==State.SelectedPage));
    private bool _pagesReady;
    private PageRailWindow? _pageRailWindow;
    internal PageRailWindow? PageTabsWindow=>_pageRailWindow;
    private double _fileScrollOffset;
    private void InitializePages()
    {
        State.SelectedPage = NormalizePage(State.SelectedPage);
        PageRoot.Children.Remove(PageRail);PageRoot.Children.Remove(PageJoin);
        _pageRailWindow=new PageRailWindow(this,PageRail,PageJoin);
        IsVisibleChanged+=(_,_)=>UpdatePageRail();LocationChanged+=(_,_)=>_pageRailWindow?.Sync(State.DockSide=="Left",SelectedPageIndex,PageButtons.Length);
        _pagesReady = true;
        SizeChanged += (_, _) => UpdatePageRail();
        Loaded += (_, _) => UpdatePageRail();
        UpdatePageLayout();
    }
    private string NormalizePage(string page)=>PageButtons.Any(b=>b.Tag as string==page)?page:"Performance";
    internal void HandleRailKey(System.Windows.Input.KeyEventArgs e)=>Window_PreviewKeyDown(this,e);
    private void PageTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string page }) SelectPage(page);
    }
    internal void SelectPage(string page, bool persist = true)
    {
        if (!_pagesReady || _captureInteractionHeld || _dragging || _movingPanel) return;
        page = NormalizePage(page);
        bool changed = State.SelectedPage != page;
        if (changed && IsFilesPage) _fileScrollOffset = FindFileScroll()?.VerticalOffset ?? 0;
        State.SelectedPage = page;
        UpdatePageLayout();
        if (changed)
        {
            Activate();if (IsFilesPage) SearchBox.Focus(); else if(IsPlannerPage)Planner.FocusAdd();else Focus();
            SetStatus(IsFilesPage ? "文件夹内浏览 · 文件单击打开" : IsPlannerPage ? "今日安排 · 点击编辑":"实时电脑状态");
            if (persist) Persist();
            if (IsFilesPage)
            {
                Dispatcher.BeginInvoke(() => FindFileScroll()?.ScrollToVerticalOffset(_fileScrollOffset));
                UpdateFolderWatcher(refresh:true);
            }
        }
    }
    private ScrollViewer? FindFileScroll()
    {
        foreach (var child in VisualChildren(CurrentFolderPath == null ? PinsList : FolderList))
            if (child is ScrollViewer scroll) return scroll;
        return null;
    }
    private void UpdatePageLayout()
    {
        if (!_pagesReady) return;
        foreach(var tab in PageButtons)tab.IsChecked=tab.Tag as string==State.SelectedPage;
        PlannerSection.Visibility=IsPlannerPage?Visibility.Visible:Visibility.Collapsed;
        FileSearchSurface.Visibility = IsFilesPage ? Visibility.Visible : Visibility.Collapsed;
        PinsSection.Visibility = IsFilesPage && CurrentFolderPath == null ? Visibility.Visible : Visibility.Collapsed;
        BrowserPanel.Visibility = IsFilesPage && CurrentFolderPath != null ? Visibility.Visible : Visibility.Collapsed;
        PerformanceSection.Visibility = IsPerformancePage ? Visibility.Visible : Visibility.Collapsed;
        UpdatePerformanceActivity(); UpdateFolderWatcher(); UpdatePageRail();
        if(IsPlannerPage)InteractionHint.Text="一次性／重复 · 点击编辑";else if(IsPerformancePage)InteractionHint.Text="电脑状态 · 点击内存整理";else UpdateFileSelection();
    }
    internal void UpdatePageRail()
    {
        if (!_pagesReady) return;
        bool right = State.DockSide == "Left";

        foreach(var tab in PageButtons)
        {
            tab.HorizontalAlignment=right?HorizontalAlignment.Left:HorizontalAlignment.Right;tab.ApplyTemplate();
            if(tab.Template.FindName("TabSurface",tab) is Border border)
                border.CornerRadius=right ? new CornerRadius(0,8,8,0) : new CornerRadius(8,0,0,8);
        }
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if(handle!=IntPtr.Zero)ApplyNativeCorners(handle);
        if (handle != IntPtr.Zero) NativeMethods.ShapePagePanel(handle,ActualWidth > 0 ? ActualWidth : Width,ActualHeight > 0 ? ActualHeight : Height);
        _pageRailWindow?.Sync(right,SelectedPageIndex,PageButtons.Length);
    }
    private void PanelResizeGrip_DragDelta(object sender,DragDeltaEventArgs e)
    {
        if (_captureInteractionHeld) return;
        Width = Math.Clamp(ActualWidth+e.HorizontalChange,MinWidth,double.IsFinite(MaxWidth) ? Math.Max(MinWidth,MaxWidth) : 4000);
        Height = Math.Clamp(ActualHeight+e.VerticalChange,MinHeight,double.IsFinite(MaxHeight) ? Math.Max(MinHeight,MaxHeight) : 4000);
    }
}
