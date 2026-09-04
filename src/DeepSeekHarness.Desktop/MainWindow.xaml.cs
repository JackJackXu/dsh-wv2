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
        Title = "DeepSeek Harness Desktop Client — DSH WV2";
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
        webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
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
