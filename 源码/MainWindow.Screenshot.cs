using System;
using System.Windows;
using System.Windows.Controls;
using FileDockDemo.Features.Screenshot;

namespace FileDockDemo;

public partial class MainWindow
{
    private bool _ownScreenshotActive;

    private void BuildScreenshotOptions(Panel panel)
    {
        panel.Children.Add(Ui.Text("截图",18));
        var include = new CheckBox {
            Content = "截图包含文件坞",
            IsChecked = State.IncludeDockInScreenshot,
            Margin = new Thickness(0,8,0,8)
        };
        include.SetResourceReference(Control.ForegroundProperty,"Ink");
        include.Click += (_,_) => {
            State.IncludeDockInScreenshot = include.IsChecked == true;
            Persist();
        };
        panel.Children.Add(include);
        panel.Children.Add(Ui.Button("快捷键设置…", () => OpenShortcutSettings(ShortcutCatalog.Screenshot)));
    }

    private void RunScreenshot() => RunScreenshot(ScreenshotSession.Run);

    internal void RunScreenshot(Func<Window,Action,Action,string> capture)
    {
        if (_ownScreenshotActive || _exitSaving || _quitting || _inDialog || _windowPicker != null ||
            _dragging || _movingPanel || MemoryPaused || UpdateCaptureVisibility()) return;

        bool panelVisible = IsVisible;
        bool edgeVisible = _dock?.IsVisible == true;
        bool panelActive = IsActive;

        void Prepare()
        {
            _inDialog = _ownScreenshotActive = true;
            if (State.IncludeDockInScreenshot) return;
            if (panelVisible) Hide();
            _dock?.Hide();
        }

        void Restore()
        {
            _inDialog = _ownScreenshotActive = false;
            if (_quitting || Dispatcher.HasShutdownStarted) return;
            if (panelVisible && !IsVisible) Show();
            if (edgeVisible && _dock?.IsVisible == false) _dock.Show();
            if (panelActive && IsVisible) Activate();
            UpdatePageRail();
            QueueDesktopPresence();
        }

        try { SetStatus(capture(this, Prepare, Restore)); }
        catch (Exception error) {
            SetStatus("截图未完成：" + error.Message, true);
            if (!IsVisible && _tray != null) {
                _tray.BalloonTipTitle = "截图未完成";
                _tray.BalloonTipText = error.Message;
                _tray.ShowBalloonTip(2200);
            }
        }
    }
}
