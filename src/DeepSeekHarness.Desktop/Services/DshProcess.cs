using System.IO;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DeepSeekHarness.Desktop.Services;

/// <summary>
/// Finds the system Node + global dsh install and starts
/// `node <bin.js> web --port <p> --no-open`, then parses the printed
/// "dsh web: http://127.0.0.1:<port>/?token=…" URL. WebView2 is a real browser,
/// so it performs the token->cookie handshake by itself (no node http probing).
/// </summary>
public sealed class DshProcess : IDisposable
{
    private Process? _proc;
    private bool _urlResolved;

    public event Action<string>? UrlResolved; // full URL incl. token
    public event Action<string>? Failed;      // user-facing error text
    public event Action<int>? ProcessExited;  // dsh service exited (code)

    public bool IsRunning => _proc is { HasExited: false };

    private static string? FindNode()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            if (dir.Length > 0 && File.Exists(Path.Combine(dir.Trim(), "node.exe")))
                return Path.Combine(dir.Trim(), "node.exe");
        return null;
    }

    private static string? FindDshEntry()
    {
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var rel = Path.Combine("node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        var candidates = new[]
        {
            Path.Combine(appdata, "npm", rel),       // npm default
            Path.Combine(local, "pnpm", rel),        // pnpm setup
            Path.Combine(local, "Volta", "bin", rel),
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        return null;
    }

    public void Start(int lastPort)
    {
        var node = FindNode();
        var entry = FindDshEntry();
        if (node is null || entry is null)
        {
            Failed?.Invoke("未找到系统 node 或 dsh。请先安装：npm install -g @deepseek-ai/dsh");
            return;
        }

        var psi = new ProcessStartInfo(node)
        {
            WorkingDirectory = Path.GetDirectoryName(entry) ?? ".",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(entry);
        psi.ArgumentList.Add("web");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(lastPort > 0 ? lastPort.ToString() : "0");
        psi.ArgumentList.Add("--no-open");

        try
        {
            _proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Failed?.Invoke("启动 dsh 失败：" + ex.Message);
            return;
        }
        if (_proc is null) { Failed?.Invoke("启动 dsh 失败（进程未创建）"); return; }
        _proc.EnableRaisingEvents = true;
        _proc.Exited += (_, _) => { try { ProcessExited?.Invoke(_proc?.ExitCode ?? -1); } catch { /* ignore */ } };

        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
        _urlResolved = false;
        _ = StartTimeout(20_000);
        _proc.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            var m = Regex.Match(e.Data, @"dsh web: (\S+)");
            if (m.Success) { _urlResolved = true; UrlResolved?.Invoke(m.Groups[1].Value); }
        };
        _proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data)) App.Log("dsh stderr: " + e.Data);
        };
    }

    private async System.Threading.Tasks.Task StartTimeout(int ms)
    {
        await System.Threading.Tasks.Task.Delay(ms);
        if (!_urlResolved && !_stopped)
            Failed?.Invoke("dsh 服务启动超时：20 秒内未收到服务地址，请检查 dsh 是否安装/可用。");
    }

    private bool _stopped = false;

    public void Stop()
    {
        _stopped = true;
        if (_proc is { HasExited: false })
        {
            try { _proc.Kill(true); } catch { /* already dead */ }
            _proc.WaitForExit(2000);
        }
        _proc?.Dispose();
        _proc = null;
    }

    public void Dispose() => Stop();
}
