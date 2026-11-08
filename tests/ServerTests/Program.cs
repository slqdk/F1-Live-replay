// ServerTests — adgangskontrol bag Nginx Proxy Manager (CIDR, betroede proxies)
// og teamradio-tekst (RadioText, transcripts.json, TranscriptService med falske whisper-programmer).

using System.Net;
using SlqF1Live.Server;

var fails = 0;
void Check(bool cond, string msg)
{
    if (!cond) { fails++; Console.WriteLine("FAIL " + msg); }
    else Console.WriteLine("ok   " + msg);
}

// IpNet
Check(IpNet.TryParse("30.11.0.0/20", out var lan) && lan.Contains(IPAddress.Parse("30.11.10.42")), "LAN /20 contains workstation");
Check(!lan.Contains(IPAddress.Parse("30.11.16.1")), "outside /20");
Check(IpNet.TryParse("10.0.0.0/20", out var wg) && wg.Contains(IPAddress.Parse("10.0.0.2")), "WireGuard pool");
Check(IpNet.TryParse("::1", out var v6) && v6.Contains(IPAddress.IPv6Loopback), "IPv6 single host");
Check(lan.Contains(IPAddress.Parse("::ffff:30.11.0.5")), "IPv4-mapped IPv6 normalised");
Check(!IpNet.TryParse("30.11.0.0/33", out _) && !IpNet.TryParse("nonsense", out _), "invalid CIDR rejected");
Check(IpNet.ParseList("30.11.0.0/20, 10.0.0.0/20;127.0.0.1").Count == 3, "list parsing");

// Uden betroede proxies: forward-headere gør afsenderen "udefra"
var ac = new AccessControl("30.11.0.0/20,10.0.0.0/20,127.0.0.0/8", null, "", "", null);
var (ip1, u1) = ac.ClientIp(IPAddress.Parse("30.11.0.20"), null, null);
Check(ip1.ToString() == "30.11.0.20" && !u1 && ac.IsAdmin(ip1, u1), "direct LAN request is admin");
var (ip2, u2) = ac.ClientIp(IPAddress.Parse("30.11.0.20"), "8.8.8.8", "8.8.8.8");
Check(u2 && !ac.IsAdmin(ip2, u2), "forwarded via untrusted proxy is never admin");

// Med NPM som betroet proxy: den videresendte IP afgør
var ac2 = new AccessControl("30.11.0.0/20,10.0.0.0/20", null, "30.11.0.103/32", "", null);
var (ip3, u3) = ac2.ClientIp(IPAddress.Parse("30.11.0.103"), "8.8.8.8", "8.8.8.8");
Check(ip3.ToString() == "8.8.8.8" && !u3 && !ac2.IsAdmin(ip3, u3), "internet client via NPM is not admin");
var (ip4, u4) = ac2.ClientIp(IPAddress.Parse("30.11.0.103"), "10.0.0.2", null);
Check(ip4.ToString() == "10.0.0.2" && ac2.IsAdmin(ip4, u4), "WireGuard client via NPM is admin");
var (ip5, _) = ac2.ClientIp(IPAddress.Parse("30.11.0.103"), "1.2.3.4, 30.11.0.103", null);
Check(ip5.ToString() == "1.2.3.4", "rightmost untrusted hop in X-Forwarded-For");
var (ip6, _) = ac2.ClientIp(IPAddress.Parse("30.11.0.103"), "6.6.6.6, 9.9.9.9", null);
Check(ip6.ToString() == "9.9.9.9", "spoofed leftmost XFF ignored");
Check(!ac2.ViewRestricted, "view open by default");
Check(new AccessControl("", null, "", "", "hemmelig").ViewRestricted, "VIEW_TOKEN restricts view");

