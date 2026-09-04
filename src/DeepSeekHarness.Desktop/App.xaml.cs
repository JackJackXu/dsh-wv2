using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace DeepSeekHarness.Desktop;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\DSH_WV2_SingleInstance";
    private const string ActivateEventName = @"Local\DSH_WV2_Activate";
    private Mutex? _mutex;
    private EventWaitHandle? _activate;

    private static readonly string LogDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSH WV2", "logs");
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
        if (!createdNew)
        {
            // Another instance is running: tell it to show/activate, then exit
            // (instead of silently doing nothing, which looks broken).
            try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); } catch { /* ignore */ }
            Shutdown();
            return;
        }
        StartActivateListener();

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

    private void StartActivateListener()
    {
        try
        {
            _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        }
        catch (Exception ex) { Log("activate listener: " + ex.Message); return; }
        var t = new System.Threading.Thread(() =>
        {
            while (_activate.WaitOne())
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (MainWindow is MainWindow win) win.ShowMain();
                });
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    internal static void Log(object message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            RotateIfLarge();
            File.AppendAllText(CrashLog, $"[{DateTime.Now:O}] {message}" + System.Environment.NewLine);
        }
        catch { /* logging must never throw */ }
    }

    // Keep error.log bounded (~5MB): on overflow keep the recent half.
    private static void RotateIfLarge()
    {
        var fi = new FileInfo(CrashLog);
        if (!fi.Exists || fi.Length <= 5 * 1024 * 1024) return;
        var all = File.ReadAllText(CrashLog);
        int mid = all.Length / 2;
        int start = all.IndexOf(System.Environment.NewLine, mid);
        if (start < 0) start = mid;
        File.WriteAllText(CrashLog, all.Substring(start + 1));
    }

    private static void TryShow(string text)
    {
        try { System.Windows.MessageBox.Show(text, "dsh-wv2", MessageBoxButton.OK, MessageBoxImage.Error); } catch { /* ignore */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { /* ignore */ }
        try { _activate?.Dispose(); } catch { /* ignore */ }
        base.OnExit(e);
    }
}
