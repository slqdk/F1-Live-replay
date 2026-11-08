// LapService — omgangstider pr. kører ud fra en optagelse (.f1rec) til
// visningens sammenligning af to kørere. Filen læses trinvis: kun linjer der
// er kommet til siden sidst, så den kan spørges ofte under en live-session.
// Kun TimingData- og TrackStatus-linjer (og snapshots) parses.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SlqF1Live.Core;

namespace SlqF1Live.Server;

public sealed class LapService
{
    private static readonly TimeSpan Idle = TimeSpan.FromHours(1);
    private readonly string _dir;
    private readonly ConcurrentDictionary<string, LapFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _nextSweep = DateTime.UtcNow + Idle;

    public LapService(string recordingsDir) => _dir = recordingsDir;

    /// { file, t, drivers: { "<nr>": [[omgang, t_ms, tid_ms|null, "flag", gab_ms|null], …] } }
    /// eller null, hvis optagelsen ikke findes. drivers = kun disse numre (null = alle).
    public string? GetJson(string name, IReadOnlyCollection<string>? drivers = null)
    {
        if (!SseServer.IsSafeRecordingName(name)) return null;
        var path = Path.Combine(_dir, name);
        if (!File.Exists(path)) return null;
        Sweep();
        var lf = _files.GetOrAdd(name, _ => new LapFile());
        lock (lf)
        {
            lf.LastUsed = DateTime.UtcNow;
            lf.Update(path);
            return lf.Table.ToJson(name, drivers);
        }
    }

    private void Sweep()
    {
        var now = DateTime.UtcNow;
        if (now < _nextSweep) return;
        _nextSweep = now + Idle;
        foreach (var (k, v) in _files)
            if (now - v.LastUsed > Idle) _files.TryRemove(k, out _);
    }

    private sealed class LapFile
    {
        public long Offset;
        public LapTable Table = new();
        public DateTime LastUsed;

        private static readonly byte[] TimingTopic = "\"topic\":\"TimingData\","u8.ToArray();
        private static readonly byte[] StatusTopic = "\"topic\":\"TrackStatus\","u8.ToArray();
        private static readonly byte[] Snapshot = "\"type\":\"snapshot\""u8.ToArray();

        /// Læs nye, hele linjer fra Offset. En halv linje til sidst venter til næste gang.
        public void Update(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < Offset) { Offset = 0; Table = new LapTable(); } // filen er skiftet ud
            if (fs.Length == Offset) return;
            fs.Seek(Offset, SeekOrigin.Begin);
            var buf = new byte[1 << 20];
            var filled = 0;
            while (true)
            {
                if (filled == buf.Length) Array.Resize(ref buf, buf.Length * 2); // meget lang linje (snapshot)
                var n = fs.Read(buf, filled, buf.Length - filled);
                if (n <= 0) break;
                filled += n;
                var start = 0;
                while (true)
                {
                    var nl = Array.IndexOf(buf, (byte)'\n', start, filled - start);
                    if (nl < 0) break;
                    Line(new ReadOnlyMemory<byte>(buf, start, nl - start));
                    Offset += nl - start + 1;
                    start = nl + 1;
                }
                if (start > 0)
                {
                    Buffer.BlockCopy(buf, start, buf, 0, filled - start);
                    filled -= start;
                }
            }
        }

        private void Line(ReadOnlyMemory<byte> line)
        {
            var head = line.Span[..Math.Min(line.Length, 96)];
            bool timing = head.IndexOf(TimingTopic) >= 0, status = !timing && head.IndexOf(StatusTopic) >= 0;
            bool snap = !timing && !status && head.IndexOf(Snapshot) >= 0;
            if (!timing && !status && !snap) return;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var t = root.TryGetProperty("t", out var te) && te.TryGetInt64(out var tv) ? tv : Table.LastT;
                if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;
                if (timing) Table.Patch(t, data);
                else if (status) Table.Status(t, data);
                else
                {
                    if (data.TryGetProperty("TrackStatus", out var ts)) Table.Status(t, ts);
                    if (data.TryGetProperty("TimingData", out var td)) Table.Snapshot(td);
                }
            }
            catch (JsonException) { /* ødelagt linje: spring over */ }
        }
    }
}

