// SLQ F1 Live Server — headless 24/7-optager og SSE-server til Proxmox/Docker.
//
//   dotnet SlqF1Live.Server.dll                 kør serveren
//   dotnet SlqF1Live.Server.dll --healthcheck   Docker HEALTHCHECK (exit 0/1)
//   dotnet SlqF1Live.Server.dll --login-check   test F1-login trin 1-2 (uden kode),
//                                               eller fuldt login hvis F1_USERNAME/
//                                               F1_PASSWORD er sat
//   dotnet SlqF1Live.Server.dll --convert <arkivmappe> <ud.f1rec>
//                                               byg .f1rec af et hentet arkiv

using System.Runtime.InteropServices;
using SlqF1Live.Core;
using SlqF1Live.Server;

if (args.Contains("--healthcheck"))
{
    var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : 8137;
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var res = await http.GetAsync($"http://127.0.0.1:{port}/health");
        return res.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception) { return 1; }
}

if (args.Contains("--convert"))
{
    var i = Array.IndexOf(args, "--convert");
    if (args.Length < i + 3) { Console.Error.WriteLine("brug: --convert <arkivmappe> <ud.f1rec>"); return 2; }
    var r = ArchiveConverter.Convert(args[i + 1], args[i + 2], Path.GetFileName(args[i + 1].TrimEnd('/')), null);
    Console.WriteLine($"{r.Lines} linjer, {r.Topics} topics, {r.DurationMs / 60000.0:0} min, start {r.StartedUtc:o}");
    return 0;
}

var cfg = ServerConfig.Load();

if (args.Contains("--login-check"))
{
    using var http = F1Login.CreateClient();
    try
    {
        var form = await F1Login.StartFlowAsync(http, F1Login.RandomUrlSafe(24),
            F1Login.CodeChallenge(F1Login.RandomUrlSafe(64)), CancellationToken.None);
        Console.WriteLine($"login trin 1-2 OK: konfiguration hentet, formular modtaget (event '{form.EventName}')");
        if (cfg.F1Username is not null && cfg.F1Password is not null)
        {
            var token = await F1Login.LoginAsync(cfg.F1Username, cfg.F1Password);
            Console.WriteLine(F1Auth.Describe(token));
        }
        else Console.WriteLine("F1_USERNAME/F1_PASSWORD ikke sat — fuldt login ikke testet");
        return 0;
    }
    catch (F1AuthException ex)
    {
        Console.WriteLine("login fejlede: " + ex.Message);
        return 1;
    }
}

var log = new LogBuffer(cfg.LogDir, cfg.LogRetentionDays);
using var cts = new CancellationTokenSource();
using var host = new ServerHost(cfg, log);

// docker stop sender SIGTERM — luk optagelsen pænt (flush) inden exit
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; cts.Cancel(); });
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; cts.Cancel(); });

try
{
    await host.RunAsync(cts.Token);
}
catch (OperationCanceledException) { /* normal nedlukning */ }
return 0;
