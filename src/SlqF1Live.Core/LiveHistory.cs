// LiveHistory — opsamler historik til graferne ud fra TimingData-tilstanden.
// Ren logik uden WinForms:
//   LapTimeParser: "1:17.207" / "17.207" → sekunder
//   GapParser:     "" (fører) → 0, "+1.234" → 1.234, "1 L"/"+1 LAP" → null
//   LapHistory:    pr. kører: omgangsnr. → omgangstid (opdateres idempotent
//                  fra den aktuelle tilstand; korrektioner overskriver)
//   GapHistory:    tidsserie af gab til P1 pr. kører (ringbuffer)

using System.Globalization;
using System.Text.Json.Nodes;

namespace SlqF1Live.Core;

public static class LapTimeParser
{
    /// "1:17.207" eller "17.207" → sekunder; tom/ugyldig → null.
    public static double? ParseSeconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split(':');
        try
        {
            switch (parts.Length)
            {
                case 1:
                    return double.Parse(parts[0], CultureInfo.InvariantCulture);
                case 2:
                    return int.Parse(parts[0], CultureInfo.InvariantCulture) * 60
                         + double.Parse(parts[1], CultureInfo.InvariantCulture);
                case 3: // h:mm:ss.fff — forekommer ikke for omgange, men vær robust
                    return int.Parse(parts[0], CultureInfo.InvariantCulture) * 3600
                         + int.Parse(parts[1], CultureInfo.InvariantCulture) * 60
                         + double.Parse(parts[2], CultureInfo.InvariantCulture);
                default:
                    return null;
            }
        }
        catch (FormatException) { return null; }
    }
}

public static class GapParser
{
    /// Førerens gab er tom streng → 0. "+1.234" → 1.234.
    /// Overhalede ("1 L", "+1 LAP" o.l.) → null.
    public static double? ParseGap(string? value)
    {
        if (value is null) return null;
        var s = value.Trim();
        if (s.Length == 0) return 0;
        if (s.Contains('L')) return null; // "1 L", "+1 LAP", "LAP 1"
        s = s.TrimStart('+');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : null;
    }
}

public sealed class LapHistory
{
    // dn -> (lapNo -> seconds)
    private readonly Dictionary<string, SortedDictionary<int, double>> _laps = new();
    private readonly object _lock = new();

    /// Opdatér fra den aktuelle TimingData-tilstand. Idempotent: samme
    /// tilstand to gange ændrer intet; en korrigeret LastLapTime for samme
    /// omgangsnummer overskriver.
    public void UpdateFrom(JsonNode? timingData)
    {
        if (timingData?["Lines"] is not JsonObject lines) return;
        lock (_lock)
        {
            foreach (var (dn, lineNode) in lines)
            {
                if (lineNode is not JsonObject line) continue;
                var lapNoStr = line["NumberOfLaps"]?.ToString();
                if (!int.TryParse(lapNoStr, out var lapNo) || lapNo <= 0) continue;
                var secs = LapTimeParser.ParseSeconds(line["LastLapTime"]?["Value"]?.GetValue<string>());
                if (secs is null) continue;
                if (!_laps.TryGetValue(dn, out var d)) _laps[dn] = d = new SortedDictionary<int, double>();
                d[lapNo] = secs.Value;
            }
        }
    }

    // Inkrementel tilstand til ApplyPatch: dn -> senest kendte omgangsnr.
    private readonly Dictionary<string, int> _curLap = new();

    /// Opdatér fra en TimingData-PATCH (delvis besked). Billig (O(patch))
    /// og robust over for at NumberOfLaps og LastLapTime kommer i hver sin
    /// besked. Bruges under afspilning/seek; UpdateFrom bruges ved polling.
    public void ApplyPatch(JsonNode? timingPatch)
    {
        if (timingPatch?["Lines"] is not JsonObject lines) return;
        lock (_lock)
        {
            foreach (var (dn, lineNode) in lines)
            {
                if (lineNode is not JsonObject line) continue;
                if (line["NumberOfLaps"] is { } lapNode &&
                    int.TryParse(lapNode.ToString(), out var lapNo) && lapNo > 0)
                {
                    _curLap[dn] = lapNo;
                }
                var value = line["LastLapTime"]?["Value"]?.GetValue<string>();
                var secs = LapTimeParser.ParseSeconds(value);
                if (secs is not null && _curLap.TryGetValue(dn, out var cur) && cur > 0)
                {
                    if (!_laps.TryGetValue(dn, out var d)) _laps[dn] = d = new SortedDictionary<int, double>();
                    d[cur] = secs.Value;
                }
            }
        }
    }

    /// Kopi af serien for en kører: (omgangsnr., sekunder), sorteret.
    public List<(int Lap, double Seconds)> Series(string dn)
    {
        lock (_lock)
        {
            return _laps.TryGetValue(dn, out var d)
                ? d.Select(kv => (kv.Key, kv.Value)).ToList()
                : new List<(int, double)>();
        }
    }

    public List<string> Drivers()
    {
        lock (_lock) return _laps.Keys.ToList();
    }

    public void Reset()
    {
        lock (_lock) { _laps.Clear(); _curLap.Clear(); }
    }
}

public sealed class GapHistory
{
    public readonly record struct Sample(double TimeS, double? Gap);

    private readonly Dictionary<string, List<Sample>> _series = new();
    private readonly object _lock = new();
    private double _maxAgeS;

    public GapHistory(double maxAgeSeconds = 45 * 60) => _maxAgeS = maxAgeSeconds;

    /// Tag én prøve fra den aktuelle TimingData-tilstand.
    /// timeS: monotont sekundtal (fx sekunder siden feed-start).
    public void SampleFrom(double timeS, JsonNode? timingData)
    {
        if (timingData?["Lines"] is not JsonObject lines) return;
        lock (_lock)
        {
            foreach (var (dn, lineNode) in lines)
            {
                if (lineNode is not JsonObject line) continue;
                var gap = GapParser.ParseGap(line["GapToLeader"]?.GetValue<string>());
                if (!_series.TryGetValue(dn, out var list)) _series[dn] = list = new List<Sample>();
                list.Add(new Sample(timeS, gap));
            }
            // Trim gamle prøver
            var cutoff = timeS - _maxAgeS;
            foreach (var list in _series.Values)
            {
                var drop = 0;
                while (drop < list.Count && list[drop].TimeS < cutoff) drop++;
                if (drop > 0) list.RemoveRange(0, drop);
            }
        }
    }

    public List<Sample> Series(string dn)
    {
        lock (_lock)
        {
            return _series.TryGetValue(dn, out var list) ? new List<Sample>(list) : new List<Sample>();
        }
    }

    public List<string> Drivers()
    {
        lock (_lock) return _series.Keys.ToList();
    }

    public void Reset()
    {
        lock (_lock) _series.Clear();
    }
}
