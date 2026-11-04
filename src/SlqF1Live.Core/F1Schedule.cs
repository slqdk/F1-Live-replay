// F1Schedule — sæsonplan og stream-status, så serveren selv ved hvornår
// den skal forbinde.
//   * Plan: Jolpica (Ergast-afløseren), api.jolpi.ca/ergast/f1/<år>.json —
//     gratis, uden nøgle, sessionstider i UTC.
//   * Stream: livetiming.formula1.com/static/StreamingStatus.json
//     ({"Status":"Available"|"Offline"}) — åben uden login (verificeret 4/11-2026)
//     og fanger også sessioner uden for planen (fx test).

using System.Globalization;
using System.Text.Json.Nodes;

namespace SlqF1Live.Core;

public sealed record ScheduledSession(int Season, int Round, string Meeting, string Name, DateTime StartUtc, TimeSpan Duration)
{
    public DateTime EndUtc => StartUtc + Duration;
    public bool IsRace => Name == "Race";
}

public static class F1Schedule
{
    public const string StreamingStatusUrl = "https://livetiming.formula1.com/static/StreamingStatus.json";
    public static string JolpicaUrl(int year) => $"https://api.jolpi.ca/ergast/f1/{year}.json?limit=100";

    private static readonly (string Key, string Name, int Minutes)[] SessionKeys =
    {
        ("FirstPractice", "Practice 1", 60),
        ("SecondPractice", "Practice 2", 60),
        ("ThirdPractice", "Practice 3", 60),
        ("SprintShootout", "Sprint Qualifying", 45),
        ("SprintQualifying", "Sprint Qualifying", 45),
        ("Sprint", "Sprint", 45),
        ("Qualifying", "Qualifying", 60),
    };

    public static List<ScheduledSession> ParseJolpica(string json)
    {
        var list = new List<ScheduledSession>();
        var races = JsonNode.Parse(StripBom(json))?["MRData"]?["RaceTable"]?["Races"] as JsonArray;
        if (races is null) return list;
        foreach (var r in races.OfType<JsonObject>())
        {
            var season = int.TryParse(Str(r["season"]), out var s) ? s : 0;
            var round = int.TryParse(Str(r["round"]), out var rd) ? rd : 0;
            var meeting = Str(r["raceName"]);
            foreach (var (key, name, minutes) in SessionKeys)
                if (r[key] is JsonObject sess && ParseUtc(Str(sess["date"]), Str(sess["time"])) is { } st)
                    list.Add(new ScheduledSession(season, round, meeting, name, st, TimeSpan.FromMinutes(minutes)));
            if (ParseUtc(Str(r["date"]), Str(r["time"])) is { } raceStart)
                list.Add(new ScheduledSession(season, round, meeting, "Race", raceStart, TimeSpan.FromMinutes(120)));
        }
        list.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
        return list;
    }

    private static DateTime? ParseUtc(string date, string time)
    {
        if (date.Length == 0) return null;
        var s = time.Length > 0 ? $"{date}T{time}" : $"{date}T12:00:00Z";
        return DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;
    }

    /// {"Status":"Available"} → "Available". Tåler BOM.
    public static string? ParseStreamingStatus(string json)
    {
        try { return JsonNode.Parse(StripBom(json))?["Status"]?.GetValue<string>(); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }

    /// Næste session der ikke er slut endnu.
    public static ScheduledSession? Next(IEnumerable<ScheduledSession> sessions, DateTime nowUtc)
        => sessions.Where(s => s.EndUtc > nowUtc).OrderBy(s => s.StartUtc).FirstOrDefault();

    /// Session hvis vindue [start − before, slut + after] indeholder nu.
    /// Løb får ekstra margin (røde flag kan forlænge dem kraftigt).
    public static ScheduledSession? ActiveWindow(IEnumerable<ScheduledSession> sessions, DateTime nowUtc,
        TimeSpan before, TimeSpan after)
        => sessions.FirstOrDefault(s =>
            nowUtc >= s.StartUtc - before &&
            nowUtc <= s.EndUtc + after + (s.IsRace ? TimeSpan.FromMinutes(90) : TimeSpan.Zero));

    public static string StripBom(string s) => s.Length > 0 && s[0] == '﻿' ? s[1..] : s;

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}
