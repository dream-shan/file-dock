using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;

namespace FileDockDemo;

public sealed class App : Application
{
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showRegistration;
    public static bool IsSelfTest { get; private set; }
    public static string VersionLabel => "文件坞 " +
        (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "未知版本") + " · 开源候选版";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--launch-probe") { File.WriteAllText(args[1], Environment.CurrentDirectory); return 0; }
        if (args.Length == 2 && args[0] == "--memory-trim-probe") return MemoryTools.RunProbe(args[1]);
        IsSelfTest = args.Length > 0 && args[0] == "--self-test";
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            DiagnosticLog.Error("dispatcher-exception", e.Exception);
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
        bool diagnosticReady = DiagnosticLog.Initialize(dataDirectory);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => {
            DiagnosticLog.Write("fatal-state", $"terminating={e.IsTerminating}");
            DiagnosticLog.Error("unhandled-exception", e.ExceptionObject as Exception);
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DiagnosticLog.Write("process-exit", $"exitCode={Environment.ExitCode}");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => DiagnosticLog.Error("unobserved-task-exception", e.Exception);
        app.SessionEnding += (_, e) => DiagnosticLog.Write("session-ending", e.ReasonSessionEnding.ToString());
        app.Exit += (_, e) => DiagnosticLog.Write("application-exit", $"exitCode={e.ApplicationExitCode}");
        MainWindow window;
        try { window = new MainWindow(dataDirectory); }
        catch (Exception ex)
        {
            DiagnosticLog.Error("startup-failure", ex);
            if (IsSelfTest) File.WriteAllText(Path.Combine(Path.GetFullPath(args[1]), "failure.txt"), ex.ToString());
            else MessageBox.Show("无法启动文件坞：" + ex.Message + "\n\n请检查此目录中的配置及写入权限：\n" + dataDirectory, "文件坞", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
        app.MainWindow = window;
        if (!diagnosticReady) window.SetStatus("本地诊断记录暂不可写，其他功能仍可使用。", true);
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
            // Preserve legacy singleton names so renamed releases cannot run alongside older versions.
            if (!StartupRegistration.MigrateLegacyPath())
                window.SetStatus("旧开机启动路径未能更新，请在设置中重新开启开机启动。");
            app._showRegistration = ThreadPool.RegisterWaitForSingleObject(app._showEvent!, (_, _) => app.Dispatcher.BeginInvoke(() => { DiagnosticLog.Write("existing-instance-activation"); window.ExpandPanel(); }), null, -1, false);
            app.Startup += (_, _) => window.Show();
        }
        int result = app.Run();
        DiagnosticLog.Write("message-loop-return", $"exitCode={result}");
        window.DisposeResources();
        app._showRegistration?.Unregister(null);
        app._showEvent?.Dispose();
        app._instanceMutex?.ReleaseMutex();
        app._instanceMutex?.Dispose();
        return result;
    }
}