// ---------- Teamradio-tekst: rene hjælpere ----------
Check(RadioText.Prompt(new[] { "Max Verstappen", "Charles  Leclerc", "max verstappen", "", "Kimi Antonelli" })
      == "F1 team radio. Box, box. Copy that. Max Verstappen, Charles Leclerc, Kimi Antonelli.", "prompt: names deduped and normalised");
Check(RadioText.Prompt(Array.Empty<string>()) == RadioText.BasePrompt, "prompt without names");
Check(RadioText.Prompt(Enumerable.Range(0, 200).Select(i => $"Driver Number{i}")).Length <= RadioText.MaxPromptChars, "prompt capped");

var vad1 = RadioText.ParseVad("\nDetected 1 speech segments:\nSpeech segment 0: start = 64.00, end = 550.00\n\n");
Check(vad1 is { Count: 1 } && Math.Abs(vad1[0].Start - 0.64) < 1e-9 && Math.Abs(vad1[0].End - 5.5) < 1e-9, "vad: centiseconds to seconds");
Check(RadioText.ParseVad("\nDetected 0 speech segments:\n\n") is { Count: 0 }, "vad: no speech");
Check(RadioText.ParseVad("error: failed to read audio data") is null, "vad: unreadable output -> null");

Check(RadioText.AudioContext(5.5) == (int)Math.Ceiling(6.5 * 50) + 64, "audio ctx follows speech end + 1 s");
Check(RadioText.AudioContext(0) == 0 && RadioText.AudioContext(26) == 0, "full window when unknown or long");
Check(RadioText.AudioContext(25) <= 1500, "audio ctx capped at 1500");

Check(RadioText.Clean(" Box, box.\n Confirm. ") == "Box, box. Confirm.", "clean: lines joined");
Check(RadioText.Clean("[MUSIC PLAYING]") == "" && RadioText.Clean("(machinery whirring)") == "", "clean: sound descriptions only -> empty");
Check(RadioText.Clean("Thank you.") == "" && RadioText.Clean(" you") == "", "clean: noise hallucinations dropped");
Check(RadioText.Clean("Thank you guys, great job.") == "Thank you guys, great job.", "clean: real thanks kept");
Check(RadioText.Clean("Push now. Push now. Push now. Push now. Push now.") == "Push now. Push now. Push now.", "clean: loops cut to 3");
Check(RadioText.Clean("(engine) Box this lap. *beep*") == "Box this lap.", "clean: inline annotations removed");
Check(RadioText.Clean(new string('a', 2000)).Length <= RadioText.MaxTextChars + 1, "clean: length capped");

// ---------- transcripts.json ----------
var txRoot = Path.Combine(Path.GetTempPath(), "slqf1test_tx_" + Guid.NewGuid().ToString("N")[..8]);
var tf = TranscriptFile.Load(txRoot);
Check(tf.Count == 0 && !tf.Has("TeamRadio/a.mp3"), "transcripts: empty when missing");
tf.Set("TeamRadio/a.mp3", "Box, box. «Æ» — ok", true, "small.en", 1234, error: false);
tf.Set("TeamRadio/b.mp3", "", false, "small.en", 0, error: true);
tf.Save();
var tf2 = TranscriptFile.Load(txRoot);
Check(tf2.Has("TeamRadio/a.mp3") && tf2.Text("TeamRadio/a.mp3") == "Box, box. «Æ» — ok", "transcripts: roundtrip");
Check(!tf2.Has("TeamRadio/b.mp3"), "transcripts: failed clip counts as missing (retried)");
Check(File.ReadAllText(tf.FilePath).Contains("«Æ»") && !File.Exists(tf.FilePath + ".tmp"), "transcripts: readable UTF-8, no temp file left");
File.WriteAllText(tf.FilePath, "{ødelagt");
Check(TranscriptFile.Load(txRoot).Count == 0, "transcripts: corrupt file -> start over");

