// TranscriptService — teamradio som tekst. F1's feed har kun mp3-filer, så
// containeren transskriberer selv med whisper.cpp (lokalt, CPU, ingen sky):
//   1. Silero-VAD (~40 ms): er der overhovedet tale? Nej → ingen tekst
//      (whisper "hører" ellers "Thank you." i ren motorstøj).
//   2. whisper-cli med en prompt af sessionens kørernavne og lydkonteksten
//      skåret til talens længde (~1,5 s pr. klip på 4 kerner med small.en).
//   3. Oprydning (RadioText.Clean) → <recordings>/<stem>/TeamRadio/transcripts.json,
//      skrevet atomisk og serveret via /media som alle andre teamradiofiler.
// Live-klip går forrest; ældre klip (arkiv, import, genstart) tages bagefter
// med lav CPU-prioritet. Modellerne hentes første gang til <data>/models/.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace SlqF1Live.Server;

public sealed class TranscriptService : IDisposable
{
    public const string VadModelName = "ggml-silero-v5.1.2.bin";
    private const string ModelUrlBase = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";
    private const string VadUrl = "https://huggingface.co/ggml-org/whisper-vad/resolve/main/" + VadModelName;

    public sealed record Job(string MediaDir, string Rel, bool Live);

    private readonly ServerConfig _cfg;
    private readonly LogBuffer _log;
    private readonly Func<IReadOnlyList<string>> _names;
    private readonly HttpClient _http;
    private readonly Channel<Job> _live = Channel.CreateUnbounded<Job>();
    private readonly Channel<Job> _later = Channel.CreateUnbounded<Job>();
    private readonly ConcurrentDictionary<string, byte> _queued = new();
    private readonly Dictionary<string, TranscriptFile> _files = new(); // kun worker-tråden
    private IReadOnlyList<string> _lastNames = Array.Empty<string>();
    private volatile bool _ready;
    private volatile string _state;
    private volatile string? _lastError;
    private int _done, _failed;
    private long _totalMs;

    public bool Enabled { get; }
    public bool Ready => _ready;
    public string Model => _cfg.TranscribeModel;
    public string Cli => Path.Combine(_cfg.WhisperDir, "whisper-cli");
    public string VadTool => Path.Combine(_cfg.WhisperDir, "whisper-vad-speech-segments");
    public string ModelPath => Path.Combine(_cfg.ModelsDir, $"ggml-{_cfg.TranscribeModel}.bin");
    public string VadModelPath => Path.Combine(_cfg.ModelsDir, VadModelName);
    public int Threads => _cfg.TranscribeThreads > 0 ? _cfg.TranscribeThreads : Math.Clamp(Environment.ProcessorCount, 1, 4);
    /// Engelske modeller (*.en) kan kun engelsk.
    public string Language => Model.EndsWith(".en", StringComparison.Ordinal) || Model.Contains(".en-", StringComparison.Ordinal)
        ? "en" : _cfg.TranscribeLanguage;

    public TranscriptService(ServerConfig cfg, LogBuffer log, Func<IReadOnlyList<string>> driverNames, HttpMessageHandler? handler = null)
    {
        _cfg = cfg;
        _log = log;
        _names = driverNames;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SLQ-F1-Live/2.0");

        string? why = !cfg.Transcribe ? "slået fra (TRANSCRIBE=false)"
            : !cfg.TeamRadioDownload ? "slået fra (kræver TEAMRADIO_DOWNLOAD)"
            : !File.Exists(Cli) || !File.Exists(VadTool) ? $"slået fra (whisper.cpp findes ikke i {cfg.WhisperDir})"
            : null;
        Enabled = why is null;
        _state = why ?? "starter";
        try
        {
            var f = Path.Combine(cfg.ModelsDir, "driver-names.txt");
            if (File.Exists(f)) _lastNames = File.ReadAllLines(f).Where(l => l.Length > 0).ToArray();
        }
        catch (IOException) { /* bruges kun til prompten */ }
    }

    public void Start(CancellationToken ct)
    {
        if (!Enabled)
        {
            _log.Info("teamradio-tekst " + _state);
            return;
        }
        _ = Task.Run(() => RunAsync(ct), ct);
    }

    /// Et klip er hentet (live = under sessionen; ellers arkiv/import/backfill).
    public void Enqueue(string mediaDir, string rel, bool live)
    {
        if (!Enabled) return;
        var key = Path.GetFullPath(Path.Combine(mediaDir, rel));
        if (!_queued.TryAdd(key, 0)) return;
        (live ? _live : _later).Writer.TryWrite(new Job(mediaDir, rel, live));
    }

    public JsonObject PublicStatus() => new()
    {
        ["enabled"] = Enabled,
        ["ready"] = _ready,
        ["model"] = Enabled ? Model : null,
    };

