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

    [Fact]
    public void QuestionAsked_triggers_on_ask_user_question_tool_call_once()
    {
        var (dir, file) = NewSession();
        try
        {
            var w = new SessionWatcher(dir);
            var got = new List<string>();
            w.QuestionAsked += (t, b) => got.Add(t + "|" + b);
            w.Poll();
            // A real ask_user_question call: tool/call whose arguments JSON holds
            // the questions (the same shape verified from real session logs).
            string row =
                "{\"type\":\"tool/call\",\"data\":{\"turn\":1,\"step\":0,\"callId\":\"call_1\"," +
                "\"name\":\"ask_user_question\"," +
                "\"arguments\":\"{\\\"questions\\\":[{\\\"id\\\":\\\"q1\\\",\\\"header\\\":\\\"\\u4e0b\\u4e00\\u6b65\\\",\\\"question\\\":\\\"\\u7ee7\\u7eed\\u5417\\uff1f\\\",\\\"options\\\":[{\\\"label\\\":\\\"A\\\"}]}]}\"}}\n";
            AppendFrame(file, row);
            w.Poll();
            Assert.Single(got);
            Assert.Contains("dsh 在问你一个问题", got[0]);
            Assert.Contains("继续吗", got[0]);
            // Re-poll must not re-notify the same already-consumed call.
            w.Poll();
            Assert.Single(got);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void OtherToolCall_does_not_trigger_question()
    {
        var (dir, file) = NewSession();
        try
        {
            var w = new SessionWatcher(dir);
            int questions = 0;
            w.QuestionAsked += (_, _) => questions++;
            w.Poll();
            AppendFrame(file, "{\"type\":\"tool/call\",\"data\":{\"turn\":1,\"step\":0,\"callId\":\"c\",\"name\":\"Bash\",\"arguments\":\"{}\"}}\n");
            w.Poll();
            Assert.Equal(0, questions);
        }
        finally { Directory.Delete(dir, true); }
    }

}
