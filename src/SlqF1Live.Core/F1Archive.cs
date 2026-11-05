// F1Archive — F1's officielle sessionsarkiv på livetiming.formula1.com/static/.
// Siden ~15/10-2026 kræver ALLE filer under static/<år>/ et F1 TV-token
// (Authorization: Bearer <subscriptionToken>); uden token svarer CloudFront 401.
//
//   static/<år>/Index.json                       → sæsonens møder og sessioner
//   static/<Path>Index.json                       → sessionens feeds:
//       {"Feeds":{"TimingData":{"KeyFramePath":"TimingData.json",
//                               "StreamPath":"TimingData.jsonStream"}, …}}
//   static/<Path><Topic>.jsonStream               → linjer "HH:MM:SS.mmm<json>"
//                                                   (UTF-8 m. BOM, CRLF), tid
//                                                   relativ til arkivets start
//   static/<Path>TeamRadio/<fil>.mp3              → teamradio
//
// ArchiveConverter bygger en .f1rec af et downloadet arkiv (k-vejs fletning af
// alle streams efter tid), så sessioner man ikke selv optog kan afspilles.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace SlqF1Live.Core;

public sealed record ArchiveResult(int Files, long Bytes, int Missing, bool Unauthorized, bool NotFound, string? Error);

public sealed class F1ArchiveClient
{
    public const string DefaultStaticBase = "https://livetiming.formula1.com/static/";
    /// Kun til test mod en lokal mock (env F1_STATIC_URL). Tokenet sendes kun til formula1.com.
    public static string StaticBase { get; set; } = DefaultStaticBase;
    private readonly HttpClient _http;
    private readonly Func<string?> _token;

    public F1ArchiveClient(HttpClient http, Func<string?> token)
    {
        _http = http;
        _token = token;
    }

    public static string UrlFor(string relative)
        => relative.StartsWith("https://", StringComparison.Ordinal) ? relative : StaticBase.TrimEnd('/') + "/" + relative.TrimStart('/');

