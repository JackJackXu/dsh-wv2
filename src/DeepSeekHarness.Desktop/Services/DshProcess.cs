using System.IO;
using System.Runtime.InteropServices;
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
    private IntPtr _job = IntPtr.Zero;
    private int _generation = 0; // bumped on every Start/Stop; stale events filtered
    private int _requestedPort;
    private bool _fallbackUsed;

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
            Path.Combine(appdata, "npm", rel),
            Path.Combine(local, "pnpm", rel),
            Path.Combine(local, "Volta", "bin", rel),
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        return null;
    }

    public void Start(int lastPort)
    {
        int gen = ++_generation;               // invalidate any prior process/events
        _urlResolved = false;
        _requestedPort = lastPort;
        _fallbackUsed = false;

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

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex) { Failed?.Invoke("启动 dsh 失败：" + ex.Message); return; }
        if (proc is null) { Failed?.Invoke("启动 dsh 失败（进程未创建）"); return; }
        _proc = proc;
        AttachJobObject(proc);

        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            // Ignore a stale exit (old process killed during a restart) whose
            // generation has already been superseded by a newer Start().
            if (gen != _generation || !ReferenceEquals(_proc, proc)) return;
            // If a fixed (saved) port was busy, dsh exits fast without a URL:
            // transparently retry once on an OS-assigned port.
            if (!_urlResolved && _requestedPort > 0 && !_fallbackUsed)
            {
                _fallbackUsed = true;
                App.Log("dsh exited before resolving on port " + _requestedPort + " - retrying on port 0");
                Start(0);
                return;
            }
            try { ProcessExited?.Invoke(proc.ExitCode); } catch { /* ignore */ }
        };

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.OutputDataReceived += (_, e) =>
        {
            if (gen != _generation || string.IsNullOrEmpty(e.Data)) return;
            var m = Regex.Match(e.Data, @"dsh web: (\S+)");
            if (m.Success) { _urlResolved = true; UrlResolved?.Invoke(m.Groups[1].Value); }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data)) App.Log("dsh stderr: " + e.Data);
        };

        _ = StartTimeout(20_000, gen);
    }

    private async System.Threading.Tasks.Task StartTimeout(int ms, int gen)
    {
        await System.Threading.Tasks.Task.Delay(ms);
        // Only act if this is still the current Start() and it never resolved.
        if (gen == _generation && !_urlResolved && _proc is { HasExited: false })
            Failed?.Invoke("dsh 服务启动超时：20 秒内未收到服务地址，请检查 dsh 是否安装/可用。");
    }

    private bool _urlResolved;

    public void Stop()
    {
        _generation++;                           // invalidate in-flight events/timeouts
        if (_proc is { HasExited: false })
        {
            try { _proc.Kill(true); } catch { /* already dead */ }
            _proc.WaitForExit(2000);
        }
        _proc?.Dispose();
        _proc = null;
        if (_job != IntPtr.Zero) { try { CloseHandle(_job); } catch { /* ignore */ } _job = IntPtr.Zero; }
    }

    // ---- Job Object: if this shell dies (incl. hard kill), the dsh node tree
    // dies with it instead of lingering as an orphan service. Best-effort: if
    // it fails we still run, just without the safety net.
    private void AttachJobObject(Process child)
    {
        try
        {
            _job = CreateJobObject(IntPtr.Zero, null);
            if (_job == IntPtr.Zero) return;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ptr, (uint)size)) return;
            }
            finally { Marshal.FreeHGlobal(ptr); }
            if (!AssignProcessToJobObject(_job, child.Handle))
                App.Log("job assign failed: " + Marshal.GetLastWin32Error());
        }
        catch (Exception ex) { App.Log("job object: " + ex.Message); }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public IntPtr MinimumWorkingSetSize;
        public IntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public IntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public IntPtr ProcessMemoryLimit;
        public IntPtr JobMemoryLimit;
        public IntPtr PeakProcessMemoryUsed;
        public IntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    public void Dispose() => Stop();
}
