// DevSeasonLoader — henter en sæsons kvalifikationer eller løb fra OpenF1 og
// regner development-punkter pr. kører (DevStats). Samme logik som websidens
// f1.js og Windows-app'ens Development-fane — begge bruger nu denne klasse,
// så server og app altid regner ens.
//
// Valg af sessioner: pr. møde de sessioner med session_type "qualifying"/
// "race"; hovedsessionen ("Qualifying"/"Race") foretrækkes frem for sprint.
// Fremtidige sessioner springes over; sessioner uden resultatdata (aflyste)
// listes i Skipped. session_result caches på disk, når sessionen er mere end
// 2 timer gammel (resultatet ændrer sig ikke længere).

using System.Globalization;
using System.Text.Json.Nodes;

namespace SlqF1Live.Core;

public sealed record DevSeason(
    int Year, bool IsQuali,
    List<string> GpLabels, List<string> GpNames, List<string> GpDates,
    List<(string Dn, List<DevPoint?> Points)> Series,
    Dictionary<string, JsonObject> Drivers,
    List<string> Skipped,
    List<string>? Failed = null)
{
    /// GP'er hvor hentningen fejlede (fx rate limit) — ikke det samme som "ingen data".
    public List<string> FailedGps => Failed ?? new List<string>();

    public string TeamOf(string dn) =>
        Drivers.TryGetValue(dn, out var d)
            ? Str(d["team_name"]) ?? Str(d["team_colour"]) ?? "Ukendt"
            : "Ukendt";

    public string NameOf(string dn) =>
        Drivers.TryGetValue(dn, out var d) ? Str(d["name_acronym"]) ?? "#" + dn : "#" + dn;

    public string FullNameOf(string dn) =>
        Drivers.TryGetValue(dn, out var d) ? Str(d["full_name"]) ?? NameOf(dn) : NameOf(dn);

    public string ColourOf(string dn) =>
        Drivers.TryGetValue(dn, out var d) ? Str(d["team_colour"]) ?? "888888" : "888888";

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}

