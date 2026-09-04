using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using DeepSeekHarness.Desktop.Services;
using Microsoft.Web.WebView2.Core;

namespace DeepSeekHarness.Desktop;

public partial class MainWindow : Window
{
    private readonly Settings _settings = Settings.Load();
    private readonly DshProcess _dsh = new();
    private NotifyIcon? _tray;
    private bool _quitting;
    private bool _webReady;

    public MainWindow()
    {
        InitializeComponent();
        Width = _settings.Width;
        Height = _settings.Height;
        Title = "DeepSeek Harness Desktop Client - Powered by C#/WPF on WebView2";
        Loaded += OnLoaded;
        Closed += (_, _) => { _dsh.Dispose(); _tray?.Dispose(); };
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        SetupTray();
        _dsh.UrlResolved += OnUrlResolved;
        _dsh.Failed += OnFailed;
        try
        {
            await webView.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            StatusText.Text = "初始化 WebView2 失败：" + ex.Message;
            return;
        }
        ConfigureWebView(webView.CoreWebView2);
        _webReady = true;
        StatusText.Text = "正在拉起 dsh 服务…";
        _dsh.Start(_settings.LastPort);
    }

    private void OnUrlResolved(string url)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!_webReady) return;
            try { _settings.LastPort = new Uri(url).Port; _settings.Save(); } catch { /* ignore */ }
            webView.CoreWebView2.Navigate(url);
            Overlay.Visibility = Visibility.Collapsed;
            webView.Visibility = Visibility.Visible;
        });
    }

    private void OnFailed(string msg)
    {
        Dispatcher.InvokeAsync(() => StatusText.Text = msg);
    }

    // Hardening that mirrors the Electron shell: no devtools, navigation locked
    // to the dsh loopback origin, new windows / foreign links go to the OS
    // browser, and every permission is denied except clipboard (the dsh UI
    // needs it). There is deliberately NO native bridge to expose.
    private void ConfigureWebView(CoreWebView2 cwv)
    {
        cwv.Settings.AreDevToolsEnabled = false;
        cwv.Settings.IsStatusBarEnabled = false;
        cwv.Settings.AreHostObjectsAllowed = false;

        // Navigation lock: only allow the dsh service (http(s) on loopback).
        cwv.NavigationStarting += (_, args) =>
        {
            if (string.IsNullOrEmpty(args.Uri)) return;
            if (args.Uri == "about:blank") return;
            try
            {
                var u = new Uri(args.Uri);
                bool loopback = u.Host is "127.0.0.1" or "localhost" or "[::1]";
                bool httpOk = u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps;
                if (loopback && httpOk) return; // the dsh origin
                args.Cancel = true;
                OpenExternal(args.Uri);
            }
            catch { args.Cancel = true; }
        };

        // Popups / new windows -> system browser.
        cwv.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            OpenExternal(args.Uri);
        };

        // Least privilege: deny all; allow clipboard read/write only.
        cwv.PermissionRequested += (_, args) =>
        {
            // Clipboard write needs no permission; allow read, deny the rest.
            args.State = args.PermissionKind == CoreWebView2PermissionKind.ClipboardRead
                ? CoreWebView2PermissionState.Allow
                : CoreWebView2PermissionState.Deny;
        };
    }

    private static void OpenExternal(string url)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
        catch { /* ignore */ }
    }

    private void SetupTray()
    {
        Icon? icon = null;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "whale.ico");
        try { if (File.Exists(iconPath)) icon = new Icon(iconPath); } catch { /* fall through */ }

        _tray = new NotifyIcon
        {
            Icon = icon ?? SystemIcons.Application,
            Visible = true,
            Text = "DSH WV2",
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => ShowMain());
        menu.Items.Add("退出", null, (_, _) => Quit());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    private void ShowMain()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void Quit()
    {
        _quitting = true;
        if (_tray is not null) _tray.Visible = false;
        System.Windows.Application.Current.Shutdown();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // Tray app: close hides to tray; real exit is via tray -> Exit.
        CaptureBounds();
        if (_quitting) return;
        e.Cancel = true;
        Hide();
    }

    private void CaptureBounds()
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.Width = (int)Width;
            _settings.Height = (int)Height;
        }
        _settings.Save();
    }
}
