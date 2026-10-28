using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;

namespace FileDockDemo;
internal static class MovementVerification
{
    internal static void Run(MainWindow window, string root, Action<bool,string> check)
    {
        check(!window.AllowsTransparency && WindowChrome.GetWindowChrome(window)?.ResizeBorderThickness.Left == 6,
            "Panel uses native window composition with a resize border");
        check(window.PinsSection.Children.OfType<TextBlock>().Any(x => x.Text == "我的常用" && MainWindow.IsMovementSurface(x)) &&
            window.PerformanceSection.Children.OfType<TextBlock>().Any(x => x.Text == "电脑状态" && MainWindow.IsMovementSurface(x)),
            "Section headings provide additional movement surfaces");
        check(!MainWindow.IsMovementSurface(window.SearchBox) && !MainWindow.IsMovementSurface(window.SettingsButton) &&
            !MainWindow.IsMovementSurface(window.PinsList), "Search buttons and file lists keep their original pointer interactions");
        var area = new Rect(-1920,0,1920,1080);
        check(MainWindow.ConstrainPanel(new Rect(-1600,200,430,650),area) == new Rect(-1600,200,430,650),
            "Free panel position is retained on a negative-coordinate display");
        check(MainWindow.ConstrainPanel(new Rect(4000,3000,430,650),new Rect(0,0,1920,1080)) == new Rect(1490,430,430,650),
            "Unavailable display positions return fully inside the working area");
        var storage = new StorageService(Path.Combine(root,"movement-data"));
        storage.Save(new AppState { PanelLeft=-1600, PanelTop=200, PanelScreen="test-secondary" });
        var saved = storage.Load();
        check(saved.PanelLeft == -1600 && saved.PanelTop == 200 && saved.PanelScreen == "test-secondary",
            "Free panel position and display identity survive configuration reload");
    }
}