    private HttpRequestMessage Request(string relative)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, UrlFor(relative));
        var host = req.RequestUri!.Host;
        if (_token() is { } t && (host.Equals("formula1.com", StringComparison.OrdinalIgnoreCase) ||
                                  host.EndsWith(".formula1.com", StringComparison.OrdinalIgnoreCase)))
            req.Headers.Authorization = new("Bearer", t);
        return req;
    }

    public async Task<(HttpStatusCode Status, JsonNode? Json)> GetJsonAsync(string relative, CancellationToken ct)
    {
        using var req = Request(relative);
        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return (res.StatusCode, null);
        var text = Encoding.UTF8.GetString(await res.Content.ReadAsByteArrayAsync(ct));
        try { return (res.StatusCode, JsonNode.Parse(F1Schedule.StripBom(text))); }
        catch (System.Text.Json.JsonException) { return (res.StatusCode, null); }
    }

    /// Hent til fil via midlertidig fil + omdøbning. Returnerer HTTP-status.
    public async Task<(HttpStatusCode Status, long Bytes)> DownloadAsync(string relative, string targetPath, CancellationToken ct)
    {
        using var req = Request(relative);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) return (res.StatusCode, 0);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
        var tmp = targetPath + ".part";
        long bytes;
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
        {
            await res.Content.CopyToAsync(fs, ct);
            bytes = fs.Length;
        }
        File.Move(tmp, targetPath, overwrite: true);
        return (res.StatusCode, bytes);
    }

    /// Sæsonindeks: {"Year":2026,"Meetings":[{"Name","Location","Sessions":[{"Key","Type","Name","StartDate","Path"}]}]}
    public Task<(HttpStatusCode Status, JsonNode? Json)> SeasonIndexAsync(int year, CancellationToken ct)
        => GetJsonAsync($"{year}/Index.json", ct);

    /// Hent hele sessionsarkivet (alle feeds i sessionens Index.json) til targetDir.
    public async Task<ArchiveResult> DownloadSessionAsync(string sessionPath, string targetDir,
        Action<string>? log, CancellationToken ct)
    {
        var basePath = sessionPath.TrimEnd('/') + "/";
        var (status, index) = await GetJsonAsync(basePath + "Index.json", ct);
        if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden)
            return new ArchiveResult(0, 0, 0, Unauthorized: true, NotFound: false, "arkivet kræver F1 TV-token");
        if (status == HttpStatusCode.NotFound)
            return new ArchiveResult(0, 0, 0, false, NotFound: true, "arkivet findes ikke (endnu)");
        if (index is null)
            return new ArchiveResult(0, 0, 0, false, false, $"Index.json: HTTP {(int)status}");

        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "Index.json"), index.ToJsonString());

        var files = new List<string>();
        if (index["Feeds"] is JsonObject feeds && feeds.Count > 0)
        {
            foreach (var (_, feed) in feeds)
            {
                if (feed?["StreamPath"] is JsonValue sp && sp.TryGetValue<string>(out var s)) files.Add(s);
                if (feed?["KeyFramePath"] is JsonValue kp && kp.TryGetValue<string>(out var k)) files.Add(k);
            }
        }
        else
        {
            // Fallback: kendte topics
            foreach (var t in F1FeedClient.Topics) { files.Add(t + ".jsonStream"); files.Add(t + ".json"); }
        }

        int ok = 0, missing = 0;
        long total = 0;
        foreach (var f in files.Distinct(StringComparer.Ordinal))
        {
            if (f.Contains("..") || f.StartsWith('/')) continue;
            var target = Path.Combine(targetDir, f);
            if (File.Exists(target) && new FileInfo(target).Length > 0) { ok++; total += new FileInfo(target).Length; continue; }
            var (st, bytes) = await DownloadAsync(basePath + f, target, ct);
            if (st == HttpStatusCode.OK) { ok++; total += bytes; }
            else missing++;
        }
        log?.Invoke($"arkiv {basePath}: {ok} filer, {total / 1024 / 1024.0:0.0} MB" + (missing > 0 ? $", {missing} mangler" : ""));
        return new ArchiveResult(ok, total, missing, false, false, null);
    }

    /// Teamradio-stier (relative til sessionens Path) fra TeamRadio-data
    /// ({"Captures":[…]} eller patch {"Captures":{"3":{…}}}).
    public static List<string> RadioPaths(JsonNode? teamRadio)
    {
        var list = new List<string>();
        var caps = teamRadio?["Captures"];
        IEnumerable<JsonNode?> items = caps switch
        {
            JsonArray a => a,
            JsonObject o => o.Select(kv => kv.Value),
            _ => Array.Empty<JsonNode?>(),
        };
        foreach (var c in items)
            if (c?["Path"] is JsonValue v && v.TryGetValue<string>(out var p) && p.Length > 0 &&
                !p.Contains("..") && !p.StartsWith('/'))
                list.Add(p);
        return list;
    }
}

public static class ArchiveConverter
{
    /// "00:01:02.345{…}" → (62345, "{…}"). Også "1.02:03:04.567…" (over et døgn).
    public static bool TryParseStreamLine(string line, out long offsetMs, out string json)
    {
        offsetMs = 0;
        json = "";
        if (line.Length > 0 && line[0] == '﻿') line = line[1..];
        if (line.Length < 13) return false;
        string ts;
        if (line[2] == ':')
        {
            ts = line[..12];
            json = line[12..];
        }
        else
        {
            var p = line.IndexOf('.');
            if (p < 0 || p + 1 + 12 > line.Length) return false;
            ts = line[..(p + 1 + 12)];
            json = line[(p + 1 + 12)..];
        }
        if (json.Length == 0 || (json[0] != '{' && json[0] != '"' && json[0] != '[')) return false;
        if (!TimeSpan.TryParseExact(ts, new[] { @"hh\:mm\:ss\.fff", @"d\.hh\:mm\:ss\.fff" },
                CultureInfo.InvariantCulture, out var span))
            return false;
        offsetMs = (long)span.TotalMilliseconds;
        return true;
    }

