// ServerHost — samler feed, tilstand, optager, login, plan, medier og HTTP.
//
// Feed-styring (FEED_MODE=auto): forbind når en planlagt session er inden for
// vinduet [start − 75 min, slut + 90 min (+90 for løb)], når F1's
// StreamingStatus er "Available", eller mens der optages. Afbryd 10 min efter
// at ingen af delene gælder. Nyt token midt i en session tages først i brug
// efter sessionen (en genforbindelse giver et lille hul i optagelsen) —
// undtagen hvis forbindelsen kører uden token, så skiftes straks (telemetri).

using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using SlqF1Live.Core;

namespace SlqF1Live.Server;

public sealed partial class ServerHost : IDisposable
{
    public ServerConfig Cfg { get; }
    public LogBuffer Log { get; }
    public StateStore Store { get; } = new();
    public F1FeedClient Feed { get; } = new();
    public TokenManager Tokens { get; }
    public RecordingManager Rec { get; }
    public ScheduleService Schedule { get; }
    public MediaService Media { get; }
    public SseServer Server { get; }
    public AccessControl Access { get; }
    public DevService Dev { get; }
    public TranscriptService Transcripts { get; }
    public HttpClient Http { get; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public static string Version { get; } =
        typeof(ServerHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "2.0.0";

    private readonly ConcurrentDictionary<string, TopicStat> _topicStats = new();
    private string? _modeOverride;
    private DateTime _lastWantUtc = DateTime.MinValue;
    private volatile bool _pendingTokenReconnect;
    private string _wantReason = "";
    private CancellationToken _ct;
    private (DateTime At, long Bytes, int Files) _diskCache;

    private sealed class TopicStat
    {
        public long Count;
        public DateTime LastUtc;
    }

    public ServerHost(ServerConfig cfg, LogBuffer log)
    {
        Cfg = cfg;
        Log = log;
        Directory.CreateDirectory(cfg.DataDir);
        Directory.CreateDirectory(cfg.RecordingsDir);

        Http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        }) { Timeout = TimeSpan.FromSeconds(60) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"SLQ-F1-Live/{Version}");

        Access = new AccessControl(cfg);
        Dev = new DevService(cfg, log);
        Tokens = new TokenManager(cfg.TokenFile, null, log.Info)
        {
            Username = cfg.F1Username,
            Password = cfg.F1Password,
        };
        Rec = new RecordingManager(cfg.RecordingsDir, null, log.Info);
        Schedule = new ScheduleService(Http, cfg, log);
        Media = new MediaService(cfg, new F1ArchiveClient(Http, () => Tokens.Current), Rec, log, () => Tokens.Current);
        Transcripts = new TranscriptService(cfg, log, DriverNames);
        Media.RadioSaved += (dir, rel, live) => Transcripts.Enqueue(dir, rel, live);
        Server = new SseServer(cfg.Port, cfg.AllowOrigin, Store, () => Feed.Wanted ? Feed.LastStatus : "venter",
            () => Feed.Connected, cfg.RecordingsDir)
        {
            IsRecordingActive = Rec.IsActiveFile,
            ExtraRoutes = HandleRouteAsync,
            ExtraHealth = () => new JsonObject
            {
                ["version"] = Version,
                ["recording"] = Rec.Current?.FileStem,
                ["token"] = Tokens.Current is not null,
            },
        };
        Server.Log += m => { if (!m.StartsWith("SSE-klient", StringComparison.Ordinal)) log.Info(m); };

        Feed.TokenProvider = () => Tokens.Current;
        Wire();
    }