/// Ren logik: omgange pr. kører ud fra TimingData-beskeder i tidsorden.
/// En omgang er fuldført, når NumberOfLaps stiger; tiden er LastLapTime fra
/// samme besked (eller en senere, indtil næste omgang). Falder NumberOfLaps
/// (ny session efter forældede data i F1's cache), startes forfra.
/// Snapshots tæller ikke som fuldførte omgange (de kan være fra forrige
/// session) — kun pit-status hentes derfra.
/// Gab: første GapToLeader efter stregen (højst 10 s efter) — samme
/// tidtagningspunkt for alle, så to køreres gab kan trækkes fra hinanden.
/// Føreren = 0 (hans "LAP 27" kommer lige FØR NumberOfLaps og ikke efter);
/// overhalede ("1 L") = ukendt.
public sealed class LapTable
{
    [Flags]
    public enum LapFlag { None = 0, Pit = 1, SafetyCar = 2, Vsc = 4, Red = 8 }

    public sealed class Lap
    {
        public int No;
        public long T;
        public int? Ms;
        public LapFlag Flags;
        public int? Gap;
    }

    private sealed class Driver
    {
        public int Laps;
        public bool InPit, PitOut;
        public LapFlag Cur;
        public Lap? WantGap;
        public string? GapStr;
        public readonly List<Lap> List = new();
    }

    private readonly Dictionary<string, Driver> _drivers = new();
    private LapFlag _status;
    public long LastT { get; private set; }

    public IReadOnlyList<Lap> LapsOf(string num) =>
        _drivers.TryGetValue(num, out var d) ? d.List : Array.Empty<Lap>();

    private Driver Get(string num)
    {
        if (!_drivers.TryGetValue(num, out var d)) _drivers[num] = d = new Driver { Cur = _status };
        return d;
    }

    private static bool Truthy(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => e.GetString() is "true" or "True" or "1",
        JsonValueKind.Number => e.TryGetInt32(out var i) && i != 0,
        _ => false,
    };

    private static int? Int(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetInt32(out var i) => i,
        JsonValueKind.String when int.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
        _ => null,
    };

    /// TrackStatus: 4 = safety car, 6/7 = VSC, 5 = rødt flag. Mærker omgangen, alle kører nu.
    public void Status(long t, JsonElement data)
    {
        if (!data.TryGetProperty("Status", out var s)) return;
        LastT = Math.Max(LastT, t);
        _status = (s.ValueKind == JsonValueKind.String ? s.GetString() : s.ToString()) switch
        {
            "4" => LapFlag.SafetyCar,
            "6" or "7" => LapFlag.Vsc,
            "5" => LapFlag.Red,
            _ => LapFlag.None,
        };
        foreach (var d in _drivers.Values) d.Cur |= _status;
    }

