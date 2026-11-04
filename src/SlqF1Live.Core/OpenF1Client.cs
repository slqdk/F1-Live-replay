// OpenF1Client — historisk data fra api.openf1.org (samme kilde som
// f1.slq.dk). Throttle ~2,5 req/s + retry med backoff ved 429, som på
// websiden. HttpMessageHandler kan injiceres i tests.

using System.Text.Json.Nodes;

namespace SlqF1Live.Core;

public sealed class OpenF1Client : IDisposable
{
    public const string Base = "https://api.openf1.org/v1";
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastCall = DateTime.MinValue;
    private static readonly TimeSpan MinSpacing = TimeSpan.FromMilliseconds(400);

    public OpenF1Client(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SLQ-F1-Live/1.0");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// GET {Base}{path} → JSON-array. path fx "/meetings?year=2026".
    public async Task<JsonArray> GetArrayAsync(string path, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var wait = _lastCall + MinSpacing - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                _lastCall = DateTime.UtcNow;

                using var res = await _http.GetAsync(Base + path, ct);
                if ((int)res.StatusCode == 429)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1500 * (attempt + 1)), ct);
                    continue;
                }
                var body = await res.Content.ReadAsStringAsync(ct);
                if ((int)res.StatusCode == 404) return new JsonArray();
                if (!res.IsSuccessStatusCode)
                    throw new InvalidOperationException($"OpenF1 svarede {(int)res.StatusCode} på {path}");
                return JsonNode.Parse(body) as JsonArray ?? new JsonArray();
            }
            throw new InvalidOperationException("OpenF1 rate limit (429) — prøv igen om lidt.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