// ---------- TranscriptService med falske whisper-programmer ----------
if (!OperatingSystem.IsWindows())
{
    var root = Path.Combine(Path.GetTempPath(), "slqf1test_ts_" + Guid.NewGuid().ToString("N")[..8]);
    var bin = Path.Combine(root, "bin");
    var models = Path.Combine(root, "models");
    var radioDir = Path.Combine(root, "recordings", "2026-11-08_Test_Race", "TeamRadio");
    Directory.CreateDirectory(bin);
    Directory.CreateDirectory(models);
    Directory.CreateDirectory(radioDir);
    void Script(string name, string body)
    {
        var path = Path.Combine(bin, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    // VAD: "noise" = ingen tale, "bad" = ulæselig fil, ellers tale 0,5–4 s
    Script("whisper-vad-speech-segments", """
        while [ $# -gt 0 ]; do [ "$1" = "-f" ] && f="$2"; shift; done
        case "$f" in
          *noise*) printf '\nDetected 0 speech segments:\n\n' ;;
          *bad*) echo "error: failed to read audio data from $f" >&2; exit 2 ;;
          *) printf '\nDetected 1 speech segments:\nSpeech segment 0: start = 50.00, end = 400.00\n\n' ;;
        esac
        """);
    Script("whisper-cli", $"""
        printf '%s\n' "$@" > "{root}/args-$$.txt"
        printf '\n [MUSIC] Box, box. Box this lap.\n'
        """);
    foreach (var m in new[] { "ggml-small.en.bin", TranscriptService.VadModelName })
        File.WriteAllBytes(Path.Combine(models, m), [.. "lmgg"u8, .. new byte[200_000]]);
    foreach (var f in new[] { "a.mp3", "noise.mp3", "bad.mp3" })
        File.WriteAllText(Path.Combine(radioDir, f), "mp3");

    var log = new LogBuffer(null, 1);
    var cfg = new ServerConfig { DataDir = root, RecordingsDir = Path.Combine(root, "recordings"), WhisperDir = bin, TranscribeThreads = 2 };
    using var svc = new TranscriptService(cfg, log, () => new[] { "Max Verstappen", "Charles Leclerc" });
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    Check(svc.Enabled, "transcribe: enabled with binaries present");
    svc.Start(cts.Token);
    File.WriteAllText(Path.Combine(radioDir, "live.mp3"), "mp3");
    svc.Enqueue(Path.Combine(root, "recordings", "2026-11-08_Test_Race"), "TeamRadio/live.mp3", live: true);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.Elapsed < TimeSpan.FromSeconds(20))
    {
        var st = svc.AdminStatus();
        if ((int)st["done"]! + (int)st["failed"]! >= 4) break;
        await Task.Delay(100);
    }
    var txs = TranscriptFile.Load(Path.Combine(root, "recordings", "2026-11-08_Test_Race"));
    Check(svc.Ready, "transcribe: existing models accepted (no download)");
    Check(txs.Text("TeamRadio/a.mp3") == "Box, box. Box this lap.", "transcribe: backfill clip transcribed + cleaned");
    Check(txs.Text("TeamRadio/live.mp3") == "Box, box. Box this lap.", "transcribe: live clip transcribed");
    var noiseJson = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(txs.FilePath))!["clips"]!["TeamRadio/noise.mp3"]!;
    Check(txs.Has("TeamRadio/noise.mp3") && noiseJson["speech"]!.GetValue<bool>() == false && txs.Text("TeamRadio/noise.mp3") == "",
        "transcribe: no speech -> whisper skipped, empty text");
    Check(!txs.Has("TeamRadio/bad.mp3") && (int)svc.AdminStatus()["failed"]! == 1, "transcribe: unreadable file -> failed, retried later");
    var argFiles = Directory.GetFiles(root, "args-*.txt");
    var args1 = argFiles.Length > 0 ? File.ReadAllLines(argFiles[0]) : Array.Empty<string>();
    Check(argFiles.Length == 2, $"transcribe: whisper run only for clips with speech ({argFiles.Length})");
    Check(args1.Contains("-sns") && args1.Contains("-nt") && string.Join(" ", args1).Contains("-l en") &&
          string.Join(" ", args1).Contains("-ac " + RadioText.AudioContext(4.0)), "transcribe: whisper args (-ac from VAD, -l en, -sns)");
    Check(args1.Any(a => a.StartsWith("F1 team radio.") && a.Contains("Charles Leclerc")), "transcribe: prompt with driver names");
    Check(File.ReadAllText(Path.Combine(models, "driver-names.txt")).Contains("Max Verstappen"), "transcribe: names remembered for backfill");
    cts.Cancel();

    var off = new TranscriptService(new ServerConfig { DataDir = root, WhisperDir = Path.Combine(root, "nope") }, log, () => Array.Empty<string>());
    Check(!off.Enabled && !(bool)off.PublicStatus()["enabled"]!, "transcribe: disabled without whisper binaries");
    try { Directory.Delete(root, true); } catch (IOException) { }
}

