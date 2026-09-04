using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace DeepSeekHarness.Desktop;

/// <summary>
/// In-app log viewer (Info/Warn/Error). Reads the same rotating error.log that
/// App.Log/LogWarn/LogError write, newest lines first, with a simple level
/// filter. Pure read-mostly helper window; never touches the app state.
/// </summary>
public partial class LogViewerWindow : Window
{
    private static readonly string LogPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSH WV2", "logs", "error.log");
    private static readonly int MaxLines = 2000;

    private readonly DispatcherTimer _timer;
    private string _filter = "all"; // all | warn | info

    public LogViewerWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Refresh();
    }

    private void LevelFilter_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        int i = LevelFilter.SelectedIndex;
        _filter = i switch { 1 => "warn", 2 => "info", _ => "all" };
        Refresh();
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e) => Refresh();

    private void OpenDirBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", Path.GetDirectoryName(LogPath)!) { UseShellExecute = true });
        }
        catch (Exception ex) { App.Log("log viewer open dir: " + ex.Message); }
    }

    private void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        try { File.WriteAllText(LogPath, ""); Refresh(); }
        catch (Exception ex) { App.Log("log viewer clear: " + ex.Message); }
    }

    private void Refresh()
    {
        // LevelFilter's SelectionChanged fires during XAML load, before LogBox
        // exists; guard so an early refresh is a no-op until the UI is built.
        if (LogBox is null) return;
        LogBox.Text = BuildText();
    }

    private string BuildText()
    {
        try
        {
            if (!File.Exists(LogPath)) return "(尚无日志，启动后自动生成)";
            var all = File.ReadAllLines(LogPath);
            int start = Math.Max(0, all.Length - MaxLines);
            var lines = new List<string>();
            for (int i = all.Length - 1; i >= start; i--)
            {
                var line = all[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!Keep(line)) continue;
                lines.Add(line);
            }
            return lines.Count == 0 ? "(当前筛选下没有日志)" : string.Join("\r\n", lines);
        }
        catch (Exception ex) { return "(读取日志失败：" + ex.Message + ")"; }
    }

    private bool Keep(string line)
    {
        bool warn = IsWarnOrError(line);
        return _filter switch
        {
            "warn" => warn,
            "info" => !warn,
            _ => true,
        };
    }

    // Level comes from the [INFO]/[WARN]/[ERROR] prefix; legacy un-prefixed lines
    // fall back to a keyword heuristic so the filter still works on old entries.
    private static bool IsWarnOrError(string line)
    {
        if (line.Contains("[ERROR]") || line.Contains("[WARN]")) return true;
        string[] errorHints = { "failed", "error", "exception", "错误", "异常", "失败", "超时", "stderr", "denied", "exited" };
        foreach (var h in errorHints)
            if (line.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }
}
