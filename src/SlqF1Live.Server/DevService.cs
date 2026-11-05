// DevService — sæsonudvikling (kvalifikation/løb) til analysesiden.
// Data fra OpenF1 via Core/DevSeasonLoader (samme beregning som websiden og
// Windows-app'en). Afsluttede sessioners resultater caches på disk
// (data/cache/openf1/), hele sæson-svaret i hukommelsen: indeværende sæson
// 20 min, ældre sæsoner 24 t. Samtidige forespørgsler deler ét hent.
// En kold sæson tager ~1 min (OpenF1's grænse pr. minut), så API'et venter
// højst et par sekunder og svarer ellers 202 med fremdrift; siden spørger
// igen. Dermed rammer ingen forespørgsel en proxy-timeout.
// Indeværende sæson varmes op ved start og efter hver afsluttet optagelse,
// så siden normalt svarer med det samme.

using System.Collections.Concurrent;
using SlqF1Live.Core;

namespace SlqF1Live.Server;

public sealed class DevService : IDisposable
{
    private readonly OpenF1Client _api = new();
    private readonly string _cacheDir;
    private readonly LogBuffer _log;
    private readonly ConcurrentDictionary<string, (DateTime At, string Json)> _mem = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inflight = new();
    private readonly ConcurrentDictionary<string, string> _progress = new();
    private readonly ConcurrentDictionary<string, (DateTime At, string Message)> _errors = new();
    private static readonly TimeSpan ErrorHold = TimeSpan.FromSeconds(20);

    public const int FirstYear = 2023; // OpenF1 har data fra 2023

    public DevService(ServerConfig cfg, LogBuffer log)
    {
        _cacheDir = Path.Combine(cfg.DataDir, "cache", "openf1");
        _log = log;
    }

    private static string Key(int year, bool quali) => $"{year}:{(quali ? "quali" : "race")}";

    private static TimeSpan MaxAge(int year) =>
        year >= DateTime.UtcNow.Year ? TimeSpan.FromMinutes(20) : TimeSpan.FromHours(24);

    /// Sæson-JSON (se DevSeasonLoader.ToJson), hvis den er klar inden for `wait`;
    /// ellers (null, fremdrift) mens hentningen fortsætter i baggrunden.
    /// Kaster, hvis hentningen fejlede (også mellem to kald, i ErrorHold).
    public async Task<(string? Json, string? Progress)> GetAsync(int year, bool quali, TimeSpan wait, CancellationToken ct)
    {
        var key = Key(year, quali);
        if (_mem.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < MaxAge(year))
            return (hit.Json, null);
        if (_errors.TryGetValue(key, out var err) && DateTime.UtcNow - err.At < ErrorHold)
            throw new InvalidOperationException(err.Message);

        var task = _inflight.GetOrAdd(key, _ => new Lazy<Task<string>>(() => LoadAsync(year, quali))).Value;
        var first = await Task.WhenAny(task, Task.Delay(wait, ct));
        if (first == task) return (await task, null);
        ct.ThrowIfCancellationRequested();
        return (null, _progress.TryGetValue(key, out var p) ? p : null);
    }

    /// Hele svaret (venter til hentningen er færdig).
    public async Task<string> GetJsonAsync(int year, bool quali, CancellationToken ct) =>
        (await GetAsync(year, quali, Timeout.InfiniteTimeSpan, ct)).Json!;

    private async Task<string> LoadAsync(int year, bool quali)
    {
        var key = Key(year, quali);
        var started = DateTime.UtcNow;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var season = await DevSeasonLoader.LoadAsync(_api, year, quali, _cacheDir,
                text => _progress[key] = text, cts.Token);
            var json = DevSeasonLoader.ToJson(season).ToJsonString();
            // Fejlede GP'er (rate limit) → behold kun kortvarigt, så næste besøg prøver igen
            var at = season.FailedGps.Count > 0 ? DateTime.UtcNow - MaxAge(year) + TimeSpan.FromMinutes(2) : DateTime.UtcNow;
            _mem[key] = (at, json);
            _errors.TryRemove(key, out _);
            if (season.FailedGps.Count > 0)
                _log.Info($"udvikling {year}: kunne ikke hente {string.Join(", ", season.FailedGps)} — prøver igen senere");
            _log.Info($"udvikling {year} {(quali ? "kvalifikation" : "løb")}: {season.GpLabels.Count} GP'er " +
                      $"hentet på {(DateTime.UtcNow - started).TotalSeconds:0} s");
            return json;
        }
        catch (OperationCanceledException ex)
        {
            const string msg = "OpenF1 svarede ikke i tide";
            _errors[key] = (DateTime.UtcNow, msg);
            throw new TimeoutException(msg, ex);
        }
        catch (Exception ex)
        {
            _errors[key] = (DateTime.UtcNow, ex.Message);
            throw;
        }
        finally
        {
            _progress.TryRemove(key, out _);
            _inflight.TryRemove(key, out _);
        }
    }

    /// Hent indeværende sæson i baggrunden (kval. + løb).
    public void Warm(CancellationToken ct, bool force = false)
    {
        var year = DateTime.UtcNow.Year;
        if (force)
        {
            _mem.TryRemove(Key(year, true), out _);
            _mem.TryRemove(Key(year, false), out _);
        }
        _ = Task.Run(async () =>
        {
            foreach (var quali in new[] { true, false })
            {
                try { await GetJsonAsync(year, quali, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Info($"udvikling {year}: kunne ikke hente fra OpenF1 ({ex.Message})");
                }
            }
        }, ct);
    }

    public void Dispose() => _api.Dispose();
}
