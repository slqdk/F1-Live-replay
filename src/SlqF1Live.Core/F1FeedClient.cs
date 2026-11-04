// F1FeedClient — SignalR *Core* mod livetiming.formula1.com/signalrcore.
// (F1 nedlagde det åbne klassiske /signalr-endpoint; det svarer nu 401.)
// Flow: POST negotiate → wss://…/signalrcore?id=… → handshake
// {"protocol":"json","version":1}\x1e → Subscribe-invocation →
// records adskilt af 0x1E: type 1 = feed-invocation, type 3 = completion
// (Subscribe-resultat = snapshot), type 6 = ping, type 7 = luk.
//
// 24/7-robusthed: klienten sender selv ping hvert 15. s, en watchdog
// genforbinder hvis intet er modtaget i 60 s, og Reconnect() tvinger en
// frisk forbindelse (= frisk snapshot). Tokenet hentes via TokenProvider
// ved HVER forbindelse, så et fornyet token bruges ved næste connect.
// Afviser F1 tokenet (401/403), affyres AuthRejected, og der forbindes
// uden token indtil TokenProvider leverer et andet token.

using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace SlqF1Live.Core;

public sealed record FeedItem(string Topic, JsonNode? Data, string? Timestamp);

public enum RecordKind { None, Handshake, Feed, Snapshot, Ping, Close }

public sealed class F1AuthRejectedException(string message) : Exception(message);

