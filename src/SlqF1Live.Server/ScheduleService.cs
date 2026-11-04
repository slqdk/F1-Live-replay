// ScheduleService — sæsonplan (Jolpica, cachet på disk) + F1's stream-status.

using System.Text.Json.Nodes;
using SlqF1Live.Core;

namespace SlqF1Live.Server;

public sealed class ScheduleService
{
    private readonly HttpClient _http;
    private readonly ServerConfig _cfg;
    private readonly LogBuffer _log;
    private readonly object _lock = new();
    private List<ScheduledSession> _sessions = new();

    public DateTime? LastRefreshUtc { get; private set; }
    public string? StreamingStatus { get; private set; }
    public DateTime? StreamingCheckedUtc { get; private set; }

    public ScheduleService(HttpClient http, ServerConfig cfg, LogBuffer log)
    {
        _http = http;
        _cfg = cfg;
        _log = log;
    }

    public List<ScheduledSession> Sessions { get { lock (_lock) return _sessions.ToList(); } }

    public async Task RefreshAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var years = new List<int> { now.Year };
        if (now.Month >= 11) years.Add(now.Year + 1);
        var all = new List<ScheduledSession>();
        var ok = false;
        foreach (var y in years)
        {
            try
            {
                var json = await _http.GetStringAsync(F1Schedule.JolpicaUrl(y), ct);
                all.AddRange(F1Schedule.ParseJolpica(json));
                ok = true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                if (ct.IsCancellationRequested) throw;
                _log.Info($"sæsonplan {y}: kunne ikke hentes ({ex.Message})");
            }
        }
        if (ok && all.Count > 0)
        {
            lock (_lock) _sessions = all.OrderBy(s => s.StartUtc).ToList();
            LastRefreshUtc = now;
            SaveCache(all);
            var next = F1Schedule.Next(all, now);
            _log.Info($"sæsonplan: {all.Count} sessioner" + (next is null ? "" :
                $" — næste: {next.Meeting} {next.Name} {next.StartUtc.ToLocalTime():ddd dd/MM HH:mm}"));
        }
        else if (Sessions.Count == 0)
        {
            LoadCache();
        }
    }

    public async Task PollStreamingAsync(CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync(F1Schedule.StreamingStatusUrl, ct);
            var status = F1Schedule.ParseStreamingStatus(json);
            if (status is not null)
            {
                if (status != StreamingStatus) _log.Info($"F1-stream: {status}");
                StreamingStatus = status;
                StreamingCheckedUtc = DateTime.UtcNow;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
        }
    }

    public ScheduledSession? ActiveWindow(DateTime nowUtc)
        => F1Schedule.ActiveWindow(Sessions, nowUtc, _cfg.WindowBefore, _cfg.WindowAfter);

    public List<ScheduledSession> Upcoming(DateTime nowUtc, int count)
        => Sessions.Where(s => s.EndUtc > nowUtc).OrderBy(s => s.StartUtc).Take(count).ToList();

    private void SaveCache(List<ScheduledSession> sessions)
    {
        try
        {
            var arr = new JsonArray();
            foreach (var s in sessions)
                arr.Add(new JsonObject
                {
                    ["season"] = s.Season, ["round"] = s.Round, ["meeting"] = s.Meeting, ["name"] = s.Name,
                    ["startUtc"] = s.StartUtc.ToString("o"), ["minutes"] = (int)s.Duration.TotalMinutes,
                });
            File.WriteAllText(_cfg.ScheduleCacheFile, arr.ToJsonString());
        }
        catch (IOException) { /* ikke kritisk */ }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cfg.ScheduleCacheFile)) return;
            var list = new List<ScheduledSession>();
            if (JsonNode.Parse(File.ReadAllText(_cfg.ScheduleCacheFile)) is JsonArray arr)
                foreach (var n in arr.OfType<JsonObject>())
                    list.Add(new ScheduledSession(
                        n["season"]!.GetValue<int>(), n["round"]!.GetValue<int>(),
                        n["meeting"]!.GetValue<string>(), n["name"]!.GetValue<string>(),
                        DateTime.Parse(n["startUtc"]!.GetValue<string>(), null,
                            System.Globalization.DateTimeStyles.AdjustToUniversal),
                        TimeSpan.FromMinutes(n["minutes"]!.GetValue<int>())));
            lock (_lock) _sessions = list;
            _log.Info($"sæsonplan: {list.Count} sessioner fra cache");
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or NullReferenceException)
        {
            _log.Info("sæsonplan-cache ulæselig: " + ex.Message);
        }
    }
}
