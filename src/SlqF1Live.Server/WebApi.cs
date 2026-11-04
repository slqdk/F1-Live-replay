// WebApi — ruter oven på SseServer: adgangskontrol, webvisning, medier og
// admin-API. Core's ruter (/state, /events, /recordings) håndteres af
// SseServer når adgang er givet.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SlqF1Live.Core;

namespace SlqF1Live.Server;

public sealed partial class ServerHost
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon",
        [".json"] = "application/json; charset=utf-8",
        [".webmanifest"] = "application/manifest+json",
        [".mp3"] = "audio/mpeg",
        [".jsonStream"] = "text/plain; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
    };

    private async Task<bool> HandleRouteAsync(HttpListenerContext ctx, string path)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        if (path == "/health") return false;

        // ---------- Admin ----------
        if (path is "/admin" or "/admin.html" or "/admin.js" || path.StartsWith("/api/", StringComparison.Ordinal) && path != "/api/public")
        {
            if (!Access.IsAdmin(req))
            {
                var (ip, untrusted) = Access.ClientIp(req);
                if (path.StartsWith("/api/", StringComparison.Ordinal))
                    SseServer.WriteJson(res, Err($"kun adgang fra LAN/WireGuard (din IP: {ip}{(untrusted ? ", via ikke-betroet proxy" : "")})"), 403);
                else
                    SendForbiddenPage(res, ip, untrusted);
                return true;
            }
            if (Cfg.AdminToken is { Length: > 0 } at && req.QueryString["key"] == at)
            {
                var secureA = string.Equals(req.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase) ? "; Secure" : "";
                res.AppendHeader("Set-Cookie", $"f1admin={Uri.EscapeDataString(at)}; Path=/; Max-Age=2592000; HttpOnly; SameSite=Strict{secureA}");
            }
            if (path is "/admin" or "/admin.html") { await ServeStaticAsync(ctx, "admin.html"); return true; }
            if (path == "/admin.js") { await ServeStaticAsync(ctx, "admin.js"); return true; }
            await HandleApiAsync(ctx, path);
            return true;
        }

        // ---------- Visning ----------
        if (!Access.CanView(req))
        {
            if (path is "/" or "/index.html")
                SendText(res, 403, "text/html; charset=utf-8",
                    "<!doctype html><meta charset=utf-8><title>SLQ F1 Live</title><body style='font:16px system-ui;background:#111;color:#ddd;padding:2em'>" +
                    "<h1>SLQ F1 Live</h1><p>Adgang kræver en nøgle: åbn siden med <code>?key=…</code>.</p>");
            else
                SseServer.WriteJson(res, Err("adgang nægtet"), 403);
            return true;
        }
        // Gyldig ?key= på siden → husk den i en cookie, så EventSource/fetch får den med
        if (Access.ViewToken is { } vt && req.QueryString["key"] == vt)
        {
            var secure = string.Equals(req.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase) ? "; Secure" : "";
            res.AppendHeader("Set-Cookie", $"f1key={Uri.EscapeDataString(vt)}; Path=/; Max-Age=31536000; HttpOnly; SameSite=Lax{secure}");
        }

        if (path == "/api/public")
        {
            SseServer.WriteJson(res, PublicStatus().ToJsonString());
            return true;
        }
        if (path.StartsWith("/media/", StringComparison.Ordinal))
        {
            await ServeMediaAsync(ctx, Uri.UnescapeDataString(path["/media/".Length..]));
            return true;
        }
        if (path is "/state" or "/events" or "/recordings" || path.StartsWith("/recordings/", StringComparison.Ordinal))
            return false; // Core

        if (path is "/" or "/index.html") { await ServeStaticAsync(ctx, "index.html"); return true; }
        var rel = path.TrimStart('/');
        if (rel.Length > 0 && !rel.Contains("..") && !rel.Contains('\\') && File.Exists(Path.Combine(Cfg.WwwRoot, rel)) &&
            !rel.StartsWith("admin", StringComparison.Ordinal))
        {
            await ServeStaticAsync(ctx, rel);
            return true;
        }
        return false;
    }

    private static string Err(string message) => new JsonObject { ["ok"] = false, ["error"] = message }.ToJsonString();
    private static string Ok(string message, JsonObject? extra = null)
    {
        var o = new JsonObject { ["ok"] = true, ["message"] = message };
        if (extra is not null) foreach (var kv in extra) o[kv.Key] = kv.Value?.DeepClone();
        return o.ToJsonString();
    }

    private async Task HandleApiAsync(HttpListenerContext ctx, string path)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var post = req.HttpMethod == "POST";
        JsonNode? body = null;
        if (post)
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            if (text.Length > 1_000_000) { SseServer.WriteJson(res, Err("for stor forespørgsel"), 413); return; }
            try { body = text.Length > 0 ? JsonNode.Parse(text) : null; }
            catch (System.Text.Json.JsonException) { SseServer.WriteJson(res, Err("ugyldig JSON"), 400); return; }
        }
        string? Field(string name) => body?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        switch (path)
        {
            case "/api/status":
                SseServer.WriteJson(res, AdminStatus().ToJsonString());
                return;

            case "/api/log":
                long.TryParse(req.QueryString["since"], out var since);
                var arr = new JsonArray();
                long last = since;
                foreach (var l in Log.Since(since))
                {
                    arr.Add(new JsonObject { ["seq"] = l.Seq, ["utc"] = l.Utc.ToString("o"), ["text"] = l.Text });
                    last = l.Seq;
                }
                SseServer.WriteJson(res, new JsonObject { ["last"] = last, ["lines"] = arr }.ToJsonString());
                return;

            case "/api/token" when post:
                var value = Field("value");
                if (string.IsNullOrWhiteSpace(value)) { SseServer.WriteJson(res, Err("tom værdi"), 400); return; }
                if (Tokens.SetToken(value, "manuel", out var msg))
                {
                    Log.Info("F1-token sat manuelt fra adminsiden — " + msg);
                    SseServer.WriteJson(res, Ok(msg));
                }
                else SseServer.WriteJson(res, Err(msg), 400);
                return;

            case "/api/login" when post:
                if (!Tokens.CredentialsConfigured)
                {
                    SseServer.WriteJson(res, Err("F1_USERNAME/F1_PASSWORD er ikke sat i .env"), 400);
                    return;
                }
                var okLogin = await Tokens.EnsureValidAsync(_ct, force: true);
                var ts = Tokens.Status;
                if (okLogin && ts.LastError is null)
                    SseServer.WriteJson(res, Ok($"logget ind · udløber {ts.ExpiresUtc?.ToLocalTime():dd/MM HH:mm}"));
                else
                    SseServer.WriteJson(res, Err(ts.LastError ?? "login fejlede"), 502);
                return;

            case "/api/feed" when post:
                switch (Field("action"))
                {
                    case "reconnect":
                        if (Feed.Wanted) Feed.Reconnect(); else Feed.Start();
                        SseServer.WriteJson(res, Ok("genforbinder"));
                        return;
                    case "always" or "off" or "auto":
                        SetFeedMode(Field("action"));
                        SseServer.WriteJson(res, Ok($"feed-tilstand: {FeedMode}"));
                        return;
                    case "close-recording":
                        Rec.CloseCurrent("lukket fra adminsiden");
                        SseServer.WriteJson(res, Ok("optagelse lukket"));
                        return;
                }
                SseServer.WriteJson(res, Err("ukendt handling"), 400);
                return;

            case "/api/schedule/refresh" when post:
                await Schedule.RefreshAsync(_ct);
                SseServer.WriteJson(res, Ok($"{Schedule.Sessions.Count} sessioner"));
                return;

            case "/api/archive/season":
                await HandleSeasonAsync(ctx);
                return;

            case "/api/archive/import" when post:
                var p = Field("path");
                if (p is null || p.Contains("..") || !p.EndsWith('/'))
                {
                    SseServer.WriteJson(res, Err("ugyldig sti"), 400);
                    return;
                }
                if (Tokens.Current is null) { SseServer.WriteJson(res, Err("arkivet kræver et F1 TV-token"), 400); return; }
                var job = Media.StartImport(p, Field("meeting"), Field("name"), Field("startDate"), _ct);
                SseServer.WriteJson(res, Ok("import startet", new JsonObject { ["job"] = job.Id }));
                return;

            case "/api/archive/fetch" when post:
            case "/api/archive/convert" when post:
                var file = Field("file") ?? "";
                if (!SseServer.IsSafeRecordingName(file)) { SseServer.WriteJson(res, Err("ukendt optagelse"), 400); return; }
                var stem = Path.GetFileNameWithoutExtension(file);
                if (stem.EndsWith("_arkiv", StringComparison.Ordinal)) stem = stem[..^"_arkiv".Length];
                if (path.EndsWith("/convert", StringComparison.Ordinal))
                {
                    var j2 = Media.StartConvert(stem, _ct);
                    SseServer.WriteJson(res, Ok("bygger optagelse fra arkiv", new JsonObject { ["job"] = j2.Id }));
                    return;
                }
                var recPath = Path.Combine(Cfg.RecordingsDir, file);
                var sessionPath = SessionRecorder.TryReadHeader(recPath, out _, out _) ? HeaderPath(recPath) : null;
                if (sessionPath is null) { SseServer.WriteJson(res, Err("optagelsen har ingen F1-sti (Path)"), 400); return; }
                var j3 = Media.StartFetch(stem, sessionPath, _ct);
                SseServer.WriteJson(res, Ok("henter arkiv", new JsonObject { ["job"] = j3.Id }));
                return;
        }
        SseServer.WriteJson(res, Err("ukendt API-kald"), 404);
    }

    private static string? HeaderPath(string recPath)
    {
        try
        {
            using var fs = new FileStream(recPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new StreamReader(fs);
            var h = JsonNode.Parse(r.ReadLine() ?? "{}");
            return h?["path"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { return null; }
    }

    private async Task HandleSeasonAsync(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        if (!int.TryParse(ctx.Request.QueryString["year"], out var year)) year = DateTime.UtcNow.Year;
        if (Tokens.Current is null) { SseServer.WriteJson(res, Err("arkivet kræver et F1 TV-token"), 400); return; }
        var client = new F1ArchiveClient(Http, () => Tokens.Current);
        var (status, json) = await client.SeasonIndexAsync(year, _ct);
        if (json is null) { SseServer.WriteJson(res, Err($"F1 svarede HTTP {(int)status}"), 502); return; }
        var meetings = new JsonArray();
        foreach (var m in (json["Meetings"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var sessions = new JsonArray();
            foreach (var s in (m["Sessions"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var sp = s["Path"]?.GetValue<string>();
                var meetingName = m["Name"]?.GetValue<string>() ?? "";
                var sessName = s["Name"]?.GetValue<string>() ?? "";
                var start = s["StartDate"]?.GetValue<string>();
                var stem = RecordingManager.FileStemFor(sp, meetingName, sessName, start, null);
                sessions.Add(new JsonObject
                {
                    ["name"] = sessName,
                    ["type"] = s["Type"]?.GetValue<string>(),
                    ["startDate"] = start,
                    ["path"] = sp,
                    ["stem"] = stem,
                    ["recorded"] = File.Exists(Path.Combine(Cfg.RecordingsDir, stem + ".f1rec")),
                    ["archived"] = File.Exists(Path.Combine(Cfg.RecordingsDir, stem, "archive", "Index.json")),
                });
            }
            meetings.Add(new JsonObject
            {
                ["name"] = m["Name"]?.GetValue<string>(),
                ["location"] = m["Location"]?.GetValue<string>(),
                ["sessions"] = sessions,
            });
        }
        SseServer.WriteJson(res, new JsonObject { ["year"] = year, ["meetings"] = meetings }.ToJsonString());
    }

    // ---------- Filer ----------

    private async Task ServeStaticAsync(HttpListenerContext ctx, string rel)
    {
        var root = Path.GetFullPath(Cfg.WwwRoot);
        var full = Path.GetFullPath(Path.Combine(root, rel));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full))
        {
            SendText(ctx.Response, 404, "text/plain; charset=utf-8", "ikke fundet");
            return;
        }
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        await SendFileAsync(ctx, full);
    }

    /// /media/<stem>/<relativ sti> → <recordings>/<stem>/<relativ sti> (teamradio m.m.)
    private async Task ServeMediaAsync(HttpListenerContext ctx, string rel)
    {
        var res = ctx.Response;
        if (rel.Contains("..") || rel.Contains('\\') || rel.StartsWith('/') || rel.Contains("/archive/") && !Access.IsAdmin(ctx.Request))
        {
            SendText(res, 404, "text/plain; charset=utf-8", "ikke fundet");
            return;
        }
        var root = Path.GetFullPath(Cfg.RecordingsDir);
        var full = Path.GetFullPath(Path.Combine(root, rel));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full) ||
            Path.GetFileName(full) == "meta.json" && !Access.IsAdmin(ctx.Request))
        {
            SendText(res, 404, "text/plain; charset=utf-8", "ikke fundet");
            return;
        }
        res.Headers["Cache-Control"] = "max-age=86400";
        await SendFileAsync(ctx, full);
    }

    private static async Task SendFileAsync(HttpListenerContext ctx, string full)
    {
        var res = ctx.Response;
        var ext = Path.GetExtension(full);
        res.ContentType = ContentTypes.TryGetValue(ext, out var ct) ? ct : "application/octet-stream";
        res.Headers["Accept-Ranges"] = "bytes";
        await using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, true);
        var total = fs.Length;
        long start = 0, end = total - 1;
        var range = ctx.Request.Headers["Range"];
        if (range is not null && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) && total > 0)
        {
            var spec = range[6..].Split(',')[0];
            var dash = spec.IndexOf('-');
            if (dash >= 0)
            {
                var a = spec[..dash];
                var b = spec[(dash + 1)..];
                if (a.Length == 0 && long.TryParse(b, out var suffix)) { start = Math.Max(0, total - suffix); }
                else
                {
                    long.TryParse(a, out start);
                    if (b.Length > 0 && long.TryParse(b, out var e2)) end = Math.Min(e2, total - 1);
                }
                if (start > end || start >= total)
                {
                    res.StatusCode = 416;
                    res.Headers["Content-Range"] = $"bytes */{total}";
                    res.Close();
                    return;
                }
                res.StatusCode = 206;
                res.Headers["Content-Range"] = $"bytes {start}-{end}/{total}";
            }
        }
        var length = Math.Max(0, end - start + 1);
        res.ContentLength64 = length;
        if (ctx.Request.HttpMethod == "HEAD") { res.Close(); return; }
        fs.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[1 << 16];
        var remaining = length;
        try
        {
            while (remaining > 0)
            {
                var n = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
                if (n <= 0) break;
                await res.OutputStream.WriteAsync(buffer.AsMemory(0, n));
                remaining -= n;
            }
            res.Close();
        }
        catch (HttpListenerException) { /* klienten afbrød (fx lyd-seek) */ }
        catch (IOException) { /* ditto */ }
    }

    private static void SendText(HttpListenerResponse res, int status, string contentType, string text)
    {
        res.StatusCode = status;
        res.ContentType = contentType;
        var bytes = Encoding.UTF8.GetBytes(text);
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.Close();
    }

    private static void SendForbiddenPage(HttpListenerResponse res, IPAddress ip, bool untrusted)
        => SendText(res, 403, "text/html; charset=utf-8",
            "<!doctype html><meta charset=utf-8><title>SLQ F1 Live — admin</title>" +
            "<body style='font:16px system-ui;background:#111;color:#ddd;padding:2em'><h1>Kun fra LAN/WireGuard</h1>" +
            $"<p>Din IP: <code>{WebUtility.HtmlEncode(ip.ToString())}</code>" +
            (untrusted ? " (via en proxy der ikke står i <code>TRUSTED_PROXIES</code>)" : "") +
            ".</p><p>Tilføj netværket til <code>ADMIN_ALLOW</code> i .env, eller brug <code>ADMIN_TOKEN</code> (<code>?key=…</code>).</p>");
}
