// ServerConfig — miljøvariabler (Docker/.env) med valgfri /data/settings.json
// som supplement. Miljøvariabler vinder altid.

using System.Text.Json.Nodes;

namespace SlqF1Live.Server;

public sealed class ServerConfig
{
    public int Port { get; init; } = 8137;
    public string DataDir { get; init; } = "/data";
    public string RecordingsDir { get; init; } = "/data/recordings";
    public string TokenFile => Path.Combine(DataDir, "f1token.json");
    public string ScheduleCacheFile => Path.Combine(DataDir, "schedule.json");
    public string LogDir => Path.Combine(DataDir, "logs");
    public string WwwRoot { get; init; } = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    public string AllowOrigin { get; init; } = "https://f1.slq.dk";

    /// auto = efter sæsonplan + F1's stream-status; always = altid forbundet; off = aldrig.
    public string FeedMode { get; init; } = "auto";
    public TimeSpan WindowBefore { get; init; } = TimeSpan.FromMinutes(75);
    public TimeSpan WindowAfter { get; init; } = TimeSpan.FromMinutes(90);

    public string? F1Username { get; init; }
    public string? F1Password { get; init; }
    public string? F1Token { get; init; }

    public string AdminAllow { get; init; } = "127.0.0.0/8,::1/128,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16";
    public string? AdminToken { get; init; }
    public string TrustedProxies { get; init; } = "";
    public string ViewAllow { get; init; } = "";
    public string? ViewToken { get; init; }

    public bool ArchiveDownload { get; init; } = true;
    public bool TeamRadioDownload { get; init; } = true;
    public int LogRetentionDays { get; init; } = 14;

    public static ServerConfig Load()
    {
        var dataDir = Env("DATA_DIR") ?? "/data";
        JsonNode? file = null;
        var settingsPath = Path.Combine(dataDir, "settings.json");
        if (File.Exists(settingsPath))
        {
            try { file = JsonNode.Parse(File.ReadAllText(settingsPath)); }
            catch (System.Text.Json.JsonException ex) { Console.Error.WriteLine($"settings.json ignoreret: {ex.Message}"); }
        }

        string? Get(string env, string json) => Env(env) ?? (file?[json] is JsonValue v ? v.ToString() : null);
        bool Bool(string env, string json, bool def)
            => Get(env, json) is { } s ? s.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "ja" : def;

        var feedMode = Get("FEED_MODE", "feedMode")?.Trim().ToLowerInvariant();
        if (feedMode is null && file?["autoStartFeed"] is JsonValue asf && asf.TryGetValue<bool>(out var auto) && !auto)
            feedMode = "off"; // kompatibilitet med Windows-app'ens settings.json
        var before = int.TryParse(Get("WINDOW_BEFORE_MIN", "windowBeforeMin"), out var b) ? b : 75;
        var after = int.TryParse(Get("WINDOW_AFTER_MIN", "windowAfterMin"), out var a) ? a : 90;

        var cfg = new ServerConfig
        {
            Port = int.TryParse(Get("PORT", "port"), out var p) ? p : 8137,
            DataDir = dataDir,
            RecordingsDir = Get("RECORDINGS_DIR", "recordingsDir") ?? Path.Combine(dataDir, "recordings"),
            WwwRoot = Get("WWW_DIR", "wwwDir") ?? Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            AllowOrigin = Get("ALLOW_ORIGIN", "allowOrigin") ?? "https://f1.slq.dk",
            FeedMode = feedMode is "always" or "off" ? feedMode : "auto",
            WindowBefore = TimeSpan.FromMinutes(before),
            WindowAfter = TimeSpan.FromMinutes(after),
            F1Username = Get("F1_USERNAME", "f1Username"),
            F1Password = Get("F1_PASSWORD", "f1Password"),
            F1Token = Get("F1_TOKEN", "authToken"),
            AdminAllow = Get("ADMIN_ALLOW", "adminAllow") ?? "127.0.0.0/8,::1/128,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16",
            AdminToken = Get("ADMIN_TOKEN", "adminToken"),
            TrustedProxies = Get("TRUSTED_PROXIES", "trustedProxies") ?? "",
            ViewAllow = Get("VIEW_ALLOW", "viewAllow") ?? "",
            ViewToken = Get("VIEW_TOKEN", "viewToken"),
            ArchiveDownload = Bool("ARCHIVE_DOWNLOAD", "archiveDownload", true),
            TeamRadioDownload = Bool("TEAMRADIO_DOWNLOAD", "teamRadioDownload", true),
            LogRetentionDays = int.TryParse(Get("LOG_RETENTION_DAYS", "logRetentionDays"), out var lr) ? lr : 14,
        };
        if (Get("LOGIN_USER_AGENT", "loginUserAgent") is { Length: > 0 } ua) SlqF1Live.Core.F1Login.UserAgent = ua;
        // Kun til test mod en lokal mock af F1's SignalR Core-server
        if (Get("F1_LIVETIMING_URL", "f1LivetimingUrl") is { Length: > 0 } lt) SlqF1Live.Core.F1FeedClient.BaseUrl = lt;
        return cfg;
    }

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }
}