    public JsonObject AdminStatus() => new()
    {
        ["enabled"] = Enabled,
        ["ready"] = _ready,
        ["model"] = Model,
        ["state"] = _state,
        ["done"] = _done,
        ["failed"] = _failed,
        ["queued"] = _queued.Count,
        ["avgMs"] = _done > 0 ? Interlocked.Read(ref _totalMs) / _done : null,
        ["threads"] = Threads,
        ["lastError"] = _lastError,
    };

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await EnsureModelsAsync(ct);
            ScanForMissing();
            await WorkAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _state = "stoppet: " + ex.Message;
            _log.Info("teamradio-tekst " + _state);
        }
    }

    // ---------- Modeller ----------

    private async Task EnsureModelsAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromMinutes(2);
        while (true)
        {
            try
            {
                await EnsureFileAsync(VadUrl, VadModelPath, "VAD-model", ct);
                await EnsureFileAsync(ModelUrlBase + $"ggml-{Model}.bin", ModelPath, $"model {Model}", ct);
                _ready = true;
                _state = "klar";
                _log.Info($"teamradio-tekst klar (whisper {Model}, {Threads} tråde)");
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException
                                       && !ct.IsCancellationRequested)
            {
                _state = $"kunne ikke hente modellen ({ex.Message}) — prøver igen om {delay.TotalMinutes:0} min";
                _log.Info("teamradio-tekst: " + _state);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromMinutes(Math.Min(60, delay.TotalMinutes * 2));
            }
        }
    }

    private async Task EnsureFileAsync(string url, string path, string label, CancellationToken ct)
    {
        if (IsGgml(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var part = path + ".part";
        _state = $"henter {label}";
        _log.Info($"teamradio-tekst: henter {label} (første gang)");
        using (var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)res.StatusCode}");
            var total = res.Content.Headers.ContentLength ?? 0;
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(part);
            var buf = new byte[1 << 16];
            long got = 0;
            int n, shown = -1;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                got += n;
                var pct = total > 0 ? (int)(got * 100 / total) : -1;
                if (pct / 5 != shown / 5) { shown = pct; _state = $"henter {label}: {pct} %"; }
            }
        }
        if (!IsGgml(part))
        {
            File.Delete(part);
            throw new InvalidDataException("filen er ikke en whisper-model");
        }
        File.Move(part, path, overwrite: true);
        _log.Info($"teamradio-tekst: {label} hentet ({new FileInfo(path).Length / 1e6:0.0} MB)");
    }

    /// whisper.cpp-modeller starter med "lmgg" (ggml-magic, little-endian).
    public static bool IsGgml(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < 100_000) return false;
            using var f = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return f.Read(head) == 4 && head.SequenceEqual("lmgg"u8);
        }
        catch (IOException) { return false; }
    }

    // ---------- Kø ----------

    /// Klip uden tekst (fx hentet før en genstart, eller fejlede) — nyeste optagelser først.
    private void ScanForMissing()
    {
        if (!Directory.Exists(_cfg.RecordingsDir)) return;
        var n = 0;
        var dirs = Directory.EnumerateDirectories(_cfg.RecordingsDir)
            .Where(d => Directory.Exists(Path.Combine(d, "TeamRadio")))
            .OrderByDescending(d => Directory.GetLastWriteTimeUtc(Path.Combine(d, "TeamRadio")));
        foreach (var dir in dirs)
        {
            var file = FileFor(dir);
            foreach (var mp3 in Directory.EnumerateFiles(Path.Combine(dir, "TeamRadio"), "*.mp3").Order(StringComparer.Ordinal))
            {
                var rel = "TeamRadio/" + Path.GetFileName(mp3);
                if (file.Has(rel)) continue;
                Enqueue(dir, rel, live: false);
                n++;
            }
        }
        if (n > 0) _log.Info($"teamradio-tekst: {n} ældre klip mangler tekst — tages i baggrunden");
    }

    private async Task WorkAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_live.Reader.TryRead(out var job) && !_later.Reader.TryRead(out job))
            {
                await Task.WhenAny(_live.Reader.WaitToReadAsync(ct).AsTask(), _later.Reader.WaitToReadAsync(ct).AsTask());
                continue;
            }
            var abs = Path.GetFullPath(Path.Combine(job.MediaDir, job.Rel));
            try
            {
                var file = FileFor(job.MediaDir);
                if (file.Has(job.Rel) || !File.Exists(abs)) continue;
                var sw = Stopwatch.StartNew();
                var (speech, text) = await TranscribeAsync(abs, job.Live, ct);
                file.Set(job.Rel, text, speech, Model, sw.ElapsedMilliseconds, error: false);
                file.Save();
                Interlocked.Increment(ref _done);
                Interlocked.Add(ref _totalMs, sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) // ét dårligt klip må aldrig stoppe køen
            {
                Interlocked.Increment(ref _failed);
                _lastError = $"{Path.GetFileName(abs)}: {ex.Message}";
                _log.Info("teamradio-tekst fejlede: " + _lastError);
                try
                {
                    var file = FileFor(job.MediaDir);
                    file.Set(job.Rel, "", false, Model, 0, error: true); // prøves igen ved næste genstart
                    file.Save();
                }
                catch (IOException) { /* næste klip */ }
            }
            finally { _queued.TryRemove(abs, out _); }
        }
    }

    private TranscriptFile FileFor(string mediaDir)
    {
        var key = Path.GetFullPath(mediaDir);
        if (!_files.TryGetValue(key, out var f)) _files[key] = f = TranscriptFile.Load(key);
        return f;
    }

    // ---------- whisper.cpp ----------

    private async Task<(bool Speech, string Text)> TranscribeAsync(string mp3, bool live, CancellationToken ct)
    {
        var t = Threads.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var vad = await RunProcessAsync(VadTool, ["-vm", VadModelPath, "-f", mp3, "-t", t, "-np"], live, TimeSpan.FromMinutes(1), ct);
        var segs = vad.ExitCode == 0 ? RadioText.ParseVad(vad.Stdout) : null;
        if (segs is { Count: 0 }) return (false, "");
        if (vad.ExitCode != 0 && vad.Stderr.Contains("failed to read audio", StringComparison.Ordinal))
            throw new InvalidOperationException("lydfilen kunne ikke læses");

        var args = new List<string>
        {
            "-m", ModelPath, "-f", mp3, "-t", t, "-nt", "-np", "-sns",
            "-l", Language, "--prompt", RadioText.Prompt(DriverNames()),
        };
        var ac = RadioText.AudioContext(segs is { Count: > 0 } ? segs.Max(s => s.End) : 0);
        if (ac > 0) args.AddRange(["-ac", ac.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        var r = await RunProcessAsync(Cli, args, live, TimeSpan.FromMinutes(3), ct);
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"whisper-cli stoppede med kode {r.ExitCode}" +
                (LastLine(r.Stderr) is { Length: > 0 } l ? $" ({l})" : ""));
        return (true, RadioText.Clean(r.Stdout));
    }

    private IReadOnlyList<string> DriverNames()
    {
        var names = _names();
        if (names.Count == 0) return _lastNames;
        if (!names.SequenceEqual(_lastNames))
        {
            _lastNames = names;
            try { File.WriteAllLines(Path.Combine(_cfg.ModelsDir, "driver-names.txt"), names); }
            catch (IOException) { /* kun en bekvemmelighed */ }
        }
        return names;
    }

    private static string LastLine(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string exe, IEnumerable<string> args, bool normalPriority, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("kunne ikke starte " + Path.GetFileName(exe));
        if (!normalPriority)
        {
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } // nice 10: live-klip og feedet går forrest
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        var stdout = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(exe)} svarede ikke inden for {timeout.TotalSeconds:0} s");
        }
        return (p.ExitCode, await stdout, await stderr);
    }

    public void Dispose() => _http.Dispose();
}

