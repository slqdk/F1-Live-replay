// OpenF1Client — historisk data fra api.openf1.org (samme kilde som
// f1.slq.dk). Gratis-adgangen er begrænset pr. sekund OG pr. minut: der er
// min. 400 ms mellem kald og højst 28 kald pr. rullende minut (en hel sæson
// er ~25 kald pr. tilstand). Ved 429 respekteres Retry-After (højst 60 s),
// ellers eksponentiel backoff (2–30 s, op til 6 forsøg). HttpMessageHandler
// og alle grænser kan sættes i tests.

using System.Text.Json.Nodes;

namespace SlqF1Live.Core;

public sealed class OpenF1Client : IDisposable
{
    public const string Base = "https://api.openf1.org/v1";
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<DateTime> _window = new();
    private DateTime _lastCall = DateTime.MinValue;
    /// Mindste afstand mellem to kald.
    public TimeSpan MinSpacing { get; set; } = TimeSpan.FromMilliseconds(400);
    /// Højst MaxPerWindow kald pr. rullende Window (standard 28 pr. minut).
    public int MaxPerWindow { get; set; } = 28;
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
    public int MaxAttempts { get; set; } = 6;
    /// Første backoff ved 429 uden Retry-After; fordobles pr. forsøg (loft 30 s).
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    public OpenF1Client(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SLQ-F1-Live/2.0");
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    /// GET {Base}{path} → JSON-array. path fx "/meetings?year=2026".
    /// 404 = tomt array (OpenF1 svarer 404 når et filter ikke giver rækker).
    public async Task<JsonArray> GetArrayAsync(string path, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                await ThrottleAsync(ct);
                using var res = await _http.GetAsync(Base + path, ct);
                if ((int)res.StatusCode == 429)
                {
                    if (attempt == MaxAttempts - 1) break;
                    var ra = res.Headers.RetryAfter;
                    var wait = ra?.Delta ?? (ra?.Date is { } at ? at - DateTimeOffset.UtcNow : (TimeSpan?)null)
                        ?? TimeSpan.FromMilliseconds(Math.Min(30000, BackoffBase.TotalMilliseconds * Math.Pow(2, attempt)));
                    if (wait > MaxRetryAfter) wait = MaxRetryAfter;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
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

    private async Task ThrottleAsync(CancellationToken ct)
    {
        var spacing = _lastCall + MinSpacing - DateTime.UtcNow;
        if (spacing > TimeSpan.Zero) await Task.Delay(spacing, ct);
        while (_window.Count > 0 && DateTime.UtcNow - _window.Peek() > Window) _window.Dequeue();
        if (_window.Count >= MaxPerWindow)
        {
            var wait = _window.Peek() + Window - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _window.Dequeue();
        }
        _lastCall = DateTime.UtcNow;
        _window.Enqueue(_lastCall);
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
