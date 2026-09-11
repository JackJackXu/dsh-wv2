using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeepSeekHarness.Desktop.Services;

/// <summary>
/// Checks the globally-installed dsh CLI for updates against the npmmirror
/// registry and, on explicit user confirmation, upgrades it with
/// `npm install -g @deepseek-ai/dsh@&lt;version&gt;`. Ported in spirit from the old
/// dsh-dle "check for dsh update" (kept simpler; never auto-runs).
/// </summary>
public sealed class DshUpdater
{
    // npmmirror registry (China-friendly mirror used by the original dsh-dle).
    public const string Registry = "https://registry.npmmirror.com/@deepseek-ai%2fdsh";

    // npm 11 blocks postinstall scripts by default; native deps used by dsh
    // (koffi/node-pty/esbuild/...) need this allow-list to install cleanly.
    private const string AllowScripts =
        "esbuild,@deepseek-ai/dsh-subprocess-local,koffi,node-pty,@google/genai,protobufjs";

    public sealed record DistTags(string? Latest, string? Next);
    public sealed record Candidate(string Version, string Tag);
    public sealed record UpdateResult(bool Ok, string Output, string? Error);

    /// <summary>Semver-ish compare handling `x.y.z[-rc.N]` (stable &gt; its own rc).</summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = Parse(a);
        var pb = Parse(b);
        if (pa is null || pb is null)
        {
            int c = string.CompareOrdinal(a, b);
            return c == 0 ? 0 : (c < 0 ? -1 : 1);
        }
        foreach (var key in new[] { 0, 1, 2, 3 })
        {
            if (pa[key] != pb[key]) return pa[key] < pb[key] ? -1 : 1;
        }
        return 0;
    }

    // [major, minor, patch, rc(Infinity when stable)]
    private static int[]? Parse(string v)
    {
        var m = Regex.Match((v ?? "").Trim(), @"^(\d+)\.(\d+)\.(\d+)(?:-rc\.(\d+))?$");
        if (!m.Success) return null;
        int rc = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : int.MaxValue;
        return new[] { int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), rc };
    }

    /// <summary>Pick the best upgrade target, or null when already current.</summary>
    public static Candidate? PickCandidate(string? local, string? latest, string? next)
    {
        if (string.IsNullOrEmpty(local)) return null;
        var cands = new List<Candidate>();
        if (!string.IsNullOrEmpty(latest) && CompareVersions(local, latest!) < 0)
            cands.Add(new Candidate(latest!, "latest"));
        if (!string.IsNullOrEmpty(next) && next != latest
            && CompareVersions(local, next!) < 0)
            cands.Add(new Candidate(next!, "next"));
        if (cands.Count == 0) return null;
        cands.Sort((x, y) => CompareVersions(x.Version, y.Version));
        return cands[^1];
    }

    /// <summary>Fetch latest/next dist-tags from npmmirror; null on any failure.</summary>
    public async Task<DistTags?> FetchDistTagsAsync(int timeoutMs = 10_000)
    {
        try
        {
            using var hc = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            string body = await hc.GetStringAsync(Registry);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("dist-tags", out var tags)) return null;
            string? latest = Str(tags, "latest");
            string? next = Str(tags, "next");
            return new DistTags(latest, next);
        }
        catch
        {
            return null; // offline / source unreachable / non-JSON
        }
    }

    private static string? Str(JsonElement o, string prop)
        => o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Upgrade the global dsh to a specific version. Never auto-invoked.</summary>
    public async Task<UpdateResult> UpdateAsync(string version)
    {
        string spec = "@deepseek-ai/dsh@" + version;
        string allow = "--allow-scripts=" + AllowScripts;
        // Dedicated cache: avoids EPERM/antivirus locks on the user's shared
        // %LOCALAPPDATA%\npm-cache that can make an in-app update fail.
        string cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSH WV2", "npm-cache");
        string output = "";
        Process? proc = null;
        try
        {
            Directory.CreateDirectory(cache);
            // Windows: drive npm's plain-JS CLI with node.exe directly to dodge
            // .cmd shell-parsing pitfalls; fall back to bare "npm" otherwise.
            var node = DshProcess.FindNodePath();
            string? npmCli = node is null ? null
                : Path.Combine(Path.GetDirectoryName(node)!, "node_modules", "npm", "bin", "npm-cli.js");

            var psi = new ProcessStartInfo();
            if (npmCli is not null && File.Exists(npmCli))
            {
                psi.FileName = node!;
                psi.ArgumentList.Add(npmCli);
            }
            else
            {
                psi.FileName = "npm";
            }
            psi.ArgumentList.Add("install");
            psi.ArgumentList.Add("-g");
            psi.ArgumentList.Add(spec);
            psi.ArgumentList.Add(allow);
            psi.ArgumentList.Add("--cache");
            psi.ArgumentList.Add(cache);
            psi.ArgumentList.Add("--no-audit");
            psi.ArgumentList.Add("--no-fund");
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            App.Log("dsh update: npm install -g " + spec + " (cache=" + cache + ")");
            proc = Process.Start(psi);
            if (proc is null) return new UpdateResult(false, output, "无法创建 npm 进程");
            proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) output += e.Data + "\n"; };
            proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) output += e.Data + "\n"; };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync();
            bool ok = proc.ExitCode == 0;
            App.Log("dsh update: exit=" + proc.ExitCode + (ok ? " (ok)" : " (failed)")
                + Environment.NewLine + Tail(output, 4000));
            return new UpdateResult(ok, output, ok ? null : "npm 退出码 " + proc.ExitCode);
        }
        catch (Exception ex)
        {
            App.Log("dsh update failed: " + ex);
            return new UpdateResult(false, output, ex.Message);
        }
        finally
        {
            if (proc is not null) { try { proc.Dispose(); } catch { /* ignore */ } }
        }
    }

    /// <summary>Last <paramref name="maxChars"/> characters of npm output (for dialogs/logs).</summary>
    public static string Tail(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.TrimEnd();
        return text.Length <= maxChars ? text : "…" + text[^maxChars..];
    }
}