    public void Snapshot(JsonElement timingData)
    {
        if (!timingData.TryGetProperty("Lines", out var lines) || lines.ValueKind != JsonValueKind.Object) return;
        foreach (var p in lines.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Object) continue;
            var d = Get(p.Name);
            if (p.Value.TryGetProperty("InPit", out var ip)) d.InPit = Truthy(ip);
            if (p.Value.TryGetProperty("PitOut", out var po)) d.PitOut = Truthy(po);
            if (d.InPit) d.Cur |= LapFlag.Pit;
        }
    }

    public void Patch(long t, JsonElement timingData)
    {
        if (!timingData.TryGetProperty("Lines", out var lines) || lines.ValueKind != JsonValueKind.Object) return;
        LastT = Math.Max(LastT, t);
        foreach (var p in lines.EnumerateObject())
        {
            var line = p.Value;
            if (line.ValueKind != JsonValueKind.Object) continue;
            var d = Get(p.Name);
            // Pit-status først: den gælder omgangen, der fuldføres i samme besked (indomgangen)
            if (line.TryGetProperty("InPit", out var ip)) d.InPit = Truthy(ip);
            if (line.TryGetProperty("PitOut", out var po))
            {
                var v = Truthy(po);
                if (v && !d.PitOut) d.Cur |= LapFlag.Pit;
                d.PitOut = v;
            }
            if (d.InPit) d.Cur |= LapFlag.Pit;

            string? gapStr = line.TryGetProperty("GapToLeader", out var gl) && gl.ValueKind == JsonValueKind.String ? gl.GetString() : null;
            if (line.TryGetProperty("NumberOfLaps", out var nl) && Int(nl) is int n)
            {
                if (n < d.Laps) { d.List.Clear(); d.Laps = 0; d.WantGap = null; } // ny session
                if (n > d.Laps)
                {
                    var lap = new Lap { No = n, T = t, Flags = d.Cur };
                    d.List.Add(lap);
                    var leader = (gapStr ?? d.GapStr)?.StartsWith("LAP", StringComparison.OrdinalIgnoreCase) == true;
                    if (leader) { lap.Gap = 0; d.WantGap = null; } else d.WantGap = lap;
                    d.Laps = n;
                    d.Cur = _status | (d.InPit ? LapFlag.Pit : LapFlag.None); // udomgangen starter i pitten
                }
            }
            if (gapStr is not null)
            {
                if (d.WantGap is { } wg)
                {
                    if (t - wg.T <= 10_000) wg.Gap = GapMs(gapStr);
                    d.WantGap = null;
                }
                d.GapStr = gapStr;
            }
            if (line.TryGetProperty("LastLapTime", out var ll) && ll.ValueKind == JsonValueKind.Object &&
                ll.TryGetProperty("Value", out var lv) && lv.ValueKind == JsonValueKind.String &&
                LapTimeParser.ParseSeconds(lv.GetString()) is double sec && sec > 0 &&
                d.List.Count > 0 && d.List[^1].No == d.Laps)
            {
                d.List[^1].Ms = (int)Math.Round(sec * 1000);
            }
        }
    }

    /// "LAP 26" (føreren) → 0, "+3.267" → 3267, "1 L"/"" → null.
    public static int? GapMs(string? v)
    {
        var s = (v ?? "").Trim();
        if (s.StartsWith("LAP", StringComparison.OrdinalIgnoreCase)) return 0;
        return GapParser.ParseGap(s) is double g && s.Length > 0 ? (int)Math.Round(g * 1000) : null;
    }

    public static string FlagText(LapFlag f)
    {
        var sb = new StringBuilder(4);
        if (f.HasFlag(LapFlag.Pit)) sb.Append('P');
        if (f.HasFlag(LapFlag.SafetyCar)) sb.Append('S');
        if (f.HasFlag(LapFlag.Vsc)) sb.Append('V');
        if (f.HasFlag(LapFlag.Red)) sb.Append('R');
        return sb.ToString();
    }

    public string ToJson(string file, IReadOnlyCollection<string>? only = null)
    {
        var drivers = new JsonObject();
        foreach (var (num, d) in _drivers.OrderBy(kv => int.TryParse(kv.Key, out var i) ? i : 999))
        {
            if (d.List.Count == 0 || only is not null && !only.Contains(num)) continue;
            var arr = new JsonArray();
            foreach (var l in d.List)
                arr.Add(new JsonArray(l.No, l.T, l.Ms is int ms ? JsonValue.Create(ms) : null, FlagText(l.Flags),
                    l.Gap is int g ? JsonValue.Create(g) : null));
            drivers[num] = arr;
        }
        return new JsonObject { ["file"] = file, ["t"] = LastT, ["drivers"] = drivers }.ToJsonString();
    }
}
