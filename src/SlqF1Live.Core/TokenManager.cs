// TokenManager — holder et gyldigt F1 TV-token til 24/7-drift.
// Kilder (højeste prioritet først): seneste login/manuelle token (gemt i
// tokenfilen), token fra konfiguration (F1_TOKEN / settings authToken).
// Har vi e-mail/adgangskode, logges der automatisk ind igen når tokenet
// udløber inden for RenewMargin, eller når F1 afviser det.
// Loginforsøg begrænses (eksponentiel backoff 10 min → 2 t) så en forkert
// adgangskode ikke hamrer løs på F1's IdP. Token/adgangskode logges aldrig.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlqF1Live.Core;

public sealed record TokenStatus(
    bool HasToken, DateTime? ExpiresUtc, string? Product, string? Subscription, string? Source,
    DateTime? ObtainedUtc, bool CredentialsConfigured, DateTime? LastAttemptUtc, string? LastError,
    DateTime? NextAttemptUtc, bool Rejected);

public sealed class TokenManager
{
    private readonly string? _storePath;
    private readonly Func<DateTime> _clock;
    private readonly Action<string> _log;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _loginGate = new(1, 1);

    private string? _token;
    private DateTime? _expires;
    private DateTime? _obtained;
    private string? _source;
    private string? _product;
    private string? _subscription;
    private bool _rejected;
    private DateTime? _lastAttempt;
    private DateTime _nextAttempt = DateTime.MinValue;
    private int _failures;
    private string? _lastError;

    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool CredentialsConfigured => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrEmpty(Password);
    public TimeSpan RenewMargin { get; set; } = TimeSpan.FromHours(12);
    public Func<string, string, CancellationToken, Task<string>> LoginFunc { get; set; }
        = (u, p, ct) => F1Login.LoginAsync(u, p, null, ct);

    /// Nyt token (kilde: "login", "manuel", "konfiguration", "fil").
    public event Action<string>? TokenChanged;

    public TokenManager(string? storePath, Func<DateTime>? clock = null, Action<string>? log = null)
    {
        _storePath = storePath;
        _clock = clock ?? (() => DateTime.UtcNow);
        _log = log ?? (_ => { });
    }

    /// Gyldigt token (ikke udløbet, ikke afvist) — ellers null.
    public string? Current
    {
        get
        {
            lock (_lock)
            {
                if (_token is null || _rejected) return null;
                if (_expires is { } e && e <= _clock()) return null;
                return _token;
            }
        }
    }

    public TokenStatus Status
    {
        get
        {
            lock (_lock)
                return new TokenStatus(Current is not null, _expires, _product, _subscription, _source, _obtained,
                    CredentialsConfigured, _lastAttempt, _lastError,
                    _nextAttempt == DateTime.MinValue ? null : _nextAttempt, _rejected);
        }
    }

