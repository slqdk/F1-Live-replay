// SessionRecorder/SessionPlayer — optagelse og afspilning af feedet.
// Format: JSON Lines (.f1rec). Første linje er en header; derefter én
// linje pr. besked med t = ms siden optagelsens start. Beskeder gemmes
// RÅT (inkl. ".z"-komprimerede topics), så afspilning går gennem præcis
// samme StateStore-pipeline som live.
//
//   {"type":"header","version":1,"started":"…","session":"…"}
//   {"t":0,"type":"snapshot","data":{…}}
//   {"t":1234,"type":"feed","topic":"TimingData","data":{…},"ts":"…"}
//
// SessionPlayer er clock-agnostisk: ejeren kalder Tick() periodisk (eller
// AdvanceTo(ms) i tests); Seek genopbygger deterministisk via OnReset +
// hurtig gen-emission af alle beskeder op til positionen.

using System.Diagnostics;
using System.Text.Json.Nodes;

namespace SlqF1Live.Core;

public sealed class SessionRecorder : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private readonly Func<DateTime> _utcNow;
    private long _lastT;
    public string Path { get; }
    public DateTime StartedUtc { get; }
    public string SessionName { get; }
    public long Lines { get; private set; }
    public long LastT => _lastT;
    /// True hvis en eksisterende fil blev genåbnet (fx efter genstart midt i en session).
    public bool Reopened { get; }

    public SessionRecorder(string path, string sessionName)
        : this(path, sessionName, headerExtra: null, append: false, utcNow: null) { }

    /// append=true og filen findes: fortsæt i samme fil — t regnes videre fra
    /// headerens "started", så tidslinjen er sammenhængende hen over genstarter.
    /// headerExtra: ekstra felter i header-linjen (ignoreres af ældre læsere).
    public SessionRecorder(string path, string sessionName, JsonObject? headerExtra, bool append,
        Func<DateTime>? utcNow = null)
    {
        Path = path;
        SessionName = sessionName;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);

        if (append && File.Exists(path) && TryReadHeader(path, out var started, out var lastT))
        {
            StartedUtc = started;
            _lastT = lastT;
            Reopened = true;
            // En halv sidste linje (nedbrud midt i skrivning) må ikke klistres
            // sammen med næste linje — start på en frisk linje.
            var needsNewline = false;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length > 0)
                {
                    fs.Seek(-1, SeekOrigin.End);
                    needsNewline = fs.ReadByte() != '\n';
                }
            }
            _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            if (needsNewline) _writer.WriteLine();
            return;
        }

        StartedUtc = _utcNow();
        // AutoFlush: linjerne skal på disk løbende, så "forsinket live"
        // kan hale filen mens sessionen stadig optages.
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
            new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        var header = new JsonObject
        {
            ["type"] = "header",
            ["version"] = 1,
            ["started"] = StartedUtc.ToString("o"),
            ["session"] = sessionName,
        };
        if (headerExtra is not null)
            foreach (var kv in headerExtra)
                if (!header.ContainsKey(kv.Key)) header[kv.Key] = kv.Value?.DeepClone();
        _writer.WriteLine(header.ToJsonString());
    }

    /// Læs header ("started") og sidste t fra en eksisterende optagelse.
    public static bool TryReadHeader(string path, out DateTime startedUtc, out long lastT)
    {
        startedUtc = default;
        lastT = 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var first = reader.ReadLine();
            if (first is null || JsonNode.Parse(first) is not JsonObject h ||
                h["type"]?.GetValue<string>() != "header" ||
                !DateTime.TryParse(h["started"]?.GetValue<string>(), null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out startedUtc))
                return false;
            lastT = ReadLastT(fs);
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static long ReadLastT(FileStream fs)
    {
        var tailLen = (int)Math.Min(65536, fs.Length);
        fs.Seek(-tailLen, SeekOrigin.End);
        var buf = new byte[tailLen];
        fs.ReadExactly(buf, 0, tailLen);
        var lines = System.Text.Encoding.UTF8.GetString(buf).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            try
            {
                if (JsonNode.Parse(lines[i]) is JsonObject o && o["t"] is { } t)
                    return t.GetValue<long>();
            }
            catch (System.Text.Json.JsonException) { /* halv linje */ }
        }
        return 0;
    }

    private long NextT()
    {
        var t = (long)(_utcNow() - StartedUtc).TotalMilliseconds;
        if (t < _lastT) t = _lastT; // monotont, også ved urjustering
        _lastT = t;
        return t;
    }

    public static string SanitizeFileName(string name)
    {
        var s = name;
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(' ', '-');
    }

    public void WriteSnapshot(JsonObject rawSnapshot)
    {
        lock (_lock)
        {
            var line = new JsonObject
            {
                ["t"] = NextT(),
                ["type"] = "snapshot",
                ["data"] = rawSnapshot.DeepClone(),
            };
            _writer.WriteLine(line.ToJsonString());
            Lines++;
        }
    }

    // Feltrækkefølgen t, type, topic, data, ts er fast: læsere må finde
    // topic hurtigt i linjens start uden fuld JSON-parse (RecordingFilter, web).
    public void WriteFeed(string topic, JsonNode? data, string? ts)
    {
        lock (_lock)
        {
            var line = new JsonObject
            {
                ["t"] = NextT(),
                ["type"] = "feed",
                ["topic"] = topic,
                ["data"] = data?.DeepClone(),
                ["ts"] = ts,
            };
            _writer.WriteLine(line.ToJsonString());
            Lines++;
        }
    }

    public long LengthBytes
    {
        get { lock (_lock) { try { return _writer.BaseStream.Length; } catch (ObjectDisposedException) { return 0; } } }
    }

    public void Dispose()
    {
        lock (_lock) { _writer.Flush(); _writer.Dispose(); }
    }
}

