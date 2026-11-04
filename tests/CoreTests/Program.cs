// CoreTests — headless validering af al Core-logik: merge/dekodning,
// SignalR-parsing, TimingView og en levende SseServer på localhost.

using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using SlqF1Live.Core;

var fails = 0;
void Check(bool cond, string msg)
{
    if (!cond) { fails++; Console.WriteLine("FAIL " + msg); }
    else Console.WriteLine("ok   " + msg);
}
void Eq(object? a, object? b, string msg) => Check(Equals(a?.ToString(), b?.ToString()), msg + $" [got '{a}', want '{b}']");

// ---------- StateStore.DeepMerge ----------
{
    var t = JsonNode.Parse("""{"a":{"b":1,"c":2}}""");
    var p = JsonNode.Parse("""{"a":{"b":9}}""");
    Eq(StateStore.DeepMerge(t, p)!.ToJsonString(), """{"a":{"b":9,"c":2}}""", "nested merge keeps siblings");

    t = JsonNode.Parse("""{"Lines":{"1":{"Pos":1},"44":{"Pos":2}}}""");
    p = JsonNode.Parse("""{"Lines":{"44":{"Pos":3}}}""");
    Eq(StateStore.DeepMerge(t, p)!.ToJsonString(), """{"Lines":{"1":{"Pos":1},"44":{"Pos":3}}}""", "numeric-key merge");

    Eq(StateStore.DeepMerge(JsonNode.Parse("""{"x":1}"""), JsonNode.Parse("5"))!.ToJsonString(), "5", "scalar replaces object");
    Eq(StateStore.DeepMerge(JsonNode.Parse("""[1,2]"""), JsonNode.Parse("""[9]"""))!.ToJsonString(), "[9]", "array replaced");
}

