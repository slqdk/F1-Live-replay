// TimingView — omsætter TimingData + DriverList til sorterede rækker til
// UI'ets grid. Ren logik uden WinForms, så den kan testes headless.

using System.Globalization;
using System.Text.Json.Nodes;

namespace SlqF1Live.Core;

public sealed record TimingRow(
    int Position, string RacingNumber, string Name, string Team, string TeamColourHex,
    string LastLap, string BestLap, string Interval, string GapToLeader,
    string Laps, bool InPit, bool Retired);

public static class TimingView
{
    public static List<TimingRow> BuildRows(JsonNode? timingData, JsonNode? driverList)
    {
        var rows = new List<TimingRow>();
        if (timingData?["Lines"] is not JsonObject lines) return rows;
        var drivers = driverList as JsonObject;

        foreach (var (num, lineNode) in lines)
        {
            if (lineNode is not JsonObject line) continue;
            var d = drivers?[num] as JsonObject;

            rows.Add(new TimingRow(
                Position: ParseInt(line["Position"], 99),
                RacingNumber: num,
                Name: Str(d?["Tla"]) is { Length: > 0 } tla ? tla : (Str(d?["FullName"]) is { Length: > 0 } fn ? fn : "#" + num),
                Team: Str(d?["TeamName"]),
                TeamColourHex: Str(d?["TeamColour"]),
                LastLap: Str(line["LastLapTime"]?["Value"]),
                BestLap: Str(line["BestLapTime"]?["Value"]),
                Interval: Str(line["IntervalToPositionAhead"]?["Value"]),
                GapToLeader: Str(line["GapToLeader"]),
                Laps: Str(line["NumberOfLaps"]),
                InPit: Bool(line["InPit"]),
                Retired: Bool(line["Retired"]) || Bool(line["Stopped"])
            ));
        }
        rows.Sort((a, b) => a.Position.CompareTo(b.Position));
        return rows;
    }

    public static string SessionHeadline(JsonNode? sessionInfo, JsonNode? trackStatus, JsonNode? lapCount)
    {
        var parts = new List<string>();
        var meeting = Str(sessionInfo?["Meeting"]?["Name"]);
        var session = Str(sessionInfo?["Name"]);
        if (meeting.Length > 0 || session.Length > 0)
            parts.Add((meeting + " — " + session).Trim(' ', '—'));
        var track = Str(trackStatus?["Message"]);
        if (track.Length > 0) parts.Add("Banestatus: " + track);
        var cur = Str(lapCount?["CurrentLap"]);
        var tot = Str(lapCount?["TotalLaps"]);
        if (cur.Length > 0 && tot.Length > 0) parts.Add($"Omgang {cur}/{tot}");
        return parts.Count > 0 ? string.Join("  ·  ", parts) : "Ingen sessionsinfo endnu.";
    }

    private static string Str(JsonNode? n)
    {
        if (n is null) return "";
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<double>(out var d)) return d.ToString(CultureInfo.InvariantCulture);
            if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
        }
        return n.ToJsonString();
    }

    private static int ParseInt(JsonNode? n, int fallback)
    {
        var s = Str(n);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;
    }

    private static bool Bool(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<string>(out var s)) return s == "true" || s == "1";
        }
        return false;
    }
}
