// CircuitService — banens omrids og svingnumre til banekortet. Hentes fra
// MultiViewer's åbne API (samme koordinatsystem som F1's Position-data) og
// gemmes på disken (data/cache/circuits/), så hver bane kun hentes én gang.
// Svaret slankes til det, kortet bruger. Uden svar tegner visningen selv
// omridset ud fra bilernes positioner, som før.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlqF1Live.Server;

public sealed class CircuitService : IDisposable
{
    public const string BaseUrl = "https://api.multiviewer.app/api/v1/circuits/";
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly string _dir;
    private readonly LogBuffer _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTime> _failedUntil = new();

    public CircuitService(ServerConfig cfg, LogBuffer log, HttpMessageHandler? handler = null)
    {
        _dir = Path.Combine(cfg.DataDir, "cache", "circuits");
        _log = log;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SLQ-F1-Live/2.0");
    }

    /// Slanket JSON (se Slim) eller null, hvis banen ikke kan hentes lige nu.
    public async Task<string?> GetJsonAsync(int key, int year, CancellationToken ct)
    {
        var id = $"{key}_{year}";
        var file = Path.Combine(_dir, $"circuit_v2_{id}.json"); // v2: med pittab
        if (File.Exists(file)) return await File.ReadAllTextAsync(file, ct);
        if (_failedUntil.TryGetValue(id, out var until) && DateTime.UtcNow < until) return null;

        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(file)) return await File.ReadAllTextAsync(file, ct);
            // MultiViewer svarer med det nyeste layout til og med året
            using var res = await _http.GetAsync($"{BaseUrl}{key}/{year}", ct);
            if (!res.IsSuccessStatusCode)
            {
                Fail(id, $"HTTP {(int)res.StatusCode}", key);
                return null;
            }
            var slim = Slim(JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)));
            if (slim is null)
            {
                Fail(id, "uventet svar", key);
                return null;
            }
            var json = slim.ToJsonString();
            Directory.CreateDirectory(_dir);
            var tmp = file + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct);
            File.Move(tmp, file, overwrite: true);
            _log.Info($"banekort: {slim["name"]} (bane {key}, {slim["year"]}) hentet");
            return json;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException
                                   && !ct.IsCancellationRequested)
        {
            Fail(id, ex.Message, key);
            return null;
        }
        finally { _gate.Release(); }
    }

    private void Fail(string id, string why, int key)
    {
        _failedUntil[id] = DateTime.UtcNow + RetryAfterFailure;
        _log.Info($"banekort: kunne ikke hente bane {key} ({why}) — tegner ud fra bilernes positioner");
    }

    /// Kun det visningen bruger: { key, year, name, rotation, x[], y[], corners[{n, x, y, angle}],
    /// pitLoss: {normal, sc, vsc} } — pittab i sekunder inkl. et normalt stop (null hvis ukendt).
    public static JsonObject? Slim(JsonNode? raw)
    {
        if (raw?["x"] is not JsonArray xs || raw["y"] is not JsonArray ys || xs.Count < 10 || xs.Count != ys.Count) return null;
        static double Num(JsonNode? n) =>
            n is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : double.NaN;

        var x = new JsonArray();
        var y = new JsonArray();
        for (var i = 0; i < xs.Count; i++)
        {
            double a = Num(xs[i]), b = Num(ys[i]);
            if (double.IsNaN(a) || double.IsNaN(b)) return null;
            x.Add(Math.Round(a));
            y.Add(Math.Round(b));
        }
        var corners = new JsonArray();
        if (raw["corners"] is JsonArray cs)
        {
            foreach (var c in cs)
            {
                double cx = Num(c?["trackPosition"]?["x"]), cy = Num(c?["trackPosition"]?["y"]), n = Num(c?["number"]);
                if (double.IsNaN(cx) || double.IsNaN(cy) || double.IsNaN(n)) continue;
                var letter = c?["letter"] is JsonValue l && l.TryGetValue<string>(out var ls) ? ls : "";
                var angle = Num(c?["angle"]);
                corners.Add(new JsonObject
                {
                    ["n"] = $"{(int)n}{letter}",
                    ["x"] = Math.Round(cx),
                    ["y"] = Math.Round(cy),
                    ["angle"] = double.IsNaN(angle) ? 0 : Math.Round(angle, 1),
                });
            }
        }
        var rotation = Num(raw["rotation"]);
        JsonObject? pitLoss = null;
        if (raw["pitLoss"] is JsonObject pl)
        {
            static double Loss(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var str)
                && double.TryParse(str, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
                ? d : Num(n);
            double normal = Loss(pl["normal"]), sc = Loss(pl["sc"]), vsc = Loss(pl["vsc"]);
            if (normal is > 5 and < 60)
                pitLoss = new JsonObject
                {
                    ["normal"] = normal,
                    ["sc"] = sc is > 3 and < 60 ? sc : Math.Round(normal * 0.65, 2),
                    ["vsc"] = vsc is > 3 and < 60 ? vsc : Math.Round(normal * 0.7, 2),
                };
        }
        return new JsonObject
        {
            ["key"] = raw["circuitKey"]?.DeepClone(),
            ["year"] = raw["year"]?.DeepClone(),
            ["name"] = raw["circuitName"]?.DeepClone(),
            ["rotation"] = double.IsNaN(rotation) ? 0 : rotation,
            ["x"] = x,
            ["y"] = y,
            ["corners"] = corners,
            ["pitLoss"] = pitLoss,
        };
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
