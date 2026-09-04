using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace DeepSeekHarness.Desktop;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\DSH_WV2_SingleInstance";
    private Mutex? _mutex;

    private static readonly string LogDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSH WV2");
    private static readonly string CrashLog = Path.Combine(LogDir, "error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Log("dispatcher: " + args.Exception);
            TryShow("运行出错（已记录到 error.log）：\n" + args.Exception.Message);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log("appdomain: " + args.ExceptionObject);
        };

        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew) { Shutdown(); return; }

        try
        {
            var win = new MainWindow();
            MainWindow = win;
            win.Show();
        }
        catch (Exception ex)
        {
            Log("startup: " + ex);
            TryShow("启动失败：\n" + ex);
            Shutdown();
        }
    }

    internal static void Log(object message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(CrashLog, $"[{DateTime.Now:O}] {message}\n");
        }
        catch { /* logging must never throw */ }
    }

    private static void TryShow(string text)
    {
        try { System.Windows.MessageBox.Show(text, "dsh-wv2", MessageBoxButton.OK, MessageBoxImage.Error); } catch { /* ignore */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { /* ignore */ }
        base.OnExit(e);
    }
}
