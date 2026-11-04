// MediaService — alt der hentes ved siden af selve feedet:
//   * Teamradio (mp3) mens sessionen kører → <recordings>/<stem>/TeamRadio/…
//   * F1's officielle arkiv efter sessionen  → <recordings>/<stem>/archive/…
//   * Import af sessioner man ikke selv optog (fra arkivet → .f1rec)
// Alt kræver F1 TV-token siden 15/10-2026. Mangler tokenet, parkeres jobbet
// og genoptages når et token kommer.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using SlqF1Live.Core;

namespace SlqF1Live.Server;

public sealed record JobInfo(string Id, string Kind, string Target, string State, string Message,
    DateTime CreatedUtc, DateTime? EndedUtc);

public sealed class MediaService
{
    private readonly ServerConfig _cfg;
    private readonly F1ArchiveClient _archive;
    private readonly RecordingManager _rec;
    private readonly LogBuffer _log;
    private readonly Func<string?> _token;

    private sealed record RadioJob(string Url, string Target, int Attempt, DateTime NotBefore);
    private readonly Channel<RadioJob> _radio = Channel.CreateUnbounded<RadioJob>();
    private readonly ConcurrentDictionary<string, byte> _radioKnown = new();
    private readonly ConcurrentQueue<RadioJob> _radioParked = new();
    private int _radioOk;

    private sealed record ArchiveJob(string Stem, string SessionPath, string MediaDir, DateTime NotBefore, DateTime GiveUpAt);
    private readonly ConcurrentDictionary<string, ArchiveJob> _archiveJobs = new();
    private readonly ConcurrentDictionary<string, JobInfo> _jobs = new();
    private readonly SemaphoreSlim _archiveGate = new(1, 1);

    public MediaService(ServerConfig cfg, F1ArchiveClient archive, RecordingManager rec, LogBuffer log, Func<string?> token)
    {
        _cfg = cfg;
        _archive = archive;
        _rec = rec;
        _log = log;
        _token = token;
    }

    public int RadioDownloaded => _radioOk;
    public int RadioParked => _radioParked.Count;
    public IReadOnlyList<JobInfo> Jobs => _jobs.Values.OrderByDescending(j => j.CreatedUtc).Take(50).ToList();

    public void Start(CancellationToken ct)
    {
        _ = Task.Run(() => RadioWorkerAsync(ct), ct);
        _ = Task.Run(() => ArchiveLoopAsync(ct), ct);
        RescheduleUnfinishedArchives();
    }

    // ---------- Teamradio ----------

    public void OnTeamRadio(JsonNode? teamRadio, RecordingInfo? current)
    {
        if (!_cfg.TeamRadioDownload || current?.Session.Path is not { } sessionPath) return;
        foreach (var rel in F1ArchiveClient.RadioPaths(teamRadio))
        {
            var target = Path.Combine(current.MediaDir, rel);
            if (!_radioKnown.TryAdd(target, 0) || File.Exists(target)) continue;
            _radio.Writer.TryWrite(new RadioJob(sessionPath.TrimEnd('/') + "/" + rel, target, 0, DateTime.UtcNow));
        }
    }

    /// Nyt token → prøv parkerede downloads igen.
    public void OnTokenAvailable()
    {
        while (_radioParked.TryDequeue(out var j)) _radio.Writer.TryWrite(j with { NotBefore = DateTime.UtcNow });
        foreach (var (k, j) in _archiveJobs) _archiveJobs[k] = j with { NotBefore = DateTime.UtcNow };
    }