public sealed class F1FeedClient : IDisposable
{
    /// Kan peges mod en lokal mock-server i test (F1_LIVETIMING_URL); ellers F1.
    public static string BaseUrl { get; set; } = "https://livetiming.formula1.com";
    public static string NegotiateUrl => BaseUrl.TrimEnd('/') + "/signalrcore/negotiate?negotiateVersion=1";
    public static string ConnectBase =>
        (BaseUrl.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws") +
        BaseUrl.TrimEnd('/')[BaseUrl.IndexOf(':')..] + "/signalrcore";
    public const char RecordSeparator = '\u001e';

    // ALLE kendte topics fra F1's live timing — optages råt, så fremtidige
    // visninger kan bygges oven på gamle optagelser uden at mangle data.
    // Ukendte topics afvises ikke af F1 (verificeret 4/11-2026). Kræver
    // F1 TV: CarData.z, Position.z, ChampionshipPrediction, DriverRaceInfo,
    // PitStopSeries, PitStop m.fl. — resten flyder uautentificeret.
    public static readonly string[] Topics =
    {
        "Heartbeat", "SessionInfo", "SessionData", "SessionStatus", "ArchiveStatus",
        "TrackStatus", "LapCount", "DriverList", "TimingData", "TimingDataF1",
        "TimingAppData", "TimingStats", "WeatherData", "WeatherDataSeries",
        "RaceControlMessages", "RcmSeries", "TlaRcm", "CarData.z", "Position.z",
        "ExtrapolatedClock", "TopThree", "TeamRadio", "ContentStreams", "AudioStreams",
        "PitLaneTimeCollection", "ChampionshipPrediction", "TyreStintSeries",
        "CurrentTyres", "DriverRaceInfo", "SPFeed", "LapSeries", "PitStopSeries",
        "PitStop", "OvertakeSeries", "DriverScore", "DriverTracker",
    };

    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public event Action<JsonObject>? Snapshot;
    public event Action<FeedItem>? Feed;
    public event Action<string>? Status;
    /// F1 afviste tokenet — ejeren bør skaffe et nyt (login igen).
    public event Action<string>? AuthRejected;

    /// Valgfrit fast F1 TV-token (bruges hvis TokenProvider ikke er sat).
    public string? AuthToken { get; set; }
    /// Kaldes ved hver forbindelse — returnér aktuelt token eller null.
    public Func<string?>? TokenProvider { get; set; }

    public bool Wanted { get; private set; }
    public bool Connected => _ws?.State == WebSocketState.Open;
    public bool UsingToken { get; private set; }
    public DateTime? ConnectedSinceUtc { get; private set; }
    public DateTime? LastRecordUtc { get; private set; }
    public long RecordsReceived => Interlocked.Read(ref _records);
    public string LastStatus { get; private set; } = "stoppet";

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private int _backoffMs = 5000;
    private long _records;
    private volatile bool _reconnectNow;
    private string? _rejectedToken;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public void Start()
    {
        if (Wanted) return;
        Wanted = true;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void Stop()
    {
        if (!Wanted) return;
        Wanted = false;
        _cts?.Cancel();
        try { _ws?.Abort(); } catch { /* ignorér */ }
        _ws = null;
        ConnectedSinceUtc = null;
        SetStatus("stoppet");
    }

    /// Afbryd og forbind straks igen (frisk snapshot, evt. nyt token).
    public void Reconnect()
    {
        if (!Wanted) return;
        _reconnectNow = true;
        try { _ws?.Abort(); } catch { /* ignorér */ }
    }

    private void SetStatus(string s)
    {
        LastStatus = s;
        Status?.Invoke(s);
    }

    private string? CurrentToken()
    {
        var t = TokenProvider is not null ? TokenProvider() : AuthToken;
        if (string.IsNullOrEmpty(t)) return null;
        // Et afvist token bruges ikke igen før der kommer et andet
        return t == _rejectedToken ? null : t;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (Wanted && !ct.IsCancellationRequested)
        {
            var delay = _backoffMs;
            try
            {
                await ConnectOnceAsync(ct);
                _backoffMs = 5000;
                delay = 2000;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (F1AuthRejectedException ex)
            {
                SetStatus("token afvist: " + ex.Message);
                AuthRejected?.Invoke(ex.Message);
                delay = 1000; // prøv straks igen uden token
            }
            catch (Exception ex) when (_reconnectNow)
            {
                _ = ex; // forventet: Reconnect() afbrød forbindelsen
            }
            catch (Exception ex)
            {
                SetStatus($"fejl: {ex.Message}");
            }
            finally
            {
                ConnectedSinceUtc = null;
            }

            if (!Wanted || ct.IsCancellationRequested) break;
            if (_reconnectNow)
            {
                _reconnectNow = false;
                SetStatus("genforbinder");
                continue;
            }
            SetStatus($"genforbinder om {Math.Max(1, delay / 1000)} s");
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
            if (delay == _backoffMs) _backoffMs = Math.Min(_backoffMs * 2, 60000);
        }
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        SetStatus("negotiate…");
        var token = CurrentToken();
        UsingToken = token is not null;
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SLQ-F1-Live/2.0");
        if (token is not null)
            http.DefaultRequestHeaders.Authorization = new("Bearer", token);

        using var negRes = await http.PostAsync(NegotiateUrl, content: null, ct);
        var negJson = await negRes.Content.ReadAsStringAsync(ct);
        if (token is not null && negRes.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _rejectedToken = token;
            throw new F1AuthRejectedException($"negotiate HTTP {(int)negRes.StatusCode}");
        }
        if (!negRes.IsSuccessStatusCode)
            throw new InvalidOperationException($"negotiate HTTP {(int)negRes.StatusCode}: {Snippet(negJson)}");
        var connId = ParseNegotiateId(negJson)
            ?? throw new InvalidOperationException($"negotiate uden connectionToken/-Id: {Snippet(negJson)}");

        using var ws = new ClientWebSocket();
        _ws = ws;
        try
        {
            await RunSocketAsync(ws, cookies, connId, token, ct);
        }
        finally
        {
            if (ReferenceEquals(_ws, ws)) _ws = null;
        }
    }

    private async Task RunSocketAsync(ClientWebSocket ws, CookieContainer cookies, string connId,
        string? token, CancellationToken ct)
    {
        ws.Options.Cookies = cookies;
        ws.Options.SetRequestHeader("User-Agent", "SLQ-F1-Live/2.0");
        if (token is not null)
            ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
        ws.Options.CollectHttpResponseDetails = true;
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        SetStatus("forbinder…");
        try
        {
            await ws.ConnectAsync(new Uri(BuildConnectUrl(connId)), ct);
        }
        catch (WebSocketException ex)
        {
            if (token is not null && ws.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _rejectedToken = token;
                throw new F1AuthRejectedException($"websocket HTTP {(int)ws.HttpStatusCode}");
            }
            var httpCode = ws.HttpStatusCode != 0 ? $" (HTTP {(int)ws.HttpStatusCode})" : "";
            throw new InvalidOperationException($"websocket-handshake fejlede{httpCode}: {ex.Message}", ex);
        }

        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(IdleTimeout);
        await SendRecordAsync(ws, BuildHandshake(), ct);
        SetStatus("handshake sendt");
        ConnectedSinceUtc = DateTime.UtcNow;

        using var pinger = new Timer(_ => _ = PingAsync(ws), null, PingInterval, PingInterval);

        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        var textBuf = new StringBuilder();
        var subscribed = false;

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, idle.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        SetStatus("forbindelse lukket af serveren");
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                idle.CancelAfter(IdleTimeout);
                LastRecordUtc = DateTime.UtcNow;
                textBuf.Append(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                foreach (var record in DrainRecords(textBuf))
                {
                    Interlocked.Increment(ref _records);
                    var (kind, snapshot, feed) = ParseRecord(record);
                    switch (kind)
                    {
                        case RecordKind.Handshake:
                            if (!subscribed)
                            {
                                subscribed = true;
                                SetStatus(UsingToken ? "forbundet (F1 TV) — abonnerer" : "forbundet — abonnerer");
                                await SendRecordAsync(ws, BuildSubscribeInvocation(Topics), ct);
                            }
                            break;
                        case RecordKind.Snapshot:
                            if (snapshot is not null)
                            {
                                SetStatus(UsingToken ? "forbundet (F1 TV)" : "forbundet");
                                Snapshot?.Invoke(snapshot);
                            }
                            break;
                        case RecordKind.Feed:
                            if (feed is not null) Feed?.Invoke(feed);
                            break;
                        case RecordKind.Ping:
                            break; // vores egen ping-timer holder forbindelsen i live
                        case RecordKind.Close:
                            SetStatus("serveren bad om lukning");
                            return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && idle.IsCancellationRequested)
        {
            SetStatus($"intet modtaget i {IdleTimeout.TotalSeconds:0} s — genforbinder");
        }
    }

    private async Task PingAsync(ClientWebSocket ws)
    {
        try
        {
            if (ws.State == WebSocketState.Open)
                await SendRecordAsync(ws, "{\"type\":6}", CancellationToken.None);
        }
        catch { /* forbindelsen er død — receive-løkken opdager det */ }
    }

    private async Task SendRecordAsync(ClientWebSocket ws, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json + RecordSeparator);
        await _sendLock.WaitAsync(ct);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { _sendLock.Release(); }
    }

    // ---------- Ren protokol-logik (testbar uden netværk) ----------

    /// negotiateVersion=1 giver connectionToken; ellers connectionId.
    public static string? ParseNegotiateId(string negotiateJson)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(negotiateJson); } catch { return null; }
        return node?["connectionToken"]?.GetValue<string>()
            ?? node?["connectionId"]?.GetValue<string>();
    }

    public static string BuildConnectUrl(string connectionId)
        => $"{ConnectBase}?id={Uri.EscapeDataString(connectionId)}";

    public static string BuildHandshake()
        => "{\"protocol\":\"json\",\"version\":1}";

    public static string BuildSubscribeInvocation(IEnumerable<string> topics)
    {
        var arr = new JsonArray();
        foreach (var t in topics) arr.Add(t);
        return new JsonObject
        {
            ["type"] = 1,
            ["invocationId"] = "1",
            ["target"] = "Subscribe",
            ["arguments"] = new JsonArray { arr },
        }.ToJsonString();
    }

    /// Træk komplette records (afsluttet med 0x1E) ud af tekstbufferen.
    public static List<string> DrainRecords(StringBuilder buf)
    {
        var records = new List<string>();
        var s = buf.ToString();
        int start = 0, idx;
        while ((idx = s.IndexOf(RecordSeparator, start)) >= 0)
        {
            var rec = s[start..idx];
            if (rec.Length > 0) records.Add(rec);
            start = idx + 1;
        }
        buf.Clear();
        if (start < s.Length) buf.Append(s, start, s.Length - start);
        return records;
    }

    /// Klassificér ét SignalR Core-record.
    /// Handshake-svaret er "{}" (uden "type"). type 1+target=="feed" med
    /// arguments [topic, data, ts] er en patch; type 3 med result-objekt er
    /// Subscribe-snapshottet; type 6 = ping; type 7 = close.
    public static (RecordKind Kind, JsonObject? Snapshot, FeedItem? Feed) ParseRecord(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); } catch { return (RecordKind.None, null, null); }
        if (node is not JsonObject obj) return (RecordKind.None, null, null);

        int? type;
        try { type = obj["type"]?.GetValue<int>(); } catch { return (RecordKind.None, null, null); }
        if (type is null)
            return (RecordKind.Handshake, null, null); // "{}" eller {"error":…}

        switch (type)
        {
            case 1:
                if (obj["target"]?.GetValue<string>() == "feed" &&
                    obj["arguments"] is JsonArray a && a.Count >= 2 &&
                    a[0] is JsonValue tv && tv.TryGetValue<string>(out var topic))
                {
                    string? ts = null;
                    if (a.Count > 2 && a[2] is JsonValue tsv && tsv.TryGetValue<string>(out var tss)) ts = tss;
                    return (RecordKind.Feed, null, new FeedItem(topic, a[1]?.DeepClone(), ts));
                }
                return (RecordKind.None, null, null);
            case 3:
                if (obj["result"] is JsonObject r)
                    return (RecordKind.Snapshot, (JsonObject)r.DeepClone(), null);
                return (RecordKind.None, null, null);
            case 6:
                return (RecordKind.Ping, null, null);
            case 7:
                return (RecordKind.Close, null, null);
            default:
                return (RecordKind.None, null, null);
        }
    }

    private static string Snippet(string s)
    {
        var one = s.Replace("\r", " ").Replace("\n", " ");
        return one.Length > 200 ? one[..200] + "…" : one;
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _ws?.Dispose();
        _sendLock.Dispose();
    }
}
