// SseServer — samme HTTP-kontrakt som Node-relayet, så live.html og
// containerens egen visning virker uændret:
//   GET /health                → status-JSON
//   GET /state                 → fuld tilstand (?exclude=CarData,Position / ?only=…)
//   GET /events                → SSE: "snapshot" ved tilslutning, dernæst "feed"/"status"
//                                (samme ?exclude/?only-filter pr. klient)
//   GET /recordings            → liste over *.f1rec
//   GET /recordings/<fil>      → rå fil, Range "bytes=N-" (forsinket live)
//   GET /recordings/<fil>?profile=timing|map|tele&from=N
//                              → filtrerede linjer fra rå-offset N; svaret har
//                                X-Next-Offset (rå offset til næste kald)
// Hver SSE-klient har sin egen kø + skrive-task: en langsom klient kan
// aldrig blokere feed-tråden (og dermed optageren). Fyldes køen, smides
// klienten af (EventSource genforbinder selv).
// HttpListener kræver på Windows en engangs-URL-ACL for at lytte udadtil:
//   netsh http add urlacl url=http://+:8137/ user=Everyone
// (se docs/README_APP.md). Uden ACL faldes tilbage til kun localhost.
// På Linux (containeren) kræves intet.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace SlqF1Live.Core;

public sealed class SseServer : IDisposable
{
    private readonly int _port;
    private readonly string _allowOrigin;
    private readonly StateStore _store;
    private readonly Func<string> _feedStatus;
    private readonly Func<bool> _feedConnected;
    private HttpListener? _listener;
    private readonly ConcurrentDictionary<Guid, SseClient> _clients = new();
    private Timer? _keepalive;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private readonly string? _recordingsDir;

    public const int ClientQueueFrames = 4000;

    public event Action<int>? ClientCountChanged;
    public event Action<string>? Log;
    public int ClientCount => _clients.Count;
    public bool LocalhostOnly { get; private set; }

    /// Kaldes FØR de indbyggede ruter. Returnér true hvis forespørgslen er
    /// håndteret (også ved afvisning, fx 403). Bruges af serveren til
    /// adgangskontrol, webvisning og admin-API.
    public Func<HttpListenerContext, string, Task<bool>>? ExtraRoutes { get; set; }
    /// Er filen (filnavn) under optagelse lige nu? Vises som "live" i listen.
    public Func<string, bool>? IsRecordingActive { get; set; }
    /// Ekstra felter i /health.
    public Func<JsonObject>? ExtraHealth { get; set; }
    /// Lyt kun på localhost (ingen URL-ACL nødvendig på Windows).
    public bool ForceLocalhost { get; set; }

    public SseServer(int port, string allowOrigin, StateStore store,
        Func<string> feedStatus, Func<bool> feedConnected, string? recordingsDir = null)
    {
        _port = port;
        _allowOrigin = allowOrigin;
        _store = store;
        _feedStatus = feedStatus;
        _feedConnected = feedConnected;
        _recordingsDir = recordingsDir;
    }