    /// Indlæs gemt token fra fil og/eller token fra konfiguration (det nyeste vinder).
    public void Load(string? configuredRaw)
    {
        if (_storePath is not null && File.Exists(_storePath))
        {
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(_storePath));
                var t = node?["token"]?.GetValue<string>();
                var src = node?["source"]?.GetValue<string>() ?? "fil";
                if (t is not null) SetTokenCore(t, src, persist: false, raise: false, out _);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
            {
                _log("kunne ikke læse gemt F1-token: " + ex.Message);
            }
        }
        if (!string.IsNullOrWhiteSpace(configuredRaw) && F1Auth.ExtractToken(configuredRaw) is { } cfg)
        {
            var (exp, _, _) = F1Auth.TokenInfo(cfg);
            lock (_lock)
            {
                // Brug kun konfigurationens token hvis det er nyere end det gemte
                if (_token is null || (exp is not null && (_expires is null || exp > _expires)))
                    SetTokenCore(cfg, "konfiguration", persist: false, raise: false, out _);
            }
        }
        var s = Status;
        _log(s.HasToken
            ? $"F1-login: token OK ({s.Source}) · {s.Product ?? "F1 TV"} · {s.Subscription ?? "?"} · udløber {s.ExpiresUtc:yyyy-MM-dd HH:mm} UTC"
            : CredentialsConfigured
                ? "F1-login: intet gyldigt token — logger ind med F1_USERNAME"
                : "F1-login: intet token og ingen login-oplysninger — kun lette topics (ingen telemetri/arkiv)");
    }

    /// Sæt token manuelt (rå JWT, login-session-cookie eller JSON).
    public bool SetToken(string raw, string source, out string message)
        => SetTokenCore(raw, source, persist: true, raise: true, out message);

    private bool SetTokenCore(string raw, string source, bool persist, bool raise, out string message)
    {
        var token = F1Auth.ExtractToken(raw);
        if (token is null) { message = "Kunne ikke finde et token i værdien"; return false; }
        var (exp, status, product) = F1Auth.TokenInfo(token);
        if (exp is { } e && e <= _clock())
        {
            message = $"Tokenet er udløbet ({e:yyyy-MM-dd HH:mm} UTC)";
            return false;
        }
        lock (_lock)
        {
            var changed = token != _token;
            _token = token;
            _expires = exp;
            _status(status, product);
            _source = source;
            _obtained = _clock();
            _rejected = false;
            if (persist) Persist();
            message = $"Token OK · {product ?? "F1 TV"} · {status ?? "?"}" + (exp is null ? "" : $" · udløber {exp:yyyy-MM-dd HH:mm} UTC");
            if (!changed) raise = false;
        }
        if (raise) TokenChanged?.Invoke(source);
        return true;

        void _status(string? st, string? pr) { _subscription = st; _product = pr; }
    }

    private void Persist()
    {
        if (_storePath is null || _token is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_storePath))!);
            var json = new JsonObject
            {
                ["token"] = _token,
                ["source"] = _source,
                ["obtainedUtc"] = _obtained?.ToString("o"),
                ["expiresUtc"] = _expires?.ToString("o"),
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var tmp = _storePath + ".tmp";
            File.WriteAllText(tmp, json);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, _storePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log("kunne ikke gemme F1-token: " + ex.Message);
        }
    }

    /// F1 afviste tokenet (fx tilbagekaldt før udløb) — log ind igen ved næste EnsureValid.
    public void MarkRejected(string reason)
    {
        lock (_lock)
        {
            if (_token is null) return;
            _rejected = true;
            _lastError = "token afvist af F1: " + reason;
        }
        _log("F1-login: token afvist af F1 (" + reason + ")");
    }

    /// Sørg for et token der holder mindst RenewMargin. Logger ind hvis nødvendigt
    /// og tilladt (legitimation + backoff). force = "log ind nu" fra adminsiden.
    public async Task<bool> EnsureValidAsync(CancellationToken ct, bool force = false)
    {
        string user, pass;
        lock (_lock)
        {
            var now = _clock();
            var needs = force || _token is null || _rejected ||
                        (_expires is { } e && e - now < RenewMargin);
            if (!needs) return true;
            if (!CredentialsConfigured) return Current is not null;
            if (!force && now < _nextAttempt) return Current is not null;
            if (force && _lastAttempt is { } la && now - la < TimeSpan.FromSeconds(30)) return Current is not null;
            user = Username!;
            pass = Password!;
        }

        if (!await _loginGate.WaitAsync(0, ct)) return Current is not null; // login kører allerede
        try
        {
            lock (_lock) _lastAttempt = _clock();
            _log("F1-login: logger ind…");
            var token = await LoginFunc(user, pass, ct);
            if (!SetToken(token, "login", out var msg))
                throw new F1AuthException(msg);
            lock (_lock)
            {
                _failures = 0;
                _nextAttempt = DateTime.MinValue;
                _lastError = null;
            }
            _log("F1-login: " + msg);
            return true;
        }
        catch (Exception ex) when (ex is F1AuthException or HttpRequestException or TaskCanceledException)
        {
            if (ex is TaskCanceledException && ct.IsCancellationRequested) throw;
            if (ex is F1AuthException) F1Login.ClearConfigCache();
            lock (_lock)
            {
                _failures++;
                var wait = TimeSpan.FromMinutes(Math.Min(120, 10 * Math.Pow(2, _failures - 1)));
                _nextAttempt = _clock() + wait;
                _lastError = ex.Message;
            }
            _log($"F1-login fejlede: {ex.Message} — næste forsøg om {Math.Min(120, 10 * Math.Pow(2, _failures - 1)):0} min");
            return Current is not null;
        }
        finally
        {
            _loginGate.Release();
        }
    }
}