    private async Task RadioWorkerAsync(CancellationToken ct)
    {
        await foreach (var job in _radio.Reader.ReadAllAsync(ct))
        {
            var wait = job.NotBefore - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            try
            {
                var (status, _) = await _archive.DownloadAsync(job.Url, job.Target, ct);
                if (status == HttpStatusCode.OK)
                {
                    Interlocked.Increment(ref _radioOk);
                    continue;
                }
                if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    if (_token() is null) { _radioParked.Enqueue(job); continue; }
                }
                if (job.Attempt < 8)
                {
                    // Filen udgives ofte lidt efter beskeden — prøv igen
                    _ = RequeueLater(job with { Attempt = job.Attempt + 1, NotBefore = DateTime.UtcNow.AddSeconds(20 * (job.Attempt + 1)) }, ct);
                }
                else _log.Info($"teamradio opgivet ({(int)status}): {Path.GetFileName(job.Target)}");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested) return;
                if (job.Attempt < 8)
                    _ = RequeueLater(job with { Attempt = job.Attempt + 1, NotBefore = DateTime.UtcNow.AddSeconds(30) }, ct);
            }
        }
    }

    private async Task RequeueLater(RadioJob job, CancellationToken ct)
    {
        try { await Task.Delay(job.NotBefore - DateTime.UtcNow is var d && d > TimeSpan.Zero ? d : TimeSpan.Zero, ct); }
        catch (TaskCanceledException) { return; }
        _radio.Writer.TryWrite(job);
    }

    // ---------- Arkiv efter session ----------

    public void ScheduleArchive(RecordingInfo info)
    {
        if (!_cfg.ArchiveDownload || info.Session.Path is null) return;
        var now = DateTime.UtcNow;
        _archiveJobs[info.FileStem] = new ArchiveJob(info.FileStem, info.Session.Path, info.MediaDir,
            now.AddMinutes(20), now.AddHours(36));
        _log.Info($"arkiv for {info.FileStem} hentes når F1 har udgivet det (prøver om 20 min)");
    }

    private void RescheduleUnfinishedArchives()
    {
        if (!_cfg.ArchiveDownload || !Directory.Exists(_cfg.RecordingsDir)) return;
        foreach (var dir in Directory.GetDirectories(_cfg.RecordingsDir))
        {
            try
            {
                var metaPath = Path.Combine(dir, "meta.json");
                if (!File.Exists(metaPath) || File.Exists(Path.Combine(dir, "archive", "Index.json"))) continue;
                var meta = JsonNode.Parse(File.ReadAllText(metaPath));
                if (meta?["state"]?.GetValue<string>() is not ("closed" or "interrupted")) continue;
                if (meta["path"]?.GetValue<string>() is not { } path) continue;
                if (meta["archive"]?["state"]?.GetValue<string>() == "opgivet") continue;
                var ended = DateTime.TryParse(meta["endedUtc"]?.GetValue<string>(), null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal, out var e) ? e : DateTime.UtcNow;
                if (DateTime.UtcNow - ended > TimeSpan.FromDays(3)) continue;
                var stem = Path.GetFileName(dir);
                _archiveJobs[stem] = new ArchiveJob(stem, path, dir, DateTime.UtcNow.AddMinutes(2), ended.AddHours(36));
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { /* spring over */ }
        }
        if (!_archiveJobs.IsEmpty) _log.Info($"arkiv: {_archiveJobs.Count} session(er) mangler stadig arkiv — genoptaget");
    }

    private async Task ArchiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (TaskCanceledException) { return; }
            foreach (var (stem, job) in _archiveJobs.ToArray())
            {
                if (DateTime.UtcNow < job.NotBefore) continue;
                if (_rec.Current?.FileStem == stem) continue; // optages stadig
                if (DateTime.UtcNow > job.GiveUpAt)
                {
                    _archiveJobs.TryRemove(stem, out _);
                    WriteArchiveMeta(job.MediaDir, "opgivet", null);
                    _log.Info($"arkiv for {stem} opgivet (ikke tilgængeligt inden for tidsfristen)");
                    continue;
                }
                if (_token() is null)
                {
                    _archiveJobs[stem] = job with { NotBefore = DateTime.UtcNow.AddMinutes(30) };
                    continue;
                }
                var result = await DownloadArchiveAsync(stem, job.SessionPath, job.MediaDir, ct);
                if (result.Error is null && result.Files > 0)
                    _archiveJobs.TryRemove(stem, out _);
                else
                    _archiveJobs[stem] = job with { NotBefore = DateTime.UtcNow.AddMinutes(30) };
            }
        }
    }

    private async Task<ArchiveResult> DownloadArchiveAsync(string stem, string sessionPath, string mediaDir, CancellationToken ct)
    {
        await _archiveGate.WaitAsync(ct);
        try
        {
            var archiveDir = Path.Combine(mediaDir, "archive");
            var r = await _archive.DownloadSessionAsync(sessionPath, archiveDir, _log.Info, ct);
            if (r.Error is not null)
            {
                if (!r.NotFound) _log.Info($"arkiv {stem}: {r.Error}");
                return r;
            }
            WriteArchiveMeta(mediaDir, "hentet", r);
            // Teamradio som evt. blev misset live
            var tr = Path.Combine(archiveDir, "TeamRadio.json");
            if (_cfg.TeamRadioDownload && File.Exists(tr))
            {
                var node = JsonNode.Parse(F1Schedule.StripBom(await File.ReadAllTextAsync(tr, ct)));
                foreach (var rel in F1ArchiveClient.RadioPaths(node))
                {
                    var target = Path.Combine(mediaDir, rel);
                    if (File.Exists(target) || !_radioKnown.TryAdd(target, 0)) continue;
                    _radio.Writer.TryWrite(new RadioJob(sessionPath.TrimEnd('/') + "/" + rel, target, 0, DateTime.UtcNow));
                }
            }
            return r;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            return new ArchiveResult(0, 0, 0, false, false, ex.Message);
        }
        finally
        {
            _archiveGate.Release();
        }
    }

    private void WriteArchiveMeta(string mediaDir, string state, ArchiveResult? r)
    {
        try
        {
            var path = Path.Combine(mediaDir, "meta.json");
            var meta = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject() : new JsonObject();
            meta["archive"] = new JsonObject
            {
                ["state"] = state,
                ["files"] = r?.Files,
                ["bytes"] = r?.Bytes,
                ["missing"] = r?.Missing,
                ["updatedUtc"] = DateTime.UtcNow.ToString("o"),
            };
            Directory.CreateDirectory(mediaDir);
            File.WriteAllText(path, meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or JsonException) { /* ikke kritisk */ }
    }

    public int PendingArchives => _archiveJobs.Count;

    // ---------- Manuelle jobs fra adminsiden ----------

    private JobInfo NewJob(string kind, string target)
    {
        var job = new JobInfo(Guid.NewGuid().ToString("N")[..8], kind, target, "kører", "", DateTime.UtcNow, null);
        _jobs[job.Id] = job;
        return job;
    }

    private void Finish(JobInfo job, string state, string message)
    {
        _jobs[job.Id] = job with { State = state, Message = message, EndedUtc = DateTime.UtcNow };
        _log.Info($"job {job.Kind} {job.Target}: {state} — {message}");
    }

    /// Hent en session fra F1's arkiv og byg en .f1rec (sessioner man ikke selv optog).
    public JobInfo StartImport(string sessionPath, string? meeting, string? name, string? startDate, CancellationToken ct)
    {
        var stem = RecordingManager.FileStemFor(sessionPath, meeting ?? "", name ?? "", startDate, null);
        var job = NewJob("import", stem);
        _ = Task.Run(async () =>
        {
            try
            {
                var mediaDir = Path.Combine(_cfg.RecordingsDir, stem);
                var r = await DownloadArchiveAsync(stem, sessionPath, mediaDir, ct);
                if (r.Error is not null || r.Files == 0) { Finish(job, "fejl", r.Error ?? "ingen filer"); return; }
                var res = BuildFromArchive(stem, sessionPath, meeting, name, startDate);
                Finish(job, "færdig", res);
            }
            catch (Exception ex) { Finish(job, "fejl", ex.Message); }
        }, ct);
        return job;
    }

    /// Byg <stem>_arkiv.f1rec af et allerede hentet arkiv (eller <stem>.f1rec hvis ingen live-optagelse).
    public JobInfo StartConvert(string stem, CancellationToken ct)
    {
        var job = NewJob("byg", stem);
        _ = Task.Run(() =>
        {
            try { Finish(job, "færdig", BuildFromArchive(stem, null, null, null, null)); }
            catch (Exception ex) { Finish(job, "fejl", ex.Message); }
        }, ct);
        return job;
    }

    /// Hent arkivet igen for en eksisterende optagelse.
    public JobInfo StartFetch(string stem, string sessionPath, CancellationToken ct)
    {
        var job = NewJob("arkiv", stem);
        _ = Task.Run(async () =>
        {
            try
            {
                var r = await DownloadArchiveAsync(stem, sessionPath, Path.Combine(_cfg.RecordingsDir, stem), ct);
                Finish(job, r.Error is null ? "færdig" : "fejl",
                    r.Error ?? $"{r.Files} filer, {r.Bytes / 1024 / 1024.0:0.0} MB");
            }
            catch (Exception ex) { Finish(job, "fejl", ex.Message); }
        }, ct);
        return job;
    }

    private string BuildFromArchive(string stem, string? sessionPath, string? meeting, string? name, string? startDate)
    {
        var mediaDir = Path.Combine(_cfg.RecordingsDir, stem);
        var archiveDir = Path.Combine(mediaDir, "archive");
        if (!Directory.Exists(archiveDir)) throw new IOException("intet arkiv hentet for " + stem);

        // Sessionsinfo fra arkivets SessionInfo.json hvis vi ikke fik den med
        var si = TryReadJson(Path.Combine(archiveDir, "SessionInfo.json"));
        meeting ??= si?["Meeting"]?["Name"]?.GetValue<string>();
        name ??= si?["Name"]?.GetValue<string>();
        sessionPath ??= si?["Path"]?.GetValue<string>();
        var headline = $"{meeting} — {name}".Trim(' ', '—');

        var live = Path.Combine(_cfg.RecordingsDir, stem + ".f1rec");
        var outPath = File.Exists(live) ? Path.Combine(_cfg.RecordingsDir, stem + "_arkiv.f1rec") : live;
        var extra = new JsonObject
        {
            ["meeting"] = meeting,
            ["sessionName"] = name,
            ["sessionType"] = si?["Type"]?.GetValue<string>(),
            ["path"] = sessionPath,
            ["startDate"] = startDate ?? si?["StartDate"]?.GetValue<string>(),
        };
        var res = ArchiveConverter.Convert(archiveDir, outPath, headline.Length > 0 ? headline : stem, extra);
        return $"{Path.GetFileName(outPath)}: {res.Lines} linjer, {res.Topics} topics, {res.DurationMs / 60000.0:0} min";
    }

    private static JsonNode? TryReadJson(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(F1Schedule.StripBom(File.ReadAllText(path))) : null; }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }
}
