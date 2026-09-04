using System.IO;
using System.Text;
using System.Text.Json;

namespace DeepSeekHarness.Desktop.Services;

/// <summary>
/// Watches dsh session logs (~/.dsh/sessions/**/session.jsonl.zstd) and raises
/// TurnEnd for completed top-level agent turns. Faithful C# port of
/// dsh-lite's session-watcher.js: each file is a concatenation of zstd frames
/// (each frame = JSONL rows); we structurally scan complete frames, decode them
/// and look for a 'turn/end' event (older logs fall back to assistant/message).
/// </summary>
public sealed class SessionWatcher
{
    public event Action<string, string>? TurnEnd; // title, body

    private const uint ZstdMagic = 4247762216; // 28 B5 2F FD
    private readonly string _sessionsDir;
    private readonly Dictionary<string, Rec> _files = new();
    private readonly CancellationTokenSource _cts = new();

    private sealed class Rec
    {
        public long Size;
        public long Consumed;
        public string? Title;
        public string? Cwd;
        public string? Id;
        public bool Baseline;
        public bool HasTurnEvents;
        public int DelegationDepth;
    }

    public SessionWatcher(string sessionsDir) => _sessionsDir = sessionsDir;

    public void Start(int intervalMs = 2000)
    {
        var t = new System.Threading.Thread(() =>
        {
            // first scan deferred a little so the UI paints first
            try { System.Threading.Thread.Sleep(400); } catch { }
            while (!_cts.IsCancellationRequested)
            {
                try { Scan(); } catch { /* keep watching */ }
                try { System.Threading.Thread.Sleep(intervalMs); } catch { }
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    public void Stop() => _cts.Cancel();

    // ----- frame scanner (structural) -----
    private static List<(long Start, long End)> ScanZstdFrames(byte[] buf)
    {
        var frames = new List<(long, long)>();
        int offset = 0;
        while (offset < buf.Length)
        {
            long start = offset;
            if (buf.Length - offset < 4 || BitConverter.ToUInt32(buf, offset) != ZstdMagic) break;
            offset += 4;
            if (offset >= buf.Length) break;
            byte descriptor = buf[offset];
            offset += 1;
            if ((descriptor & 24) != 0) break;
            int contentSizeFlag = descriptor >> 6;
            bool singleSegment = (descriptor & 32) != 0;
            bool checksum = (descriptor & 4) != 0;
            int dictionaryFlag = descriptor & 3;
            int dictionaryBytes = dictionaryFlag == 3 ? 4 : dictionaryFlag;
            int contentSizeBytes = contentSizeFlag == 0 ? (singleSegment ? 1 : 0) : (1 << contentSizeFlag);
            int remainingHeader = (singleSegment ? 0 : 1) + dictionaryBytes + contentSizeBytes;
            if (buf.Length - offset < remainingHeader) break;
            offset += remainingHeader;
            for (; ; )
            {
                if (buf.Length - offset < 3) break;
                int blockHeader = buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16);
                offset += 3;
                bool lastBlock = (blockHeader & 1) != 0;
                int blockType = (blockHeader >> 1) & 3;
                int blockSize = blockHeader >> 3;
                if (blockType == 3) return frames;
                int payload = blockType == 1 ? 1 : blockSize;
                if (buf.Length - offset < payload) return frames;
                offset += payload;
                if (lastBlock) break;
            }
            if (checksum)
            {
                if (buf.Length - offset < 4) return frames;
                offset += 4;
            }
            frames.Add((start, offset));
        }
        return frames;
    }

    private static byte[] Decode(byte[] buf)
    {
        using var d = new ZstdSharp.Decompressor();
        return d.Unwrap(buf).ToArray();
    }

    // ----- expand one JSONL row into events (storage rows pack chunk events) -----
    private static IEnumerable<JsonElement> ExpandRow(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) yield break;
        string? type = row.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (type is "text-chunks" or "reasoning-chunks" || type == "tool-call-chunks")
        {
            if (row.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                string arrName = type == "tool-call-chunks" ? "args" : "texts";
                if (data.TryGetProperty(arrName, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var el in arr.EnumerateArray()) yield return el;
            }
        }
        else
        {
            yield return row;
        }
    }

    private static string? Str(JsonElement o, string prop)
    {
        if (o.ValueKind != JsonValueKind.Object) return null;
        if (o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        return null;
    }

    private static int Int(JsonElement o, string prop)
    {
        if (o.ValueKind != JsonValueKind.Object) return 0;
        if (o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number) return v.GetInt32();
        return 0;
    }

    private void Scan()
    {
        var live = ListLogs();
        foreach (var key in _files.Keys.ToList()) if (!live.Contains(key)) _files.Remove(key);
        foreach (var file in live)
        {
            try { Process(file); } catch { /* keep going */ }
        }
    }

    private List<string> ListLogs()
    {
        var outL = new List<string>();
        if (!Directory.Exists(_sessionsDir)) return outL;
        foreach (var f in Directory.EnumerateFiles(_sessionsDir, "session.jsonl.zstd", SearchOption.AllDirectories))
            outL.Add(f);
        return outL;
    }

    private void Process(string file)
    {
        var fi = new FileInfo(file);
        if (!fi.Exists) { _files.Remove(file); return; }
        if (!_files.TryGetValue(file, out var rec))
        {
            rec = new Rec();
            _files[file] = rec;
        }
        long size = fi.Length;
        if (size <= rec.Consumed && rec.Baseline) return;
        if (size < rec.Consumed) { rec.Consumed = 0; rec.Title = null; rec.Baseline = false; rec.HasTurnEvents = false; }

        bool first = !rec.Baseline;
        long readFrom = rec.Consumed;
        byte[] tail;
        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            long len = size - readFrom;
            tail = new byte[len];
            fs.Position = readFrom;
            int pos = 0;
            while (pos < len)
            {
                int n = fs.Read(tail, pos, (int)(len - pos));
                if (n <= 0) break;
                pos += n;
            }
            if (pos < len) tail = tail[..pos];
        }
        if (tail.Length < 4) return;

        if (!first && BitConverter.ToUInt32(tail, 0) != ZstdMagic)
        {
            rec.Consumed = 0; rec.Title = null; rec.Baseline = false; rec.HasTurnEvents = false;
            Process(file);
            return;
        }

        var frames = ScanZstdFrames(tail);
        if (frames.Count == 0) return;

        if (first)
        {
            var (s0, e0) = frames[0];
            try
            {
                string text = Encoding.UTF8.GetString(Decode(tail[(int)s0..(int)e0]));
                var lines = text.Split('\n');
                using var doc = JsonDocument.Parse(lines[0]);
                var h = doc.RootElement;
                if (Str(h, "type") == "session")
                {
                    rec.Cwd = Str(h, "cwd");
                    rec.Id = Str(h, "id");
                    rec.DelegationDepth = Int(h, "delegationDepth");
                }
                for (int i = 1; i < lines.Length; i++)
                {
                    if (string.IsNullOrEmpty(lines[i])) continue;
                    using var rowDoc = JsonDocument.Parse(lines[i]);
                    foreach (var ev in ExpandRow(rowDoc.RootElement))
                    {
                        if (ev.ValueKind != JsonValueKind.Object) continue;
                        string? et = Str(ev, "type");
                        if (et == "session/title")
                        {
                            var ttl = ev.TryGetProperty("data", out var d) && d.TryGetProperty("title", out var tt) ? tt.GetString() : null;
                            if (!string.IsNullOrEmpty(ttl)) rec.Title = ttl;
                        }
                        if (et == "turn/start" || et == "turn/end") rec.HasTurnEvents = true;
                    }
                }
            }
            catch { /* header damaged, retry next pass */ }
            rec.Consumed = readFrom + frames[^1].End;
            rec.Baseline = true;
            rec.Size = size;
            return;
        }

        int turnEnds = 0, assistantMessages = 0;
        long consumed = readFrom;
        foreach (var (s, e) in frames)
        {
            string text;
            try { text = Encoding.UTF8.GetString(Decode(tail[(int)s..(int)e])); }
            catch { break; }
            foreach (var line in text.Split('\n'))
            {
                if (string.IsNullOrEmpty(line)) continue;
                using var rowDoc = JsonDocument.Parse(line);
                foreach (var ev in ExpandRow(rowDoc.RootElement))
                {
                    if (ev.ValueKind != JsonValueKind.Object) continue;
                    string? et = Str(ev, "type");
                    if (et == "session/title")
                    {
                        var ttl = ev.TryGetProperty("data", out var d) && d.TryGetProperty("title", out var tt) ? tt.GetString() : null;
                        if (!string.IsNullOrEmpty(ttl)) rec.Title = ttl;
                    }
                    if (et == "turn/start" || et == "turn/end") rec.HasTurnEvents = true;
                    if (et == "turn/end") turnEnds++;
                    if (et == "assistant/message") assistantMessages++;
                }
            }
            consumed = readFrom + e;
        }
        rec.Consumed = consumed;
        rec.Size = size;

        int count = rec.HasTurnEvents ? turnEnds : assistantMessages;
        if (count > 0) Emit(rec, count);
    }

    private void Emit(Rec rec, int count)
    {
        if (rec.DelegationDepth > 0) return; // subagent logs are noise
        string title = string.IsNullOrEmpty(rec.Title) ? "DSH task finished" : rec.Title!;
        string cwdBase = string.IsNullOrEmpty(rec.Cwd) ? "" : (Path.GetFileName(rec.Cwd) ?? "");
        string shortId = string.IsNullOrEmpty(rec.Id) || rec.Id.Length < 8 ? "" : rec.Id[^8..];
        var parts = new List<string>();
        if (cwdBase.Length > 0) parts.Add(cwdBase);
        if (shortId.Length > 0) parts.Add("session " + shortId);
        string body = string.Join(" · ", parts);
        if (count > 1) body += " (" + count + " turns finished)";
        TurnEnd?.Invoke(title, body);
    }
}
