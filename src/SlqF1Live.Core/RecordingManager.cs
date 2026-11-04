// RecordingManager — automatisk optagelse pr. session til 24/7-drift.
//
// Regler:
// - Én .f1rec pr. session, navngivet ud fra SessionInfo.Path, fx
//   "2026-11-06_Sao_Paulo_Grand_Prix_Practice_1.f1rec". Samme session efter
//   genforbindelse/genstart → der fortsættes (append) i samme fil.
// - Et snapshot af en allerede afsluttet session (Finalised/Ends + arkiv
//   Complete — det F1 serverer mellem weekenderne) optages IKKE.
// - Ny session i feedet (SessionInfo skifter) → aktuel fil lukkes og der
//   bedes om et frisk snapshot (ResubscribeRequested), som åbner den nye.
// - Lukning: 10 min efter "Ends", 30 min efter "Finalised", efter 45 min
//   uden data (Heartbeat tæller ikke), eller 20 min efter streamen gik offline.
// - Pr. session oprettes mappen <stem>/ til meta.json, teamradio og arkiv.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SlqF1Live.Core;

public sealed record SessionIdentity(
    string Id, string? Path, int? Key, string Meeting, string Name, string Type,
    string? StartDate, string? EndDate, string? GmtOffset, string FileStem, string Headline);

public sealed record RecordingInfo(
    string FileStem, string FilePath, string MediaDir, SessionIdentity Session,
    DateTime StartedUtc, DateTime? EndedUtc, long Lines, long Bytes,
    IReadOnlyDictionary<string, long> TopicCounts, string? CloseReason, bool Reopened)
{
    public bool HasTelemetry => TopicCounts.TryGetValue("CarData.z", out var c) && c > 0;
}

public sealed partial class RecordingManager : IDisposable
{
    private readonly string _dir;
    private readonly Func<DateTime> _clock;
    private readonly Action<string> _log;
    private readonly object _lock = new();
    private readonly List<Action> _pending = new();

    private SessionRecorder? _rec;
    private SessionIdentity? _recSession;
    private Dictionary<string, long> _topicCounts = new();
    private DateTime _lastActivity;
    private string? _endStatus;
    private DateTime? _endStatusSince;
    private DateTime? _offlineSince;

    private JsonNode? _sessionInfo;
    private string? _sessionStatus;
    private string? _archiveStatus;
    private JsonObject? _pendingSnapshot;
    private DateTime _lastResubscribe = DateTime.MinValue;
    private string? _lastStaleId;

