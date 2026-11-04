// ServerTests — adgangskontrol bag Nginx Proxy Manager (CIDR, betroede proxies).

using System.Net;
using SlqF1Live.Server;

var fails = 0;
void Check(bool cond, string msg)
{
    if (!cond) { fails++; Console.WriteLine("FAIL " + msg); }
    else Console.WriteLine("ok   " + msg);
}

// IpNet
Check(IpNet.TryParse("30.11.0.0/20", out var lan) && lan.Contains(IPAddress.Parse("30.11.10.42")), "LAN /20 contains workstation");
Check(!lan.Contains(IPAddress.Parse("30.11.16.1")), "outside /20");
Check(IpNet.TryParse("10.0.0.0/20", out var wg) && wg.Contains(IPAddress.Parse("10.0.0.2")), "WireGuard pool");
Check(IpNet.TryParse("::1", out var v6) && v6.Contains(IPAddress.IPv6Loopback), "IPv6 single host");
Check(lan.Contains(IPAddress.Parse("::ffff:30.11.0.5")), "IPv4-mapped IPv6 normalised");
Check(!IpNet.TryParse("30.11.0.0/33", out _) && !IpNet.TryParse("nonsense", out _), "invalid CIDR rejected");
Check(IpNet.ParseList("30.11.0.0/20, 10.0.0.0/20;127.0.0.1").Count == 3, "list parsing");

// Uden betroede proxies: forward-headere gør afsenderen "udefra"
var ac = new AccessControl("30.11.0.0/20,10.0.0.0/20,127.0.0.0/8", null, "", "", null);
var (ip1, u1) = ac.ClientIp(IPAddress.Parse("30.11.0.20"), null, null);
Check(ip1.ToString() == "30.11.0.20" && !u1 && ac.IsAdmin(ip1, u1), "direct LAN request is admin");
var (ip2, u2) = ac.ClientIp(IPAddress.Parse("30.11.0.20"), "8.8.8.8", "8.8.8.8");
Check(u2 && !ac.IsAdmin(ip2, u2), "forwarded via untrusted proxy is never admin");

// Med NPM som betroet proxy: den videresendte IP afgør
var ac2 = new AccessControl("30.11.0.0/20,10.0.0.0/20", null, "30.11.0.103/32", "", null);
var (ip3, u3) = ac2.ClientIp(IPAddress.Parse("30.11.0.103"), "8.8.8.8", "8.8.8.8");
Check(ip3.ToString() == "8.8.8.8" && !u3 && !ac2.IsAdmin(ip3, u3), "internet client via NPM is not admin");
var (ip4, u4) = ac2.ClientIp(IPAddress.Parse("30.11.0.103"), "10.0.0.2", null);
Check(ip4.ToString() == "10.0.0.2" && ac2.IsAdmin(ip4, u4), "WireGuard client via NPM is admin");
var (ip5, _) = ac2.ClientIp(IPAddress.Parse("30.11.0.103"), "1.2.3.4, 30.11.0.103", null);
Check(ip5.ToString() == "1.2.3.4", "rightmost untrusted hop in X-Forwarded-For");
var (ip6, _) = ac2.ClientIp(IPAddress.Parse("30.11.0.103"), "6.6.6.6, 9.9.9.9", null);
Check(ip6.ToString() == "9.9.9.9", "spoofed leftmost XFF ignored");
Check(!ac2.ViewRestricted, "view open by default");
Check(new AccessControl("", null, "", "", "hemmelig").ViewRestricted, "VIEW_TOKEN restricts view");

Console.WriteLine(fails > 0 ? $"\n{fails} FAILURES" : "\nALL SERVER TESTS PASSED");
return fails > 0 ? 1 : 0;
