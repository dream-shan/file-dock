using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace FileDockDemo;

public sealed class App : Application
{
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showRegistration;
    public static bool IsSelfTest { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--launch-probe") { File.WriteAllText(args[1], Environment.CurrentDirectory); return 0; }
        IsSelfTest = args.Length > 0 && args[0] == "--self-test";
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            if (IsSelfTest) return;
            MessageBox.Show("文件坞遇到问题：\n" + e.Exception.Message, "文件坞", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        };
        if (!IsSelfTest)
        {
            app._instanceMutex = new Mutex(true, "Local\\FileDockDemo.0.1", out bool first);
            app._showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\FileDockDemo.Show.0.1");
            if (!first) { app._showEvent.Set(); app._showEvent.Dispose(); app._instanceMutex.Dispose(); return 0; }
        }
        string dataDirectory = IsSelfTest && args.Length > 1 ? Path.Combine(Path.GetFullPath(args[1]), "data") : Path.Combine(AppContext.BaseDirectory, "Data");
        MainWindow window;
        try { window = new MainWindow(dataDirectory); }
        catch (Exception ex)
        {
            if (IsSelfTest) File.WriteAllText(Path.Combine(Path.GetFullPath(args[1]), "failure.txt"), ex.ToString());
            else MessageBox.Show("无法启动文件坞：" + ex.Message + "\n\n请检查此目录中的配置及写入权限：\n" + dataDirectory, "文件坞", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
        app.MainWindow = window;
        if (IsSelfTest)
        {
            window.ShowActivated = false;
            window.Left = -10000;
            window.Top = -10000;
            app.Startup += async (_, _) =>
            {
                try { await DemoVerification.Run(window, Path.GetFullPath(args[1])); app.Shutdown(0); }
                catch (Exception ex) { File.WriteAllText(Path.Combine(Path.GetFullPath(args[1]), "failure.txt"), ex.ToString()); app.Shutdown(1); }
            };
        }
        else
        {
            app._showRegistration = ThreadPool.RegisterWaitForSingleObject(app._showEvent!, (_, _) => app.Dispatcher.BeginInvoke(window.ExpandPanel), null, -1, false);
            app.Startup += (_, _) => window.Show();
        }
        int result = app.Run();
        window.DisposeResources();
        app._showRegistration?.Unregister(null);
        app._showEvent?.Dispose();
        app._instanceMutex?.ReleaseMutex();
        app._instanceMutex?.Dispose();
        return result;
    }
}
