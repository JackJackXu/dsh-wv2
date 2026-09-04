using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using DeepSeekHarness.Desktop.Services;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;

namespace DeepSeekHarness.Desktop;

public partial class MainWindow : Window
{
    private static readonly string FullName = "DeepSeek Harness Desktop Client - Powered by C#/WPF on WebView2";
    private readonly Settings _settings = Settings.Load();
    private readonly DshProcess _dsh = new();
    private NotifyIcon? _tray;
    private Icon? _trayIcon;      // normal (whale)
    private Icon? _trayAlertIcon; // "attention" variant used while flashing
    private bool _quitting;
    private bool _webReady;
    private bool _intentionalStop; // suppress exit-notice during manual restart
    private bool _trayHintShown;
    private DateTime _lastBoundsSave = DateTime.MinValue;
    private string? _pendingUrl;
    private System.Windows.Forms.Timer? _flashTimer;
    private ToolStripMenuItem? _loginItem;
    private ToolStripMenuItem? _notifItem;
    private Services.SessionWatcher? _notifWatcher;
    private LogViewerWindow? _logViewer;
    partial void ShellReady();

    public MainWindow()
    {
        InitializeComponent();
        ApplyBounds();
        SyncLaunchAtLoginFromRegistry();
        Title = FullName;
        Loaded += OnLoaded;
        StateChanged += (_, _) => CaptureBounds();
        LocationChanged += (_, _) => CaptureBoundsThrottled();
        SizeChanged += (_, _) => CaptureBoundsThrottled();
        // B: stop tray flashing the moment the user focuses the window.
        Activated += (_, _) => StopFlash();
        Closed += (_, _) => { _dsh.Dispose(); _tray?.Dispose(); };
    }

