// F1Auth — hjælper til F1 TV-autentificering (samme mekanisme som FastF1):
// tokenet er "subscriptionToken" fra formula1.com's "login-session"-cookie
// og sendes som "Authorization: Bearer …" (det gør F1FeedClient allerede).
// ExtractToken accepterer råt JWT, hele cookie-værdien (URL-encodet JSON)
// eller ren JSON — så brugeren bare kan indsætte hvad de kopierer.

using System.Text;
using System.Text.Json.Nodes;

namespace SlqF1Live.Core;

public static class F1Auth
{
    /// Udtræk subscription-tokenet af det brugeren har indsat.
    /// Accepterer: råt JWT ("xxx.yyy.zzz"), URL-encodet login-session-cookie
    /// (%7B%22data%22…), eller JSON {"data":{"subscriptionToken":"…"}}.
    /// Returnerer null hvis intet token kan findes.
    public static string? ExtractToken(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim().Trim('"');

        // URL-encodet cookie? Dekod først.
        if (s.Contains('%')) s = Uri.UnescapeDataString(s);

        if (s.StartsWith('{'))
        {
            try
            {
                var node = JsonNode.Parse(s);
                var token = node?["data"]?["subscriptionToken"]?.GetValue<string>()
                    ?? node?["subscriptionToken"]?.GetValue<string>();
                return string.IsNullOrWhiteSpace(token) ? null : token;
            }
            catch (System.Text.Json.JsonException) { return null; }
        }

        // Råt JWT: tre punktum-adskilte base64url-dele
        return s.Count(c => c == '.') == 2 && !s.Contains(' ') ? s : null;
    }

    /// Læs udløb og abonnementsstatus af tokenets payload (uden verifikation
    /// — kun til statuslinjer i loggen).
    public static (DateTime? ExpiresUtc, string? Status, string? Product) TokenInfo(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return (null, null, null);
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            var node = JsonNode.Parse(json);
            DateTime? exp = null;
            if (node?["exp"] is { } expNode && long.TryParse(expNode.ToString(), out var unix))
                exp = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            return (exp,
                node?["SubscriptionStatus"]?.GetValue<string>(),
                node?["SubscribedProduct"]?.GetValue<string>());
        }
        catch (FormatException) { return (null, null, null); }
        catch (System.Text.Json.JsonException) { return (null, null, null); }
    }

    /// Dansk statuslinje til loggen.
    public static string Describe(string? rawSetting)
    {
        var token = ExtractToken(rawSetting);
        if (token is null)
            return "F1-login: intet token — kører uden telemetri (banekort/DRS utilgængelige)";
        var (exp, status, product) = TokenInfo(token);
        if (exp is { } e && e < DateTime.UtcNow)
            return $"F1-login: token UDLØBET {e:yyyy-MM-dd HH:mm} UTC — forny login-session-cookien";
        var parts = new List<string> { "F1-login: token OK" };
        if (product is not null) parts.Add(product);
        if (status is not null) parts.Add(status);
        if (exp is { } e2) parts.Add($"udløber {e2:yyyy-MM-dd HH:mm} UTC");
        return string.Join(" · ", parts);
    }
}