    public sealed record ConvertResult(int Topics, long Lines, DateTime StartedUtc, long DurationMs);

    /// Flet alle *.jsonStream i archiveDir til én .f1rec (samme format som live-optagelser).
    public static ConvertResult Convert(string archiveDir, string outPath, string sessionName, JsonObject? headerExtra)
    {
        var streams = Directory.GetFiles(archiveDir, "*.jsonStream").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        var readers = new List<(string Topic, StreamReader Reader)>();
        try
        {
            foreach (var f in streams)
            {
                var name = Path.GetFileName(f);
                readers.Add((name[..^".jsonStream".Length],
                    new StreamReader(f, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true)));
            }

            var t0 = FindT0(archiveDir) ?? DateTime.UtcNow;
            var pq = new PriorityQueue<(int Idx, long Offset, string Json), (long, int)>();
            void Advance(int i)
            {
                string? line;
                while ((line = readers[i].Reader.ReadLine()) is not null)
                {
                    if (TryParseStreamLine(line, out var off, out var js)) { pq.Enqueue((i, off, js), (off, i)); return; }
                }
            }
            for (var i = 0; i < readers.Count; i++) Advance(i);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            var tmp = outPath + ".part";
            long lines = 0, lastT = 0;
            using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
            {
                var header = new JsonObject
                {
                    ["type"] = "header",
                    ["version"] = 1,
                    ["started"] = t0.ToString("o"),
                    ["session"] = sessionName,
                    ["source"] = "archive",
                };
                if (headerExtra is not null)
                    foreach (var kv in headerExtra)
                        if (!header.ContainsKey(kv.Key)) header[kv.Key] = kv.Value?.DeepClone();
                w.WriteLine(header.ToJsonString());
                w.WriteLine("{\"t\":0,\"type\":\"snapshot\",\"data\":{}}");
                var sb = new StringBuilder(4096);
                while (pq.TryDequeue(out var item, out _))
                {
                    var topic = readers[item.Idx].Topic;
                    var ts = (t0 + TimeSpan.FromMilliseconds(item.Offset)).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                    sb.Clear();
                    sb.Append("{\"t\":").Append(item.Offset.ToString(CultureInfo.InvariantCulture))
                      .Append(",\"type\":\"feed\",\"topic\":").Append(System.Text.Json.JsonSerializer.Serialize(topic))
                      .Append(",\"data\":").Append(item.Json.TrimEnd())
                      .Append(",\"ts\":\"").Append(ts).Append("\"}");
                    w.WriteLine(sb.ToString());
                    lines++;
                    lastT = Math.Max(lastT, item.Offset);
                    Advance(item.Idx);
                }
            }
            File.Move(tmp, outPath, overwrite: true);
            return new ConvertResult(readers.Count, lines, t0, lastT);
        }
        finally
        {
            foreach (var (_, r) in readers) r.Dispose();
        }
    }

    /// Arkivets tid 0 i UTC: første Heartbeat {"Utc":…} minus dens offset.
    public static DateTime? FindT0(string archiveDir)
    {
        var hb = Path.Combine(archiveDir, "Heartbeat.jsonStream");
        if (!File.Exists(hb)) return null;
        using var r = new StreamReader(hb, new UTF8Encoding(false), true);
        string? line;
        while ((line = r.ReadLine()) is not null)
        {
            if (!TryParseStreamLine(line, out var off, out var js)) continue;
            try
            {
                var utc = JsonNode.Parse(js)?["Utc"]?.GetValue<string>();
                if (utc is not null && DateTime.TryParse(utc, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var u))
                    return u - TimeSpan.FromMilliseconds(off);
            }
            catch (System.Text.Json.JsonException) { /* næste linje */ }
        }
        return null;
    }
}
