// HistoryStats — 1:1-port af de rene funktioner i f1.slq.dk's js/f1.js
// (fastestPerDriver, rankWithGaps, parseRaceResult, raceGapToNext,
// raceMeters, trackLengthFor, fmtM, bestDuration) plus label-opbygningen
// for de tre weekend-grafer. Testene bruger SAMME talværdier som
// website-testene, så platformene garanteret regner ens.
// Kørernumre håndteres som strenge (nøgler i JSON).

using System.Globalization;
using System.Text.Json.Nodes;

namespace SlqF1Live.Core;

public sealed record BarGroup(double? Value, string Text, float Alpha);
public sealed record BarRow(string Name, string ColourHex, List<BarGroup> Groups);

public static class HistoryStats
{
    // ---------- Banelængder [m] — identisk tabel med websiden ----------
    private static readonly (string Key, int Len)[] TrackLengths =
    {
        ("sakhir", 5412), ("bahrain", 5412),
        ("jeddah", 6174),
        ("melbourne", 5278),
        ("suzuka", 5807),
        ("shanghai", 5451),
        ("miami", 5412),
        ("imola", 4909),
        ("monte carlo", 3337), ("monaco", 3337),
        ("montreal", 4361),
        ("catalunya", 4657), ("barcelona", 4657),
        ("spielberg", 4318), ("red bull", 4318),
        ("silverstone", 5891),
        ("hungaroring", 4381),
        ("spa", 7004),
        ("zandvoort", 4259),
        ("monza", 5793),
        ("baku", 6003),
        ("singapore", 4940),
        ("austin", 5513),
        ("mexico", 4304),
        ("interlagos", 4309), ("sao paulo", 4309), ("são paulo", 4309),
        ("las vegas", 6201), ("vegas", 6201),
        ("lusail", 5419), ("losail", 5419),
        ("yas marina", 5281), ("abu dhabi", 5281),
        ("madrid", 5470), ("madring", 5470),
    };

    public static int? TrackLengthFor(string? circuitName)
    {
        var n = (circuitName ?? "").ToLowerInvariant();
        foreach (var (key, len) in TrackLengths)
            if (n.Contains(key)) return len;
        return null;
    }

    public static string FormatLapTime(double? sec)
    {
        if (sec is null || !double.IsFinite(sec.Value)) return "–";
        var m = (int)(sec.Value / 60);
        var s = sec.Value - m * 60;
        var sStr = s.ToString("00.000", CultureInfo.InvariantCulture);
        return m > 0 ? $"{m}:{sStr}" : s.ToString("0.000", CultureInfo.InvariantCulture);
    }

    public static string? FmtM(double? m)
    {
        if (m is null || !double.IsFinite(m.Value)) return null;
        return m.Value >= 1000
            ? (m.Value / 1000).ToString("0.00", CultureInfo.InvariantCulture) + " km"
            : m.Value.ToString("0.0", CultureInfo.InvariantCulture) + " m";
    }

