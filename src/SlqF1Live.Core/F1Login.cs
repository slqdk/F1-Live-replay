// F1Login — log ind på F1-kontoen med e-mail + adgangskode og hent F1 TV-
// "subscriptionToken" (sendes som Bearer til livetiming.formula1.com).
//
// OAuth2 Authorization Code + PKCE mod F1's identity provider (PingOne).
// Med PingOne's redirectless response_mode=pi.flow er hvert trin et JSON-kald:
//   1. GET  account.formula1.com/scripts/main.min.js → davinci:{clientId, acrValues}
//   2. GET  id.formula1.com/as/authorize            → JSON med formular + næste url
//   3. POST <næste url>                             → brugernavn/kode → authorization code
//   4. POST id.formula1.com/as/token                → access_token (JWT)
//   5. access_token-claim "subscriptionToken"       → tokenet til livetiming
// Der udstedes ingen refresh-token; når tokenet udløber (~4 dage), logges
// der ind igen. Flowet er porteret fra FastF1 (MIT, © 2026 Philipp Schäfer),
// se THIRD_PARTY_NOTICES.md. Adgangskoden sendes kun til id.formula1.com.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SlqF1Live.Core;

public sealed class F1AuthException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record F1LoginForm(string SubmitUrl, string InteractionId, string FlowId, string EventName);

public static partial class F1Login
{
    public const string AccountOrigin = "https://account.formula1.com";
    public const string IdpOrigin = "https://id.formula1.com";
    public const string AuthorizeUrl = IdpOrigin + "/as/authorize";
    public const string TokenUrl = IdpOrigin + "/as/token";
    public const string ConfigScriptUrl = AccountOrigin + "/scripts/main.min.js";

    /// Browser-lignende UA (IdP'en afviser ofte ukendte klienter).
    public static string UserAgent { get; set; } =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";

    public static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        handler ??= new HttpClientHandler
        {
            CookieContainer = new CookieContainer(), // IdP binder trinene sammen via cookies
            UseCookies = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        return http;
    }

