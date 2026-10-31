using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace FileDockDemo;
public partial class MainWindow
{
    internal bool IsFilesPage => State.SelectedPage == "Files";
    private bool _pagesReady;
    private double _fileScrollOffset;
    private void InitializePages()
    {
        State.SelectedPage = State.SelectedPage == "Files" ? "Files" : "Performance";
        _pagesReady = true;
        SizeChanged += (_, _) => UpdatePageRail();
        Loaded += (_, _) => UpdatePageRail();
        UpdatePageLayout();
    }
    private void PageTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string page }) SelectPage(page);
    }
    internal void SelectPage(string page, bool persist = true)
    {
        if (!_pagesReady || _captureInteractionHeld || _dragging || _movingPanel) return;
        page = page == "Files" ? "Files" : "Performance";
        bool changed = State.SelectedPage != page;
        if (changed && IsFilesPage) _fileScrollOffset = FindFileScroll()?.VerticalOffset ?? 0;
        State.SelectedPage = page;
        UpdatePageLayout();
        if (changed)
        {
            if (IsFilesPage) SearchBox.Focus(); else PerformancePageTab.Focus();
            SetStatus(IsFilesPage ? "文件夹内浏览 · 文件单击打开" : "实时电脑状态");
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
        FilesPageTab.IsChecked = IsFilesPage; PerformancePageTab.IsChecked = !IsFilesPage;
        FileSearchSurface.Visibility = IsFilesPage ? Visibility.Visible : Visibility.Collapsed;
        PinsSection.Visibility = IsFilesPage && CurrentFolderPath == null ? Visibility.Visible : Visibility.Collapsed;
        BrowserPanel.Visibility = IsFilesPage && CurrentFolderPath != null ? Visibility.Visible : Visibility.Collapsed;
        PerformanceSection.Visibility = IsFilesPage ? Visibility.Collapsed : Visibility.Visible;
        UpdatePerformanceActivity(); UpdateFolderWatcher(); UpdatePageRail();
        if (!IsFilesPage) InteractionHint.Text="电脑状态 · 点击内存整理"; else UpdateFileSelection();
    }
    internal void UpdatePageRail()
    {
        if (!_pagesReady) return;
        bool right = State.DockSide == "Left";
        PageRoot.ColumnDefinitions[0].Width = right ? new GridLength(1,GridUnitType.Star) : new GridLength(32);
        PageRoot.ColumnDefinitions[1].Width = right ? new GridLength(32) : new GridLength(1,GridUnitType.Star);
        Grid.SetColumn(PanelSurface,right ? 0 : 1); Grid.SetColumn(PanelResizeGrip,right ? 0 : 1); Grid.SetColumn(PageRail,right ? 1 : 0);
        FilesPageTab.HorizontalAlignment = PerformancePageTab.HorizontalAlignment = right ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Grid.SetColumn(PageJoin,right ? 0 : 1);
        PageJoin.HorizontalAlignment=right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        PageJoin.Margin=right ? new Thickness(0,IsFilesPage ? 90 : 142,-2,0) : new Thickness(-2,IsFilesPage ? 90 : 142,0,0);
        foreach(var tab in new[]{FilesPageTab,PerformancePageTab})
        {
            tab.ApplyTemplate();
            if(tab.Template.FindName("TabSurface",tab) is Border border)
                border.CornerRadius=right ? new CornerRadius(0,8,8,0) : new CornerRadius(8,0,0,8);
        }
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if(handle!=IntPtr.Zero)ApplyNativeCorners(handle);
        if (handle != IntPtr.Zero) NativeMethods.ShapePagePanel(handle,ActualWidth > 0 ? ActualWidth : Width,ActualHeight > 0 ? ActualHeight : Height,right,IsFilesPage);
    }
    private void PanelResizeGrip_DragDelta(object sender,DragDeltaEventArgs e)
    {
        if (_captureInteractionHeld) return;
        Width = Math.Clamp(ActualWidth+e.HorizontalChange,MinWidth,double.IsFinite(MaxWidth) ? Math.Max(MinWidth,MaxWidth) : 4000);
        Height = Math.Clamp(ActualHeight+e.VerticalChange,MinHeight,double.IsFinite(MaxHeight) ? Math.Max(MinHeight,MaxHeight) : 4000);
    }
}