/// <recordings>/<stem>/TeamRadio/transcripts.json — én pr. optagelse.
/// Nøgle = capture-stien fra F1 ("TeamRadio/MAXVER01_1_…mp3"), som i TeamRadio.Captures[].Path.
public sealed class TranscriptFile
{
    public const string FileName = "transcripts.json";
    private readonly JsonObject _clips;

    public string FilePath { get; }
    public int Count => _clips.Count;

    private TranscriptFile(string path, JsonObject clips)
    {
        FilePath = path;
        _clips = clips;
    }

    public static TranscriptFile Load(string mediaDir)
    {
        var path = Path.Combine(mediaDir, "TeamRadio", FileName);
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?["clips"] is JsonObject o)
                return new TranscriptFile(path, (JsonObject)o.DeepClone());
        }
        catch (Exception ex) when (ex is IOException or JsonException) { /* begynd forfra */ }
        return new TranscriptFile(path, new JsonObject());
    }

    /// Har klippet en tekst (fejlede forsøg tæller ikke).
    public bool Has(string rel) => _clips[rel] is JsonObject o && o["error"] is null;

    public string? Text(string rel) => _clips[rel]?["text"]?.GetValue<string>();

    public void Set(string rel, string text, bool speech, string model, long ms, bool error)
    {
        var o = new JsonObject
        {
            ["text"] = text,
            ["speech"] = speech,
            ["model"] = model,
            ["ms"] = ms,
            ["at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        };
        if (error) o["error"] = true;
        _clips[rel] = o;
    }

    /// Atomisk: midlertidig fil + omdøbning, så /media aldrig serverer en halv fil.
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var doc = new JsonObject { ["version"] = 1, ["clips"] = _clips.DeepClone() };
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, doc.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
