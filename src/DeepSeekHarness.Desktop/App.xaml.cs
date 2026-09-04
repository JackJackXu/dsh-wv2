using System.Windows;

namespace DeepSeekHarness.Desktop;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\DSH_WV2_SingleInstance";
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance already owns the tray/session; don't stack a second.
            Shutdown();
            return;
        }
        var win = new MainWindow();
        MainWindow = win;
        win.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { /* ignore */ }
        base.OnExit(e);
    }
}
