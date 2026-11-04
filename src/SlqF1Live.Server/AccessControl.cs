// AccessControl — hvem må hvad.
//   Admin (kontrolside + /api/*-styring): klient-IP i ADMIN_ALLOW (LAN/WireGuard)
//     eller korrekt ADMIN_TOKEN.
//   Visning (/, /events, /recordings, /media): alle — medmindre VIEW_ALLOW
//     og/eller VIEW_TOKEN er sat.
// Bag Nginx Proxy Manager er afsenderen altid NPM's IP. Derfor:
//   * forespørgsler med X-Forwarded-For/X-Real-IP fra en IKKE-betroet afsender
//     regnes som udefra (aldrig admin via IP),
//   * fra en betroet proxy (TRUSTED_PROXIES) bruges den videresendte klient-IP.

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SlqF1Live.Server;

public readonly record struct IpNet(IPAddress Network, int PrefixLength)
{
    public static bool TryParse(string s, out IpNet net)
    {
        net = default;
        s = s.Trim();
        if (s.Length == 0) return false;
        var slash = s.IndexOf('/');
        var addrPart = slash >= 0 ? s[..slash] : s;
        if (!IPAddress.TryParse(addrPart, out var addr)) return false;
        addr = Normalize(addr);
        var max = addr.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = max;
        if (slash >= 0 && (!int.TryParse(s[(slash + 1)..], out prefix) || prefix < 0 || prefix > max)) return false;
        net = new IpNet(addr, prefix);
        return true;
    }

    public static List<IpNet> ParseList(string? list)
    {
        var result = new List<IpNet>();
        if (string.IsNullOrWhiteSpace(list)) return result;
        foreach (var part in list.Split(',', ';', ' '))
            if (TryParse(part, out var n)) result.Add(n);
        return result;
    }

    public static IPAddress Normalize(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    public bool Contains(IPAddress address)
    {
        address = Normalize(address);
        if (address.AddressFamily != Network.AddressFamily) return false;
        var a = address.GetAddressBytes();
        var n = Network.GetAddressBytes();
        var bits = PrefixLength;
        for (var i = 0; i < a.Length && bits > 0; i++, bits -= 8)
        {
            var mask = bits >= 8 ? 0xFF : (byte)(0xFF << (8 - bits));
            if ((a[i] & mask) != (n[i] & mask)) return false;
        }
        return true;
    }

    public override string ToString() => $"{Network}/{PrefixLength}";
}

public sealed class AccessControl
{
    private readonly List<IpNet> _admin;
    private readonly List<IpNet> _trusted;
    private readonly List<IpNet> _view;
    private readonly string? _adminToken;
    private readonly string? _viewToken;

    public AccessControl(ServerConfig cfg)
        : this(cfg.AdminAllow, cfg.AdminToken, cfg.TrustedProxies, cfg.ViewAllow, cfg.ViewToken) { }

    public AccessControl(string adminAllow, string? adminToken, string trusted, string viewAllow, string? viewToken)
    {
        _admin = IpNet.ParseList(adminAllow);
        _trusted = IpNet.ParseList(trusted);
        _view = IpNet.ParseList(viewAllow);
        _adminToken = string.IsNullOrWhiteSpace(adminToken) ? null : adminToken;
        _viewToken = string.IsNullOrWhiteSpace(viewToken) ? null : viewToken;
    }

    public bool ViewRestricted => _view.Count > 0 || _viewToken is not null;
    public string? ViewToken => _viewToken;

    /// Klient-IP + om forespørgslen kom via en ikke-betroet proxy.
    public (IPAddress Ip, bool UntrustedForward) ClientIp(IPAddress peer, string? xff, string? xRealIp)
    {
        peer = IpNet.Normalize(peer);
        var hasFwd = !string.IsNullOrWhiteSpace(xff) || !string.IsNullOrWhiteSpace(xRealIp);
        if (!hasFwd) return (peer, false);
        if (!_trusted.Any(n => n.Contains(peer))) return (peer, true);

        if (!string.IsNullOrWhiteSpace(xRealIp) && IPAddress.TryParse(xRealIp.Trim(), out var real))
            return (IpNet.Normalize(real), false);
        var hops = (xff ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = hops.Length - 1; i >= 0; i--)
        {
            if (!IPAddress.TryParse(hops[i], out var hop)) continue;
            hop = IpNet.Normalize(hop);
            if (!_trusted.Any(n => n.Contains(hop))) return (hop, false);
        }
        return (peer, false);
    }

    public (IPAddress Ip, bool UntrustedForward) ClientIp(HttpListenerRequest req)
        => ClientIp(req.RemoteEndPoint?.Address ?? IPAddress.None, req.Headers["X-Forwarded-For"], req.Headers["X-Real-IP"]);

    public bool IsAdmin(HttpListenerRequest req)
    {
        if (_adminToken is not null && TokenMatches(req, _adminToken, "f1admin")) return true;
        var (ip, untrusted) = ClientIp(req);
        if (untrusted) return false;
        return _admin.Any(n => n.Contains(ip));
    }

    public bool IsAdmin(IPAddress ip, bool untrusted) => !untrusted && _admin.Any(n => n.Contains(ip));

    public bool CanView(HttpListenerRequest req)
    {
        if (!ViewRestricted) return true;
        if (IsAdmin(req)) return true;
        if (_viewToken is not null && TokenMatches(req, _viewToken, "f1key")) return true;
        if (_view.Count > 0)
        {
            var (ip, untrusted) = ClientIp(req);
            if (!untrusted && _view.Any(n => n.Contains(ip))) return true;
        }
        return false;
    }

    /// Token via ?key=…, Authorization: Bearer …, X-Api-Key eller cookie.
    public static bool TokenMatches(HttpListenerRequest req, string expected, string cookieName)
    {
        var candidates = new List<string?>
        {
            req.QueryString["key"],
            req.Headers["X-Api-Key"],
            req.Cookies[cookieName]?.Value,
        };
        var auth = req.Headers["Authorization"];
        if (auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            candidates.Add(auth[7..].Trim());
        var exp = Encoding.UTF8.GetBytes(expected);
        foreach (var c in candidates)
        {
            if (string.IsNullOrEmpty(c)) continue;
            var got = Encoding.UTF8.GetBytes(c);
            if (got.Length == exp.Length && CryptographicOperations.FixedTimeEquals(got, exp)) return true;
        }
        return false;
    }
}