public static class DevSeasonLoader
{
    public static async Task<DevSeason> LoadAsync(OpenF1Client api, int year, bool isQuali, string? cacheDir,
        Action<string>? progress, CancellationToken ct)
    {
        progress?.Invoke("Henter kalender…");
        var meetings = (await api.GetArrayAsync($"/meetings?year={year}", ct))
            .OrderBy(m => m?["date_start"]?.GetValue<string>() ?? "", StringComparer.Ordinal).ToList();
        var seasonSessions = await api.GetArrayAsync($"/sessions?year={year}", ct);

        var wantType = isQuali ? "qualifying" : "race";
        var wantName = isQuali ? "Qualifying" : "Race";
        var targets = new List<(JsonNode Meeting, JsonNode Session)>();
        foreach (var m in meetings)
        {
            var mk = m?["meeting_key"]?.ToString();
            var ss = seasonSessions.Where(s => s?["meeting_key"]?.ToString() == mk &&
                    string.Equals(s?["session_type"]?.GetValue<string>(), wantType, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s?["date_start"]?.GetValue<string>() ?? "", StringComparer.Ordinal).ToList();
            if (ss.Count == 0) continue;
            var main = ss.FirstOrDefault(s => s?["session_name"]?.GetValue<string>() == wantName) ?? ss[^1];
            var start = main?["date_start"]?.GetValue<string>();
            if (start is null || !DateTime.TryParse(start, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal, out var st) || st > DateTime.UtcNow)
                continue;
            targets.Add((m!, main!));
        }

        var drivers = new Dictionary<string, JsonObject>();
        if (targets.Count > 0)
        {
            // Hele sæsonens kørerlister i ét kald (seneste række pr. nummer), så
            // reserver der ikke kørte sidste løb også får et hold. Fallback:
            // kun sidste sessions liste (den oprindelige websideadfærd).
            var firstKey = targets[0].Session["session_key"]!.ToString();
            var lastKey = targets[^1].Session["session_key"]!.ToString();
            var bestKey = new Dictionary<string, long>();
            JsonArray rowsAll;
            try { rowsAll = await api.GetArrayAsync($"/drivers?session_key>={firstKey}&session_key<={lastKey}", ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { rowsAll = new JsonArray(); }
            if (rowsAll.Count == 0)
                rowsAll = await api.GetArrayAsync($"/drivers?session_key={lastKey}", ct);
            foreach (var d in rowsAll)
            {
                var dn = d?["driver_number"]?.ToString();
                if (dn is null || d is not JsonObject o) continue;
                var sk = long.TryParse(o["session_key"]?.ToString(), out var k) ? k : 0;
                if (!bestKey.TryGetValue(dn, out var prev) || sk >= prev)
                {
                    bestKey[dn] = sk;
                    drivers[dn] = o;
                }
            }
        }

        var gpLabels = new List<string>();
        var gpNames = new List<string>();
        var gpDates = new List<string>();
        var perGp = new List<Dictionary<string, DevPoint>>();
        var skipped = new List<string>();
        var failed = new List<string>();
        for (var i = 0; i < targets.Count; i++)
        {
            var (meeting, session) = targets[i];
            progress?.Invoke($"Henter resultater… ({i + 1}/{targets.Count})");
            var circuit = meeting["circuit_short_name"]?.GetValue<string>() ?? "?";
            JsonArray rows;
            try
            {
                rows = await CachedResultAsync(api, session["session_key"]!.ToString(),
                    session["date_end"]?.GetValue<string>(), cacheDir, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { failed.Add(circuit); continue; } // fx 429, 5xx eller HTTP-timeout
            if (rows.Count == 0) { skipped.Add(circuit); continue; }
            var len = HistoryStats.TrackLengthFor(circuit);
            gpLabels.Add(circuit);
            gpNames.Add(meeting["meeting_name"]?.GetValue<string>() ?? circuit);
            gpDates.Add((session["date_start"]?.GetValue<string>() ?? "").Split('T')[0]);
            perGp.Add(DevStats.DevPointsForSession(rows, isQuali, len));
        }

        var allDn = perGp.SelectMany(g => g.Keys).Distinct().ToList();
        var series = allDn.Select(dn => (dn,
            perGp.Select(g => g.TryGetValue(dn, out var p) ? p : null).ToList())).ToList();
        var status = new List<string>();
        if (skipped.Count > 0) status.Add("Uden resultatdata: " + string.Join(", ", skipped));
        if (failed.Count > 0) status.Add("Kunne ikke hentes (prøv igen): " + string.Join(", ", failed));
        progress?.Invoke(string.Join(" · ", status));
        return new DevSeason(year, isQuali, gpLabels, gpNames, gpDates, series, drivers, skipped, failed);
    }

    private static async Task<JsonArray> CachedResultAsync(OpenF1Client api, string sessionKey, string? dateEnd,
        string? cacheDir, CancellationToken ct)
    {
        string? file = null;
        if (cacheDir is not null)
        {
            Directory.CreateDirectory(cacheDir);
            file = Path.Combine(cacheDir, "openf1_result_" + sessionKey + ".json");
            if (File.Exists(file))
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(file)) is JsonArray cached && cached.Count > 0)
                        return cached;
                }
                catch (System.Text.Json.JsonException) { /* korrupt — hent igen */ }
            }
        }
        var rows = await api.GetArrayAsync($"/session_result?session_key={sessionKey}", ct);
        var ended = dateEnd is not null && DateTime.TryParse(dateEnd, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal, out var end) && DateTime.UtcNow - end > TimeSpan.FromHours(2);
        if (file is not null && rows.Count > 0 && ended)
        {
            try { File.WriteAllText(file, rows.ToJsonString()); }
            catch (IOException) { /* ignorér */ }
        }
        return rows;
    }

    /// Serialisering til web: GP'er, kørere med punkter, hold sorteret
    /// bedst først (DevStats.TeamStats) og standardvalget (bedste + dårligste).
    public static JsonObject ToJson(DevSeason s)
    {
        var stats = DevStats.TeamStats(s.Series, s.TeamOf);
        var hidden = DevStats.DefaultHiddenTeams(stats);
        var gps = new JsonArray();
        for (var i = 0; i < s.GpLabels.Count; i++)
            gps.Add(new JsonObject { ["label"] = s.GpLabels[i], ["name"] = s.GpNames[i], ["date"] = s.GpDates[i] });

        var drivers = new JsonArray();
        foreach (var (dn, points) in s.Series)
        {
            var pts = new JsonArray();
            foreach (var p in points)
                pts.Add(p is null ? null : new JsonObject
                {
                    ["s"] = p.GapS is { } g ? Math.Round(g, 3) : null,
                    ["m"] = p.GapM is { } m ? Math.Round(m, 1) : null,
                    ["note"] = p.Note,
                });
            drivers.Add(new JsonObject
            {
                ["dn"] = dn,
                ["tla"] = s.NameOf(dn),
                ["name"] = s.FullNameOf(dn),
                ["team"] = s.TeamOf(dn),
                ["colour"] = "#" + s.ColourOf(dn).TrimStart('#'),
                ["points"] = pts,
            });
        }

        var teams = new JsonArray();
        foreach (var (team, (avg, count)) in stats.OrderBy(kv => kv.Value.Avg))
        {
            var dn = s.Series.Select(x => x.Dn).FirstOrDefault(d => s.TeamOf(d) == team);
            teams.Add(new JsonObject
            {
                ["name"] = team,
                ["colour"] = "#" + (dn is null ? "888888" : s.ColourOf(dn).TrimStart('#')),
                ["avg"] = count > 0 ? Math.Round(avg, 3) : null,
                ["count"] = count,
            });
        }
        var hiddenArr = new JsonArray();
        foreach (var h in hidden.Order(StringComparer.Ordinal)) hiddenArr.Add(h);
        var skipped = new JsonArray();
        foreach (var sk in s.Skipped) skipped.Add(sk);
        var failedArr = new JsonArray();
        foreach (var f in s.FailedGps) failedArr.Add(f);

        return new JsonObject
        {
            ["year"] = s.Year,
            ["mode"] = s.IsQuali ? "quali" : "race",
            ["gps"] = gps,
            ["skipped"] = skipped,
            ["failed"] = failedArr,
            ["drivers"] = drivers,
            ["teams"] = teams,
            ["defaultHidden"] = hiddenArr,
            ["generatedUtc"] = DateTime.UtcNow.ToString("o"),
        };
    }
}