    /// Fuldt login → subscription-token (JWT). Kaster F1AuthException.
    public static async Task<string> LoginAsync(string username, string password,
        HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            throw new F1AuthException("F1_USERNAME/F1_PASSWORD er ikke sat");
        using var http = CreateClient(handler);
        try
        {
            var verifier = RandomUrlSafe(64);
            var state = RandomUrlSafe(24);
            var form = await StartFlowAsync(http, state, CodeChallenge(verifier), ct);

            var payload = new JsonObject
            {
                ["id"] = form.FlowId,
                ["eventName"] = form.EventName,
                ["interactionId"] = form.InteractionId,
                ["parameters"] = new JsonObject
                {
                    ["eventType"] = "submit",
                    ["data"] = new JsonObject
                    {
                        ["actionKey"] = "SIGNIN",
                        ["formData"] = new JsonObject { ["username"] = username, ["password"] = password },
                    },
                },
            };
            using var submit = new HttpRequestMessage(HttpMethod.Post, form.SubmitUrl)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            submit.Headers.TryAddWithoutValidation("X-Requested-With", "ping-sdk");
            submit.Headers.TryAddWithoutValidation("interactionId", form.InteractionId);
            using var submitRes = await http.SendAsync(submit, ct);
            var submitBody = await submitRes.Content.ReadAsStringAsync(ct);
            var code = ParseFlowCompletion(submitBody, state, (int)submitRes.StatusCode);

            var config = await FetchConfigAsync(http, ct);
            using var tokenReq = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = config.ClientId,
                    ["code"] = code,
                    ["code_verifier"] = verifier,
                }),
            };
            using var tokenRes = await http.SendAsync(tokenReq, ct);
            var tokenBody = await tokenRes.Content.ReadAsStringAsync(ct);
            if (!tokenRes.IsSuccessStatusCode)
                throw new F1AuthException($"token-udveksling fejlede (HTTP {(int)tokenRes.StatusCode})");
            var access = TryGetString(JsonNode.Parse(tokenBody), "access_token")
                ?? throw new F1AuthException("intet access_token i svaret");
            return SubscriptionTokenFromAccessToken(access)
                ?? throw new F1AuthException("adgangstokenet har intet subscriptionToken — kræver et aktivt F1 TV-abonnement");
        }
        catch (HttpRequestException ex) { throw new F1AuthException("netværksfejl under login: " + ex.Message, ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new F1AuthException("timeout under login", ex); }
        catch (System.Text.Json.JsonException ex) { throw new F1AuthException("uventet svar under login", ex); }
    }

    private static (string ClientId, string PolicyId)? _config;
    private static DateTime _configAt;

    /// Trin 1: læs klient-id og DaVinci-policy fra account.formula1.com's frontend.
    /// Caches i 6 timer (F1 kan ændre værdierne uden varsel).
    public static async Task<(string ClientId, string PolicyId)> FetchConfigAsync(HttpClient http, CancellationToken ct)
    {
        if (_config is { } c && DateTime.UtcNow - _configAt < TimeSpan.FromHours(6)) return c;
        using var res = await http.GetAsync(ConfigScriptUrl, ct);
        if (!res.IsSuccessStatusCode)
            throw new F1AuthException($"kunne ikke hente login-konfiguration (HTTP {(int)res.StatusCode})");
        var cfg = ParseFrontendConfig(await res.Content.ReadAsStringAsync(ct));
        _config = cfg;
        _configAt = DateTime.UtcNow;
        return cfg;
    }

    public static void ClearConfigCache() => _config = null;

    /// Trin 1+2 uden legitimationsoplysninger — bruges også af "--login-check".
    public static async Task<F1LoginForm> StartFlowAsync(HttpClient http, string state, string codeChallenge,
        CancellationToken ct)
    {
        var (clientId, policyId) = await FetchConfigAsync(http, ct);
        var url = BuildAuthorizeUrl(clientId, policyId, state, RandomUrlSafe(24), codeChallenge);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("X-Requested-With", "ping-sdk");
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new F1AuthException($"login-start fejlede (HTTP {(int)res.StatusCode})");
        return ParseFlowStart(body);
    }

    public static string BuildAuthorizeUrl(string clientId, string policyId, string state, string nonce, string codeChallenge)
    {
        var q = new (string, string)[]
        {
            ("client_id", clientId), ("response_type", "code"), ("response_mode", "pi.flow"),
            ("scope", "openid"), ("state", state), ("nonce", nonce),
            ("code_challenge", codeChallenge), ("code_challenge_method", "S256"),
            ("acr_values", policyId),
        };
        return AuthorizeUrl + "?" + string.Join("&", q.Select(p => p.Item1 + "=" + Uri.EscapeDataString(p.Item2)));
    }

    /// davinci:{…clientId:"…",acrValues:"…"…} i det minificerede script.
    public static (string ClientId, string PolicyId) ParseFrontendConfig(string script)
    {
        var obj = DavinciObject().Match(script);
        if (!obj.Success) throw new F1AuthException("login-konfiguration ikke fundet (davinci-objekt mangler)");
        string Field(string name)
        {
            var m = Regex.Match(obj.Groups[1].Value, $@"\b{name}:""([^""]+)""");
            if (!m.Success) throw new F1AuthException($"login-konfiguration mangler feltet {name}");
            return m.Groups[1].Value;
        }
        return (Field("clientId"), Field("acrValues"));
    }

    [GeneratedRegex(@"\bdavinci:\{([^}]*)\}")]
    private static partial Regex DavinciObject();

    public static F1LoginForm ParseFlowStart(string body)
    {
        JsonNode? form;
        try { form = JsonNode.Parse(body); }
        catch (System.Text.Json.JsonException ex) { throw new F1AuthException("uventet svar ved login-start", ex); }
        var submitUrl = TryGetString(form?["_links"]?["next"], "href");
        var interactionId = TryGetString(form, "interactionId");
        var flowId = TryGetString(form, "id");
        var eventName = TryGetString(form, "eventName");
        if (submitUrl is null || interactionId is null || flowId is null || eventName is null)
            throw new F1AuthException("uventet svar ved login-start (formularfelter mangler)");
        // Send aldrig adgangskoden andre steder hen end F1's IdP
        if (!submitUrl.StartsWith(IdpOrigin + "/", StringComparison.Ordinal))
            throw new F1AuthException("uventet login-url: " + submitUrl);
        return new F1LoginForm(submitUrl, interactionId, flowId, eventName);
    }

    /// Slutsvaret fra DaVinci: {"status":"COMPLETED","authorizeResponse":{code,state}}.
    public static string ParseFlowCompletion(string body, string expectedState, int httpStatus)
    {
        JsonNode? data;
        try { data = JsonNode.Parse(body); }
        catch (System.Text.Json.JsonException)
        {
            throw new F1AuthException($"uventet svar fra login (HTTP {httpStatus})");
        }
        if (TryGetString(data, "status") != "COMPLETED")
        {
            var msg = TryGetString(data, "message");
            throw new F1AuthException(msg is not null ? "login afvist: " + msg : $"login fejlede (HTTP {httpStatus})");
        }
        var auth = data?["authorizeResponse"];
        if (TryGetString(auth, "state") != expectedState)
            throw new F1AuthException("login fejlede: state matcher ikke");
        return TryGetString(auth, "code") ?? throw new F1AuthException("login fejlede: ingen authorization code");
    }

    /// Udtræk claim "subscriptionToken" af access-tokenets payload (uverificeret —
    /// F1's API verificerer selv signaturen).
    public static string? SubscriptionTokenFromAccessToken(string accessToken)
    {
        var claims = DecodeJwtPayload(accessToken);
        var sub = TryGetString(claims, "subscriptionToken");
        return string.IsNullOrWhiteSpace(sub) ? null : sub;
    }

    public static JsonNode? DecodeJwtPayload(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var p = parts[1].Replace('-', '+').Replace('_', '/');
            p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
            return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(p)));
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public static string CodeChallenge(string verifier)
        => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string RandomUrlSafe(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? TryGetString(JsonNode? node, string key)
    {
        if (node is not JsonObject o || o[key] is not JsonValue v) return null;
        return v.TryGetValue<string>(out var s) ? s : null;
    }
}
