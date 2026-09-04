using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DeepSeekHarness.Desktop.Services;
using Xunit;

namespace DeepSeekHarness.Desktop.Tests;

public class SessionWatcherTests
{
    private static byte[] ZstdFrame(string jsonl)
    {
        using var c = new ZstdSharp.Compressor();
        return c.Wrap(Encoding.UTF8.GetBytes(jsonl)).ToArray();
    }

    private static void AppendFrame(string file, string jsonl)
    {
        using var fs = new FileStream(file, FileMode.Append, FileAccess.Write);
        var frame = ZstdFrame(jsonl);
        fs.Write(frame, 0, frame.Length);
    }

    private static (string dir, string file) NewSession(string delegationDepth = "0")
    {
        var dir = Path.Combine(Path.GetTempPath(), "dshwv2test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var sessionDir = Path.Combine(dir, "session-abc123");
        Directory.CreateDirectory(sessionDir);
        var file = Path.Combine(sessionDir, "session.jsonl.zstd");
        var header = "{\"type\":\"session\",\"version\":0,\"id\":\"session-abc123\",\"createdAt\":1,\"cwd\":\"C:\\work\",\"delegationDepth\":" + delegationDepth + ",\"agentPreset\":\"test\"}\n";
        AppendFrame(file, header);
        return (dir, file);
    }

    [Fact]
    public void Baseline_does_not_notify()
    {
        var (dir, _) = NewSession();
        try
        {
            var w = new SessionWatcher(dir);
            int turn = 0, approval = 0;
            w.TurnEnd += (_, _) => turn++;
            w.ApprovalAsked += (_, _) => approval++;
            w.Poll();
            Assert.Equal(0, turn);
            Assert.Equal(0, approval);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TurnEnd_triggers_once_on_new_frame()
    {
        var (dir, file) = NewSession();
        try
        {
            var w = new SessionWatcher(dir);
            int turn = 0;
            w.TurnEnd += (_, _) => turn++;
            w.Poll();
            AppendFrame(file, "{\"type\":\"turn/end\",\"data\":{}}\n");
            w.Poll();
            Assert.Equal(1, turn);
            w.Poll();
            Assert.Equal(1, turn);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ApprovalAsked_reads_data_fields()
    {
        var (dir, file) = NewSession();
        try
        {
            var w = new SessionWatcher(dir);
            var got = new List<(string tool, string reason)>();
            w.ApprovalAsked += (t, r) => got.Add((t, r));
            w.Poll();
            AppendFrame(file, "{\"type\":\"approval/asked\",\"data\":{\"id\":\"x\",\"toolName\":\"Bash\",\"reason\":\"needs your approval\"}}\n");
            w.Poll();
            Assert.Single(got);
            Assert.Equal("Bash", got[0].tool);
            Assert.Equal("needs your approval", got[0].reason);
        }
        finally { Directory.Delete(dir, true); }
    }

}