// ---------- DecodeTopic (.z) ----------
static string DeflateB64(string json)
{
    using var ms = new MemoryStream();
    using (var ds = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
    {
        var b = Encoding.UTF8.GetBytes(json);
        ds.Write(b, 0, b.Length);
    }
    return Convert.ToBase64String(ms.ToArray());
}
var carJson = """{"Entries":{"1":{"Speed":312}}}""";
{
    var (topic, data) = StateStore.DecodeTopic("CarData.z", JsonValue.Create(DeflateB64(carJson)));
    Eq(topic, "CarData", ".z suffix stripped");
    Eq(data!.ToJsonString(), carJson, ".z inflated to JSON");
    var (t2, d2) = StateStore.DecodeTopic("TimingData", JsonNode.Parse("""{"x":1}"""));
    Check(t2 == "TimingData" && d2!.ToJsonString() == """{"x":1}""", "plain topic passthrough");
}

// ---------- StateStore apply/snapshot ----------
{
    var st = new StateStore();
    var snapJson = """{"TimingData":{"Lines":{"1":{"LastLapTime":{"Value":"1:23.456"}}}},"CarData.z":"__B64__"}"""
        .Replace("__B64__", DeflateB64(carJson));
    st.ApplySnapshot((JsonObject)JsonNode.Parse(snapJson)!);
    Eq(st.GetTopic("CarData")!.ToJsonString(), carJson, "snapshot decodes .z");
    st.Apply("TimingData", JsonNode.Parse("""{"Lines":{"1":{"LastLapTime":{"Value":"1:22.999"}}}}"""), "2026-01-01T00:00:00Z");
    Eq(JsonNode.Parse(st.SnapshotJson())!["state"]!["TimingData"]!["Lines"]!["1"]!["LastLapTime"]!["Value"], "1:22.999", "patch applied");
    Eq(st.UpdatedAt, "2026-01-01T00:00:00Z", "updatedAt from ts");
    var (dt, _) = st.Apply("CarData.z", JsonValue.Create(DeflateB64(carJson)), null);
    Eq(dt, "CarData", "apply returns decoded topic");
    st.Reset();
    Eq(st.SnapshotJson(), """{"updatedAt":null,"state":{}}""", "reset clears");
}

// ---------- F1FeedClient protokol-parsing (SignalR Core) ----------
{
    Eq(F1FeedClient.ParseNegotiateId("""{"negotiateVersion":1,"connectionId":"abc","connectionToken":"tok/1+="}"""), "tok/1+=", "negotiate: token foretrækkes");
    Eq(F1FeedClient.ParseNegotiateId("""{"connectionId":"abc"}"""), "abc", "negotiate: fallback til connectionId");
    Check(F1FeedClient.ParseNegotiateId("ikke json") is null, "negotiate: garbage tolereres");

    var url = F1FeedClient.BuildConnectUrl("tok/1+=");
    Check(url.StartsWith("wss://livetiming.formula1.com/signalrcore?id="), "connect url base");
    Check(url.Contains(Uri.EscapeDataString("tok/1+=")), "id urlencoded");

    Eq(F1FeedClient.BuildHandshake(), """{"protocol":"json","version":1}""", "handshake record");

    var sub = JsonNode.Parse(F1FeedClient.BuildSubscribeInvocation(F1FeedClient.Topics))!;
    Check(sub["type"]!.GetValue<int>() == 1 && sub["target"]!.GetValue<string>() == "Subscribe", "subscribe invocation");
    Check(((JsonArray)sub["arguments"]![0]!).Count == F1FeedClient.Topics.Length, "all topics in subscribe");

    // DrainRecords: komplette + delvis record i buffer
    var buf = new StringBuilder("{\"a\":1}\u001e{\"b\":2}\u001e{\"halv");
    var recs = F1FeedClient.DrainRecords(buf);
    Check(recs.Count == 2 && recs[0] == "{\"a\":1}" && recs[1] == "{\"b\":2}", "two complete records drained");
    Eq(buf.ToString(), "{\"halv", "partial record kept in buffer");
    buf.Append("}\u001e");
    recs = F1FeedClient.DrainRecords(buf);
    Check(recs.Count == 1 && recs[0] == "{\"halv}", "completed partial drained");

    // ParseRecord
    var (k1, _, _) = F1FeedClient.ParseRecord("{}");
    Check(k1 == RecordKind.Handshake, "handshake response classified");

    var (k2, _, feed) = F1FeedClient.ParseRecord(
        """{"type":1,"target":"feed","arguments":["TimingData",{"Lines":{"1":{"Position":"2"}}},"ts1"]}""");
    Check(k2 == RecordKind.Feed && feed!.Topic == "TimingData" && feed.Timestamp == "ts1", "feed invocation parsed");

    var (k3, snap, _) = F1FeedClient.ParseRecord(
        """{"type":3,"invocationId":"1","result":{"TimingData":{"Lines":{"1":{"Position":"1"}}}}}""");
    Check(k3 == RecordKind.Snapshot && snap!["TimingData"]!["Lines"]!["1"]!["Position"]!.GetValue<string>() == "1", "completion snapshot parsed");

    var (k4, _, _) = F1FeedClient.ParseRecord("""{"type":6}""");
    Check(k4 == RecordKind.Ping, "ping classified");
    var (k5, _, _) = F1FeedClient.ParseRecord("""{"type":7}""");
    Check(k5 == RecordKind.Close, "close classified");
    var (k6, _, _) = F1FeedClient.ParseRecord("""{"type":1,"target":"other","arguments":[]}""");
    Check(k6 == RecordKind.None, "non-feed invocation ignored");
    var (k7, _, _) = F1FeedClient.ParseRecord("ikke json");
    Check(k7 == RecordKind.None, "garbage tolerated");
}

// ---------- TimingView ----------
{
    var td = JsonNode.Parse("""
{"Lines":{
      "1":{"Position":"2","LastLapTime":{"Value":"1:23.000"},"BestLapTime":{"Value":"1:22.500"},"IntervalToPositionAhead":{"Value":"+0.512"},"GapToLeader":"+0.512","NumberOfLaps":12,"InPit":false},
      "44":{"Position":"1","LastLapTime":{"Value":"1:22.900"},"BestLapTime":{"Value":"1:22.400"},"NumberOfLaps":12},
      "18":{"Position":"19","Retired":true}
}}
""");
    var dl = JsonNode.Parse("""
{"1":{"Tla":"VER","TeamName":"Red Bull Racing","TeamColour":"3671C6"},
 "44":{"Tla":"HAM","TeamName":"Ferrari","TeamColour":"E80020"}}
""");
    var rows = TimingView.BuildRows(td, dl);
    Eq(rows.Count, 3, "3 rows");
    Eq(rows[0].Name, "HAM", "sorted by position");
    Eq(rows[1].Interval, "+0.512", "interval mapped");
    Eq(rows[1].TeamColourHex, "3671C6", "team colour mapped");
    Check(rows[2].Retired && rows[2].Name == "#18", "retired + unknown driver fallback");

    var head = TimingView.SessionHeadline(
        JsonNode.Parse("""{"Meeting":{"Name":"Belgian Grand Prix"},"Name":"Race"}"""),
        JsonNode.Parse("""{"Message":"AllClear"}"""),
        JsonNode.Parse("""{"CurrentLap":12,"TotalLaps":44}"""));
    Check(head.Contains("Belgian Grand Prix — Race") && head.Contains("Omgang 12/44"), "session headline");
}

// ---------- SseServer integration (localhost) ----------
{
    var st = new StateStore();
    var status = "test";
    using var server = new SseServer(8991, "https://f1.slq.dk", st, () => status, () => false);
    server.Start();
    await Task.Delay(100);

    using var http = new HttpClient();
    var h = await http.GetAsync("http://localhost:8991/health");
    Check(h.IsSuccessStatusCode, "health 200");
    Check(h.Headers.TryGetValues("Access-Control-Allow-Origin", out var cors) && cors.First() == "https://f1.slq.dk", "CORS header");
    var hj = JsonNode.Parse(await h.Content.ReadAsStringAsync())!;
    Check(hj["ok"]!.GetValue<bool>() && hj["feed"]!.GetValue<string>() == "test", "health payload");

    var s1 = JsonNode.Parse(await http.GetStringAsync("http://localhost:8991/state"))!;
    Eq(s1["state"]!.ToJsonString(), "{}", "state empty initially");

    // SSE: læs snapshot + broadcast
    using var sseReq = new HttpRequestMessage(HttpMethod.Get, "http://localhost:8991/events");
    using var sseRes = await http.SendAsync(sseReq, HttpCompletionOption.ResponseHeadersRead);
    Check(sseRes.Content.Headers.ContentType?.MediaType == "text/event-stream", "SSE content-type");
    using var stream = await sseRes.Content.ReadAsStreamAsync();
    using var reader = new StreamReader(stream);

    async Task<(string Ev, JsonNode Data)?> ReadEventAsync(string wanted, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        string? ev = null; string? data = null;
        while (DateTime.UtcNow < deadline)
        {
            var lineTask = reader.ReadLineAsync();
            if (await Task.WhenAny(lineTask, Task.Delay(300)) != lineTask) continue;
            var line = lineTask.Result;
            if (line is null) return null;
            if (line.StartsWith("event: ")) ev = line[7..];
            else if (line.StartsWith("data: ")) data = line[6..];
            else if (line.Length == 0 && ev is not null && data is not null)
            {
                if (ev == wanted) return (ev, JsonNode.Parse(data)!);
                ev = null; data = null;
            }
        }
        return null;
    }

    var snapEv = await ReadEventAsync("snapshot");
    Check(snapEv is not null, "initial snapshot event");
    await Task.Delay(50);
    Eq(server.ClientCount, 1, "client counted");

    st.Apply("TimingData", JsonNode.Parse("""{"Lines":{"44":{"Position":"3"}}}"""), "ts9");
    server.BroadcastFeed("TimingData", JsonNode.Parse("""{"Lines":{"44":{"Position":"3"}}}"""), "ts9");
    var feedEv = await ReadEventAsync("feed");
    Check(feedEv is not null && feedEv.Value.Data["topic"]!.GetValue<string>() == "TimingData"
        && feedEv.Value.Data["ts"]!.GetValue<string>() == "ts9", "feed broadcast over SSE");

    var s2 = JsonNode.Parse(await http.GetStringAsync("http://localhost:8991/state"))!;
    Eq(s2["state"]!["TimingData"]!["Lines"]!["44"]!["Position"], "3", "state reflects update");

    var nf = await http.GetAsync("http://localhost:8991/foo");
    Eq((int)nf.StatusCode, 404, "unknown path 404");
}


// ---------- LiveHistory ----------
{
    Eq(LapTimeParser.ParseSeconds("1:17.207"), 77.207, "lap parse m:ss");
    Eq(LapTimeParser.ParseSeconds("17.5"), 17.5, "lap parse ss");
    Check(LapTimeParser.ParseSeconds("") is null && LapTimeParser.ParseSeconds(null) is null, "lap parse empty/null");
    Check(LapTimeParser.ParseSeconds("abc") is null, "lap parse garbage");

    Eq(GapParser.ParseGap(""), 0d, "gap leader empty -> 0");
    Eq(GapParser.ParseGap("+1.234"), 1.234, "gap plus parsed");
    Check(GapParser.ParseGap("1 L") is null && GapParser.ParseGap("+1 LAP") is null, "gap lapped -> null");
    Check(GapParser.ParseGap(null) is null, "gap null");

    var lh = new LapHistory();
    var td1 = JsonNode.Parse("""{"Lines":{"1":{"NumberOfLaps":3,"LastLapTime":{"Value":"1:20.000"}},"44":{"NumberOfLaps":3,"LastLapTime":{"Value":"1:20.500"}}}}""");
    lh.UpdateFrom(td1);
    lh.UpdateFrom(td1); // idempotent
    Eq(lh.Series("1").Count, 1, "one lap recorded, idempotent");
    var td2 = JsonNode.Parse("""{"Lines":{"1":{"NumberOfLaps":4,"LastLapTime":{"Value":"1:19.500"}}}}""");
    lh.UpdateFrom(td2);
    Eq(lh.Series("1").Count, 2, "second lap appended");
    Eq(lh.Series("1")[1].Seconds, 79.5, "lap 4 time");
    // korrektion af samme omgang
    lh.UpdateFrom(JsonNode.Parse("""{"Lines":{"1":{"NumberOfLaps":4,"LastLapTime":{"Value":"1:19.400"}}}}"""));
    Eq(lh.Series("1")[1].Seconds, 79.4, "correction overwrites");
    // ugyldig tid ignoreres
    lh.UpdateFrom(JsonNode.Parse("""{"Lines":{"1":{"NumberOfLaps":5,"LastLapTime":{"Value":""}}}}"""));
    Eq(lh.Series("1").Count, 2, "empty lap time ignored");

    var gh = new GapHistory(maxAgeSeconds: 100);
    var tdg = JsonNode.Parse("""{"Lines":{"1":{"GapToLeader":""},"44":{"GapToLeader":"+2.500"},"18":{"GapToLeader":"1 L"}}}""");
    gh.SampleFrom(10, tdg);
    gh.SampleFrom(20, tdg);
    Eq(gh.Series("1").Count, 2, "two samples");
    Eq(gh.Series("1")[0].Gap, 0d, "leader gap 0");
    Eq(gh.Series("44")[1].Gap, 2.5, "gap sample value");
    Check(gh.Series("18")[0].Gap is null, "lapped sample null");
    gh.SampleFrom(150, tdg); // cutoff = 50 -> prøver ved 10 og 20 trimmes
    Eq(gh.Series("44").Count, 1, "old samples trimmed");
    Eq(gh.Series("44")[0].TimeS, 150d, "remaining sample is newest");
}


// ---------- SessionRecorder + SessionPlayer ----------
{
    var tmp = Path.Combine(Path.GetTempPath(), "slqf1test", "rec.f1rec");
    if (File.Exists(tmp)) File.Delete(tmp);

    JsonObject snapRaw;
    using (var rec = new SessionRecorder(tmp, "Test — Qualifying"))
    {
        snapRaw = (JsonObject)JsonNode.Parse("""{"TimingData":{"Lines":{"1":{"Position":"1"}}}}""")!;
        rec.WriteSnapshot(snapRaw);
        rec.WriteFeed("TimingData", JsonNode.Parse("""{"Lines":{"1":{"NumberOfLaps":1,"LastLapTime":{"Value":"1:20.000"}}}}"""), "ts1");
        rec.WriteFeed("TimingData", JsonNode.Parse("""{"Lines":{"1":{"NumberOfLaps":2,"LastLapTime":{"Value":"1:19.000"}}}}"""), "ts2");
    }
    Check(File.Exists(tmp), "recording file written");
    Eq(SessionRecorder.SanitizeFileName("Hungarian Grand Prix — Qualifying"), "Hungarian-Grand-Prix-—-Qualifying", "filename sanitized");

    // Manipulér t-værdier så timingen er kendt (optageren skrev ~0 ms)
    var lines = File.ReadAllLines(tmp);
    var edited = new List<string> { lines[0] };
    long[] times = { 0, 1000, 2000 };
    for (var i = 1; i < lines.Length; i++)
    {
        var o = (JsonObject)JsonNode.Parse(lines[i])!;
        o["t"] = times[i - 1];
        edited.Add(o.ToJsonString());
    }
    File.WriteAllLines(tmp, edited);

    var player = new SessionPlayer();
    player.Load(tmp);
    Eq(player.SessionName, "Test — Qualifying", "header session name");
    Eq(player.DurationMs, 2000L, "duration from last entry");

    var events = new List<string>();
    var store2 = new StateStore();
    var lh2 = new LapHistory();
    player.Reset += () => { events.Add("reset"); store2.Reset(); lh2.Reset(); };
    player.Snapshot += s2 => { events.Add("snap"); store2.ApplySnapshot(s2); };
    player.Feed += f2 => { events.Add("feed@" + f2.Timestamp); store2.Apply(f2.Topic, f2.Data, f2.Timestamp); lh2.UpdateFrom(store2.GetTopic("TimingData")); };

    // Play + Tick-baseret fremdrift ved 2× hastighed
    player.Speed = 2.0;
    player.Play();
    player.Tick(1000);        // baseline
    player.Tick(1250);        // +250 ms × 2 = 500 ms → kun snapshot (t=0)
    Check(events.Count == 1 && events[0] == "snap", "snapshot emitted at start");
    player.Tick(1600);        // +350 × 2 = 700 → pos 1200 → feed ts1
    Check(events.Count == 2 && events[1] == "feed@ts1", "first feed at 2x speed");
    player.Tick(2100);        // +500 × 2 = 1000 → pos 2200 → clamp 2000 → feed ts2, stop
    Check(events.Count == 3 && events[2] == "feed@ts2", "second feed emitted");
    Check(!player.IsPlaying && player.PositionMs == 2000, "auto-pause at end");
    Eq(lh2.Series("1").Count, 2, "lap history rebuilt during playback");

    // Seek tilbage: reset + gen-emission op til 1500 ms
    events.Clear();
    player.Seek(1500);
    Check(events[0] == "reset" && events.Contains("snap") && events.Contains("feed@ts1") && !events.Contains("feed@ts2"), "seek rebuilds up to position");
    Eq(lh2.Series("1").Count, 1, "history matches seek position");
    Eq(player.PositionMs, 1500L, "position after seek");

    // AdvanceTo bagud delegerer til Seek
    events.Clear();
    player.AdvanceTo(500);
    Check(events[0] == "reset", "backward advance seeks");
}


// ---------- LapHistory.ApplyPatch (split-patches) ----------
{
    var lh2 = new LapHistory();
    // NumberOfLaps og LastLapTime i hver sin patch
    lh2.ApplyPatch(JsonNode.Parse("""{"Lines":{"1":{"NumberOfLaps":7}}}"""));
    lh2.ApplyPatch(JsonNode.Parse("""{"Lines":{"1":{"LastLapTime":{"Value":"1:21.000"}}}}"""));
    Eq(lh2.Series("1").Count, 1, "split patch recorded");
    Eq(lh2.Series("1")[0].Lap, 7, "lap number from earlier patch");
    // Korrektion via LastLapTime alene overskriver samme omgang
    lh2.ApplyPatch(JsonNode.Parse("""{"Lines":{"1":{"LastLapTime":{"Value":"1:20.900"}}}}"""));
    Eq(lh2.Series("1")[0].Seconds, 80.9, "correction via patch");
    // Samlet besked virker også
    lh2.ApplyPatch(JsonNode.Parse("""{"Lines":{"1":{"NumberOfLaps":8,"LastLapTime":{"Value":"1:19.800"}}}}"""));
    Eq(lh2.Series("1").Count, 2, "combined patch appends");
    lh2.Reset();
    lh2.ApplyPatch(JsonNode.Parse("""{"Lines":{"1":{"LastLapTime":{"Value":"1:19.000"}}}}"""));
    Eq(lh2.Series("1").Count, 0, "no lap number known after reset -> ignored");
}


// ---------- HistoryStats (paritet med websidens f1.js-tests) ----------
{
    Eq(HistoryStats.TrackLengthFor("Monza"), 5793, "track Monza");
    Eq(HistoryStats.TrackLengthFor("Spa-Francorchamps"), 7004, "track Spa");
    Check(HistoryStats.TrackLengthFor("Ukendtbane") is null, "unknown track null");

    var laps = (JsonArray)JsonNode.Parse("""
[{"driver_number":1,"lap_number":1,"lap_duration":null},
 {"driver_number":1,"lap_number":2,"lap_duration":92.0},
 {"driver_number":1,"lap_number":3,"lap_duration":91.5},
 {"driver_number":44,"lap_number":2,"lap_duration":92.4}]
""")!;
    var best = HistoryStats.FastestPerDriver(laps);
    Eq(best["1"], 91.5, "fastest drv 1");
    Eq(best["44"], 92.4, "fastest drv 44");

    var ranked = HistoryStats.RankWithGaps(new[] { ("44", 92.4), ("1", 91.5) }, 5793);
    Eq(ranked[0].Dn, "1", "rank order");
    Eq(Math.Round(ranked[1].GapS, 3), 0.9, "gapS 0.9");
    Eq(Math.Round(ranked[1].GapM!.Value, 1), 56.4, "gapM ~56.4 (paritet med website)");

    var res = (JsonArray)JsonNode.Parse("""
[{"driver_number":44,"position":2,"number_of_laps":57,"gap_to_leader":5.2},
 {"driver_number":1,"position":1,"number_of_laps":57,"gap_to_leader":null},
 {"driver_number":18,"position":15,"number_of_laps":56,"gap_to_leader":"+1 LAP"},
 {"driver_number":11,"position":18,"number_of_laps":40,"gap_to_leader":null,"dnf":true}]
""")!;
    var cls = HistoryStats.ParseRaceResult(res);
    Eq(string.Join(",", cls.Select(r => r.Dn)), "1,44,18,11", "classification sorted");
    Eq(cls[0].GapLeaderS, 0d, "winner gap 0");
    Eq(cls[2].Lapped, 1, "lapped parsed");
    HistoryStats.RaceGapToNext(cls);
    Eq(cls[1].GapNextS, 5.2, "gap to next 5.2");
    Check(cls[2].GapNextS is null, "lapped gapNext null");
    var fastest = new Dictionary<string, double> { ["1"] = 91.0, ["44"] = 91.8 };
    HistoryStats.RaceMeters(cls, fastest, 5412);
    Eq(Math.Round(cls[1].GapM!.Value, 1), Math.Round(5.2 * 5412 / 91.8, 1), "race meters P2");

    Eq(HistoryStats.FormatLapTime(83.456), "1:23.456", "format lap");
    Eq(HistoryStats.FmtM(56.42), "56.4 m", "fmtM meters");
    Eq(HistoryStats.FmtM(5793), "5.79 km", "fmtM km");
    Eq(HistoryStats.BestDuration(JsonNode.Parse("[81.0,80.5,null]")), 80.5, "bestDuration array");

    string NameOf(string dn) => dn == "1" ? "VER" : dn == "44" ? "HAM" : "#" + dn;
    string ColOf(string dn) => "888888";

    // Quali-labels (samme tal som website-testen: 0.4 s gab, Monza)
    var qBest = new Dictionary<string, double> { ["1"] = 80.5, ["44"] = 80.9 };
    var (qTimes, qGap) = HistoryStats.BuildQualiRows(qBest, 5793, NameOf, ColOf);
    Eq(qTimes[0].Groups[0].Text, "1:20.500", "quali P1 label");
    Check(qTimes[1].Groups[0].Text.Contains("(+0.400 s / 28.6 m)"), "quali gap-to-P1 label with meters");
    Check(qGap[1].Groups[0].Text.Contains("+0.400 s  (28.6 m)"), "quali gap-to-next label");

    // Race-labels inkl. lapped-estimat (85 s hurtigste omgang, 5793 m)
    var rRes = (JsonArray)JsonNode.Parse("""
[{"driver_number":1,"position":1,"number_of_laps":53,"gap_to_leader":null},
 {"driver_number":44,"position":2,"number_of_laps":53,"gap_to_leader":3.5},
 {"driver_number":18,"position":3,"number_of_laps":52,"gap_to_leader":"+1 LAP"}]
""")!;
    var rFast = new Dictionary<string, double> { ["1"] = 84.0, ["44"] = 84.3, ["18"] = 85.0 };
    var (rTimes, rGap) = HistoryStats.BuildRaceRows(rRes, rFast, 5793, NameOf, ColOf);
    Eq(rTimes[0].Groups[0].Text, "Vinder", "race winner label");
    Check(rTimes[1].Groups[0].Text.Contains("+3.500 s"), "race gap seconds");
    Check(rTimes[2].Groups[0].Text.Contains("+1 omgang") && rTimes[2].Groups[0].Text.Contains("\u2248 +85 s / \u2248 5.79 km"), "lapped estimate label (paritet)");
    Check(rGap[2].Groups[0].Text.Contains("bagud"), "lapped gap-next explanatory");

    // Practice grouped: sorteret efter bedste samlet, manglende session = "–"
    var per = new List<(string, Dictionary<string, double>)>
    {
        ("Practice 1", new() { ["1"] = 82.5, ["44"] = 83.1 }),
        ("Practice 2", new() { ["1"] = 81.9, ["44"] = 82.2 }),
        ("Practice 3", new() { ["44"] = 81.8 }),
    };
    var (pg, pgap) = HistoryStats.BuildPracticeRows(per, 5793, NameOf, ColOf);
    Eq(pg[0].Name, "HAM", "practice sorted by overall best (44 = 81.8)");
    Eq(pg[1].Groups[2].Text, "\u2013", "missing session dash");
    Eq(pg[0].Groups.Count, 3, "three session groups");
    Check(pgap.Count == 2 && pgap[0].Groups[0].Text == "reference", "practice gap-next reference");
}


// ---------- DevStats (paritet med websidens dev-tests) ----------
{
    // Quali uden gap_to_leader: gab udledes af Q-tiderne
    var qRows = (JsonArray)JsonNode.Parse("""
[{"driver_number":1,"position":1,"gap_to_leader":null,"duration":[81.0,80.5,80.2]},
 {"driver_number":44,"position":2,"gap_to_leader":null,"duration":[81.2,80.9,80.6]},
 {"driver_number":4,"position":3,"gap_to_leader":null,"duration":[81.5,null,null]},
 {"driver_number":18,"position":20,"gap_to_leader":null}]
""")!;
    var qp = DevStats.DevPointsForSession(qRows, isQuali: true, 5793);
    Eq(qp["1"].GapS, 0d, "dev quali pole gap 0");
    Eq(Math.Round(qp["44"].GapS!.Value, 3), 0.4, "dev quali gap derived from Q-times");
    Eq(Math.Round(qp["4"].GapS!.Value, 3), 1.3, "dev quali gap from Q1-only");
    Eq(Math.Round(qp["44"].GapM!.Value, 1), Math.Round(0.4 * 5793 / 80.6, 1), "dev quali meters from own best");
    Eq(qp["18"].Note, "\u2013", "dev quali no time -> note");

    // Race: meter via vinderens gennemsnitsfart; lapped/DNF som noter
    var rRows = (JsonArray)JsonNode.Parse("""
[{"driver_number":1,"position":1,"number_of_laps":50,"gap_to_leader":null,"duration":4500},
 {"driver_number":44,"position":2,"number_of_laps":50,"gap_to_leader":9.0},
 {"driver_number":18,"position":15,"number_of_laps":49,"gap_to_leader":"+1 LAP"},
 {"driver_number":11,"position":null,"gap_to_leader":null,"dnf":true}]
""")!;
    var rp = DevStats.DevPointsForSession(rRows, isQuali: false, 5000);
    Eq(Math.Round(rp["44"].GapM!.Value, 0), 500d, "dev race meters via winner avg (500 m)");
    Eq(rp["18"].Note, "+1 omg.", "dev race lapped note");
    Eq(rp["11"].Note, "DNF", "dev race dnf note");

    // Holdstatistik + standardvalg: kun bedste og dårligste synlige
    string TeamOf(string dn) => dn switch { "1" => "Red Bull", "11" => "Red Bull", "44" => "Mercedes", _ => "Aston" };
    var series = new List<(string, List<DevPoint?>)>
    {
        ("1", new() { new DevPoint(0, 0, null), new DevPoint(0.2, 10, null) }),
        ("11", new() { new DevPoint(0.6, 30, null), null }),
        ("44", new() { new DevPoint(0.1, 5, null), new DevPoint(0.15, 8, null) }),
        ("18", new() { new DevPoint(2.5, 120, null), new DevPoint(2.6, 130, null) }),
    };
    var stats = DevStats.TeamStats(series.Select(s => (s.Item1, s.Item2)), TeamOf);
    Eq(Math.Round(stats["Mercedes"].Avg, 3), 0.125, "team avg Mercedes");
    var hidden = DevStats.DefaultHiddenTeams(stats);
    // Mercedes bedst (0.125), Aston dårligst (2.55) -> Red Bull skjult
    Check(hidden.Count == 1 && hidden.Contains("Red Bull"), "default hides middle team");

    Eq(DevStats.TooltipValue(new DevPoint(0, 0, null), true), "Pole", "tooltip pole");
    Eq(DevStats.TooltipValue(new DevPoint(0.312, 22.4, null), true), "+0.312 s (22.4 m)", "tooltip value with meters");
    Eq(DevStats.TooltipValue(new DevPoint(null, null, "DNF"), false), "DNF", "tooltip note");
}


// ---------- SseServer: /recordings ----------
{
    var recDir = Path.Combine(Path.GetTempPath(), "slqf1test_rec");
    if (Directory.Exists(recDir)) Directory.Delete(recDir, true);
    Directory.CreateDirectory(recDir);
    var recPath = Path.Combine(recDir, "2026-07-26_race.f1rec");
    using (var rec = new SessionRecorder(recPath, "Hungarian Grand Prix — Race"))
    {
        rec.WriteSnapshot((JsonObject)JsonNode.Parse("""{"TimingData":{"Lines":{}}}""")!);
        rec.WriteFeed("TimingData", JsonNode.Parse("""{"Lines":{"1":{"Position":"1"}}}"""), "ts");
    }
    // Kendt varighed i sidste linje
    var recLines = File.ReadAllLines(recPath);
    var lastObj = (JsonObject)JsonNode.Parse(recLines[^1])!;
    lastObj["t"] = 5400000;
    recLines[^1] = lastObj.ToJsonString();
    File.WriteAllLines(recPath, recLines);

    var st2 = new StateStore();
    using var srv = new SseServer(8992, "https://f1.slq.dk", st2, () => "x", () => false, recDir);
    srv.Start();
    await Task.Delay(100);
    using var http2 = new HttpClient();

    var list = JsonNode.Parse(await http2.GetStringAsync("http://localhost:8992/recordings")) as JsonArray;
    Check(list is { Count: 1 }, "recordings listed");
    Eq(list![0]!["file"]!.GetValue<string>(), "2026-07-26_race.f1rec", "recording filename");
    Eq(list[0]!["session"]!.GetValue<string>(), "Hungarian Grand Prix \u2014 Race", "recording session from header");
    Eq(list[0]!["durationMs"]!.GetValue<long>(), 5400000L, "duration from tail");

    var body = await http2.GetStringAsync("http://localhost:8992/recordings/2026-07-26_race.f1rec");
    Check(body.Contains("\"type\":\"header\"") && body.Contains("ts"), "recording downloadable");

    var trav = await http2.GetAsync("http://localhost:8992/recordings/..%2Fsecret.f1rec");
    Eq((int)trav.StatusCode, 404, "path traversal rejected");
    var wrongExt = await http2.GetAsync("http://localhost:8992/recordings/x.txt");
    Eq((int)wrongExt.StatusCode, 404, "non-f1rec rejected");
}


// ---------- Range-support (forsinket live) ----------
{
    var recDir2 = Path.Combine(Path.GetTempPath(), "slqf1test_rec"); // genbruger fil fra forrige blok
    var st3 = new StateStore();
    using var srv2 = new SseServer(8993, "https://f1.slq.dk", st3, () => "x", () => false, recDir2);
    srv2.Start();
    await Task.Delay(100);
    using var http3 = new HttpClient();

    var full = await http3.GetByteArrayAsync("http://localhost:8993/recordings/2026-07-26_race.f1rec");
    var req = new HttpRequestMessage(HttpMethod.Get, "http://localhost:8993/recordings/2026-07-26_race.f1rec");
    req.Headers.TryAddWithoutValidation("Range", "bytes=" + (full.Length - 20) + "-");
    var partial = await http3.SendAsync(req);
    Eq((int)partial.StatusCode, 206, "range gives 206");
    var tailBytes = await partial.Content.ReadAsByteArrayAsync();
    Eq(tailBytes.Length, 20, "range length");
    Check(tailBytes.SequenceEqual(full[^20..]), "range content matches file tail");
    Check(partial.Content.Headers.ContentRange?.ToString().Contains("/" + full.Length) == true, "content-range total");

    var req2 = new HttpRequestMessage(HttpMethod.Get, "http://localhost:8993/recordings/2026-07-26_race.f1rec");
    req2.Headers.TryAddWithoutValidation("Range", "bytes=" + full.Length + "-");
    var beyond = await http3.SendAsync(req2);
    Eq((int)beyond.StatusCode, 416, "range at EOF gives 416");
}


// ---------- F1Auth (token-udtræk + info) ----------
{
    static string B64Url(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var futureExp = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
    var jwt = B64Url("""{"alg":"RS256","kid":"x"}""") + "." +
        B64Url($$"""{"exp":{{futureExp}},"SubscriptionStatus":"active","SubscribedProduct":"F1 TV Pro"}""") + ".sig";

    Eq(F1Auth.ExtractToken(jwt), jwt, "raw JWT passes through");
    Eq(F1Auth.ExtractToken("  \"" + jwt + "\"  "), jwt, "quoted/padded JWT trimmed");

    var cookieJson = """{"data":{"subscriptionToken":"__T__","other":1}}""".Replace("__T__", jwt);
    Eq(F1Auth.ExtractToken(cookieJson), jwt, "plain JSON cookie extracted");
    Eq(F1Auth.ExtractToken(Uri.EscapeDataString(cookieJson)), jwt, "URL-encoded login-session cookie extracted");

    Check(F1Auth.ExtractToken("") is null && F1Auth.ExtractToken(null) is null, "empty -> null");
    Check(F1Auth.ExtractToken("not a token") is null, "garbage -> null");
    Check(F1Auth.ExtractToken("{\"data\":{}}") is null, "json without token -> null");

    var (exp, status, product) = F1Auth.TokenInfo(jwt);
    Check(exp is not null && exp > DateTime.UtcNow, "token exp parsed");
    Eq(status, "active", "subscription status parsed");
    Eq(product, "F1 TV Pro", "product parsed");

    Check(F1Auth.Describe(null).Contains("uden telemetri"), "describe: no token");
    Check(F1Auth.Describe(jwt).Contains("token OK") && F1Auth.Describe(jwt).Contains("F1 TV Pro"), "describe: valid token");
    var pastExp = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
    var oldJwt = B64Url("""{"alg":"RS256"}""") + "." + B64Url($$"""{"exp":{{pastExp}}}""") + ".sig";
    Check(F1Auth.Describe(oldJwt).Contains("UDL\u00d8BET"), "describe: expired token");
}

// =====================================================================
// 24/7-server-delen (v2)
// =====================================================================

// ---------- DeepMerge: index-patches på arrays + _deleted ----------
{
    var t = JsonNode.Parse("""{"Stints":[{"Compound":"SOFT","TotalLaps":10}]}""");
    StateStore.DeepMerge(t, JsonNode.Parse("""{"Stints":{"0":{"TotalLaps":12}}}"""));
    Eq(t!["Stints"]!.ToJsonString(), """[{"Compound":"SOFT","TotalLaps":12}]""", "index patch updates array element");
    StateStore.DeepMerge(t, JsonNode.Parse("""{"Stints":{"1":{"Compound":"HARD","TotalLaps":0}}}"""));
    Eq(((JsonArray)t["Stints"]!).Count, 2, "index patch at Count appends");
    Eq(t["Stints"]![1]!["Compound"], "HARD", "appended element");
    var empty = JsonNode.Parse("""{"Messages":[]}""");
    StateStore.DeepMerge(empty, JsonNode.Parse("""{"Messages":{"0":{"Message":"GREEN"}}}"""));
    Eq(empty!["Messages"]!.ToJsonString(), """[{"Message":"GREEN"}]""", "index patch into empty array");
    var gap = JsonNode.Parse("""{"A":[1]}""");
    StateStore.DeepMerge(gap, JsonNode.Parse("""{"A":{"3":4}}"""));
    Eq(gap!["A"]!.ToJsonString(), "[1,null,null,4]", "index gap padded with null");
    var del = JsonNode.Parse("""{"Lines":{"1":{"x":1},"2":{"x":2}}}""");
    StateStore.DeepMerge(del, JsonNode.Parse("""{"Lines":{"_deleted":["2"]}}"""));
    Eq(del!.ToJsonString(), """{"Lines":{"1":{"x":1}}}""", "_deleted removes object key");
    var named = JsonNode.Parse("""{"X":[1,2]}""");
    StateStore.DeepMerge(named, JsonNode.Parse("""{"X":{"a":1}}"""));
    Eq(named!["X"]!.ToJsonString(), """{"a":1}""", "named-key patch replaces array (as before)");
    var st = new StateStore();
    var (bt, bd) = st.Apply("CarData.z", JsonValue.Create("ikke-base64!!"), null);
    Check(bt == "CarData" && bd is null, "invalid .z data skipped, no exception");
}

// ---------- SessionRecorder: append/genåbning ----------
{
    var dir = Path.Combine(Path.GetTempPath(), "slqf1test_v2_rec");
    if (Directory.Exists(dir)) Directory.Delete(dir, true);
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, "a.f1rec");
    var clock = new DateTime(2026, 11, 8, 14, 0, 0, DateTimeKind.Utc);
    using (var r = new SessionRecorder(path, "Test", new JsonObject { ["path"] = "2026/x/y/" }, append: true, () => clock))
    {
        r.WriteSnapshot(new JsonObject { ["A"] = 1 });
        clock = clock.AddSeconds(5);
        r.WriteFeed("TimingData", JsonNode.Parse("""{"x":1}"""), "ts1");
    }
    var header = JsonNode.Parse(File.ReadLines(path).First())!;
    Eq(header["path"], "2026/x/y/", "header extra field written");
    File.AppendAllText(path, "{\"t\":5001,\"type\":\"feed\",\"topic\":\"Halv");  // simuleret nedbrud midt i en linje
    clock = clock.AddSeconds(55);
    using (var r = new SessionRecorder(path, "Test", null, append: true, () => clock))
    {
        Check(r.Reopened, "existing file reopened");
        Eq(r.StartedUtc, new DateTime(2026, 11, 8, 14, 0, 0, DateTimeKind.Utc), "start time from header");
        r.WriteFeed("LapCount", JsonNode.Parse("""{"CurrentLap":2}"""), "ts2");
    }
    var lines = File.ReadAllLines(path);
    Eq(lines.Count(l => l.Contains("\"type\":\"header\"")), 1, "only one header after reopen");
    Check(lines[^1].StartsWith("{\"t\":60000,"), "t continues from header start (60 s)");
    Check(JsonNode.Parse(lines[^1])!["topic"]!.GetValue<string>() == "LapCount", "half line not glued to next line");
    Eq(RecordingFilter.PeekT(lines[^1]), 60000L, "PeekT fast path");
}

// ---------- RecordingManager ----------
{
    var dir = Path.Combine(Path.GetTempPath(), "slqf1test_v2_mgr");
    if (Directory.Exists(dir)) Directory.Delete(dir, true);
    var now = new DateTime(2026, 11, 6, 15, 0, 0, DateTimeKind.Utc);
    var log = new List<string>();
    var mgr = new RecordingManager(dir, () => now, log.Add);
    var opened = new List<RecordingInfo>();
    var closed = new List<RecordingInfo>();
    var resub = new List<string>();
    mgr.Opened += opened.Add;
    mgr.Closed += closed.Add;
    mgr.ResubscribeRequested += resub.Add;

    JsonObject Snap(string path, string meeting, string name, string status, string archive, string end) => (JsonObject)JsonNode.Parse($$$"""
        {"SessionInfo":{"Meeting":{"Name":"{{{meeting}}}","Circuit":{"Key":14}},"Key":11410,"Type":"Practice","Name":"{{{name}}}",
          "StartDate":"2026-11-06T12:30:00","EndDate":"{{{end}}}","GmtOffset":"-03:00:00","Path":"{{{path}}}",
          "ArchiveStatus":{"Status":"{{{archive}}}"},"SessionStatus":"{{{status}}}"},
         "SessionStatus":{"Status":"{{{status}}}"},"Heartbeat":{"Utc":"x"}}
        """)!;

    // Gammel, afsluttet session (det F1 serverer mellem weekenderne) → ingen optagelse
    mgr.OnSnapshot(Snap("2026/2026-11-01_Mexico_City_Grand_Prix/2026-11-01_Race/", "Mexico City Grand Prix", "Race", "Ends", "Complete", "2026-11-01T16:00:00"));
    Check(!mgr.IsRecording && opened.Count == 0, "stale finished session not recorded");

    // Ny session (FP1) → optagelse med navn fra Path (diakritika foldet)
    var fp1 = "2026/2026-11-08_São_Paulo_Grand_Prix/2026-11-06_Practice_1/";
    mgr.OnSnapshot(Snap(fp1, "São Paulo Grand Prix", "Practice 1", "Inactive", "Generating", "2026-11-06T13:30:00"));
    Check(mgr.IsRecording && opened.Count == 1, "live session opens recording");
    Eq(opened[0].FileStem, "2026-11-06_Sao_Paulo_Grand_Prix_Practice_1", "file stem from Path");
    Check(File.Exists(Path.Combine(dir, "2026-11-06_Sao_Paulo_Grand_Prix_Practice_1", "meta.json")), "meta.json written");
    Check(mgr.IsActiveFile("2026-11-06_Sao_Paulo_Grand_Prix_Practice_1.f1rec"), "active file reported");

    now = now.AddMinutes(1);
    mgr.OnFeed(new FeedItem("SessionStatus", JsonNode.Parse("""{"Status":"Started"}"""), "t"));
    mgr.OnFeed(new FeedItem("CarData.z", JsonValue.Create("abc"), "t"));
    mgr.OnFeed(new FeedItem("Heartbeat", JsonNode.Parse("""{"Utc":"x"}"""), "t"));
    Check(mgr.Current!.HasTelemetry, "telemetry counted");

    // Genforbindelse midt i sessionen → samme fil, snapshot tilføjes
    mgr.OnSnapshot(Snap(fp1, "São Paulo Grand Prix", "Practice 1", "Started", "Generating", "2026-11-06T13:30:00"));
    Check(opened.Count == 1 && closed.Count == 0, "reconnect continues same recording");

    // Heartbeat alene holder den ikke i live; 45 min uden data → luk
    now = now.AddMinutes(30);
    mgr.OnFeed(new FeedItem("Heartbeat", JsonNode.Parse("""{"Utc":"x"}"""), "t"));
    mgr.Tick();
    Check(mgr.IsRecording, "still recording after 30 min heartbeat-only");
    now = now.AddMinutes(16);
    mgr.Tick();
    Check(!mgr.IsRecording && closed.Count == 1, "closed after 45 min without data");
    Check(closed[0].CloseReason!.Contains("ingen data"), "close reason idle");
    var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "2026-11-06_Sao_Paulo_Grand_Prix_Practice_1", "meta.json")))!;
    Eq(meta["state"], "closed", "meta state closed");
    Check(meta["telemetry"]!.GetValue<bool>(), "meta telemetry flag");

    // Ny session via SessionInfo-patch → frisk snapshot ønskes; snapshot åbner FP2
    var fp2 = "2026/2026-11-08_São_Paulo_Grand_Prix/2026-11-06_Practice_2/";
    now = now.AddHours(2);
    mgr.OnFeed(new FeedItem("SessionInfo", JsonNode.Parse($$"""{"Name":"Practice 2","Path":"{{fp2}}","Key":11411,"EndDate":"2026-11-06T17:00:00","ArchiveStatus":{"Status":"Generating"},"SessionStatus":"Inactive"}"""), "t"));
    Check(resub.Count == 1, "new session in SessionInfo requests fresh snapshot");
    mgr.OnSnapshot(Snap(fp2, "São Paulo Grand Prix", "Practice 2", "Inactive", "Generating", "2026-11-06T17:00:00"));
    Check(mgr.IsRecording && opened.Count == 2 && opened[1].FileStem.EndsWith("Practice_2"), "FP2 recording opened");

    // "Ends" → luk 10 min senere
    mgr.OnFeed(new FeedItem("SessionStatus", JsonNode.Parse("""{"Status":"Ends"}"""), "t"));
    now = now.AddMinutes(9);
    mgr.Tick();
    Check(mgr.IsRecording, "not closed before Ends grace");
    now = now.AddMinutes(2);
    mgr.Tick();
    Check(!mgr.IsRecording && closed[^1].CloseReason!.Contains("Ends"), "closed after Ends grace");

    Eq(RecordingManager.FileStemFor(null, "Las Vegas Grand Prix", "Race", "2026-11-21T20:00:00", null), "2026-11-21_Las_Vegas_Grand_Prix_Race", "stem without Path");
    Eq(RecordingManager.SafeName("Émilia-Romagna / Imola"), "Emilia-Romagna_Imola", "safe name folds and cleans");
    var id = RecordingManager.Identify(JsonNode.Parse("""{"EndDate":"2026-11-01T16:00:00","GmtOffset":"-06:00:00","Path":"2026/a/2026-11-01_Race/","Meeting":{"Name":"M"},"Name":"Race"}"""))!;
    Eq(RecordingManager.SessionEndUtc(id), new DateTime(2026, 11, 1, 22, 0, 0, DateTimeKind.Utc), "session end UTC from local + offset");
}