    public TimeSpan IdleClose { get; set; } = TimeSpan.FromMinutes(45);
    public TimeSpan EndsGrace { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan FinalisedGrace { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan OfflineGrace { get; set; } = TimeSpan.FromMinutes(20);
    public TimeSpan ResubscribeMinInterval { get; set; } = TimeSpan.FromSeconds(60);

    public event Action<RecordingInfo>? Opened;
    public event Action<RecordingInfo>? Closed;
    public event Action<string>? ResubscribeRequested;

    public RecordingManager(string recordingsDir, Func<DateTime>? clock = null, Action<string>? log = null)
    {
        _dir = recordingsDir;
        _clock = clock ?? (() => DateTime.UtcNow);
        _log = log ?? (_ => { });
        Directory.CreateDirectory(_dir);
    }

    public string RecordingsDir => _dir;
    public bool IsRecording { get { lock (_lock) return _rec is not null; } }
    public string? SessionStatus { get { lock (_lock) return _sessionStatus; } }
    public string? ArchiveStatus { get { lock (_lock) return _archiveStatus; } }
    public SessionIdentity? KnownSession { get { lock (_lock) return Identify(_sessionInfo); } }

    public RecordingInfo? Current
    {
        get { lock (_lock) return _rec is null ? null : BuildInfoLocked(null, null); }
    }

    public bool IsActiveFile(string fileName)
    {
        lock (_lock)
            return _rec is not null && string.Equals(System.IO.Path.GetFileName(_rec.Path), fileName, StringComparison.Ordinal);
    }

    // ---------- Input fra feedet ----------

    public void OnSnapshot(JsonObject raw)
    {
        lock (_lock) OnSnapshotLocked(raw);
        RunPending();
    }

    private void OnSnapshotLocked(JsonObject raw)
    {
        var now = _clock();
        var si = raw["SessionInfo"];
        _sessionInfo = si?.DeepClone();
        _sessionStatus = FirstNonEmpty(Str(raw["SessionStatus"]?["Status"]), Str(si?["SessionStatus"]));
        _archiveStatus = FirstNonEmpty(Str(raw["ArchiveStatus"]?["Status"]), Str(si?["ArchiveStatus"]?["Status"]));
        UpdateEndTrackingLocked(now);

        var id = Identify(_sessionInfo);
        if (id is null)
        {
            _pendingSnapshot = (JsonObject)raw.DeepClone();
            _log("snapshot uden SessionInfo — venter på sessionsinfo");
            return;
        }
        _pendingSnapshot = null;

        if (_rec is not null && _recSession!.Id == id.Id)
        {
            _rec.WriteSnapshot(raw);
            CountLocked("(snapshot)");
            _lastActivity = now;
            return;
        }
        if (_rec is not null) CloseLocked("ny session i snapshot");

        if (IsStaleLocked(id, now))
        {
            if (_lastStaleId != id.Id)
            {
                _lastStaleId = id.Id;
                _log($"seneste session ({id.Headline}) er afsluttet — venter på næste session");
            }
            return;
        }
        OpenLocked(id, now);
        _rec!.WriteSnapshot(raw);
        CountLocked("(snapshot)");
    }

    public void OnFeed(FeedItem item)
    {
        lock (_lock)
        {
            var now = _clock();
            switch (item.Topic)
            {
                case "SessionInfo":
                    _sessionInfo = StateStore.DeepMerge(_sessionInfo ?? new JsonObject(), item.Data);
                    var ar = Str(_sessionInfo?["ArchiveStatus"]?["Status"]);
                    if (ar.Length > 0) _archiveStatus = ar;
                    var st = Str(_sessionInfo?["SessionStatus"]);
                    if (st.Length > 0) { _sessionStatus = st; UpdateEndTrackingLocked(now); }
                    var newId = Identify(_sessionInfo);
                    if (newId is not null)
                    {
                        if (_rec is not null && newId.Id != _recSession!.Id)
                        {
                            CloseLocked("ny session: " + newId.Headline);
                            RequestResubscribeLocked(now, "ny session: " + newId.Headline);
                        }
                        else if (_rec is null && !IsStaleLocked(newId, now))
                        {
                            if (_pendingSnapshot is not null)
                            {
                                OpenLocked(newId, now);
                                _rec!.WriteSnapshot(_pendingSnapshot);
                                _pendingSnapshot = null;
                            }
                            else RequestResubscribeLocked(now, "session aktiv: " + newId.Headline);
                        }
                    }
                    break;
                case "SessionStatus":
                    var s = Str(item.Data?["Status"]);
                    if (s.Length > 0)
                    {
                        _sessionStatus = s;
                        UpdateEndTrackingLocked(now);
                        var known = Identify(_sessionInfo);
                        if (_rec is null && known is not null && s is "Inactive" or "Started" or "Aborted" &&
                            !IsStaleLocked(known, now))
                            RequestResubscribeLocked(now, $"sessionsstatus {s}");
                    }
                    break;
                case "ArchiveStatus":
                    var a = Str(item.Data?["Status"]);
                    if (a.Length > 0) _archiveStatus = a;
                    break;
            }

            if (_rec is not null)
            {
                _rec.WriteFeed(item.Topic, item.Data, item.Timestamp);
                CountLocked(item.Topic);
                if (item.Topic != "Heartbeat") _lastActivity = now;
            }
        }
        RunPending();
    }

    public void OnStreamingStatus(string status)
    {
        lock (_lock)
        {
            if (status.Equals("Offline", StringComparison.OrdinalIgnoreCase)) _offlineSince ??= _clock();
            else _offlineSince = null;
        }
    }

    /// Kaldes periodisk (fx hvert sekund) — lukker afsluttede/tavse optagelser.
    public void Tick()
    {
        lock (_lock)
        {
            if (_rec is null) return;
            var now = _clock();
            string? reason = null;
            if (_endStatus == "Ends" && _endStatusSince is { } e1 && now - e1 >= EndsGrace)
                reason = "sessionen er slut (Ends)";
            else if (_endStatus == "Finalised" && _endStatusSince is { } e2 && now - e2 >= FinalisedGrace)
                reason = "resultatet er endeligt (Finalised)";
            else if (now - _lastActivity >= IdleClose)
                reason = $"ingen data i {IdleClose.TotalMinutes:0} min";
            else if (_offlineSince is { } off && now - off >= OfflineGrace)
                reason = "F1-streamen er offline";
            if (reason is not null) CloseLocked(reason);
        }
        RunPending();
    }

    public void CloseCurrent(string reason)
    {
        lock (_lock)
        {
            if (_rec is not null) CloseLocked(reason);
        }
        RunPending();
    }

    // ---------- Intern logik ----------

    private void RunPending()
    {
        Action[] actions;
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            actions = _pending.ToArray();
            _pending.Clear();
        }
        foreach (var a in actions)
        {
            try { a(); } catch (Exception ex) { _log("fejl i optagelses-event: " + ex.Message); }
        }
    }

    private void RequestResubscribeLocked(DateTime now, string reason)
    {
        if (now - _lastResubscribe < ResubscribeMinInterval) return;
        _lastResubscribe = now;
        _log("beder om frisk snapshot (" + reason + ")");
        _pending.Add(() => ResubscribeRequested?.Invoke(reason));
    }

    private void UpdateEndTrackingLocked(DateTime now)
    {
        var s = _sessionStatus;
        if (s is "Ends" or "Finalised")
        {
            if (_endStatus != s) { _endStatus = s; _endStatusSince = now; }
        }
        else if (!string.IsNullOrEmpty(s) && s != "Finished")
        {
            _endStatus = null;
            _endStatusSince = null;
        }
    }

    private bool IsStaleLocked(SessionIdentity id, DateTime now)
    {
        var status = _sessionStatus ?? "";
        var archiveDone = string.Equals(_archiveStatus, "Complete", StringComparison.OrdinalIgnoreCase);
        if (archiveDone && status is "Finalised" or "Ends") return true;
        // Slutter efter sessionens planlagte slut + 12 t → gammel
        if (SessionEndUtc(id) is { } end && now - end > TimeSpan.FromHours(12)) return true;
        return false;
    }

    /// EndDate er lokal tid; UTC = lokal − GmtOffset.
    public static DateTime? SessionEndUtc(SessionIdentity id)
    {
        if (string.IsNullOrEmpty(id.EndDate) ||
            !DateTime.TryParse(id.EndDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return null;
        var offset = TimeSpan.Zero;
        if (!string.IsNullOrEmpty(id.GmtOffset))
        {
            var g = id.GmtOffset.Trim();
            var neg = g.StartsWith('-');
            if (TimeSpan.TryParse(g.TrimStart('-', '+'), CultureInfo.InvariantCulture, out var ts))
                offset = neg ? -ts : ts;
        }
        return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
    }

    private void OpenLocked(SessionIdentity id, DateTime now)
    {
        var file = System.IO.Path.Combine(_dir, id.FileStem + ".f1rec");
        if (File.Exists(file) && !SessionRecorder.TryReadHeader(file, out _, out _))
        {
            // Ulæselig eksisterende fil overskrives aldrig — vælg nyt navn
            for (var i = 2; File.Exists(file); i++)
                file = System.IO.Path.Combine(_dir, $"{id.FileStem}_{i}.f1rec");
        }
        var header = new JsonObject
        {
            ["meeting"] = id.Meeting,
            ["sessionName"] = id.Name,
            ["sessionType"] = id.Type,
            ["sessionKey"] = id.Key,
            ["path"] = id.Path,
            ["startDate"] = id.StartDate,
            ["gmtOffset"] = id.GmtOffset,
            ["source"] = "live",
            ["recorder"] = "SlqF1Live.Server",
        };
        _rec = new SessionRecorder(file, id.Headline, header, append: true, _clock);
        _recSession = id;
        _lastActivity = now;
        _lastStaleId = null;
        _topicCounts = LoadCountsLocked(id) ?? new Dictionary<string, long>();
        Directory.CreateDirectory(MediaDirFor(id));
        var info = BuildInfoLocked(null, null);
        WriteMetaLocked(info, "recording");
        _log((_rec.Reopened ? "fortsætter optagelse: " : "optager: ") + System.IO.Path.GetFileName(file));
        _pending.Add(() => Opened?.Invoke(info));
    }

    private void CloseLocked(string reason)
    {
        if (_rec is null) return;
        var info = BuildInfoLocked(_clock(), reason);
        try { _rec.Dispose(); } catch (IOException ex) { _log("fejl ved lukning: " + ex.Message); }
        _rec = null;
        _recSession = null;
        WriteMetaLocked(info, "closed");
        _log($"optagelse afsluttet: {System.IO.Path.GetFileName(info.FilePath)} — {reason} " +
             $"({info.Lines} linjer, {info.Bytes / 1024 / 1024.0:0.0} MB{(info.HasTelemetry ? ", med telemetri" : "")})");
        _pending.Add(() => Closed?.Invoke(info));
    }

    private void CountLocked(string topic)
    {
        _topicCounts.TryGetValue(topic, out var c);
        _topicCounts[topic] = c + 1;
    }

    public string MediaDirFor(SessionIdentity id) => System.IO.Path.Combine(_dir, id.FileStem);

    private RecordingInfo BuildInfoLocked(DateTime? ended, string? reason)
    {
        var rec = _rec!;
        var id = _recSession!;
        long bytes = 0;
        try { bytes = new FileInfo(rec.Path).Length; } catch (IOException) { /* ignorér */ }
        return new RecordingInfo(
            System.IO.Path.GetFileNameWithoutExtension(rec.Path), rec.Path, MediaDirFor(id), id,
            rec.StartedUtc, ended, rec.Lines, bytes,
            new Dictionary<string, long>(_topicCounts), reason, rec.Reopened);
    }

    private Dictionary<string, long>? LoadCountsLocked(SessionIdentity id)
    {
        try
        {
            var meta = System.IO.Path.Combine(MediaDirFor(id), "meta.json");
            if (!File.Exists(meta)) return null;
            var node = JsonNode.Parse(File.ReadAllText(meta));
            if (node?["topics"] is not JsonObject topics) return null;
            var d = new Dictionary<string, long>();
            foreach (var kv in topics)
                if (kv.Value is JsonValue v && v.TryGetValue<long>(out var n)) d[kv.Key] = n;
            return d;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private void WriteMetaLocked(RecordingInfo info, string state)
    {
        try
        {
            var path = System.IO.Path.Combine(info.MediaDir, "meta.json");
            var existing = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
            var meta = existing ?? new JsonObject();
            meta["file"] = System.IO.Path.GetFileName(info.FilePath);
            meta["state"] = state;
            meta["session"] = info.Session.Headline;
            meta["meeting"] = info.Session.Meeting;
            meta["sessionName"] = info.Session.Name;
            meta["sessionType"] = info.Session.Type;
            meta["path"] = info.Session.Path;
            meta["sessionKey"] = info.Session.Key;
            meta["startedUtc"] = info.StartedUtc.ToString("o");
            meta["endedUtc"] = info.EndedUtc?.ToString("o");
            meta["closeReason"] = info.CloseReason;
            meta["lines"] = info.Lines;
            meta["bytes"] = info.Bytes;
            meta["telemetry"] = info.HasTelemetry;
            var topics = new JsonObject();
            foreach (var kv in info.TopicCounts.OrderBy(k => k.Key, StringComparer.Ordinal)) topics[kv.Key] = kv.Value;
            meta["topics"] = topics;
            Directory.CreateDirectory(info.MediaDir);
            File.WriteAllText(path, meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log("kunne ikke skrive meta.json: " + ex.Message);
        }
    }

    // ---------- Navngivning ----------

    public static SessionIdentity? Identify(JsonNode? si)
    {
        if (si is not JsonObject o) return null;
        var path = Str(o["Path"]);
        int? key = null;
        if (o["Key"] is JsonValue kv)
        {
            if (kv.TryGetValue<int>(out var ki)) key = ki;
            else if (kv.TryGetValue<string>(out var ks) && int.TryParse(ks, out var kp)) key = kp;
        }
        var meeting = Str(o["Meeting"]?["Name"]);
        var name = Str(o["Name"]);
        if (path.Length == 0 && key is null && meeting.Length == 0) return null;
        var start = Str(o["StartDate"]);
        var stem = FileStemFor(path, meeting, name, start, key);
        var id = path.Length > 0 ? path : key?.ToString(CultureInfo.InvariantCulture) ?? stem;
        var headline = (meeting + " — " + name).Trim(' ', '—');
        return new SessionIdentity(id, path.Length > 0 ? path : null, key, meeting, name, Str(o["Type"]),
            start.Length > 0 ? start : null, NullIfEmpty(Str(o["EndDate"])), NullIfEmpty(Str(o["GmtOffset"])),
            stem, headline.Length > 0 ? headline : stem);
    }

    /// "2026/2026-11-08_São_Paulo_Grand_Prix/2026-11-06_Practice_1/" →
    /// "2026-11-06_Sao_Paulo_Grand_Prix_Practice_1"
    public static string FileStemFor(string? path, string meeting, string name, string? startDate, int? key)
    {
        string date = "", meet = "", sess = "";
        if (!string.IsNullOrEmpty(path))
        {
            var seg = path.Trim('/').Split('/');
            if (seg.Length >= 3)
            {
                meet = SplitDatePrefix(seg[1]).Remainder;
                (date, sess) = SplitDatePrefix(seg[2]);
            }
        }
        if (date.Length == 0 && DateTime.TryParse(startDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var sd))
            date = sd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (meet.Length == 0) meet = meeting;
        if (sess.Length == 0) sess = name;
        if (date.Length == 0) date = "ukendt-dato";
        if (meet.Length == 0) meet = key?.ToString(CultureInfo.InvariantCulture) ?? "session";
        return SafeName(sess.Length > 0 ? $"{date}_{meet}_{sess}" : $"{date}_{meet}");
    }

    private static (string Date, string Remainder) SplitDatePrefix(string s)
    {
        var m = DatePrefix().Match(s);
        return m.Success ? (m.Groups[1].Value, s[m.Length..]) : ("", s);
    }

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2})_")]
    private static partial Regex DatePrefix();

    public static string SafeName(string s)
    {
        var norm = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(norm.Length);
        foreach (var c in norm)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '.') sb.Append(c);
            else sb.Append('_');
        }
        var r = Regex.Replace(sb.ToString(), "_+", "_").Trim('_', '.');
        return r.Length > 0 ? r : "session";
    }

    private static string Str(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
        }
        return "";
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => v.Length > 0) ?? "";
    private static string? NullIfEmpty(string s) => s.Length > 0 ? s : null;

    /// Ved nedlukning: luk filen pænt UDEN Closed-event (genstart fortsætter i samme fil).
    public void Dispose()
    {
        lock (_lock)
        {
            if (_rec is null) return;
            var info = BuildInfoLocked(_clock(), "nedlukning");
            try { _rec.Dispose(); } catch (IOException) { /* ignorér */ }
            _rec = null;
            WriteMetaLocked(info, "interrupted");
            _recSession = null;
        }
    }
}