    public static double? AsDouble(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<string>(out var s) &&
                double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d2)) return d2;
        }
        return null;
    }

    /// duration i session_result kan være tal eller array [Q1,Q2,Q3].
    public static double? BestDuration(JsonNode? d)
    {
        if (d is JsonArray arr)
        {
            double? best = null;
            foreach (var x in arr)
            {
                var v = AsDouble(x);
                if (v is > 0 && (best is null || v < best)) best = v;
            }
            return best;
        }
        var single = AsDouble(d);
        return single is > 0 ? single : null;
    }

    // ---------- Kernefunktioner (port af js/f1.js) ----------

    /// Hurtigste gyldige omgang pr. kører fra /laps-rækker.
    public static Dictionary<string, double> FastestPerDriver(JsonArray laps)
    {
        var best = new Dictionary<string, double>();
        foreach (var l in laps)
        {
            var dn = l?["driver_number"]?.ToString();
            var t = AsDouble(l?["lap_duration"]);
            if (dn is null || t is not > 0) continue;
            if (!best.TryGetValue(dn, out var b) || t < b) best[dn] = t.Value;
        }
        return best;
    }

    public sealed record Ranked(string Dn, double T, double GapS, double? GapM);

    /// Sortér efter tid; gab til foranliggende i s og m (fart = L / egen tid).
    public static List<Ranked> RankWithGaps(IEnumerable<(string Dn, double T)> entries, int? trackLen)
    {
        var sorted = entries.OrderBy(e => e.T).ToList();
        var result = new List<Ranked>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var (dn, t) = sorted[i];
            var gapS = i == 0 ? 0 : t - sorted[i - 1].T;
            double? gapM = i == 0 ? 0 : (trackLen is { } len ? gapS * (len / t) : null);
            result.Add(new Ranked(dn, t, gapS, gapM));
        }
        return result;
    }

    public sealed record RaceRow(string Dn, int? Pos, int? Laps, double? GapLeaderS,
        int? Lapped, bool Dnf, bool Dns, bool Dsq)
    {
        public double? GapNextS { get; set; }
        public double? GapM { get; set; }
    }

    /// Parse session_result-rækker til sorteret klassifikation.
    public static List<RaceRow> ParseRaceResult(JsonArray rows)
    {
        var outRows = new List<RaceRow>();
        foreach (var r in rows)
        {
            var dn = r?["driver_number"]?.ToString();
            if (dn is null) continue;
            double? gapLeaderS = null;
            int? lapped = null;
            var g = r!["gap_to_leader"];
            if (g is JsonValue gv)
            {
                if (gv.TryGetValue<double>(out var num)) gapLeaderS = num;
                else if (gv.TryGetValue<string>(out var s))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(s, @"\+?\s*(\d+)\s*LAP",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success) lapped = int.Parse(m.Groups[1].Value);
                    else if (double.TryParse(s.Replace("+", ""), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var num2)) gapLeaderS = num2;
                }
            }
            int? pos = int.TryParse(r["position"]?.ToString(), out var p) ? p : null;
            int? laps = int.TryParse(r["number_of_laps"]?.ToString(), out var nl) ? nl : null;
            bool B(string k) => r[k] is JsonValue bv && bv.TryGetValue<bool>(out var b) && b;
            outRows.Add(new RaceRow(dn, pos, laps, gapLeaderS, lapped, B("dnf"), B("dns"), B("dsq")));
        }
        outRows.Sort((a, b) => (a.Pos ?? 999).CompareTo(b.Pos ?? 999));
        if (outRows.Count > 0 && !outRows[0].Dnf && !outRows[0].Dns && outRows[0].GapLeaderS is null)
            outRows[0] = outRows[0] with { GapLeaderS = 0 };
        return outRows;
    }

    /// Gab til foranliggende ved målstregen ud fra gap_to_leader.
    public static void RaceGapToNext(List<RaceRow> classification)
    {
        for (var i = 0; i < classification.Count; i++)
        {
            var r = classification[i];
            if (i == 0) { r.GapNextS = 0; continue; }
            var prev = classification[i - 1];
            r.GapNextS = (r.GapLeaderS is null || prev.GapLeaderS is null)
                ? null : r.GapLeaderS - prev.GapLeaderS;
        }
    }

    /// Meter ved målstregen: gab × (banelængde / kørerens hurtigste løbsomgang).
    public static void RaceMeters(List<RaceRow> rows, Dictionary<string, double> fastest, int? trackLen)
    {
        foreach (var r in rows)
        {
            r.GapM = (r.GapNextS is { } gap && trackLen is { } len &&
                      fastest.TryGetValue(r.Dn, out var tFast))
                ? gap * (len / tFast) : null;
        }
    }

    // ---------- Række-opbygning til graferne (labels som på websiden) ----------

    public static (List<BarRow> Times, List<BarRow> GapNext) BuildQualiRows(
        Dictionary<string, double> best, int? trackLen,
        Func<string, string> nameOf, Func<string, string> colourOf)
    {
        var ranked = RankWithGaps(best.Select(kv => (kv.Key, kv.Value)), trackLen);
        var times = new List<BarRow>();
        var t0 = ranked.Count > 0 ? ranked[0].T : 0;
        for (var i = 0; i < ranked.Count; i++)
        {
            var r = ranked[i];
            var gapP1 = r.T - t0;
            double? mP1 = trackLen is { } len ? gapP1 * (len / r.T) : null;
            var paren = i == 0 ? "" :
                $"  (+{gapP1.ToString("0.000", CultureInfo.InvariantCulture)} s" +
                (mP1 is not null ? " / " + FmtM(mP1) : "") + ")";
            times.Add(new BarRow($"{i + 1}. {nameOf(r.Dn)}", colourOf(r.Dn),
                new List<BarGroup> { new(gapP1, FormatLapTime(r.T) + paren, 1f) }));
        }
        return (times, BuildGapNextRows(ranked, nameOf, colourOf));
    }

    public static List<BarRow> BuildGapNextRows(List<Ranked> ranked,
        Func<string, string> nameOf, Func<string, string> colourOf)
    {
        var rows = new List<BarRow>();
        for (var i = 0; i < ranked.Count; i++)
        {
            var r = ranked[i];
            string text;
            double? value = null;
            if (i == 0) { text = "reference"; value = 0; }
            else
            {
                value = r.GapS;
                text = $"+{r.GapS.ToString("0.000", CultureInfo.InvariantCulture)} s" +
                    (r.GapM is not null ? $"  ({FmtM(r.GapM)})" : "");
            }
            rows.Add(new BarRow($"{i + 1}. {nameOf(r.Dn)}", colourOf(r.Dn),
                new List<BarGroup> { new(value, text, 1f) }));
        }
        return rows;
    }

    public static (List<BarRow> Times, List<BarRow> GapNext) BuildRaceRows(
        JsonArray resultRows, Dictionary<string, double> fastest, int? trackLen,
        Func<string, string> nameOf, Func<string, string> colourOf)
    {
        var full = ParseRaceResult(resultRows);
        RaceGapToNext(full);
        RaceMeters(full, fastest, trackLen);

        var times = new List<BarRow>();
        for (var i = 0; i < full.Count; i++)
        {
            var r = full[i];
            string text;
            double? value = null;
            if (r.Dns) text = "DNS";
            else if (r.Dsq) text = "DSQ";
            else if (r.Dnf) text = "DNF" + (r.Laps is not null ? $" (omg. {r.Laps})" : "");
            else if (r.Lapped is { } n)
            {
                double? estS = fastest.TryGetValue(r.Dn, out var tf) ? n * tf : null;
                double? estM = trackLen is { } len ? (double?)(n * (double)len) : null;
                var parts = new List<string>();
                if (estS is not null) parts.Add("≈ +" + estS.Value.ToString("0", CultureInfo.InvariantCulture) + " s");
                if (estM is not null) parts.Add("≈ " + FmtM(estM));
                text = $"+{n} omgang{(n > 1 ? "e" : "")}" + (parts.Count > 0 ? $"  ({string.Join(" / ", parts)})" : "");
            }
            else if (i == 0) { text = "Vinder"; value = 0; }
            else
            {
                double? mW = (trackLen is { } len && fastest.TryGetValue(r.Dn, out var tf))
                    ? r.GapLeaderS * (len / tf) : null;
                text = $"+{r.GapLeaderS!.Value.ToString("0.000", CultureInfo.InvariantCulture)} s" +
                    (mW is not null ? $"  ({FmtM(mW)})" : "");
                value = r.GapLeaderS;
            }
            var alpha = r.Dnf || r.Dns || r.Dsq ? 0.35f : 1f;
            times.Add(new BarRow($"{(r.Pos?.ToString() ?? "–")}. {nameOf(r.Dn)}", colourOf(r.Dn),
                new List<BarGroup> { new(value, text, alpha) }));
        }

        var gapRows = new List<BarRow>();
        var classified = full.Where(r => !r.Dnf && !r.Dns && !r.Dsq).ToList();
        for (var i = 0; i < classified.Count; i++)
        {
            var r = classified[i];
            string text;
            double? value = null;
            if (i == 0) { text = "reference"; value = 0; }
            else if (r.GapNextS is null)
                text = r.Lapped is { } n ? $"+{n} omg. bagud — gab til foranliggende ukendt" : "–";
            else
            {
                value = r.GapNextS;
                text = $"+{r.GapNextS.Value.ToString("0.000", CultureInfo.InvariantCulture)} s" +
                    (r.GapM is not null ? $"  ({FmtM(r.GapM)})" : "");
            }
            gapRows.Add(new BarRow($"{(r.Pos?.ToString() ?? (i + 1).ToString())}. {nameOf(r.Dn)}",
                colourOf(r.Dn), new List<BarGroup> { new(value, text, 1f) }));
        }
        return (times, gapRows);
    }

    /// Practice: grupperede søjler (én pr. session, alpha-nuancer) sorteret
    /// efter bedste samlede tid; plus tid-til-næste ud fra bedste samlet.
    public static (List<BarRow> Grouped, List<BarRow> GapNext) BuildPracticeRows(
        List<(string SessionName, Dictionary<string, double> Best)> perSession, int? trackLen,
        Func<string, string> nameOf, Func<string, string> colourOf)
    {
        float[] alphas = { 0.45f, 0.7f, 1f, 1f };
        var overall = new Dictionary<string, double>();
        foreach (var (_, best) in perSession)
            foreach (var (dn, t) in best)
                if (!overall.TryGetValue(dn, out var b) || t < b) overall[dn] = t;

        var bestAll = overall.Count > 0 ? overall.Values.Min() : 0;
        var order = overall.Keys.OrderBy(dn => overall[dn]).ToList();

        var grouped = new List<BarRow>();
        foreach (var dn in order)
        {
            var groups = new List<BarGroup>();
            for (var i = 0; i < perSession.Count; i++)
            {
                var has = perSession[i].Best.TryGetValue(dn, out var t);
                groups.Add(new BarGroup(
                    has ? t - bestAll : null,
                    has ? FormatLapTime(t) : "–",
                    alphas[Math.Min(i, 3)]));
            }
            grouped.Add(new BarRow(nameOf(dn), colourOf(dn), groups));
        }

        var ranked = RankWithGaps(overall.Select(kv => (kv.Key, kv.Value)), trackLen);
        return (grouped, BuildGapNextRows(ranked, nameOf, colourOf));
    }
}