    public void Start()
    {
        _listener = new HttpListener();
        try
        {
            if (ForceLocalhost) throw new HttpListenerException(5);
            _listener.Prefixes.Add($"http://+:{_port}/");
            _listener.Start();
        }
        catch (HttpListenerException)
        {
            // Ingen URL-ACL — fald tilbage til localhost (app'en virker lokalt,
            // men f1.slq.dk kan ikke nå den; se docs/README_APP.md).
            try { _listener.Close(); } catch { /* ignorér */ }
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Start();
            LocalhostOnly = true;
            if (!ForceLocalhost)
                Log?.Invoke($"ADVARSEL: lytter kun på localhost:{_port} — kør netsh-kommandoen fra README for ekstern adgang");
        }
        Log?.Invoke($"HTTP/SSE-server på port {_port}" + (LocalhostOnly ? " (kun localhost)" : ""));
        _ = AcceptLoopAsync();
        _keepalive = new Timer(_ => EnqueueAll(Encoding.UTF8.GetBytes(": ping\n\n"), null), null, 20000, 20000);
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private void ApplyCors(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var origin = ctx.Request.Headers["Origin"];
        string? allow = null;
        if (_allowOrigin == "*") allow = "*";
        else if (_allowOrigin.Contains(','))
        {
            var list = _allowOrigin.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (origin is not null && list.Contains(origin, StringComparer.OrdinalIgnoreCase)) allow = origin;
            res.Headers["Vary"] = "Origin";
        }
        else allow = _allowOrigin;
        if (allow is not null)
        {
            res.Headers["Access-Control-Allow-Origin"] = allow;
            res.Headers["Access-Control-Expose-Headers"] = "X-Next-Offset, Content-Range, Content-Length";
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        ApplyCors(ctx);
        res.Headers["Cache-Control"] = "no-store";
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        try
        {
            if (ctx.Request.HttpMethod == "OPTIONS")
            {
                res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                res.Headers["Access-Control-Allow-Headers"] = "Range, Content-Type, Authorization";
                res.StatusCode = 204;
                res.Close();
                return;
            }
            if (ExtraRoutes is not null && await ExtraRoutes(ctx, path)) return;

            switch (path)
            {
                case "/health":
                    var health = new JsonObject
                    {
                        ["ok"] = true,
                        ["feed"] = _feedStatus(),
                        ["connected"] = _feedConnected(),
                        ["clients"] = _clients.Count,
                        ["uptimeS"] = (int)(DateTime.UtcNow - _startedAt).TotalSeconds,
                        ["updatedAt"] = _store.UpdatedAt,
                    };
                    if (ExtraHealth?.Invoke() is { } extra)
                        foreach (var kv in extra) health[kv.Key] = kv.Value?.DeepClone();
                    WriteJson(res, health.ToJsonString());
                    break;
                case "/state":
                    var (ex, on) = ParseFilter(ctx.Request.QueryString);
                    WriteJson(res, _store.SnapshotJson(ex, on));
                    break;
                case "/events":
                    HandleSse(ctx);
                    return; // holdes åben
                case "/recordings":
                    WriteJson(res, ListRecordingsJson());
                    break;
                default:
                    if (path.StartsWith("/recordings/", StringComparison.Ordinal))
                    {
                        await ServeRecordingAsync(ctx, Uri.UnescapeDataString(path["/recordings/".Length..]));
                        break;
                    }
                    res.StatusCode = 404;
                    WriteText(res, "SLQ F1 Live: /state, /events, /health, /recordings");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke("HTTP-fejl: " + ex.Message);
            try { res.Abort(); } catch { /* ignorér */ }
        }
    }

    // ---------- SSE ----------

    private sealed class SseClient
    {
        public required Guid Id;
        public required HttpListenerResponse Res;
        public required Channel<byte[]> Queue;
        public HashSet<string>? Exclude;
        public HashSet<string>? Only;
        public string FilterKey => (Exclude is null ? "" : string.Join(",", Exclude.Order())) + "|" +
                                   (Only is null ? "" : string.Join(",", Only.Order()));
        public bool Wants(string topic) =>
            (Exclude is null || !Exclude.Contains(topic)) && (Only is null || Only.Contains(topic));
    }

    /// "?exclude=CarData,Position.z" → {"CarData","Position"} (dekodede navne).
    public static (HashSet<string>? Exclude, HashSet<string>? Only) ParseFilter(System.Collections.Specialized.NameValueCollection q)
    {
        static HashSet<string>? Parse(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                set.Add(part.EndsWith(".z", StringComparison.Ordinal) ? part[..^2] : part);
            return set.Count > 0 ? set : null;
        }
        return (Parse(q["exclude"]), Parse(q["only"]));
    }

    private void HandleSse(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        res.ContentType = "text/event-stream";
        res.Headers["X-Accel-Buffering"] = "no"; // nginx/NPM: ingen buffering af strømmen
        res.SendChunked = true;
        var (exclude, only) = ParseFilter(ctx.Request.QueryString);
        var client = new SseClient
        {
            Id = Guid.NewGuid(),
            Res = res,
            Queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(ClientQueueFrames)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
            }),
            Exclude = exclude,
            Only = only,
        };
        client.Queue.Writer.TryWrite(Frame("snapshot", _store.SnapshotJson(exclude, only)));
        _clients[client.Id] = client;
        ClientCountChanged?.Invoke(_clients.Count);
        Log?.Invoke($"SSE-klient til ({_clients.Count} i alt)");
        _ = WriterLoopAsync(client);
    }

    private async Task WriterLoopAsync(SseClient c)
    {
        try
        {
            await foreach (var frame in c.Queue.Reader.ReadAllAsync())
            {
                await c.Res.OutputStream.WriteAsync(frame);
                await c.Res.OutputStream.FlushAsync();
            }
        }
        catch { /* klienten er væk */ }
        finally
        {
            RemoveClient(c.Id);
        }
    }

    private static byte[] Frame(string eventName, string json)
        => Encoding.UTF8.GetBytes($"event: {eventName}\ndata: {json}\n\n");

    private void EnqueueAll(byte[] frame, string? topic)
    {
        foreach (var (id, c) in _clients)
        {
            if (topic is not null && !c.Wants(topic)) continue;
            if (!c.Queue.Writer.TryWrite(frame))
            {
                Log?.Invoke("SSE-klient for langsom — afbrydes");
                RemoveClient(id);
            }
        }
    }

    /// Send en event til alle. "snapshot" filtreres pr. klient (tilstanden
    /// genberegnes for klienter med exclude/only).
    public void Broadcast(string eventName, string json)
    {
        if (_clients.IsEmpty) return;
        if (eventName != "snapshot" || _clients.Values.All(c => c.Exclude is null && c.Only is null))
        {
            EnqueueAll(Frame(eventName, json), null);
            return;
        }
        var cache = new Dictionary<string, byte[]>();
        foreach (var (id, c) in _clients)
        {
            byte[] frame;
            if (c.Exclude is null && c.Only is null) frame = cache.TryGetValue("", out var f0) ? f0 : cache[""] = Frame(eventName, json);
            else
            {
                var key = c.FilterKey;
                if (!cache.TryGetValue(key, out frame!))
                    frame = cache[key] = Frame(eventName, _store.SnapshotJson(c.Exclude, c.Only));
            }
            if (!c.Queue.Writer.TryWrite(frame)) RemoveClient(id);
        }
    }

    public void BroadcastFeed(string topic, JsonNode? data, string? ts)
    {
        if (_clients.IsEmpty) return;
        var payload = new JsonObject
        {
            ["topic"] = topic,
            ["data"] = data?.DeepClone(),
            ["ts"] = ts,
        };
        EnqueueAll(Frame("feed", payload.ToJsonString()), topic);
    }

    private void RemoveClient(Guid id)
    {
        if (_clients.TryRemove(id, out var c))
        {
            c.Queue.Writer.TryComplete();
            try { c.Res.Abort(); } catch { /* ignorér */ }
            ClientCountChanged?.Invoke(_clients.Count);
            Log?.Invoke($"SSE-klient fra ({_clients.Count} tilbage)");
        }
    }

    // ---------- Optagelser ----------

    /// Liste over optagelser: filnavn, sessionsnavn (header), størrelse,
    /// ændret-tid og varighed (sidste linjes t, læst fra filens hale) +
    /// headerens ekstra felter (meeting, sessionName, path, source …).
    private string ListRecordingsJson()
    {
        var arr = new JsonArray();
        if (_recordingsDir is not null && Directory.Exists(_recordingsDir))
        {
            var files = new DirectoryInfo(_recordingsDir).GetFiles("*.f1rec")
                .OrderByDescending(f => f.LastWriteTimeUtc);
            foreach (var f in files)
            {
                var item = new JsonObject
                {
                    ["file"] = f.Name,
                    ["session"] = "",
                    ["sizeBytes"] = f.Length,
                    ["modified"] = f.LastWriteTimeUtc.ToString("o"),
                    ["durationMs"] = 0L,
                };
                try
                {
                    using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs);
                    var header = reader.ReadLine();
                    if (header is not null && JsonNode.Parse(header) is JsonObject h)
                    {
                        item["session"] = h["session"]?.GetValue<string>() ?? "";
                        foreach (var key in new[] { "started", "meeting", "sessionName", "sessionType", "path", "source", "sessionKey" })
                            if (h[key] is { } v) item[key] = v.DeepClone();
                    }
                    item["durationMs"] = ReadLastT(fs);
                }
                catch (Exception) { /* korrupt fil — vis den alligevel */ }
                var stem = Path.GetFileNameWithoutExtension(f.Name);
                item["live"] = IsRecordingActive?.Invoke(f.Name) ?? false;
                item["media"] = Directory.Exists(Path.Combine(_recordingsDir, stem)) ? stem : null;
                arr.Add(item);
            }
        }
        return arr.ToJsonString();
    }

