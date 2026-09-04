using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DeepSeekHarness.Desktop.Services;

/// <summary>
/// Connects to the dsh web app's mux WebSocket (/api/events.mux) using the
/// auth token + the HttpOnly dsh-auth cookie that the WebView2 session already
/// holds, plus an Origin header to satisfy the /api browser-trust fence.
/// Raises Attention when an approval/question needs the user.
/// </summary>
public sealed class MuxWatcher
{
    private readonly string _wsUrl;
    private readonly string? _cookie;
    private readonly string _origin;
    private readonly CancellationTokenSource _cts = new();

    // kind: "approval" | "question"; then title-ish + message
    public event Action<string, string, string>? Attention;

    public MuxWatcher(string httpBaseUrl, string token, string? cookie)
    {
        var u = new Uri(httpBaseUrl);
        string scheme = u.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        _wsUrl = $"{scheme}://{u.Host}:{u.Port}/api/events.mux?token={token}";
        _origin = $"{u.Scheme}://{u.Host}:{u.Port}";
        _cookie = cookie;
    }

    public void Start()
    {
        var t = new System.Threading.Thread(Run);
        t.IsBackground = true;
        t.Start();
    }

    public void Stop() => _cts.Cancel();

    private async void Run()
    {
        int attempt = 0;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("Origin", _origin);
                if (!string.IsNullOrEmpty(_cookie)) ws.Options.SetRequestHeader("Cookie", _cookie);
                await ws.ConnectAsync(new Uri(_wsUrl), _cts.Token);
                attempt = 0;
                var buf = new byte[16384];
                while (ws.State == WebSocketState.Open)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token);
                        ms.Write(buf, 0, r.Count);
                    } while (!r.EndOfMessage);
                    if (r.MessageType == WebSocketMessageType.Text)
                        ProcessMessage(Encoding.UTF8.GetString(ms.ToArray()));
                }
            }
            catch { /* reconnect below */ }
            if (_cts.IsCancellationRequested) break;
            int delay = Math.Min(30000, 5000 * (1 << Math.Min(attempt, 4)));
            attempt++;
            try { await Task.Delay(delay, _cts.Token); } catch { break; }
        }
    }

    private void ProcessMessage(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;
        JsonElement payload = root;
        if (root.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object) payload = p;
        string? type = payload.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (type == "approval/requested")
        {
            string tool = payload.TryGetProperty("toolName", out var tn) && tn.ValueKind == JsonValueKind.String ? tn.GetString()! : "tool";
            string reason = payload.TryGetProperty("reason", out var rn) && rn.ValueKind == JsonValueKind.String ? rn.GetString()! : "";
            Attention?.Invoke("approval", tool, reason);
        }
        else if (type == "question/requested")
        {
            if (payload.TryGetProperty("questions", out var qs) && qs.ValueKind == JsonValueKind.Array)
            {
                foreach (var q in qs.EnumerateArray())
                {
                    string header = q.TryGetProperty("header", out var hd) && hd.ValueKind == JsonValueKind.String ? hd.GetString()! : "";
                    string question = q.TryGetProperty("question", out var qq) && qq.ValueKind == JsonValueKind.String ? qq.GetString()! : "有一个问题等待你回答";
                    Attention?.Invoke("question", header, question);
                }
            }
        }
    }
}
