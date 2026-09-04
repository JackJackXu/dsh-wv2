using System.IO;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;

namespace DeepSeekHarness.Desktop;

public partial class MainWindow
{
    private int _recoveryTries;
    private string _currentUrl = "";
    // health monitor state (stability: don't rely only on process-alive)
    private System.Threading.CancellationTokenSource? _healthCts;
    private bool _hadUrl;
    private int _healthPort;
    private int _unreachableCount;
    private bool _serviceLostHandled;
    private bool _autoRestarting;

    partial void ShellReady()
    {
        WireRecovery(webView.CoreWebView2);
        WireWake();
        StartTaskNotifications();
        StartHealthMonitor();
        Closed += (_, _) =>
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            try { _healthCts?.Cancel(); _healthCts?.Dispose(); _healthCts = null; } catch { /* ignore */ }
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
            if (e.IsSuccess)
            {
                _recoveryTries = 0;
                Dispatcher.InvokeAsync(() => { Overlay.Visibility = System.Windows.Visibility.Collapsed; });
                return;
            }
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
            _notifWatcher.ApprovalAsked += OnApprovalAsked;
            _notifWatcher.QuestionAsked += OnQuestionAsked;
            _notifWatcher.Start();
            Closed += (_, _) => _notifWatcher?.Stop();
        }
        catch (Exception ex) { App.Log("notif watcher: " + ex.Message); }
    }

    private void OnTurnEnd(string title, string body)
    {
        // Notify(): balloon when 通知 on, then classic blink after it closes;
        // classic blink immediately when 通知 off. Always until window focused.
        Dispatcher.InvokeAsync(() =>
            Notify("任务完成：" + title, string.IsNullOrEmpty(body) ? "点击查看" : body, System.Windows.Forms.ToolTipIcon.Info, 4000));
    }

    private void OnApprovalAsked(string toolName, string reason)
    {
        Dispatcher.InvokeAsync(() =>
        {
            string body = reason.Length > 0 ? reason : ("有一个工具请求需要批准");
            Notify("需要你的审批：" + toolName, body, System.Windows.Forms.ToolTipIcon.Warning, 6000);
        });
    }

    private void OnQuestionAsked(string title, string summary)
    {
        Dispatcher.InvokeAsync(() =>
            Notify(title, summary, System.Windows.Forms.ToolTipIcon.Warning, 6000));
    }

    // ---------- health check: probe the port, auto-restart with backoff ----------
    private void StartHealthMonitor()
    {
        _healthCts = new System.Threading.CancellationTokenSource();
        var ct = _healthCts.Token;
        var t = new System.Threading.Thread(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { System.Threading.Thread.Sleep(15_000); } catch { }
                if (ct.IsCancellationRequested) break;
                try { HealthTick(); } catch (Exception ex) { App.Log("health tick: " + ex.Message); }
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    private void HealthTick()
    {
        if (_quitting || _intentionalStop || _autoRestarting) return;
        bool running = _dsh.IsRunning;

        // Service was up and vanished on its own -> schedule one auto-restart.
        if (!running && _hadUrl && !_serviceLostHandled)
        {
            _serviceLostHandled = true;
            App.Log("health: dsh service lost unexpectedly - restarting in 3s");
            _autoRestarting = true;
            Dispatcher.InvokeAsync(() => RestartService(() => { _autoRestarting = false; _unreachableCount = 0; }));
            return;
        }
        if (running && _serviceLostHandled) _serviceLostHandled = false;

        // Process is alive but the web port stopped answering -> treat as hung.
        if (running && _healthPort > 0)
        {
            if (PortReachable(_healthPort)) { _unreachableCount = 0; return; }
            if (++_unreachableCount >= 3)
            {
                _unreachableCount = 0;
                App.Log("health: dsh alive but port " + _healthPort + " unreachable x3 - restarting");
                _autoRestarting = true;
                Dispatcher.InvokeAsync(() => RestartService(() => { _autoRestarting = false; }));
            }
        }
    }

    private static bool PortReachable(int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var task = client.ConnectAsync("127.0.0.1", port);
            return task.Wait(1500) && client.Connected;
        }
        catch { return false; }
    }
}