    // Trust the actual Windows startup entry over our persisted flag on boot.
    private void SyncLaunchAtLoginFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            _settings.LaunchAtLogin = key?.GetValue("DSH WV2") is not null;
            _settings.Save();
        }
        catch (Exception ex) { App.Log("login sync: " + ex.Message); }
    }

    private static readonly string _settingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSH WV2");

    // ---------- window geometry memory ----------
    private void ApplyBounds()
    {
        var sw = SystemParameters.WorkArea;
        var w = _settings.Width > 0 ? _settings.Width : 1280;
        var h = _settings.Height > 0 ? _settings.Height : 840;
        if (_settings.X is { } x && _settings.Y is { } y)
        {
            if (x < sw.Right - 40 && x + w > sw.Left && y < sw.Bottom - 40 && y + h > sw.Top)
            { Left = x; Top = y; }
        }
        Width = Math.Min(w, sw.Width);
        Height = Math.Min(h, sw.Height);
        if (_settings.Maximized) WindowState = WindowState.Maximized;
    }

    private void CaptureBounds()
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.X = (int)Left; _settings.Y = (int)Top;
            _settings.Width = (int)Width; _settings.Height = (int)Height;
            _settings.Maximized = false;
        }
        else if (WindowState == WindowState.Maximized) _settings.Maximized = true;
        _settings.Save();
    }

    // Save geometry at most every ~600ms during a drag/resize (CaptureBounds
    // writes a file each call; a drag fires hundreds of events).
    private void CaptureBoundsThrottled()
    {
        if ((DateTime.Now - _lastBoundsSave).TotalMilliseconds < 600) return;
        _lastBoundsSave = DateTime.Now;
        CaptureBounds();
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        SetupTray();
        _dsh.UrlResolved += OnUrlResolved;
        _dsh.Failed += OnFailed;
        _dsh.ProcessExited += OnDshExited;
        // One-time migration of a legacy (pre-1.1) WebView2 profile that lived
        // next to the exe. Must finish before WebView2 creates the new profile,
        // so it runs synchronously and first.
        TryMigrateLegacyProfile();

        // D (startup parallelization): WebView2 init and the dsh service boot
        // are the two slow parts. Kick both off at once instead of serializing.
        var webInit = InitWebViewAsync();
        StatusText.Text = "正在拉起 dsh 服务…";
        _dsh.Start(_settings.LastPort); // dsh prints its URL asynchronously
        bool webOk = await webInit;
        if (!webOk)
        {
            // Nothing to render with: don't leave a headless dsh service running.
            try { _dsh.Stop(); } catch { /* ignore */ }
            return;
        }
        // The dsh URL may already have resolved while WebView2 was warming up.
        if (!string.IsNullOrEmpty(_pendingUrl)) NavigateTo(_pendingUrl!);
        ShellReady();
    }

    private async System.Threading.Tasks.Task<bool> InitWebViewAsync()
    {
        // Keep the WebView2 browser profile under our own app-data dir (not
        // next to the exe): writeable even from Program Files, and stable
        // across moves/updates. User-data stays off the exe directory.
        try
        {
            var wv2Data = Path.Combine(_settingsDir, "WebView2");
            Directory.CreateDirectory(wv2Data);
            var env = await CoreWebView2Environment.CreateAsync(null, wv2Data);
            await webView.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            StatusText.Text = "初始化 WebView2 失败：" + ex.Message;
            if (ErrorActions is not null) ErrorActions.Visibility = Visibility.Visible;
            // "重试启动" only restarts the dsh service; it cannot fix a missing
            // WebView2 runtime, so hide it here and keep the log/terminal actions.
            if (RetryBtn is not null) RetryBtn.Visibility = Visibility.Collapsed;
            App.Log("webview init: " + ex);
            return false;
        }
        ConfigureWebView(webView.CoreWebView2);
        _webReady = true;
        return true;
    }

    // ----- legacy WebView2 profile migration (old data sat next to the exe) -----
    private static string LegacyWebView2Dir =>
        Path.Combine(AppContext.BaseDirectory, "DSH WV2.exe.WebView2");
    private static string LegacyEbvDir =>
        Path.Combine(LegacyWebView2Dir, "EBWebView");

    private void TryMigrateLegacyProfile()
    {
        try
        {
            if (!Directory.Exists(LegacyEbvDir)) return; // nothing old to migrate
            var newDir = Path.Combine(_settingsDir, "WebView2");
            var newEbv = Path.Combine(newDir, "EBWebView");
            if (Directory.Exists(newEbv) && Directory.EnumerateFileSystemEntries(newEbv).Any()) return;
            var answer = System.Windows.MessageBox.Show(
                "检测到旧版本(1.0 及更早)的登录/缓存数据还在程序旁边。\n\n" +
                "是否把它迁移到新的数据目录？\n\n" +
                "旧位置：\n" + LegacyWebView2Dir + "\n\n" +
                "新位置：\n" + newDir + "\n\n迁移后旧文件夹可手动删除。",
                "DSH WV2 · 旧数据迁移",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            Directory.CreateDirectory(newDir);
            CopyDirectory(LegacyWebView2Dir, newDir);
            _tray?.ShowBalloonTip(3000, "DSH WV2", "旧数据已迁移到新位置，旧文件夹可删除。", ToolTipIcon.Info);
        }
        catch (Exception ex) { App.Log("profile migrate: " + ex.Message); }
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var d in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(d.Replace(src, dst));
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(f, f.Replace(src, dst), true);
    }

    private void OnUrlResolved(string url)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_quitting) return;
            _intentionalStop = false;
            try
            {
                int port = new Uri(url).Port;
                _settings.LastPort = port;
                _healthPort = port;
                _hadUrl = true;
                _settings.Save();
            }
            catch { /* ignore */ }
            _pendingUrl = url;
            if (_webReady) NavigateTo(url);
        });
    }

    private void NavigateTo(string url)
    {
        if (!_webReady || _quitting) return;
        try
        {
            webView.CoreWebView2.Navigate(url);
            webView.Visibility = Visibility.Visible; // render under the overlay
            // (Overlay is hidden on NavigationCompleted success, not here, so
            // there is no white-flash before the page actually renders)
        }
        catch { /* ignore */ }
    }

    private void OnFailed(string msg)
    {
        App.Log("dsh failed: " + msg);
        Dispatcher.InvokeAsync(() => { StatusText.Text = msg; if (ErrorActions is not null) ErrorActions.Visibility = Visibility.Visible; });
    }

    private void OnDshExited(int code)
    {
        if (_quitting || _intentionalStop) return;
        App.Log("dsh service exited: " + code);
        Dispatcher.InvokeAsync(() =>
        {
            StatusText.Text = "dsh 服务已停止 (code " + code + ")。用托盘「重启服务」恢复。";
            Overlay.Visibility = Visibility.Visible;
            webView.Visibility = Visibility.Collapsed;
            if (ErrorActions is not null) ErrorActions.Visibility = Visibility.Visible;
            _tray?.ShowBalloonTip(3000, "DSH WV2", "dsh 服务意外退出 (code " + code + ")。点托盘「重启服务」", ToolTipIcon.Warning);
            Flash();
        });
    }

    // ---------- tray ----------
    private void SetupTray()
    {
        Icon? icon = null;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "whale.ico");
        try { if (File.Exists(iconPath)) icon = new Icon(iconPath); } catch { /* fall through */ }
        _trayIcon = icon ?? SystemIcons.Application;
        _trayAlertIcon = MakeAlertIcon(_trayIcon);

        _tray = new NotifyIcon { Icon = _trayIcon, Visible = true, Text = "DSH WV2" };
        // C: clicking any notification balloon brings the main window forward so
        // the user can see/act on the underlying task or approval.
        _tray.BalloonTipClicked += (_, _) => Dispatcher.InvokeAsync(ShowMain);
        var m = new ContextMenuStrip();
        m.Items.Add("打开主窗口", null, (_, _) => ShowMain());
        m.Items.Add("重新加载 UI", null, (_, _) => Reload());
        m.Items.Add("重启 dsh 服务", null, (_, _) => RestartService());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("打开数据目录", null, (_, _) => OpenFolder(_settingsDir));
        m.Items.Add("打开日志目录", null, (_, _) => OpenFolder(Path.Combine(_settingsDir, "logs")));
        m.Items.Add("日志查看器", null, (_, _) => ShowLogViewer());
        m.Items.Add("打开终端（会话目录）", null, (_, _) => OpenTerminal());
        m.Items.Add(new ToolStripSeparator());
        _notifItem = new ToolStripMenuItem("通知") { Checked = _settings.NotificationsEnabled };
        _notifItem.Click += (_, _) => ToggleNotifications();
        m.Items.Add(_notifItem);
        _loginItem = new ToolStripMenuItem("开机自启") { Checked = _settings.LaunchAtLogin };
        _loginItem.Click += (_, _) => ToggleLaunchAtLogin();
        m.Items.Add(_loginItem);
        m.Items.Add("关于", null, (_, _) => ShowAbout());
        m.Items.Add("检查 dsh 更新", null, (_, _) => CheckForDshUpdate());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("退出", null, (_, _) => Quit());
        _tray.ContextMenuStrip = m;
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    // B: whenever something needs the user's attention (task finished, approval
    // needed, dsh crashed) blink the tray icon WeChat-style — by swapping between
    // the normal and an "attention" icon (NOT hiding/showing, which would cancel
    // a visible balloon). One uniform pattern for every kind of state. It keeps
    // flashing until the window is focused (Activated -> StopFlash).
    private void Flash()
    {
        if (_tray is null) return;
        // Already looking at the app? Nothing to attract.
        if (IsActive) { StopFlash(); return; }
        if (_flashTimer is null)
        {
            _flashTimer = new System.Windows.Forms.Timer { Interval = 450 };
            _flashTimer.Tick += (_, _) => FlashTick();
        }
        if (!_flashTimer.Enabled) _flashTimer.Start();
    }

    private void FlashTick()
    {
        if (_tray is null) { StopFlash(); return; }
        _tray.Icon = ReferenceEquals(_tray.Icon, _trayAlertIcon) ? _trayIcon : _trayAlertIcon;
    }

    private void StopFlash()
    {
        if (_flashTimer is { Enabled: true }) _flashTimer.Stop();
        if (_tray is not null && _tray.Icon != _trayIcon) _tray.Icon = _trayIcon;
    }

    // Build an "attention" whale (a small red badge) used while flashing.
    private static Icon MakeAlertIcon(Icon baseIcon)
    {
        try
        {
            using var bmp = new Bitmap(baseIcon.ToBitmap(), 16, 16);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            using (var brush = new SolidBrush(Color.FromArgb(226, 27, 45)))
            {
                g.FillEllipse(brush, 11, 0, 5, 5); // top-right red dot
            }
            return System.Drawing.Icon.FromHandle(bmp.GetHicon());
        }
        catch
        {
            return SystemIcons.Application; // degenerate fallback
        }
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (ErrorActions is not null) ErrorActions.Visibility = Visibility.Collapsed;
        RestartService();
    }

    private void OpenTerminalBtn_Click(object sender, RoutedEventArgs e) => OpenTerminal();

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        OpenFolder(Path.Combine(_settingsDir, "logs"));
    }

    public void ShowMain()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ShowLogViewer()
    {
        if (_logViewer is null)
        {
            _logViewer = new LogViewerWindow();
            _logViewer.Closed += (_, _) => _logViewer = null;
            _logViewer.Owner = this;
        }
        _logViewer.Show();
        _logViewer.Activate();
    }

    private void Reload()
    {
        if (_webReady) { try { webView.CoreWebView2.Reload(); } catch { /* ignore */ } }
    }

    private void RestartService(Action? done = null)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            Overlay.Visibility = Visibility.Visible;
            webView.Visibility = Visibility.Collapsed;
            StatusText.Text = "正在重启 dsh 服务…";
            _intentionalStop = true;
            // Stop() does a process kill + wait; keep it off the UI thread so the
            // window never freezes for up to 2s.
            try
            {
                await System.Threading.Tasks.Task.Run(() =>
                {
                    _dsh.Stop();
                    _dsh.Start(_settings.LastPort);
                });
            }
            finally
            {
                _intentionalStop = false;
                done?.Invoke();
            }
        });
    }

    private static void OpenFolder(string dir)
    {
        try { Directory.CreateDirectory(dir); Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true }); }
        catch (Exception ex) { App.Log("open folder: " + ex.Message); }
    }

    private void ToggleNotifications()
    {
        _settings.NotificationsEnabled = !_settings.NotificationsEnabled;
        _settings.Save();
        if (_notifItem is not null) _notifItem.Checked = _settings.NotificationsEnabled;
        _tray?.ShowBalloonTip(2000, "DSH WV2", _settings.NotificationsEnabled ? "通知已开启" : "通知已关闭", ToolTipIcon.Info);
    }

    private void ToggleLaunchAtLogin()
    {
        bool next = !_settings.LaunchAtLogin;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key is null) throw new InvalidOperationException("无法打开 Run 键");
            if (next) key.SetValue("DSH WV2", "\"" + Environment.ProcessPath + "\"");
            else key.DeleteValue("DSH WV2", false);
        }
        catch (Exception ex)
        {
            App.Log("login item toggle failed: " + ex.Message);
            // Roll back the in-memory state so the checkbox doesn't lie.
            _settings.LaunchAtLogin = !next;
            if (_loginItem is not null) _loginItem.Checked = !next;
            _tray?.ShowBalloonTip(3000, "DSH WV2", "开机自启设置失败：" + ex.Message, ToolTipIcon.Error);
            return;
        }
        _settings.LaunchAtLogin = next;
        _settings.Save();
        if (_loginItem is not null) _loginItem.Checked = next;
        _tray?.ShowBalloonTip(2000, "DSH WV2", next ? "已开启开机自启" : "已关闭开机自启", ToolTipIcon.Info);
    }

    private void ShowAbout()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        System.Windows.MessageBox.Show(FullName + "\n版本 " + ver + "\n\nC#/WPF + WebView2 原生壳（Windows）", "关于 DSH WV2", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ----- check for dsh CLI updates (check + explicit confirm -> one-click upgrade) -----
    private async void CheckForDshUpdate()
    {
        string? local;
        try { local = Services.DshProcess.FindLocalDshVersion(); }
        catch { local = null; }
        if (string.IsNullOrEmpty(local))
        {
            System.Windows.MessageBox.Show("未找到已安装的 dsh，无法检查更新。\n请先执行：npm install -g @deepseek-ai/dsh",
                "检查 dsh 更新", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _tray?.ShowBalloonTip(2000, "DSH WV2", "正在检查 dsh 更新…", ToolTipIcon.Info);
        var updater = new Services.DshUpdater();
        var tags = await System.Threading.Tasks.Task.Run(() => updater.FetchDistTagsAsync());
        if (tags is null)
        {
            System.Windows.MessageBox.Show("检查失败：无法访问版本源（可能离线或源不可达）。",
                "检查 dsh 更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var cand = Services.DshUpdater.PickCandidate(local, tags.Latest, tags.Next);
        if (cand is null)
        {
            string extra = string.IsNullOrEmpty(tags.Next) ? "" : " / next：" + tags.Next;
            System.Windows.MessageBox.Show("当前已是最新。\n\n已安装 dsh：" + local
                + "\n源最新 latest：" + (string.IsNullOrEmpty(tags.Latest) ? "?" : tags.Latest) + extra,
                "检查 dsh 更新", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var ask = System.Windows.MessageBox.Show(
            "发现新版 dsh：\n\n当前：" + local + "\n候选：" + cand.Version + "（" + cand.Tag + "）\n\n" +
            "是否现在联网升级？\n（命令：npm i -g @deepseek-ai/dsh@" + cand.Version + "）",
            "检查 dsh 更新", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes) return;

        _tray?.ShowBalloonTip(2500, "DSH WV2", "正在升级 dsh 到 " + cand.Version + "…", ToolTipIcon.Info);
        var res = await System.Threading.Tasks.Task.Run(() => updater.UpdateAsync(cand.Version));
        if (!res.Ok)
        {
            System.Windows.MessageBox.Show("升级失败：\n" + (string.IsNullOrEmpty(res.Error) ? res.Output : res.Error),
                "检查 dsh 更新", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        var restart = System.Windows.MessageBox.Show(
            "dsh 已升级到 " + cand.Version + "。\n\n需要重启 dsh 服务才能生效。是否现在重启？",
            "检查 dsh 更新", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (restart == MessageBoxResult.Yes) RestartService();
        else _tray?.ShowBalloonTip(3000, "DSH WV2", "dsh 已升级。稍后可点托盘「重启 dsh 服务」生效。", ToolTipIcon.Info);
    }

    private void Quit()
    {
        _quitting = true;
        // Self-contained single-file WPF can crash on graceful Application.Shutdown
        // (PresentationFramework telemetry fails to load System.Diagnostics.Tracing).
        // Clean up explicitly, then exit the process directly to bypass that path.
        try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { /* ignore */ }
        try { StopFlash(); _flashTimer?.Dispose(); _flashTimer = null; } catch { /* ignore */ }
        try { ClearAuthCookies(); } catch { /* ignore */ }
        try { _tray?.Dispose(); _tray = null; } catch { /* ignore */ }
        try { _dsh.Stop(); } catch { /* ignore */ }
        try { webView.Dispose(); } catch { /* ignore */ }
        CaptureBounds();
        try { _settings.Save(); } catch { /* ignore */ }
        Environment.Exit(0);
    }

    // E: clear the session cookies (incl. the dsh-auth JWT) on exit. The shell
    // always re-authenticates with a fresh token on next launch, so this removes
    // local auth residue without any login cost.
    private void ClearAuthCookies()
    {
        if (webView.CoreWebView2 is null) return;
        try { webView.CoreWebView2.CookieManager.DeleteAllCookies(); } catch { /* ignore */ }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        CaptureBounds();
        if (_quitting) return;
        e.Cancel = true;
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray?.ShowBalloonTip(2000, "DSH WV2", "已最小化到托盘，可随时从托盘打开或退出", ToolTipIcon.Info);
        }
    }

    // ---------- WebView2 security hardening ----------
    private void ConfigureWebView(CoreWebView2 cwv)
    {
        cwv.Settings.AreDevToolsEnabled = false;
        cwv.Settings.IsStatusBarEnabled = false;
        cwv.Settings.AreHostObjectsAllowed = false;
        // No page <-> host messaging bridge is used; keep window.chrome.webview
        // from being exposed at all (strict "no native bridge").
        cwv.Settings.IsWebMessageEnabled = false;
        // Remove page-authored right-click context menu (the WebUI doesn't need
        // it, and it can leak "open in browser" type actions into the shell).
        try { cwv.Settings.AreDefaultContextMenusEnabled = false; } catch { /* older SDK */ }
        try { cwv.Settings.IsGeneralAutofillEnabled = false; } catch { /* older SDK */ }
        try { cwv.Settings.IsPasswordAutosaveEnabled = false; } catch { /* older SDK */ }

        cwv.NavigationStarting += (_, args) =>
        {
            if (string.IsNullOrEmpty(args.Uri) || args.Uri == "about:blank") return;
            try
            {
                var u = new Uri(args.Uri);
                bool loopback = u.Host is "127.0.0.1" or "localhost" or "[::1]";
                bool httpOk = u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps;
                if (loopback && httpOk) return;
                args.Cancel = true;
                OpenExternal(args.Uri);
            }
            catch { args.Cancel = true; }
        };

        cwv.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (string.IsNullOrEmpty(args.Uri) || args.Uri == "about:blank") return;
            OpenExternal(args.Uri);
        };

        cwv.PermissionRequested += (_, args) =>
        {
            args.State = args.PermissionKind == CoreWebView2PermissionKind.ClipboardRead
                ? CoreWebView2PermissionState.Allow
                : CoreWebView2PermissionState.Deny;
        };
    }

    private static void OpenExternal(string url)
    {
        try
        {
            var u = new Uri(url);
            // Only hand web/mail links to the OS browser; never let page content
            // trigger ms-settings:/file:/etc. via ShellExecute.
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps && u.Scheme != "mailto") return;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }
}
