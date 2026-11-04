using System;
using System.Windows;
namespace FileDockDemo;
internal static class AnchorVerification
{
    internal static void Run(Action<bool,string> check)
    {
        var area=new Rect(0,0,1920,1080);
        check(MainWindow.InferDocked(new AppState(),area,380),"New panel position defaults to edge anchoring");
        check(MainWindow.InferDocked(new AppState{PanelLeft=1490,PanelTop=20},area,380),"Old near-edge coordinates migrate to anchored placement");
        check(MainWindow.DockedLeft("Right",area,380)+380==area.Right && MainWindow.DockedLeft("Right",area,480)+480==area.Right,"Right dock stays flush for different panel widths");
        check(MainWindow.DockedLeft("Left",new Rect(-1920,0,1920,1080),380)==-1920,"Left docking respects negative-coordinate working areas");
        check(!MainWindow.InferDocked(new AppState{PanelLeft=700,PanelTop=200},area,380),"Old floating positions remain free");
        check(!MainWindow.InferDocked(new AppState{PanelDocked=false,PanelLeft=1530,PanelTop=200},area,380),"Explicit free placement is retained near an edge");
    }
}
