// DevStats — 1:1-port af websidens development-logik (devPointsForSession,
// holdgennemsnit og standardvalget "kun bedste + dårligste hold synlige").
// Paritetstestet mod websidens testværdier.

using System.Globalization;
using System.Text.Json.Nodes;

namespace SlqF1Live.Core;

public sealed record DevPoint(double? GapS, double? GapM, string? Note);

public static class DevStats
{
    /// Pr.-kører-punkt for én session: gab til pole/vinderen i s og m.
    /// Quali: session_result udfylder ofte ikke gap_to_leader — gabet
    /// udledes af Q-tiderne (duration-arrayet); meter via egen bedste tid.
    /// Race: meter via vinderens gennemsnitsfart (samlet tid / omgange).
    /// DNF/DNS/DSQ/"+N omg." bliver noter (hul i grafen).
    public static Dictionary<string, DevPoint> DevPointsForSession(
        JsonArray resultRows, bool isQuali, int? trackLen)
    {
        var parsed = HistoryStats.ParseRaceResult(resultRows);
        var result = new Dictionary<string, DevPoint>();

        double? winnerAvg = null;
        if (!isQuali && parsed.Count > 0)
        {
            var winnerRaw = resultRows.FirstOrDefault(r => r?["driver_number"]?.ToString() == parsed[0].Dn);
            var dur = HistoryStats.BestDuration(winnerRaw?["duration"]);
            if (dur is not null && parsed[0].Laps is { } laps && laps > 0)
                winnerAvg = dur / laps;
        }

        double? poleT = null;
        var ownT = new Dictionary<string, double>();
        if (isQuali)
        {
            foreach (var raw in resultRows)
            {
                var dn = raw?["driver_number"]?.ToString();
                var t = HistoryStats.BestDuration(raw?["duration"]);
                if (dn is null || t is null) continue;
                ownT[dn] = t.Value;
                if (poleT is null || t < poleT) poleT = t;
            }
        }

        foreach (var r in parsed)
        {
            var raw = resultRows.FirstOrDefault(x => x?["driver_number"]?.ToString() == r.Dn);
            double? gapS = null, gapM = null;
            string? note = null;
            if (r.Dnf) note = "DNF";
            else if (r.Dns) note = "DNS";
            else if (r.Dsq) note = "DSQ";
            else if (r.Lapped is { } n) note = $"+{n} omg.";
            else
            {
                if (r.GapLeaderS is not null) gapS = r.GapLeaderS;
                else if (isQuali && poleT is not null && ownT.TryGetValue(r.Dn, out var t))
                    gapS = t - poleT;
                if (gapS is null) note = "–";
            }
            if (gapS is not null && trackLen is { } len)
            {
                if (isQuali)
                {
                    var tOwn = ownT.TryGetValue(r.Dn, out var t) ? t : HistoryStats.BestDuration(raw?["duration"]);
                    if (tOwn is not null) gapM = gapS * (len / tOwn);
                }
                else if (winnerAvg is not null)
                {
                    gapM = gapS * (len / winnerAvg);
                }
            }
            result[r.Dn] = new DevPoint(gapS, gapM, note);
        }
        return result;
    }

    /// Gennemsnitligt gab pr. hold over sæsonen (kun gyldige punkter).
    /// teamOf: dn -> holdnavn.
    public static Dictionary<string, (double Avg, int Count)> TeamStats(
        IEnumerable<(string Dn, List<DevPoint?> Points)> series, Func<string, string> teamOf)
    {
        var sums = new Dictionary<string, (double Sum, int Count)>();
        foreach (var (dn, points) in series)
        {
            var team = teamOf(dn);
            sums.TryGetValue(team, out var acc);
            foreach (var p in points)
                if (p?.GapS is { } g) acc = (acc.Sum + g, acc.Count + 1);
            sums[team] = acc;
        }
        return sums.ToDictionary(kv => kv.Key,
            kv => (kv.Value.Count > 0 ? kv.Value.Sum / kv.Value.Count : 1e9, kv.Value.Count));
    }

    /// Standardvalg: skjul alle hold undtagen bedste og dårligste (med data).
    public static HashSet<string> DefaultHiddenTeams(
        Dictionary<string, (double Avg, int Count)> stats)
    {
        var hidden = new HashSet<string>();
        var withData = stats.Where(kv => kv.Value.Count > 0)
            .OrderBy(kv => kv.Value.Avg).Select(kv => kv.Key).ToList();
        if (withData.Count == 0) return hidden;
        var keep = new HashSet<string> { withData[0], withData[^1] };
        foreach (var team in stats.Keys)
            if (!keep.Contains(team)) hidden.Add(team);
        return hidden;
    }

    public static string TooltipValue(DevPoint p, bool isQuali)
    {
        if (p.GapS is null) return p.Note ?? "–";
        if (p.GapS == 0) return isQuali ? "Pole" : "Vinder";
        return "+" + p.GapS.Value.ToString("0.000", CultureInfo.InvariantCulture) + " s" +
            (p.GapM is not null ? " (" + HistoryStats.FmtM(p.GapM) + ")" : "");
    }
}
