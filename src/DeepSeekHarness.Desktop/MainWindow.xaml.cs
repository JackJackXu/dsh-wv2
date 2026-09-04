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
    private bool _quitting;
    private bool _webReady;
    private bool _intentionalStop; // suppress exit-notice during manual restart
    private ToolStripMenuItem? _loginItem;
    private Services.SessionWatcher? _notifWatcher;
    private Services.MuxWatcher? _mux;
    partial void ShellReady();

    public MainWindow()
    {
        InitializeComponent();
        ApplyBounds();
        Title = FullName;
        Loaded += OnLoaded;
        StateChanged += (_, _) => CaptureBounds();
        Closed += (_, _) => { _dsh.Dispose(); _tray?.Dispose(); };
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

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        SetupTray();
        _dsh.UrlResolved += OnUrlResolved;
        _dsh.Failed += OnFailed;
        _dsh.ProcessExited += OnDshExited;
        try { await webView.EnsureCoreWebView2Async(); }
        catch (Exception ex) { StatusText.Text = "初始化 WebView2 失败：" + ex.Message; App.Log("webview init: " + ex); return; }
        ConfigureWebView(webView.CoreWebView2);
        _webReady = true;
        StatusText.Text = "正在拉起 dsh 服务…";
        _dsh.Start(_settings.LastPort);
        ShellReady();
    }

    private void OnUrlResolved(string url)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!_webReady) return;
            _intentionalStop = false;
            try { _settings.LastPort = new Uri(url).Port; _settings.Save(); } catch { /* ignore */ }
            webView.CoreWebView2.Navigate(url);
            Overlay.Visibility = Visibility.Collapsed;
            webView.Visibility = Visibility.Visible;
        });
    }

    private void OnFailed(string msg)
    {
        App.Log("dsh failed: " + msg);
        Dispatcher.InvokeAsync(() => StatusText.Text = msg);
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
            _tray?.ShowBalloonTip(3000, "DSH WV2", "dsh 服务意外退出 (code " + code + ")。点托盘「重启服务」", ToolTipIcon.Warning);
        });
    }

    // ---------- tray ----------
    private void SetupTray()
    {
        Icon? icon = null;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "whale.ico");
        try { if (File.Exists(iconPath)) icon = new Icon(iconPath); } catch { /* fall through */ }

        _tray = new NotifyIcon { Icon = icon ?? SystemIcons.Application, Visible = true, Text = "DSH WV2" };
        var m = new ContextMenuStrip();
        m.Items.Add("打开主窗口", null, (_, _) => ShowMain());
        m.Items.Add("重新加载 UI", null, (_, _) => Reload());
        m.Items.Add("重启 dsh 服务", null, (_, _) => RestartService());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("打开数据目录", null, (_, _) => OpenFolder(_settingsDir));
        m.Items.Add("打开日志目录", null, (_, _) => OpenFolder(Path.Combine(_settingsDir, "logs")));
        m.Items.Add("打开终端（会话目录）", null, (_, _) => OpenTerminal());
        m.Items.Add(new ToolStripSeparator());
        _loginItem = new ToolStripMenuItem("开机自启") { Checked = _settings.LaunchAtLogin };
        _loginItem.Click += (_, _) => ToggleLaunchAtLogin();
        m.Items.Add(_loginItem);
        m.Items.Add("关于", null, (_, _) => ShowAbout());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("退出", null, (_, _) => Quit());
        _tray.ContextMenuStrip = m;
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    private void ShowMain()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void Reload()
    {
        if (_webReady) { try { webView.CoreWebView2.Reload(); } catch { /* ignore */ } }
    }

    private void RestartService()
    {
        Dispatcher.InvokeAsync(() =>
        {
            Overlay.Visibility = Visibility.Visible;
            webView.Visibility = Visibility.Collapsed;
            StatusText.Text = "正在重启 dsh 服务…";
            _intentionalStop = true;
            _dsh.Stop();
            _dsh.Start(_settings.LastPort);
        });
    }

    private static void OpenFolder(string dir)
    {
        try { Directory.CreateDirectory(dir); Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true }); }
        catch (Exception ex) { App.Log("open folder: " + ex.Message); }
    }

    private void ToggleLaunchAtLogin()
    {
        bool next = !_settings.LaunchAtLogin;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key is null) return;
            if (next) key.SetValue("DSH WV2", "\"" + Environment.ProcessPath + "\"");
            else key.DeleteValue("DSH WV2", false);
        }
        catch (Exception ex) { App.Log("login item: " + ex.Message); }
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

    private void Quit()
    {
        _quitting = true;
        // Self-contained single-file WPF can crash on graceful Application.Shutdown
        // (PresentationFramework telemetry fails to load System.Diagnostics.Tracing).
        // Clean up explicitly, then exit the process directly to bypass that path.
        try { UnregisterHotKey(); } catch { /* ignore */ }
        try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { /* ignore */ }
        try { _tray?.Dispose(); _tray = null; } catch { /* ignore */ }
        try { _dsh.Stop(); } catch { /* ignore */ }
        CaptureBounds();
        try { _settings.Save(); } catch { /* ignore */ }
        Environment.Exit(0);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        CaptureBounds();
        if (_quitting) return;
        e.Cancel = true;
        Hide();
    }

    // ---------- WebView2 security hardening ----------
    private void ConfigureWebView(CoreWebView2 cwv)
    {
        cwv.Settings.AreDevToolsEnabled = false;
        cwv.Settings.IsStatusBarEnabled = false;
        cwv.Settings.AreHostObjectsAllowed = false;

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

        cwv.NewWindowRequested += (_, args) => { args.Handled = true; OpenExternal(args.Uri); };

        cwv.PermissionRequested += (_, args) =>
        {
            args.State = args.PermissionKind == CoreWebView2PermissionKind.ClipboardRead
                ? CoreWebView2PermissionState.Allow
                : CoreWebView2PermissionState.Deny;
        };
    }

    private static void OpenExternal(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }
}