// ---------- Banekort: CircuitService ----------
{
    var raw = System.Text.Json.Nodes.JsonNode.Parse("""
        {"circuitKey":14,"circuitName":"Interlagos","year":2023,"rotation":258,"meetingName":"x",
         "x":[0,10.4,20,30,40,50,60,70,80,90,100],"y":[0,1,2,3,4,5,6,7,8,9,10.6],
         "corners":[{"number":1,"angle":-87.79,"trackPosition":{"x":-2393.43,"y":-8518.95}},
                    {"number":4,"letter":"a","angle":12,"trackPosition":{"x":5,"y":6}},
                    {"number":9,"trackPosition":{"x":"bad","y":1}}],
         "pitLoss":{"normal":"21.84","sc":"14.16","vsc":15.05}}
        """);
    var slim = CircuitService.Slim(raw)!;
    Check(slim is not null && (int)slim["key"]! == 14 && (int)slim["year"]! == 2023 && (double)slim["rotation"]! == 258,
        "circuit: key, year, rotation kept");
    Check(slim!["x"]!.AsArray().Count == 11 && (double)slim["x"]![1]! == 10 && (double)slim["y"]![10]! == 11, "circuit: coordinates rounded");
    var corners = slim["corners"]!.AsArray();
    Check(corners.Count == 2 && (string)corners[0]!["n"]! == "1" && (string)corners[1]!["n"]! == "4a" && (double)corners[0]!["x"]! == -2393,
        "circuit: corners slimmed (letter kept, bad entry skipped)");
    Check(slim["meetingName"] is null, "circuit: unused fields dropped");
    Check(CircuitService.Slim(System.Text.Json.Nodes.JsonNode.Parse("""{"x":[1,2],"y":[1,2]}""")) is null, "circuit: too few points -> null");
    var pl = slim["pitLoss"]!;
    Check((double)pl["normal"]! == 21.84 && (double)pl["sc"]! == 14.16 && (double)pl["vsc"]! == 15.05,
        "circuit: pit loss as numbers (strings and numbers accepted)");
    static System.Text.Json.Nodes.JsonNode? Loss(string json) => CircuitService.Slim(System.Text.Json.Nodes.JsonNode.Parse(
        $$"""{"x":[0,1,2,3,4,5,6,7,8,9],"y":[0,1,2,3,4,5,6,7,8,9]{{json}}}"""))!["pitLoss"];
    Check(Loss("") is null && Loss(""","pitLoss":{"normal":"x"}""") is null && Loss(""","pitLoss":{"normal":"250"}""") is null,
        "circuit: missing or nonsense pit loss -> null");
    var partial = Loss(""","pitLoss":{"normal":"20"}""")!;
    Check((double)partial["sc"]! == 13 && (double)partial["vsc"]! == 14, "circuit: missing SC/VSC loss estimated from normal");

    var calls = 0;
    var body = raw!.ToJsonString();
    var handler = new ServerFakeHttp(path => { calls++; return path.EndsWith("/14/2026") ? (200, body) : (404, "{}"); });
    var croot = Path.Combine(Path.GetTempPath(), "slqf1test_circ_" + Guid.NewGuid().ToString("N")[..8]);
    using (var svc = new CircuitService(new ServerConfig { DataDir = croot }, new LogBuffer(null, 1), handler))
    {
        var j1 = await svc.GetJsonAsync(14, 2026, CancellationToken.None);
        var j2 = await svc.GetJsonAsync(14, 2026, CancellationToken.None);
        Check(j1 is not null && j1 == j2 && calls == 1, $"circuit: fetched once, then from disk ({calls} calls)");
        Check(File.Exists(Path.Combine(croot, "cache", "circuits", "circuit_v2_14_2026.json")), "circuit: cached on disk");
        var miss1 = await svc.GetJsonAsync(99, 2026, CancellationToken.None);
        var miss2 = await svc.GetJsonAsync(99, 2026, CancellationToken.None);
        Check(miss1 is null && miss2 is null && calls == 2, $"circuit: failure remembered, no hammering ({calls} calls)");
    }
    try { Directory.Delete(croot, true); } catch (IOException) { }
}