    private static long ReadLastT(FileStream fs)
    {
        var tailLen = (int)Math.Min(65536, fs.Length);
        if (tailLen == 0) return 0;
        fs.Seek(-tailLen, SeekOrigin.End);
        var buf = new byte[tailLen];
        fs.ReadExactly(buf, 0, tailLen);
        var text = Encoding.UTF8.GetString(buf);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var t = RecordingFilter.PeekT(lines[i]);
            if (t is not null) return t.Value;
        }
        return 0;
    }

    /// Navnet skal være et rent filnavn med .f1rec — ingen stier.
    public static bool IsSafeRecordingName(string name)
        => name.Length > 6 && !name.Contains('/') && !name.Contains('\\') && !name.Contains("..") &&
           name.EndsWith(".f1rec", StringComparison.OrdinalIgnoreCase);

    private async Task ServeRecordingAsync(HttpListenerContext ctx, string name)
    {
        var res = ctx.Response;
        if (_recordingsDir is null || !IsSafeRecordingName(name))
        {
            res.StatusCode = 404;
            WriteText(res, "Ukendt optagelse");
            return;
        }
        var path = Path.Combine(_recordingsDir, name);
        if (!File.Exists(path))
        {
            res.StatusCode = 404;
            WriteText(res, "Ukendt optagelse");
            return;
        }

        // Filen kan stadig være under optagelse — åbn med delt skrivning
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1 << 16, useAsync: true);
        var q = ctx.Request.QueryString;
        var profile = RecordingFilter.ParseProfile(q["profile"]);

        if (profile != RecordingFilter.Profile.Full || q["from"] is not null)
        {
            // Filtreret/halet: kun komplette linjer fra rå-offset "from".
            long.TryParse(q["from"], out var from);
            var end = RecordingFilter.CompleteEnd(fs);
            if (from < 0 || from > end) from = end;
            res.ContentType = "application/x-ndjson";
            res.Headers["X-Next-Offset"] = end.ToString();
            res.SendChunked = true;
            await RecordingFilter.WriteFilteredAsync(fs, from, end, res.OutputStream, profile);
            res.Close();
            return;
        }

        res.ContentType = "application/octet-stream";
        var total = fs.Length;
        // Range-support ("bytes=N-") til forsinket live: klienten haler
        // kun nye bytes. Ende-delen af range ignoreres (altid til EOF).
        long start = 0;
        var rangeHeader = ctx.Request.Headers["Range"];
        if (rangeHeader is not null && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec = rangeHeader[6..];
            var dash = spec.IndexOf('-');
            if (dash > 0 && long.TryParse(spec[..dash], out var s2)) start = s2;
        }
        if (start >= total && total > 0)
        {
            res.StatusCode = 416;
            res.Headers["Content-Range"] = $"bytes */{total}";
            res.Close();
            return;
        }
        if (start > 0)
        {
            res.StatusCode = 206;
            res.Headers["Content-Range"] = $"bytes {start}-{total - 1}/{total}";
            fs.Seek(start, SeekOrigin.Begin);
        }
        res.ContentLength64 = total - start;
        var buffer = new byte[1 << 16];
        var remaining = total - start;
        while (remaining > 0)
        {
            var n = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
            if (n <= 0) break;
            await res.OutputStream.WriteAsync(buffer.AsMemory(0, n));
            remaining -= n;
        }
        res.Close();
    }

    public static void WriteJson(HttpListenerResponse res, string json, int status = 200)
    {
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(json);
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.Close();
    }

    public static void WriteText(HttpListenerResponse res, string text, int? status = null)
    {
        if (status is not null) res.StatusCode = status.Value;
        res.ContentType = "text/plain; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(text);
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.Close();
    }

    public void Dispose()
    {
        _keepalive?.Dispose();
        foreach (var (id, _) in _clients) RemoveClient(id);
        try { _listener?.Stop(); } catch { /* ignorér */ }
        try { _listener?.Close(); } catch { /* ignorér */ }
    }
}