public sealed class SessionPlayer
{
    public sealed record Entry(long T, bool IsSnapshot, string? Topic, JsonNode? Data, string? Ts);

    public event Action? Reset;                    // før genopbygning (Seek/genstart)
    public event Action<JsonObject>? Snapshot;     // rå snapshot (som fra feedet)
    public event Action<FeedItem>? Feed;           // rå feed-besked

    public string SessionName { get; private set; } = "";
    public long DurationMs { get; private set; }
    public long PositionMs { get; private set; }
    public bool IsPlaying { get; private set; }
    public double Speed { get; set; } = 1.0;

    private readonly List<Entry> _entries = new();
    private int _cursor;                            // næste entry der skal emittes
    private long _lastTickMs = -1;

    public void Load(string path)
    {
        _entries.Clear();
        _cursor = 0;
        PositionMs = 0;
        IsPlaying = false;
        SessionName = "";
        using var reader = new StreamReader(path);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(line); } catch { continue; }
            if (node is not JsonObject obj) continue;
            var type = obj["type"]?.GetValue<string>();
            switch (type)
            {
                case "header":
                    SessionName = obj["session"]?.GetValue<string>() ?? "";
                    break;
                case "snapshot":
                    _entries.Add(new Entry(obj["t"]!.GetValue<long>(), true, null,
                        obj["data"]?.DeepClone(), null));
                    break;
                case "feed":
                    _entries.Add(new Entry(obj["t"]!.GetValue<long>(), false,
                        obj["topic"]?.GetValue<string>(),
                        obj["data"]?.DeepClone(),
                        obj["ts"]?.GetValue<string>()));
                    break;
            }
        }
        _entries.Sort((a, b) => a.T.CompareTo(b.T));
        DurationMs = _entries.Count > 0 ? _entries[^1].T : 0;
    }

    public void Play()
    {
        if (_entries.Count == 0) return;
        IsPlaying = true;
        _lastTickMs = -1;
    }

    public void Pause() => IsPlaying = false;

    /// Kaldes periodisk af ejeren med et monotont ur (ms).
    public void Tick(long nowMs)
    {
        if (!IsPlaying) { _lastTickMs = nowMs; return; }
        if (_lastTickMs < 0) { _lastTickMs = nowMs; return; }
        var delta = (long)((nowMs - _lastTickMs) * Speed);
        _lastTickMs = nowMs;
        AdvanceTo(PositionMs + delta);
    }

    /// Emit alle beskeder frem til target (i optagelsestid, ms).
    public void AdvanceTo(long targetMs)
    {
        if (targetMs < PositionMs) { Seek(targetMs); return; }
        PositionMs = Math.Min(targetMs, DurationMs);
        while (_cursor < _entries.Count && _entries[_cursor].T <= PositionMs)
        {
            Emit(_entries[_cursor]);
            _cursor++;
        }
        if (PositionMs >= DurationMs) IsPlaying = false;
    }

    /// Spring til position: nulstil og gen-emit alt op til punktet
    /// (deterministisk tilstandsgenopbygning; ejeren nulstiller store m.m.
    /// i Reset-handleren).
    public void Seek(long targetMs)
    {
        targetMs = Math.Clamp(targetMs, 0, DurationMs);
        Reset?.Invoke();
        _cursor = 0;
        PositionMs = targetMs;
        while (_cursor < _entries.Count && _entries[_cursor].T <= targetMs)
        {
            Emit(_entries[_cursor]);
            _cursor++;
        }
        _lastTickMs = -1;
    }

    private void Emit(Entry e)
    {
        if (e.IsSnapshot)
        {
            if (e.Data is JsonObject snap) Snapshot?.Invoke((JsonObject)snap.DeepClone());
        }
        else if (e.Topic is not null)
        {
            Feed?.Invoke(new FeedItem(e.Topic, e.Data?.DeepClone(), e.Ts));
        }
    }
}
