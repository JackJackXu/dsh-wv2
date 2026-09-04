using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;

namespace DeepSeekHarness.Desktop;

public partial class MainWindow
{
    private const int HotKeyId = 0xD5E1;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint VkD = 0x44;

    private bool _hotkeyHooked;
    private int _recoveryTries;
    private string _currentUrl = "";

    partial void ShellReady()
    {
        WireRecovery(webView.CoreWebView2);
        WireHotKey();
        WireWake();
        StartTaskNotifications();
        Closed += (_, _) =>
        {
            UnregisterHotKey();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        };
    }

    // ---------- recovery: renderer crash -> reload; load failure -> backoff ----------
    private void WireRecovery(CoreWebView2 cwv)
    {
        cwv.ProcessFailed += (_, e) =>
        {
            App.Log("webview process failed: " + e.ProcessFailedKind);
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(_currentUrl)) cwv.Navigate(_currentUrl);
                    else if (_webReady) cwv.Reload();
                }
                catch { /* ignore */ }
            });
        };

        cwv.NavigationStarting += (_, e) =>
        {
            try { _currentUrl = e.Uri; } catch { /* ignore */ }
        };

        cwv.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess) { _recoveryTries = 0; return; }
            if (_recoveryTries >= 5)
            {
                App.Log("navigation giving up after " + _recoveryTries + " tries");
                return;
            }
            _recoveryTries++;
            int delay = Math.Min(8000, 1000 * (1 << (_recoveryTries - 1)));
            App.Log("navigation failed (" + e.WebErrorStatus + ") retry#" + _recoveryTries + " in " + delay + "ms");
            ScheduleRetry(delay);
        };
    }

    private void ScheduleRetry(int ms)
    {
        var _ = System.Threading.Tasks.Task.Delay(ms).ContinueWith(_ =>
            Dispatcher.InvokeAsync(() =>
            {
                if (_webReady)
                {
                    try { webView.CoreWebView2.Navigate(_currentUrl); } catch { /* ignore */ }
                }
            }), System.Threading.Tasks.TaskScheduler.Default);
    }

    // ---------- global hotkey Ctrl+Alt+D ----------
    private void WireHotKey()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        _hotkeyHooked = RegisterHotKey(handle, HotKeyId, ModControl | ModAlt, VkD);
        App.Log("hotkey Ctrl+Alt+D registered: " + _hotkeyHooked);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotKeyId)
        {
            ToggleWindow();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ToggleWindow()
    {
        if (IsVisible && WindowState != System.Windows.WindowState.Minimized) Hide();
        else ShowMain();
    }

    private void UnregisterHotKey()
    {
        if (!_hotkeyHooked) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) UnregisterHotKey(handle, HotKeyId);
        _hotkeyHooked = false;
    }

    // ---------- wake / resume ----------
    private void WireWake()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (_webReady)
            {
                try { webView.CoreWebView2.Reload(); } catch { /* ignore */ }
            }
        });
    }

    // ---------- open a terminal in the shared dsh dir ----------
    public void OpenTerminal()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        try
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                WorkingDirectory = dir,
                UseShellExecute = false,
            });
        }
        catch (Exception ex) { App.Log("open terminal: " + ex.Message); }
    }

    private void StartTaskNotifications()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "sessions");
        try
        {
            _notifWatcher = new Services.SessionWatcher(dir);
            _notifWatcher.TurnEnd += OnTurnEnd;
            _notifWatcher.Start();
            Closed += (_, _) => _notifWatcher?.Stop();
        }
        catch (Exception ex) { App.Log("notif watcher: " + ex.Message); }
    }

    private void OnTurnEnd(string title, string body)
    {
        if (!_settings.NotificationsEnabled) return;
        // Always notify on task completion (front window or not). ShowBalloonTip
        // must run on the UI thread; OnTurnEnd fires from the watcher thread.
        Dispatcher.InvokeAsync(() =>
        {
            _tray?.ShowBalloonTip(4000, "任务完成：" + title, string.IsNullOrEmpty(body) ? "点击查看" : body, System.Windows.Forms.ToolTipIcon.Info);
        });
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