// ---------- RecordingFilter (profiler til browseren) ----------
{
    string Z(string json) => DeflateB64(json);
    var pos = Z("""{"Position":[{"Timestamp":"a","Entries":{"1":{"X":1,"Y":2}}},{"Timestamp":"b","Entries":{"1":{"X":3,"Y":4}}}]}""");
    var car = Z("""{"Entries":[{"Utc":"a","Cars":{"1":{"Channels":{"2":100}}}},{"Utc":"b","Cars":{"1":{"Channels":{"2":200}}}}]}""");
    var rec = string.Join("\n", new[]
    {
        """{"type":"header","version":1,"started":"2026-11-08T14:00:00Z","session":"S"}""",
        $$$"""{"t":0,"type":"snapshot","data":{"TimingData":{"Lines":{}},"CarData.z":"{{{car}}}","Position.z":"{{{pos}}}"}}""",
        """{"t":100,"type":"feed","topic":"TimingData","data":{"x":1},"ts":"a"}""",
        $$"""{"t":200,"type":"feed","topic":"Position.z","data":"{{pos}}","ts":"a"}""",
        $$"""{"t":700,"type":"feed","topic":"Position.z","data":"{{pos}}","ts":"a"}""",
        $$"""{"t":1300,"type":"feed","topic":"Position.z","data":"{{pos}}","ts":"a"}""",
        $$"""{"t":1400,"type":"feed","topic":"CarData.z","data":"{{car}}","ts":"a"}""",
    }) + "\n";
    var bytes = Encoding.UTF8.GetBytes(rec);
    var full = Encoding.UTF8.GetString(RecordingFilter.FilterBytes(bytes, RecordingFilter.Profile.Full));
    Eq(full, rec, "full profile passes bytes through");
    var timing = Encoding.UTF8.GetString(RecordingFilter.FilterBytes(bytes, RecordingFilter.Profile.Timing)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Eq(timing.Length, 3, "timing: header + snapshot + TimingData");
    Check(!timing[1].Contains("CarData") && !timing[1].Contains("Position"), "timing snapshot stripped of telemetry");
    var map = Encoding.UTF8.GetString(RecordingFilter.FilterBytes(bytes, RecordingFilter.Profile.Map)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    var posLines = map.Where(l => l.Contains("\"topic\":\"Position\"")).ToList();
    Eq(posLines.Count, 2, "map: Position downsampled to 1/s (200 kept, 700 dropped, 1300 kept)");
    var reduced = JsonNode.Parse(posLines[0])!["data"]!["Position"]!.AsArray();
    Check(reduced.Count == 1 && reduced[0]!["Timestamp"]!.GetValue<string>() == "b", "map: only latest sample kept");
    Check(map[1].Contains("\"Position\":{\"Position\"") && !map[1].Contains("CarData"), "map snapshot: Position decoded, CarData dropped");
    var tele = Encoding.UTF8.GetString(RecordingFilter.FilterBytes(bytes, RecordingFilter.Profile.Tele));
    Check(tele.Contains("\"topic\":\"CarData\"") && tele.Contains("\"Channels\":{\"2\":200}"), "tele: CarData decoded, latest sample");
    using var ms = new MemoryStream(Encoding.UTF8.GetBytes("a\nb\nhalv"));
    Eq(RecordingFilter.CompleteEnd(ms), 4L, "CompleteEnd stops after last newline");
    Eq(RecordingFilter.ParseProfile("MAP"), RecordingFilter.Profile.Map, "profile parse case-insensitive");
}

// ---------- F1Login: protokol-parsing (uden netværk) ----------
{
    var script = """var x=1;foo({davinci:{apiRoot:"https://x",clientId:"abc-123",acrValues:"policy-9",other:"z"},b:2});""";
    var (cid, pol) = F1Login.ParseFrontendConfig(script);
    Check(cid == "abc-123" && pol == "policy-9", "frontend config scraped");
    try { F1Login.ParseFrontendConfig("ingen config"); Check(false, "missing config throws"); }
    catch (F1AuthException) { Check(true, "missing config throws"); }

    var form = F1Login.ParseFlowStart("""{"_links":{"next":{"href":"https://id.formula1.com/davinci/x"}},"interactionId":"i1","id":"f1","eventName":"continue"}""");
    Check(form.SubmitUrl.StartsWith("https://id.formula1.com/") && form.InteractionId == "i1" && form.EventName == "continue", "flow start parsed");
    try { F1Login.ParseFlowStart("""{"_links":{"next":{"href":"https://evil.example/x"}},"interactionId":"i","id":"f","eventName":"e"}"""); Check(false, "foreign submit url rejected"); }
    catch (F1AuthException) { Check(true, "foreign submit url rejected"); }

    Eq(F1Login.ParseFlowCompletion("""{"status":"COMPLETED","authorizeResponse":{"code":"C0DE","state":"s1"}}""", "s1", 200), "C0DE", "completion code");
    try { F1Login.ParseFlowCompletion("""{"status":"FAILED","message":"Invalid password"}""", "s1", 400); Check(false, "wrong password surfaces message"); }
    catch (F1AuthException ex) { Check(ex.Message.Contains("Invalid password"), "wrong password surfaces message"); }
    try { F1Login.ParseFlowCompletion("""{"status":"COMPLETED","authorizeResponse":{"code":"C","state":"other"}}""", "s1", 200); Check(false, "state mismatch rejected"); }
    catch (F1AuthException) { Check(true, "state mismatch rejected"); }

    static string B64U(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var access = B64U("""{"alg":"RS256"}""") + "." + B64U("""{"sub":"u","subscriptionToken":"SUB.TOK.EN"}""") + ".sig";
    Eq(F1Login.SubscriptionTokenFromAccessToken(access), "SUB.TOK.EN", "subscription token from access token claims");
    Eq(F1Login.CodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"), "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", "PKCE S256 (RFC 7636 vector)");
}

// ---------- TokenManager ----------
{
    var dir = Path.Combine(Path.GetTempPath(), "slqf1test_v2_tok");
    if (Directory.Exists(dir)) Directory.Delete(dir, true);
    var now = DateTime.UtcNow;
    static string B64U(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    string Jwt(DateTime exp) => B64U("""{"alg":"RS256"}""") + "." +
        B64U($$"""{"exp":{{new DateTimeOffset(exp).ToUnixTimeSeconds()}},"SubscriptionStatus":"active","SubscribedProduct":"F1 TV Access"}""") + ".sig";

    var logins = 0;
    var failNext = false;
    var tm = new TokenManager(Path.Combine(dir, "tok.json"), () => now)
    {
        Username = "u@x",
        Password = "pw",
        LoginFunc = (_, _, _) =>
        {
            logins++;
            if (failNext) throw new F1AuthException("login afvist: forkert kode");
            return Task.FromResult(Jwt(now.AddDays(4)));
        },
    };
    var changed = 0;
    tm.TokenChanged += _ => changed++;
    tm.Load(null);
    Check(tm.Current is null, "no token initially");
    Check(await tm.EnsureValidAsync(CancellationToken.None) && tm.Current is not null && logins == 1, "auto-login obtains token");
    Eq(changed, 1, "TokenChanged raised");
    Check(File.Exists(Path.Combine(dir, "tok.json")), "token persisted");
    await tm.EnsureValidAsync(CancellationToken.None);
    Eq(logins, 1, "no re-login while token is fresh");
    now = now.AddDays(3).AddHours(13); // < 12 t tilbage
    await tm.EnsureValidAsync(CancellationToken.None);
    Eq(logins, 2, "renews when expiring within margin");
    failNext = true;
    tm.MarkRejected("negotiate HTTP 401");
    Check(tm.Current is null, "rejected token not used");
    await tm.EnsureValidAsync(CancellationToken.None);
    Check(tm.Status.LastError!.Contains("forkert kode") && tm.Status.NextAttemptUtc > now, "failed login sets backoff");
    var before = logins;
    await tm.EnsureValidAsync(CancellationToken.None);
    Eq(logins, before, "no login during backoff");

    var tm2 = new TokenManager(Path.Combine(dir, "tok.json"), () => now);
    tm2.Load(null);
    Check(tm2.Status.Source == "login", "token reloaded from file after restart");
    Check(!tm2.SetToken(Jwt(now.AddDays(-1)), "manuel", out var msg) && msg.Contains("udløbet"), "expired manual token rejected");
    Check(tm2.SetToken(Uri.EscapeDataString($$$"""{"data":{"subscriptionToken":"{{{Jwt(now.AddDays(4))}}}"}}"""), "manuel", out _), "cookie value accepted");
}

// ---------- F1Schedule ----------
{
    var json = """
    {"MRData":{"RaceTable":{"Races":[
      {"season":"2026","round":"20","raceName":"Brazilian Grand Prix","date":"2026-11-08","time":"17:00:00Z",
       "FirstPractice":{"date":"2026-11-06","time":"15:30:00Z"},"Qualifying":{"date":"2026-11-07","time":"18:00:00Z"},
       "SprintQualifying":{"date":"2026-11-07","time":"14:30:00Z"},"Sprint":{"date":"2026-11-07","time":"15:30:00Z"}}]}}}
    """;
    var sessions = F1Schedule.ParseJolpica(json);
    Eq(sessions.Count, 5, "jolpica: FP1, SQ, Sprint, Q, Race");
    Eq(sessions[0].Name, "Practice 1", "sorted by start");
    Eq(sessions[^1].StartUtc, new DateTime(2026, 11, 8, 17, 0, 0, DateTimeKind.Utc), "race start UTC");
    Eq(F1Schedule.ParseStreamingStatus("﻿{\"Status\":\"Available\"}"), "Available", "streaming status with BOM");
    var before = TimeSpan.FromMinutes(75);
    var after = TimeSpan.FromMinutes(90);
    Check(F1Schedule.ActiveWindow(sessions, new DateTime(2026, 11, 6, 14, 20, 0, DateTimeKind.Utc), before, after)?.Name == "Practice 1", "window opens 75 min before");
    Check(F1Schedule.ActiveWindow(sessions, new DateTime(2026, 11, 6, 14, 10, 0, DateTimeKind.Utc), before, after) is null, "not before window");
    Check(F1Schedule.ActiveWindow(sessions, new DateTime(2026, 11, 8, 21, 0, 0, DateTimeKind.Utc), before, after)?.Name == "Race", "race window extended (red flags)");
    Eq(F1Schedule.Next(sessions, new DateTime(2026, 11, 7, 16, 0, 0, DateTimeKind.Utc))?.Name, "Sprint", "next = running/upcoming");
}

// ---------- Arkiv: jsonStream-linjer og konvertering ----------
{
    Check(ArchiveConverter.TryParseStreamLine("﻿00:01:02.345{\"a\":1}", out var off, out var js) && off == 62345 && js == "{\"a\":1}", "stream line HH:MM:SS.fff");
    Check(ArchiveConverter.TryParseStreamLine("1.02:00:00.500\"QUJD\"", out var off2, out var js2) && off2 == 93600500 && js2 == "\"QUJD\"", "stream line with day prefix");
    Check(!ArchiveConverter.TryParseStreamLine("garbage", out _, out _), "garbage line rejected");
    Eq(string.Join(",", F1ArchiveClient.RadioPaths(JsonNode.Parse("""{"Captures":[{"Path":"TeamRadio/A.mp3"},{"Path":"../x"}]}"""))), "TeamRadio/A.mp3", "radio paths from array, traversal dropped");
    Eq(string.Join(",", F1ArchiveClient.RadioPaths(JsonNode.Parse("""{"Captures":{"4":{"Path":"TeamRadio/B.mp3"}}}"""))), "TeamRadio/B.mp3", "radio paths from index patch");

    var dir = Path.Combine(Path.GetTempPath(), "slqf1test_v2_arch");
    if (Directory.Exists(dir)) Directory.Delete(dir, true);
    Directory.CreateDirectory(dir);
    var bom = "﻿";
    File.WriteAllText(Path.Combine(dir, "Heartbeat.jsonStream"), bom + "00:00:10.000{\"Utc\":\"2026-11-08T13:00:10.000Z\"}\r\n00:01:10.000{\"Utc\":\"2026-11-08T13:01:10.000Z\"}\r\n");
    File.WriteAllText(Path.Combine(dir, "TimingData.jsonStream"), bom + "00:00:05.000{\"Lines\":{\"1\":{\"Position\":\"1\"}}}\r\n00:00:20.000{\"Lines\":{\"1\":{\"NumberOfLaps\":1}}}\r\n");
    File.WriteAllText(Path.Combine(dir, "Position.z.jsonStream"), bom + "00:00:15.000\"" + DeflateB64("""{"Position":[]}""") + "\"\r\n");
    var outPath = Path.Combine(dir, "out.f1rec");
    var res = ArchiveConverter.Convert(dir, outPath, "Test — Race", new JsonObject { ["path"] = "2026/a/b/" });
    Eq(res.StartedUtc, new DateTime(2026, 11, 8, 13, 0, 0, DateTimeKind.Utc), "t0 from first heartbeat");
    var outLines = File.ReadAllLines(outPath);
    Eq(outLines.Length, 2 + 5, "header + snapshot + 5 merged lines");
    var ts = outLines.Skip(2).Select(l => JsonNode.Parse(l)!["t"]!.GetValue<long>()).ToArray();
    Check(ts.SequenceEqual(new long[] { 5000, 10000, 15000, 20000, 70000 }), "lines merged in time order");
    Check(outLines[4].Contains("\"topic\":\"Position.z\"") && outLines[4].Contains("\"ts\":\"2026-11-08T13:00:15.000Z\""), ".z kept raw, ts from t0");
    var player = new SessionPlayer();
    player.Load(outPath);
    var st2 = new StateStore();
    player.Snapshot += s => st2.ApplySnapshot(s);
    player.Feed += f => st2.Apply(f.Topic, f.Data, f.Timestamp);
    player.Seek(res.DurationMs);
    Eq(st2.GetTopic("TimingData")!["Lines"]!["1"]!["NumberOfLaps"], 1, "converted file plays through SessionPlayer");
    Eq(JsonNode.Parse(outLines[0])!["source"], "archive", "header marks archive source");
}

// ---------- SseServer: filter pr. klient + profil + X-Accel-Buffering ----------
{
    var recDir = Path.Combine(Path.GetTempPath(), "slqf1test_v2_sse");
    if (Directory.Exists(recDir)) Directory.Delete(recDir, true);
    Directory.CreateDirectory(recDir);
    File.WriteAllText(Path.Combine(recDir, "x.f1rec"),
        "{\"type\":\"header\",\"version\":1,\"started\":\"2026-11-08T14:00:00Z\",\"session\":\"X\",\"meeting\":\"M\",\"path\":\"2026/a/b/\"}\n" +
        "{\"t\":0,\"type\":\"snapshot\",\"data\":{\"A\":1}}\n" +
        "{\"t\":10,\"type\":\"feed\",\"topic\":\"CarData.z\",\"data\":\"" + DeflateB64("{\"Entries\":[]}") + "\",\"ts\":\"a\"}\n" +
        "{\"t\":20,\"type\":\"feed\",\"topic\":\"TimingData\",\"data\":{\"x\":1},\"ts\":\"a\"}\n");
    var st = new StateStore();
    st.ApplySnapshot((JsonObject)JsonNode.Parse("""{"TimingData":{"a":1},"CarData":{"big":1}}""")!);
    using var srv = new SseServer(8994, "https://f1.slq.dk", st, () => "x", () => true, recDir) { IsRecordingActive = f => f == "x.f1rec" };
    srv.Start();
    await Task.Delay(100);
    using var http = new HttpClient();

    var list = JsonNode.Parse(await http.GetStringAsync("http://localhost:8994/recordings"))!.AsArray();
    Check(list[0]!["live"]!.GetValue<bool>() && list[0]!["meeting"]!.GetValue<string>() == "M", "list has live flag + header fields");

    var resp = await http.GetAsync("http://localhost:8994/recordings/x.f1rec?profile=timing&from=0");
    var body = await resp.Content.ReadAsStringAsync();
    Check(resp.Headers.TryGetValues("X-Next-Offset", out var nxt) && long.Parse(nxt.First()) > 0, "X-Next-Offset header");
    Check(!body.Contains("CarData") && body.Contains("TimingData"), "profile=timing drops CarData");
    var tail = await http.GetStringAsync($"http://localhost:8994/recordings/x.f1rec?profile=timing&from={nxt!.First()}");
    Eq(tail, "", "tail from end offset is empty");

    using var req = new HttpRequestMessage(HttpMethod.Get, "http://localhost:8994/events?exclude=CarData");
    using var sres = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    Check(sres.Headers.TryGetValues("X-Accel-Buffering", out var xab) && xab.First() == "no", "SSE sets X-Accel-Buffering: no");
    using var reader = new StreamReader(await sres.Content.ReadAsStreamAsync());
    var first = "";
    while (!first.StartsWith("data: ")) first = await reader.ReadLineAsync() ?? "";
    Check(first.Contains("TimingData") && !first.Contains("CarData"), "snapshot filtered per client");
    srv.BroadcastFeed("CarData", JsonNode.Parse("""{"Entries":[]}"""), "t1");
    srv.BroadcastFeed("TimingData", JsonNode.Parse("""{"b":2}"""), "t2");
    string? feedLine = null;
    var deadline = DateTime.UtcNow.AddSeconds(3);
    while (DateTime.UtcNow < deadline && feedLine is null)
    {
        var l = await reader.ReadLineAsync();
        if (l is not null && l.StartsWith("data: ") && l.Contains("\"topic\"")) feedLine = l;
    }
    Check(feedLine is not null && feedLine.Contains("TimingData") && !feedLine.Contains("CarData"), "excluded topic not sent, next topic sent");
}

Console.WriteLine(fails > 0 ? $"\n{fails} FAILURES" : "\nALL CORE TESTS PASSED");
Environment.Exit(fails > 0 ? 1 : 0);
