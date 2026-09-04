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
        WireDomNotifications();
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

    private async System.Threading.Tasks.Task StartMuxAsync(string httpUrl)
    {
        try
        {
            var baseUri = new Uri(httpUrl);
            string origin = baseUri.GetLeftPart(UriPartial.Authority);
            string token = "";
            var q = baseUri.Query.TrimStart('?');
            foreach (var pair in q.Split('&'))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && kv[0] == "token") { token = System.Uri.UnescapeDataString(kv[1]); break; }
            }

            // Obtain the HttpOnly dsh-auth cookie via the token->303 handshake
            // (WebView2's CookieManager did not return it reliably). GET
            // /?token=... -> 303 + Set-Cookie: dsh-auth-...=JWT.
            string cookie = "";
            try
            {
                using var hc = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false });
                using var resp = await hc.GetAsync(origin + "/?token=" + System.Uri.EscapeDataString(token));
                if (resp.Headers.TryGetValues("Set-Cookie", out var sc))
                    cookie = string.Join("; ", sc.Select(c => c.Split(';')[0]));
                if (cookie.Length == 0) App.Log("mux: no Set-Cookie on " + resp.StatusCode);
            }
            catch (Exception ex) { App.Log("mux cookie handshake: " + ex.Message); }

            _mux?.Stop();
            _mux = new Services.MuxWatcher(origin, token, cookie.Length > 0 ? cookie : null);
            _mux.Attention += OnAttention;
            _mux.Start();
            App.Log("mux started for " + origin + (cookie.Length > 0 ? " (cookie ok)" : " (no cookie)"));
        }
        catch (Exception ex) { App.Log("mux start: " + ex.Message); }
    }

    private void OnAttention(string kind, string title, string message)
    {
        if (!_settings.NotificationsEnabled) return;
        Dispatcher.InvokeAsync(() =>
        {
            string head = kind == "approval"
                ? "需要你的审批：" + title
                : "需要你回答" + (title.Length > 0 ? "「" + title + "」" : "");
            string body = kind == "approval"
                ? (message.Length > 0 ? message : "有一个工具请求需要批准")
                : (message.Length > 0 ? message : "有一个问题等待你回答");
            _tray?.ShowBalloonTip(5000, head, body, System.Windows.Forms.ToolTipIcon.Warning);
        });
    }

    // ----- DOM observation: detect pending approval/question in the page -----
    private void WireDomNotifications()
    {
        try
        {
            webView.CoreWebView2.WebMessageReceived += OnWebMessage;
            string script = @"
(function(){
  var KEYS=['需要你的审批','需要你审批','请允许','是否允许','批准','需要你回答','向你提问','等待你回答','有请求需要'];
  var seen={};
  setInterval(function(){
    try{
      var body=document.body; if(!body) return;
      var txt=body.innerText||'';
      var hit=null;
      for(var i=0;i<KEYS.length;i++){ var k=KEYS[i]; var idx=txt.indexOf(k); if(idx>=0){ hit=txt.slice(Math.max(0,idx-30),idx+k.length+80); break; } }
      if(hit){
        var key=hit.slice(0,60);
        if(!seen[key]){ seen[key]=1; if(window.chrome&&chrome.webview) chrome.webview.postMessage({kind:'attention',text:hit}); }
      }
    }catch(e){}
  },1200);
})();";
            webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);
        }
        catch (Exception ex) { App.Log("dom notify wiring: " + ex.Message); }
    }

    private void OnWebMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string json = e.TryGetWebMessageAsString() ?? "";
            App.Log("page-msg: " + json);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("kind", out var k) && k.GetString() == "attention")
            {
                string text = r.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                if (_settings.NotificationsEnabled)
                    _tray?.ShowBalloonTip(5000, "需要你处理", text, System.Windows.Forms.ToolTipIcon.Warning);
            }
        }
        catch { /* ignore */ }
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