// ---------- Omgangstider: LapTable + LapService (syntetiske linjer) ----------
{
    static string Td(long t, string lines) => "{\"t\":" + t + ",\"type\":\"feed\",\"topic\":\"TimingData\",\"data\":{\"Lines\":{" + lines + "}},\"ts\":\"x\"}";
    static string Ts(long t, string st) => "{\"t\":" + t + ",\"type\":\"feed\",\"topic\":\"TrackStatus\",\"data\":{\"Status\":\"" + st + "\",\"Message\":\"x\"},\"ts\":\"x\"}";
    var lroot = Path.Combine(Path.GetTempPath(), "slqf1test_laps_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(lroot);
    var lfile = Path.Combine(lroot, "2026-11-08_Test_Race.f1rec");
    var lines = new List<string>
    {
        """{"type":"header","version":1}""",
        // forældet snapshot fra forrige session: 24 omgange — må ikke blive til en omgang
        """{"t":0,"type":"snapshot","data":{"TrackStatus":{"Status":"1"},"TimingData":{"Lines":{"1":{"NumberOfLaps":24,"LastLapTime":{"Value":"1:20.000"}},"4":{"NumberOfLaps":24}}}}}""",
        Td(1000, "\"1\":{\"NumberOfLaps\":1,\"LastLapTime\":{\"Value\":\"1:25.619\"}}"),            // ny session: 1 < 24
        Td(1500, "\"4\":{\"NumberOfLaps\":1}"),                                                     // tid i en senere besked
        Td(1600, "\"4\":{\"LastLapTime\":{\"Value\":\"1:26.100\"}}"),
        Td(2000, "\"1\":{\"InPit\":true}"),                                                         // indomgang
        Td(2500, "\"1\":{\"NumberOfLaps\":2,\"LastLapTime\":{\"Value\":\"1:30.500\"}}"),
        Td(2600, "\"1\":{\"InPit\":false,\"PitOut\":true}"),                                      // udomgang
        Ts(2700, "4"),
        Td(3000, "\"1\":{\"NumberOfLaps\":3,\"LastLapTime\":{\"Value\":\"1:48.000\"}}"),
        Ts(3100, "1"),
        Td(4000, "\"1\":{\"NumberOfLaps\":4,\"LastLapTime\":{\"Value\":\"1:40.000\"}}"),        // SC sluttede i omgangen
        Td(5000, "\"1\":{\"NumberOfLaps\":5,\"LastLapTime\":{\"Value\":\"1:16.256\"}}"),
        Td(7000, "\"1\":{\"GapToLeader\":\"+3.267\"}"),                                         // gab efter stregen
        Td(7100, "\"1\":{\"GapToLeader\":\"+3.300\"}"),                                         // senere: tæller ikke
        Td(7200, "\"4\":{\"GapToLeader\":\"LAP 3\"}"),                                           // føreren: "LAP n" før stregen
        Td(7300, "\"4\":{\"NumberOfLaps\":2,\"LastLapTime\":{\"Value\":\"1:20.000\"}}"),
    };
    File.WriteAllText(lfile, string.Join("\n", lines) + "\n" + "{\"t\":6000,\"type\":\"feed\",\"topic\":\"TimingData\",\"data\":{\"Lines\":{\"1\":{\"NumberOfLaps\":6,");  // halv linje til sidst
    var laps = new LapService(lroot);
    var j = System.Text.Json.Nodes.JsonNode.Parse(laps.GetJson("2026-11-08_Test_Race.f1rec")!)!;
    var d1 = j["drivers"]!["1"]!.AsArray();
    string LapStr(System.Text.Json.Nodes.JsonNode? n) => $"{n![0]}:{n[2]?.ToString() ?? "-"}:{n[3]}@{n[1]}";
    Check(string.Join(" ", d1.Select(LapStr)) == "1:85619:@1000 2:90500:P@2500 3:108000:PS@3000 4:100000:S@4000 5:76256:@5000",
        $"laps: lap list with pit/SC flags ({string.Join(" ", d1.Select(LapStr))})");
    var d4 = j["drivers"]!["4"]!.AsArray();
    Check(d4.Count == 2 && LapStr(d4[0]) == "1:86100:@1500", $"laps: time in a later message ({string.Join(" ", d4.Select(LapStr))})");
    Check((int?)d4[1]![4] == 0, "laps: the leader's gap is 0");
    Check((long)j["t"]! == 7300, "laps: half line at the end is not read yet");
    Check((int)d1[4]![4]! == 3267 && d1[0]![4] is null, "laps: first gap to leader after the line is the lap's gap");
    Check(LapTable.GapMs("LAP 26") == 0 && LapTable.GapMs("+1.5") == 1500 && LapTable.GapMs("1 L") is null && LapTable.GapMs("") is null,
        "laps: gap parsing (leader 0, lapped unknown)");

    File.AppendAllText(lfile, "\"LastLapTime\":{\"Value\":\"1:16.100\"}}}},\"ts\":\"x\"}\n");
    var j2 = System.Text.Json.Nodes.JsonNode.Parse(laps.GetJson("2026-11-08_Test_Race.f1rec", new HashSet<string> { "1" })!)!;
    var d1b = j2["drivers"]!["1"]!.AsArray();
    Check(d1b.Count == 6 && LapStr(d1b[5]) == "6:76100:@6000" && j2["drivers"]!["4"] is null,
        "laps: incremental read completes the line; drivers= filters");
    File.WriteAllText(lfile, lines[0] + "\n" + Td(100, "\"1\":{\"NumberOfLaps\":1,\"LastLapTime\":{\"Value\":\"1:30.000\"}}") + "\n");
    var j3 = System.Text.Json.Nodes.JsonNode.Parse(laps.GetJson("2026-11-08_Test_Race.f1rec")!)!;
    Check(j3["drivers"]!["1"]!.AsArray().Count == 1, "laps: shorter file (replaced) starts over");
    Check(laps.GetJson("../x.f1rec") is null && laps.GetJson("missing.f1rec") is null, "laps: unsafe or missing name -> null");
    try { Directory.Delete(lroot, true); } catch (IOException) { }
}

Console.WriteLine(fails > 0 ? $"\n{fails} FAILURES" : "\nALL SERVER TESTS PASSED");
return fails > 0 ? 1 : 0;

/// Falsk HTTP til CircuitService-test: (status, body) pr. sti.
sealed class ServerFakeHttp(Func<string, (int Status, string Body)> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var (status, body) = respond(request.RequestUri!.AbsolutePath);
        return Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });
    }
}