    private void Wire()
    {
        Feed.Status += s =>
        {
            Log.Info("feed: " + s);
            Server.Broadcast("status", new JsonObject { ["status"] = s }.ToJsonString());
        };
        Feed.Snapshot += snap =>
        {
            Rec.OnSnapshot(snap);
            var newPath = PathOf(snap["SessionInfo"]);
            var oldPath = PathOf(Store.GetTopic("SessionInfo"));
            if (newPath != oldPath) Store.Reset();
            Store.ApplySnapshot(snap);
            Server.Broadcast("snapshot", Store.SnapshotJson());
            Media.OnTeamRadio(snap["TeamRadio"], Rec.Current);
            var topics = string.Join(", ", snap.Select(kv => kv.Key));
            Log.Info($"snapshot: {snap.Count} topics ({topics})");
        };
        Feed.Feed += item =>
        {
            Rec.OnFeed(item);
            var (topic, data) = Store.Apply(item.Topic, item.Data, item.Timestamp);
            if (data is not null) Server.BroadcastFeed(topic, data, item.Timestamp);
            if (item.Topic == "TeamRadio") Media.OnTeamRadio(item.Data, Rec.Current);
            var st = _topicStats.GetOrAdd(item.Topic, _ => new TopicStat());
            Interlocked.Increment(ref st.Count);
            st.LastUtc = DateTime.UtcNow;
        };
        Feed.AuthRejected += msg =>
        {
            Tokens.MarkRejected(msg);
            _ = Task.Run(() => Tokens.EnsureValidAsync(_ct));
        };
        Tokens.TokenChanged += src =>
        {
            Media.OnTokenAvailable();
            if (!Feed.Wanted) return;
            if (!Feed.UsingToken || !LiveSessionRunning())
            {
                Log.Info("nyt F1-token — genforbinder");
                Feed.Reconnect();
            }
            else
            {
                _pendingTokenReconnect = true;
                Log.Info("nyt F1-token tages i brug efter sessionen (undgår hul i optagelsen)");
            }
        };
        Rec.ResubscribeRequested += _ => Feed.Reconnect();
        Rec.Closed += info =>
        {
            Media.ScheduleArchive(info);
            _topicStats.Clear();
            // OpenF1 har resultatet ca. 30 min efter sessionen — opdatér analysen
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(TimeSpan.FromMinutes(45), _ct); } catch (OperationCanceledException) { return; }
                Dev.Warm(_ct, force: true);
            });
        };
    }

    private static string? PathOf(JsonNode? si) => si?["Path"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// "Max Verstappen", "Charles Leclerc" … fra sessionens DriverList (prompt til whisper).
    private IReadOnlyList<string> DriverNames()
    {
        var list = new List<string>();
        if (Store.GetTopic("DriverList") is not JsonObject drivers) return list;
        foreach (var (_, d) in drivers)
        {
            if (d is not JsonObject o) continue;
            var first = o["FirstName"] is JsonValue f && f.TryGetValue<string>(out var fs) ? fs.Trim() : "";
            var last = o["LastName"] is JsonValue l && l.TryGetValue<string>(out var ls) ? ls.Trim() : "";
            var name = $"{first} {last}".Trim();
            if (name.Length > 0) list.Add(name);
        }
        return list;
    }

    public bool LiveSessionRunning()
        => Rec.IsRecording && Rec.SessionStatus is "Started" or "Inactive" or "Aborted" or "Finished";

    public string FeedMode => _modeOverride ?? Cfg.FeedMode;

    public void SetFeedMode(string? mode)
    {
        _modeOverride = mode is "always" or "off" or "auto" ? (mode == Cfg.FeedMode ? null : mode) : null;
        Log.Info($"feed-tilstand: {FeedMode}");
        DecideFeed(DateTime.UtcNow, force: true);
    }

    private void DecideFeed(DateTime now, bool force = false)
    {
        string? reason = FeedMode switch
        {
            "always" => "altid forbundet",
            "off" => null,
            _ => Schedule.ActiveWindow(now) is { } s ? $"planlagt: {s.Meeting} {s.Name}"
               : Schedule.StreamingStatus == "Available" ? "F1-stream tilgængelig"
               : Rec.IsRecording ? "optagelse i gang"
               : null,
        };
        if (reason is not null)
        {
            _lastWantUtc = now;
            _wantReason = reason;
            if (!Feed.Wanted)
            {
                Log.Info($"forbinder til F1 ({reason})");
                Feed.Start();
            }
        }
        else if (Feed.Wanted && (force || now - _lastWantUtc > TimeSpan.FromMinutes(10)))
        {
            Log.Info(FeedMode == "off" ? "feed slået fra" : "ingen aktiv session — afbryder F1-forbindelsen");
            Feed.Stop();
            _wantReason = "";
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _ct = ct;
        Log.Info($"SLQ F1 Live Server {Version} — port {Cfg.Port}, data {Cfg.DataDir}, feed {Cfg.FeedMode}");
        Log.Info($"admin tilladt fra: {Cfg.AdminAllow}" + (Cfg.TrustedProxies.Length > 0 ? $" · betroede proxies: {Cfg.TrustedProxies}" : " · ingen betroede proxies (TRUSTED_PROXIES)"));
        Tokens.Load(Cfg.F1Token);
        Server.Start();
        Media.Start(ct);
        Transcripts.Start(ct);

        try { await Schedule.RefreshAsync(ct); } catch (OperationCanceledException) { return; }
        try { await Schedule.PollStreamingAsync(ct); } catch (OperationCanceledException) { return; }
        _ = Task.Run(() => Tokens.EnsureValidAsync(ct), ct);
        Dev.Warm(ct);

        _ = Every(TimeSpan.FromSeconds(60), async c =>
        {
            await Schedule.PollStreamingAsync(c);
            if (Schedule.StreamingStatus is { } s) Rec.OnStreamingStatus(s);
        }, ct);
        _ = Every(TimeSpan.FromMinutes(10), c => Tokens.EnsureValidAsync(c), ct);
        _ = Every(TimeSpan.FromHours(6), c => Schedule.RefreshAsync(c), ct);

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { break; }
            try
            {
                Rec.Tick();
                DecideFeed(DateTime.UtcNow);
                if (_pendingTokenReconnect && !LiveSessionRunning())
                {
                    _pendingTokenReconnect = false;
                    if (Feed.Wanted) { Log.Info("tager nyt F1-token i brug"); Feed.Reconnect(); }
                }
            }
            catch (Exception ex) { Log.Info("fejl i hovedløkke: " + ex.Message); }
        }
    }

    private async Task Every(TimeSpan interval, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { return; }
            try { await action(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { Log.Info("baggrundsjob fejlede: " + ex.Message); }
        }
    }

    // ---------- Status ----------

    public JsonObject PublicStatus()
    {
        var now = DateTime.UtcNow;
        var cur = Rec.Current;
        var known = Rec.KnownSession;
        var next = new JsonArray();
        foreach (var s in Schedule.Upcoming(now, 4))
            next.Add(new JsonObject
            {
                ["meeting"] = s.Meeting,
                ["name"] = s.Name,
                ["startUtc"] = s.StartUtc.ToString("o"),
                ["endUtc"] = s.EndUtc.ToString("o"),
            });
        var carSeen = _topicStats.TryGetValue("CarData.z", out var cs) && now - cs.LastUtc < TimeSpan.FromMinutes(2);
        return new JsonObject
        {
            ["version"] = Version,
            ["serverUtc"] = now.ToString("o"),
            ["feed"] = new JsonObject
            {
                ["status"] = Feed.Wanted ? Feed.LastStatus : "venter på næste session",
                ["wanted"] = Feed.Wanted,
                ["connected"] = Feed.Connected,
                ["usingToken"] = Feed.Connected && Feed.UsingToken,
                ["telemetry"] = carSeen,
                ["reason"] = _wantReason,
                ["lastRecordUtc"] = Feed.LastRecordUtc?.ToString("o"),
            },
            ["recording"] = cur is null ? null : new JsonObject
            {
                ["file"] = Path.GetFileName(cur.FilePath),
                ["stem"] = cur.FileStem,
                ["session"] = cur.Session.Headline,
                ["startedUtc"] = cur.StartedUtc.ToString("o"),
                ["lines"] = cur.Lines,
                ["bytes"] = cur.Bytes,
                ["telemetry"] = cur.HasTelemetry,
            },
            ["session"] = known is null ? null : new JsonObject
            {
                ["headline"] = known.Headline,
                ["status"] = Rec.SessionStatus,
                ["path"] = known.Path,
                ["stem"] = known.FileStem,
            },
            ["streaming"] = Schedule.StreamingStatus,
            ["next"] = next,
            ["transcribe"] = Transcripts.PublicStatus(),
            ["clients"] = Server.ClientCount,
        };
    }

    public JsonObject AdminStatus()
    {
        var o = PublicStatus();
        var t = Tokens.Status;
        o["token"] = new JsonObject
        {
            ["hasToken"] = t.HasToken,
            ["expiresUtc"] = t.ExpiresUtc?.ToString("o"),
            ["product"] = t.Product,
            ["subscription"] = t.Subscription,
            ["source"] = t.Source,
            ["obtainedUtc"] = t.ObtainedUtc?.ToString("o"),
            ["credentials"] = t.CredentialsConfigured,
            ["lastAttemptUtc"] = t.LastAttemptUtc?.ToString("o"),
            ["nextAttemptUtc"] = t.NextAttemptUtc?.ToString("o"),
            ["lastError"] = t.LastError,
            ["rejected"] = t.Rejected,
            ["pendingReconnect"] = _pendingTokenReconnect,
        };
        var topics = new JsonObject();
        foreach (var (k, v) in _topicStats.OrderBy(k => k.Key, StringComparer.Ordinal))
            topics[k] = new JsonObject { ["count"] = v.Count, ["lastUtc"] = v.LastUtc.ToString("o") };
        o["topics"] = topics;
        var disk = DiskUsage();
        o["disk"] = disk;
        o["media"] = new JsonObject
        {
            ["radioDownloaded"] = Media.RadioDownloaded,
            ["radioWaitingForToken"] = Media.RadioParked,
            ["archivesPending"] = Media.PendingArchives,
        };
        o["transcribe"] = Transcripts.AdminStatus();
        var jobs = new JsonArray();
        foreach (var j in Media.Jobs)
            jobs.Add(new JsonObject
            {
                ["id"] = j.Id, ["kind"] = j.Kind, ["target"] = j.Target, ["state"] = j.State,
                ["message"] = j.Message, ["createdUtc"] = j.CreatedUtc.ToString("o"), ["endedUtc"] = j.EndedUtc?.ToString("o"),
            });
        o["jobs"] = jobs;
        o["config"] = new JsonObject
        {
            ["feedMode"] = FeedMode,
            ["configuredFeedMode"] = Cfg.FeedMode,
            ["windowBeforeMin"] = (int)Cfg.WindowBefore.TotalMinutes,
            ["windowAfterMin"] = (int)Cfg.WindowAfter.TotalMinutes,
            ["adminAllow"] = Cfg.AdminAllow,
            ["trustedProxies"] = Cfg.TrustedProxies,
            ["viewRestricted"] = Access.ViewRestricted,
            ["archiveDownload"] = Cfg.ArchiveDownload,
            ["teamRadioDownload"] = Cfg.TeamRadioDownload,
            ["allowOrigin"] = Cfg.AllowOrigin,
        };
        o["uptimeS"] = (long)(DateTime.UtcNow - StartedUtc).TotalSeconds;
        o["scheduleRefreshedUtc"] = Schedule.LastRefreshUtc?.ToString("o");
        return o;
    }

    private JsonObject DiskUsage()
    {
        if (DateTime.UtcNow - _diskCache.At > TimeSpan.FromMinutes(1))
        {
            long bytes = 0;
            var files = 0;
            try
            {
                foreach (var f in new DirectoryInfo(Cfg.RecordingsDir).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    bytes += f.Length;
                    if (f.Extension == ".f1rec") files++;
                }
            }
            catch (IOException) { /* ignorér */ }
            _diskCache = (DateTime.UtcNow, bytes, files);
        }
        long? free = null;
        try { free = new DriveInfo(Path.GetFullPath(Cfg.RecordingsDir)).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { /* ukendt */ }
        return new JsonObject
        {
            ["recordingsBytes"] = _diskCache.Bytes,
            ["recordings"] = _diskCache.Files,
            ["freeBytes"] = free,
        };
    }

    public void Dispose()
    {
        Log.Info("lukker ned…");
        Feed.Stop();
        Rec.Dispose();
        Server.Dispose();
        Dev.Dispose();
        Transcripts.Dispose();
        Http.Dispose();
    }
}
